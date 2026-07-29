using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using XPanel.Core.Communication;

namespace XPanel.Communication.Bluetooth
{
    /// <summary>
    /// BLE GATT 通道实现：使用固定 Service/RX/TX 特征进行收发。
    /// </summary>
    public sealed class BleGattCommunicationChannel : ICommunicationChannel
    {
        private const int BleFragmentHeaderSize = 6;
        private static readonly Guid ServiceUuid = new("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
        private static readonly Guid RxCharacteristicUuid = new("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
        private static readonly Guid TxCharacteristicUuid = new("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");

        private readonly ulong _bluetoothAddress;
        private readonly string _addressHex;
        private readonly BleAddressType _addressType;

        private BluetoothLEDevice _bleDevice;
        private GattDeviceService _service;
        private GattCharacteristic _rxCharacteristic;
        private GattCharacteristic _txCharacteristic;
        private bool _disposed;
        private ConnectionState _state = ConnectionState.Disconnected;
        private ushort _outFragmentSessionId = 1;
        private readonly object _fragmentLock = new();
        private ushort _inFragmentSessionId;
        private byte _inFragmentTotal;
        private Dictionary<byte, byte[]> _inFragments = new();

        public BleGattCommunicationChannel(ulong bluetoothAddress, string addressHex, BleAddressType addressType)
        {
            _bluetoothAddress = bluetoothAddress;
            _addressHex = addressHex;
            _addressType = addressType;
            ChannelName = $"BLE-{addressHex}";
        }

        public string ChannelName { get; }

        public ConnectionState State
        {
            get => _state;
            private set
            {
                if (_state == value)
                {
                    return;
                }

                var oldState = _state;
                _state = value;
                ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs
                {
                    OldState = oldState,
                    NewState = value,
                    Message = $"BLE 连接状态从 {oldState} 变为 {value}",
                });
            }
        }

        public event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;
        public event EventHandler<DataReceivedEventArgs> DataReceived;
        public event EventHandler<ErrorEventArgs> ErrorOccurred;

        public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (State == ConnectionState.Connected)
                {
                    return true;
                }

                State = ConnectionState.Connecting;

                _bleDevice = await ConnectDeviceWithAddressTypeAsync();
                if (_bleDevice == null)
                {
                    State = ConnectionState.Failed;
                    return false;
                }

                var serviceResult = await AwaitWithTimeout(
                    _bleDevice.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached).AsTask(),
                    TimeSpan.FromSeconds(8),
                    "发现 Service");
                if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
                {
                    State = ConnectionState.Failed;
                    return false;
                }

                _service = serviceResult.Services[0];

                var rxResult = await AwaitWithTimeout(
                    _service.GetCharacteristicsForUuidAsync(RxCharacteristicUuid, BluetoothCacheMode.Uncached).AsTask(),
                    TimeSpan.FromSeconds(6),
                    "发现 RX 特征");
                if (rxResult.Status != GattCommunicationStatus.Success || rxResult.Characteristics.Count == 0)
                {
                    State = ConnectionState.Failed;
                    return false;
                }

                var txResult = await AwaitWithTimeout(
                    _service.GetCharacteristicsForUuidAsync(TxCharacteristicUuid, BluetoothCacheMode.Uncached).AsTask(),
                    TimeSpan.FromSeconds(6),
                    "发现 TX 特征");
                if (txResult.Status != GattCommunicationStatus.Success || txResult.Characteristics.Count == 0)
                {
                    State = ConnectionState.Failed;
                    return false;
                }

                _rxCharacteristic = rxResult.Characteristics[0];
                _txCharacteristic = txResult.Characteristics[0];

                _txCharacteristic.ValueChanged += TxCharacteristic_ValueChanged;
                var notifyStatus = await AwaitWithTimeout(
                    _txCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(),
                    TimeSpan.FromSeconds(5),
                    "开启 Notify");

                if (notifyStatus != GattCommunicationStatus.Success)
                {
                    State = ConnectionState.Failed;
                    return false;
                }

                State = ConnectionState.Connected;
                return true;
            }
            catch (Exception ex)
            {
                State = ConnectionState.Failed;
                OnError(ex, $"BLE 连接失败: {_addressHex}");
                return false;
            }
        }

        public async Task<bool> DisconnectAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (State == ConnectionState.Disconnected)
                {
                    return true;
                }

                State = ConnectionState.Disconnecting;
                await StopReceivingAsync();
                CleanupBleResources();
                State = ConnectionState.Disconnected;
                return true;
            }
            catch (Exception ex)
            {
                State = ConnectionState.Failed;
                OnError(ex, "BLE 断开失败");
                return false;
            }
        }

        public async Task<bool> SendAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            if (State != ConnectionState.Connected || _rxCharacteristic == null)
            {
                throw new InvalidOperationException("BLE 未连接，无法发送数据");
            }

            try
            {
                int maxPayload = GetMaxWritePayload();
                if (data.Length <= maxPayload)
                {
                    return await WriteChunkAsync(data);
                }

                int maxFragmentPayload = Math.Max(1, maxPayload - BleFragmentHeaderSize);
                int totalFragments = (data.Length + maxFragmentPayload - 1) / maxFragmentPayload;
                if (totalFragments > byte.MaxValue)
                {
                    throw new InvalidOperationException("BLE 分片数量超过上限");
                }

                ushort sessionId = _outFragmentSessionId++;
                int offset = 0;

                for (byte index = 0; index < totalFragments; index++)
                {
                    int fragmentPayloadLength = Math.Min(maxFragmentPayload, data.Length - offset);
                    byte[] packet = new byte[BleFragmentHeaderSize + fragmentPayloadLength];
                    packet[0] = (byte)((sessionId >> 8) & 0xFF);
                    packet[1] = (byte)(sessionId & 0xFF);
                    packet[2] = index;
                    packet[3] = (byte)totalFragments;
                    packet[4] = (byte)((fragmentPayloadLength >> 8) & 0xFF);
                    packet[5] = (byte)(fragmentPayloadLength & 0xFF);
                    System.Buffer.BlockCopy(data, offset, packet, BleFragmentHeaderSize, fragmentPayloadLength);

                    if (!await WriteChunkAsync(packet))
                    {
                        return false;
                    }

                    offset += fragmentPayloadLength;
                }

                return true;
            }
            catch (Exception ex)
            {
                OnError(ex, "BLE 发送失败");
                return false;
            }
        }

        public Task StartReceivingAsync(CancellationToken cancellationToken = default)
        {
            // BLE 通知在 ConnectAsync 中已启用。
            return Task.CompletedTask;
        }

        public async Task StopReceivingAsync()
        {
            if (_txCharacteristic == null)
            {
                return;
            }

            try
            {
                _txCharacteristic.ValueChanged -= TxCharacteristic_ValueChanged;
                await _txCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch
            {
                // 断开阶段可忽略通知关闭失败。
            }
        }

        private void TxCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            try
            {
                using var reader = DataReader.FromBuffer(args.CharacteristicValue);
                var bytes = new byte[args.CharacteristicValue.Length];
                reader.ReadBytes(bytes);

                if (TryReassembleIncomingFrame(bytes, out var frame))
                {
                    DataReceived?.Invoke(this, new DataReceivedEventArgs
                    {
                        Data = frame,
                        ReceiveTime = DateTime.Now,
                    });
                }
            }
            catch (Exception ex)
            {
                OnError(ex, "处理 BLE 接收数据失败");
            }
        }

        private async Task<bool> WriteChunkAsync(byte[] chunk)
        {
            using var writer = new DataWriter();
            writer.WriteBytes(chunk);
            var status = await _rxCharacteristic.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
            return status == GattCommunicationStatus.Success;
        }

        private bool TryReassembleIncomingFrame(byte[] incomingPacket, out byte[] frame)
        {
            frame = Array.Empty<byte>();

            if (!TryParseFragmentHeader(incomingPacket, out var fragmentSessionId, out var fragmentIndex, out var fragmentTotal, out var fragmentPayload))
            {
                // 不符合分片头时，按原始 XPF 帧透传。
                frame = incomingPacket;
                return true;
            }

            lock (_fragmentLock)
            {
                if (_inFragmentSessionId != fragmentSessionId)
                {
                    _inFragmentSessionId = fragmentSessionId;
                    _inFragmentTotal = fragmentTotal;
                    _inFragments.Clear();
                }

                _inFragments[fragmentIndex] = fragmentPayload;

                if (_inFragments.Count < _inFragmentTotal)
                {
                    return false;
                }

                int totalLength = 0;
                for (byte i = 0; i < _inFragmentTotal; i++)
                {
                    if (!_inFragments.TryGetValue(i, out var piece))
                    {
                        return false;
                    }

                    totalLength += piece.Length;
                }

                frame = new byte[totalLength];
                int offset = 0;
                for (byte i = 0; i < _inFragmentTotal; i++)
                {
                    byte[] piece = _inFragments[i];
                    System.Buffer.BlockCopy(piece, 0, frame, offset, piece.Length);
                    offset += piece.Length;
                }

                _inFragments.Clear();
                _inFragmentTotal = 0;
                return true;
            }
        }

        private static bool TryParseFragmentHeader(
            byte[] packet,
            out ushort sessionId,
            out byte fragmentIndex,
            out byte fragmentTotal,
            out byte[] payload)
        {
            sessionId = 0;
            fragmentIndex = 0;
            fragmentTotal = 0;
            payload = Array.Empty<byte>();

            if (packet.Length < BleFragmentHeaderSize)
            {
                return false;
            }

            sessionId = (ushort)((packet[0] << 8) | packet[1]);
            fragmentIndex = packet[2];
            fragmentTotal = packet[3];
            ushort payloadLen = (ushort)((packet[4] << 8) | packet[5]);

            if (fragmentTotal == 0)
            {
                return false;
            }

            if (fragmentIndex >= fragmentTotal)
            {
                return false;
            }

            if (payloadLen != packet.Length - BleFragmentHeaderSize)
            {
                return false;
            }

            payload = new byte[payloadLen];
            System.Buffer.BlockCopy(packet, BleFragmentHeaderSize, payload, 0, payloadLen);
            return true;
        }

        private void CleanupBleResources()
        {
            _txCharacteristic = null;
            _rxCharacteristic = null;

            _service?.Dispose();
            _service = null;

            _bleDevice?.Dispose();
            _bleDevice = null;
        }

        private int GetMaxWritePayload()
        {
            // 当前 API 版本下不依赖 MTU 查询，保守使用 20 字节分片。
            return 20;
        }

        private async Task<BluetoothLEDevice> ConnectDeviceWithAddressTypeAsync()
        {
            BluetoothLEDevice device;

            if (_addressType == BleAddressType.Random)
            {
                device = await AwaitWithTimeout(
                    BluetoothLEDevice.FromBluetoothAddressAsync(_bluetoothAddress, BluetoothAddressType.Random).AsTask(),
                    TimeSpan.FromSeconds(8),
                    "按 Random 地址连接 BLE 设备");
                return device;
            }

            if (_addressType == BleAddressType.Public)
            {
                device = await AwaitWithTimeout(
                    BluetoothLEDevice.FromBluetoothAddressAsync(_bluetoothAddress, BluetoothAddressType.Public).AsTask(),
                    TimeSpan.FromSeconds(8),
                    "按 Public 地址连接 BLE 设备");
                return device;
            }

            device = await AwaitWithTimeout(
                BluetoothLEDevice.FromBluetoothAddressAsync(_bluetoothAddress).AsTask(),
                TimeSpan.FromSeconds(8),
                "按默认地址连接 BLE 设备");

            if (device != null)
            {
                return device;
            }

            device = await AwaitWithTimeout(
                BluetoothLEDevice.FromBluetoothAddressAsync(_bluetoothAddress, BluetoothAddressType.Public).AsTask(),
                TimeSpan.FromSeconds(8),
                "按 Public 地址重试连接 BLE 设备");

            if (device != null)
            {
                return device;
            }

            device = await AwaitWithTimeout(
                BluetoothLEDevice.FromBluetoothAddressAsync(_bluetoothAddress, BluetoothAddressType.Random).AsTask(),
                TimeSpan.FromSeconds(8),
                "按 Random 地址重试连接 BLE 设备");

            return device;
        }

        private static async Task<T> AwaitWithTimeout<T>(Task<T> task, TimeSpan timeout, string stage)
        {
            var timeoutTask = Task.Delay(timeout);
            Task completedTask = await Task.WhenAny(task, timeoutTask);
            if (completedTask == timeoutTask)
            {
                throw new TimeoutException($"{stage}超时");
            }

            return await task;
        }

        private void OnError(Exception ex, string message)
        {
            ErrorOccurred?.Invoke(this, new ErrorEventArgs
            {
                Exception = ex,
                ErrorMessage = message,
            });
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await DisconnectAsync();
                }
                catch
                {
                    // Dispose 路径避免将清理异常传播到 UI 线程。
                }
            });
            GC.SuppressFinalize(this);
        }
    }
}
