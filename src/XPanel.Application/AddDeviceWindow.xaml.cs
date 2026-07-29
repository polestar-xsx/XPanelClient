using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using XPanel.Communication.Bluetooth;
using XPanel.Core.Communication;
using XPanel.Core.Protocol;

namespace XPanel.Application
{
    public partial class AddDeviceWindow : Window
    {
        private static readonly Regex XPanelBleNamePattern = new("^XPanel-[0-9A-Z]{6}$", RegexOptions.Compiled);
        private static readonly string BleConnectLogPath = Path.Combine(Path.GetTempPath(), "xpanel-ble-connect.log");
        private readonly BluetoothDeviceManager _bluetoothDeviceManager = new();
        private readonly List<BleDeviceListItem> _bleDeviceItems = new();
        private bool _isConnecting;

        public string ConnectedDeviceName { get; private set; } = string.Empty;
        public string ConnectedMethodDisplay { get; private set; } = string.Empty;
        public string ConnectedDeviceAddress { get; private set; } = string.Empty;
        public uint ConnectedSessionId { get; private set; }

        public AddDeviceWindow()
        {
            InitializeComponent();
            Loaded += AddDeviceWindow_Loaded;
            // 默认选择第一项（蓝牙）
            ConnectionMethodCombo.SelectedIndex = 0;
        }

        private async void AddDeviceWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await UpdateDeviceListAsync("BLE");
        }

        private async void ConnectionMethod_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ConnectionMethodCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem)
            {
                string? method = selectedItem.Tag?.ToString();
                if (method != null)
                {
                    await UpdateDeviceListAsync(method);
                }
            }
        }

        private async Task UpdateDeviceListAsync(string connectionMethod)
        {
            AvailableDevicesList.Items.Clear();

            switch (connectionMethod)
            {
                case "BLE":
                    DeviceListLabel.Text = "Available Bluetooth Devices:";
                    await PopulateBleDevicesAsync();
                    break;
                case "SerialPort":
                    DeviceListLabel.Text = "Available Serial Ports:";
                    PopulateSerialPorts();
                    break;
                case "MQTT":
                    DeviceListLabel.Text = "Available MQTT Brokers:";
                    PopulateMqttBrokers();
                    break;
            }
        }

        private async Task PopulateBleDevicesAsync()
        {
            AvailableDevicesList.Items.Add("Scanning BLE devices...");

            try
            {
                var scannedDevices = await _bluetoothDeviceManager.ScanBleDevicesAsync(TimeSpan.FromSeconds(5));
                var matchedDeviceNames = scannedDevices
                    .Where(d => !string.IsNullOrWhiteSpace(d.DeviceName))
                    .Select(d => new BleDeviceListItem(
                        d.DeviceName.Trim(),
                        d.DeviceAddress,
                        d.AddressType,
                        d.SignalStrength))
                    .Where(item => XPanelBleNamePattern.IsMatch(item.DeviceName))
                    .OrderBy(item => item.DeviceName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.DeviceAddress, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                AvailableDevicesList.Items.Clear();
                _bleDeviceItems.Clear();

                foreach (var deviceItem in matchedDeviceNames)
                {
                    _bleDeviceItems.Add(deviceItem);
                    AvailableDevicesList.Items.Add(deviceItem);
                }

                if (AvailableDevicesList.Items.Count == 0)
                {
                    var rawNames = scannedDevices
                        .Select(d => d.DeviceName?.Trim())
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(8)
                        .ToList();

                    string rawNamePreview = rawNames.Count == 0
                        ? "(none)"
                        : string.Join(", ", rawNames);

                    MessageBox.Show(
                        $"Scanned {scannedDevices.Length} BLE devices, but none matched rule XPanel-<ID6>.\nRaw names: {rawNamePreview}",
                        "Scan Result",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                AvailableDevicesList.Items.Clear();
                MessageBox.Show(
                    $"BLE scan failed: {ex.Message}",
                    "Bluetooth Scan Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void PopulateSerialPorts()
        {
            // 模拟串口列表
            string[] mockComPorts = new string[]
            {
                "COM1",
                "COM3",
                "COM5",
            };

            foreach (var port in mockComPorts)
            {
                AvailableDevicesList.Items.Add(port);
            }
        }

        private void PopulateMqttBrokers()
        {
            // 模拟 MQTT broker 列表
            string[] mockBrokers = new string[]
            {
                "localhost:1883",
                "192.168.1.100:1883",
                "mqtt.example.com:1883",
            };

            foreach (var broker in mockBrokers)
            {
                AvailableDevicesList.Items.Add(broker);
            }
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnecting)
            {
                return;
            }

            _ = ConnectSelectedDeviceAsync();
        }

        private async Task ConnectSelectedDeviceAsync()
        {
            if (AvailableDevicesList.SelectedItem == null)
            {
                MessageBox.Show("Please select a device", "Select Device", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? connectionMethod = (ConnectionMethodCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "Unknown";
            if (connectionMethod != "BLE")
            {
                string selectedDevice = AvailableDevicesList.SelectedItem.ToString() ?? "Unknown Device";
                MessageBox.Show(
                    $"Connecting to: {selectedDevice}\nMethod: {connectionMethod}\n\nThis is a placeholder for non-BLE connection logic.",
                    "Connect Device",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                this.Close();
                return;
            }

            if (AvailableDevicesList.SelectedItem is not BleDeviceListItem selectedBleDevice)
            {
                MessageBox.Show("Invalid BLE device selection.", "Connect Device", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                LogBle("Connect button clicked.");
                _isConnecting = true;
                ConnectButton.IsEnabled = false;
                ConnectButton.Content = "Connecting...";

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var connectAndHandshakeTask = Task.Run(async () =>
                {
                    LogBle($"Connect start: {selectedBleDevice.DeviceName}, addr={selectedBleDevice.DeviceAddress}, type={selectedBleDevice.AddressType}");
                    ICommunicationChannel bleChannel = await _bluetoothDeviceManager.ConnectBleDeviceAsync(
                        selectedBleDevice.DeviceAddress,
                        selectedBleDevice.AddressType);
                    LogBle("BLE channel connected.");

                    SessionHandshakeResult handshakeResult;
                    try
                    {
                        handshakeResult = await PerformSessionHelloHandshakeAsync(
                            bleChannel,
                            endpointId: Environment.MachineName,
                            keepaliveMs: 25000,
                            cancellationToken: timeoutCts.Token);
                        LogBle($"Handshake success. SessionId={handshakeResult.SessionId}, Keepalive={handshakeResult.KeepaliveMs}");
                    }
                    catch
                    {
                        LogBle("Handshake failed, disposing channel.");
                        SafeDisposeChannel(bleChannel);
                        throw;
                    }

                    return (bleChannel, handshakeResult);
                });

                Task completed = await Task.WhenAny(connectAndHandshakeTask, Task.Delay(TimeSpan.FromSeconds(12)));
                if (completed != connectAndHandshakeTask)
                {
                    timeoutCts.Cancel();
                    LogBle("Connect/handshake timeout.");
                    throw new TimeoutException("BLE 建立连接或握手超时");
                }

                var (bleChannel, handshakeResult) = await connectAndHandshakeTask;

                if (System.Windows.Application.Current is App app)
                {
                    string channelKey = $"BLE:{selectedBleDevice.DeviceAddress}";
                    bool registered = app.RegisterConnectedChannel(channelKey, bleChannel);
                    if (!registered)
                    {
                        SafeDisposeChannel(bleChannel);
                        throw new InvalidOperationException("该设备已存在活动连接");
                    }
                }

                MessageBox.Show(
                    $"Handshake success\nDevice: {selectedBleDevice.DeviceName}\nSessionId: {handshakeResult.SessionId}\nKeepalive: {handshakeResult.KeepaliveMs} ms",
                    "BLE Connected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                ConnectedDeviceName = selectedBleDevice.DeviceName;
                ConnectedDeviceAddress = selectedBleDevice.DeviceAddress;
                ConnectedMethodDisplay = "Bluetooth (BLE)";
                ConnectedSessionId = handshakeResult.SessionId;
                DialogResult = true;
                this.Close();
            }
            catch (OperationCanceledException)
            {
                LogBle("Operation canceled.");
                MessageBox.Show(
                    "Handshake timeout (no HELLO response).",
                    "BLE Handshake Timeout",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                LogBle($"Connect exception: {ex}");
                MessageBox.Show(
                    $"Handshake failed: {ex.Message}",
                    "BLE Handshake Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                LogBle("Connect flow ended.");
                _isConnecting = false;
                ConnectButton.Content = "Connect";
                ConnectButton.IsEnabled = true;
            }
        }

        private static void SafeDisposeChannel(ICommunicationChannel channel)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    channel.Dispose();
                }
                catch
                {
                    // 失败路径的清理异常不影响 UI。
                }
            });
        }

        private static void LogBle(string message)
        {
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(BleConnectLogPath, line);
            }
            catch
            {
                // 诊断日志失败不影响主流程。
            }
        }

        private static async Task<SessionHandshakeResult> PerformSessionHelloHandshakeAsync(
            ICommunicationChannel channel,
            string endpointId,
            ushort keepaliveMs,
            CancellationToken cancellationToken)
        {
            var responseTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var receiveBuffer = new List<byte>(256);
            void OnDataReceived(object? sender, DataReceivedEventArgs args)
            {
                if (args.Data == null || args.Data.Length == 0)
                {
                    return;
                }

                lock (receiveBuffer)
                {
                    receiveBuffer.AddRange(args.Data);
                    if (TryExtractFirstXpfFrame(receiveBuffer, out var frameBytes))
                    {
                        responseTcs.TrySetResult(frameBytes);
                    }
                }
            }

            channel.DataReceived += OnDataReceived;

            try
            {
                await channel.StartReceivingAsync(cancellationToken);

                uint msgId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                uint clientNonce = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                uint tsSec = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var helloFrame = new XpfFrame
                {
                    MessageType = XpfMessageType.Cmd,
                    Flags = 0x01,
                    QosLevel = 1,
                    Hop = 0,
                    AppId = XpfProtocolConstants.AppIdProtocolMgr,
                    OpCode = XpfProtocolConstants.OpSessionHello,
                    MsgId = msgId,
                    TimestampSec = tsSec,
                };

                helloFrame.Tlvs[XpfProtocolConstants.TlvEndpointId] = XpfCodec.EncodeUtf8(endpointId);
                helloFrame.Tlvs[XpfProtocolConstants.TlvClientNonce] = XpfCodec.EncodeUInt32(clientNonce);
                helloFrame.Tlvs[XpfProtocolConstants.TlvKeepaliveMs] = XpfCodec.EncodeUInt16(keepaliveMs);

                byte[] helloPayload = XpfCodec.Serialize(helloFrame);
                bool sent = await channel.SendAsync(helloPayload, cancellationToken);
                if (!sent)
                {
                    throw new InvalidOperationException("HELLO frame 发送失败");
                }

                using var reg = cancellationToken.Register(() => responseTcs.TrySetCanceled(cancellationToken));
                byte[] responseBytes = await responseTcs.Task;
                XpfFrame responseFrame = XpfCodec.Deserialize(responseBytes);

                if (responseFrame.OpCode != XpfProtocolConstants.OpSessionHello)
                {
                    throw new InvalidDataException($"收到非 HELLO 响应 op_code: 0x{responseFrame.OpCode:X4}");
                }

                if (!XpfCodec.TryReadUInt32(responseFrame.Tlvs, XpfProtocolConstants.TlvAckForMsgId, out uint ackForMsgId) || ackForMsgId != msgId)
                {
                    throw new InvalidDataException("HELLO 响应中 ack_for_msg_id 无效");
                }

                if (responseFrame.MessageType == XpfMessageType.Error)
                {
                    throw new InvalidOperationException("设备返回 ERROR 响应");
                }

                if (!XpfCodec.TryReadUInt32(responseFrame.Tlvs, XpfProtocolConstants.TlvSessionId, out uint sessionId))
                {
                    throw new InvalidDataException("HELLO 响应缺少 session_id");
                }

                ushort negotiatedKeepalive = keepaliveMs;
                if (XpfCodec.TryReadUInt16(responseFrame.Tlvs, XpfProtocolConstants.TlvKeepaliveMs, out ushort deviceKeepalive))
                {
                    negotiatedKeepalive = deviceKeepalive;
                }

                return new SessionHandshakeResult(sessionId, negotiatedKeepalive);
            }
            finally
            {
                channel.DataReceived -= OnDataReceived;
            }
        }

        private static bool TryExtractFirstXpfFrame(List<byte> buffer, out byte[] frameBytes)
        {
            frameBytes = Array.Empty<byte>();
            const int headerLength = 24;

            if (buffer.Count < headerLength)
            {
                return false;
            }

            int start = -1;
            for (int i = 0; i <= buffer.Count - 2; i++)
            {
                if (buffer[i] == 0x58 && buffer[i + 1] == 0x50)
                {
                    start = i;
                    break;
                }
            }

            if (start < 0)
            {
                buffer.Clear();
                return false;
            }

            if (start > 0)
            {
                buffer.RemoveRange(0, start);
            }

            if (buffer.Count < headerLength)
            {
                return false;
            }

            ushort bodyLen = (ushort)((buffer[20] << 8) | buffer[21]);
            int frameLen = headerLength + bodyLen;
            if (buffer.Count < frameLen)
            {
                return false;
            }

            frameBytes = buffer.Take(frameLen).ToArray();
            buffer.RemoveRange(0, frameLen);
            return true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _bluetoothDeviceManager.Dispose();
            base.OnClosed(e);
        }

        private sealed record BleDeviceListItem(string DeviceName, string DeviceAddress, BleAddressType AddressType, int Rssi)
        {
            public override string ToString() => DeviceName;
        }

        private sealed record SessionHandshakeResult(uint SessionId, ushort KeepaliveMs);
    }
}
