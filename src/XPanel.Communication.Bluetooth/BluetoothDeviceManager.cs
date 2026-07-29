using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using XPanel.Core.Communication;

namespace XPanel.Communication.Bluetooth
{
    /// <summary>
    /// 蓝牙设备管理器 - 统一管理 BLE 和经典蓝牙
    /// </summary>
    public class BluetoothDeviceManager : IDisposable
    {
        private BleDeviceDriver _bleDriver;
        private ClassicBluetoothDriver _classicDriver;
        private bool _disposed = false;

        public BluetoothDeviceManager()
        {
        }

        /// <summary>
        /// 扫描可用的 BLE 设备
        /// </summary>
        public async Task<BluetoothDeviceInfo[]> ScanBleDevicesAsync(TimeSpan duration)
        {
            _bleDriver ??= new BleDeviceDriver();
            return await _bleDriver.ScanDevicesAsync(duration);
        }

        /// <summary>
        /// 扫描已配对的经典蓝牙设备
        /// </summary>
        public async Task<BluetoothDeviceInfo[]> ScanClassicBluetoothDevicesAsync()
        {
            _classicDriver ??= new ClassicBluetoothDriver();
            return await _classicDriver.GetPairedDevicesAsync();
        }

        /// <summary>
        /// 连接 BLE 设备
        /// </summary>
        public async Task<ICommunicationChannel> ConnectBleDeviceAsync(string deviceAddress, BleAddressType addressType = BleAddressType.Unknown)
        {
            _bleDriver ??= new BleDeviceDriver();
            return await _bleDriver.ConnectAsync(deviceAddress, addressType);
        }

        /// <summary>
        /// 连接经典蓝牙设备
        /// </summary>
        public async Task<ICommunicationChannel> ConnectClassicBluetoothDeviceAsync(string deviceAddress)
        {
            _classicDriver ??= new ClassicBluetoothDriver();
            return await _classicDriver.ConnectAsync(deviceAddress);
        }

        public void Dispose()
        {
            if (_disposed) return;

            _bleDriver?.Dispose();
            _classicDriver?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// BLE (蓝牙低功耗) 驱动 - 不需要配对
    /// </summary>
    public class BleDeviceDriver : IDisposable
    {
        private bool _disposed = false;

        public async Task<BluetoothDeviceInfo[]> ScanDevicesAsync(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                duration = TimeSpan.FromSeconds(5);
            }

            var discoveredDevices = new ConcurrentDictionary<ulong, BluetoothDeviceInfo>();
            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
            {
                string name = args.Advertisement.LocalName;
                var addressType = args.BluetoothAddressType == BluetoothAddressType.Random
                    ? BleAddressType.Random
                    : BleAddressType.Public;

                discoveredDevices.AddOrUpdate(
                    args.BluetoothAddress,
                    _ => new BluetoothDeviceInfo
                    {
                        DeviceAddress = args.BluetoothAddress.ToString("X12"),
                        DeviceName = name ?? string.Empty,
                        DeviceType = BluetoothDeviceType.BLE,
                        AddressType = addressType,
                        SignalStrength = args.RawSignalStrengthInDBm,
                        IsPaired = false,
                        DiscoveryTime = DateTime.UtcNow,
                    },
                    (_, existing) =>
                    {
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            existing.DeviceName = name;
                        }

                        existing.AddressType = addressType;

                        existing.SignalStrength = Math.Max(existing.SignalStrength, args.RawSignalStrengthInDBm);
                        existing.DiscoveryTime = DateTime.UtcNow;
                        return existing;
                    });
            }

            watcher.Received += OnAdvertisementReceived;

            try
            {
                watcher.Start();
                await Task.Delay(duration);
            }
            finally
            {
                watcher.Stop();
                watcher.Received -= OnAdvertisementReceived;
            }

            // 某些设备广播包不带 LocalName，尝试按地址回查系统设备名。
            foreach (var entry in discoveredDevices)
            {
                var device = entry.Value;
                if (!string.IsNullOrWhiteSpace(device.DeviceName))
                {
                    continue;
                }

                try
                {
                    var resolved = device.AddressType switch
                    {
                        BleAddressType.Random => await BluetoothLEDevice.FromBluetoothAddressAsync(entry.Key, BluetoothAddressType.Random),
                        BleAddressType.Public => await BluetoothLEDevice.FromBluetoothAddressAsync(entry.Key, BluetoothAddressType.Public),
                        _ => await BluetoothLEDevice.FromBluetoothAddressAsync(entry.Key),
                    };
                    if (resolved != null && !string.IsNullOrWhiteSpace(resolved.Name))
                    {
                        device.DeviceName = resolved.Name.Trim();
                    }
                }
                catch
                {
                    // 忽略单个设备名解析失败，保留扫描结果继续处理。
                }
            }

            return discoveredDevices.Values
                .Where(d => !string.IsNullOrWhiteSpace(d.DeviceName))
                .OrderBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.DeviceAddress, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public async Task<ICommunicationChannel> ConnectAsync(string deviceAddress, BleAddressType addressType = BleAddressType.Unknown)
        {
            if (string.IsNullOrWhiteSpace(deviceAddress))
            {
                throw new ArgumentException("BLE 设备地址不能为空", nameof(deviceAddress));
            }

            if (!ulong.TryParse(deviceAddress, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            {
                throw new ArgumentException($"无效的 BLE 地址: {deviceAddress}", nameof(deviceAddress));
            }

            var channel = new BleGattCommunicationChannel(address, deviceAddress, addressType);
            bool connected = await channel.ConnectAsync();

            if (!connected)
            {
                channel.Dispose();
                throw new InvalidOperationException($"BLE 连接失败: {deviceAddress}");
            }

            return channel;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 经典蓝牙 (BR/EDR) 驱动 - 需要事先配对
    /// </summary>
    public class ClassicBluetoothDriver : IDisposable
    {
        private bool _disposed = false;

        public async Task<BluetoothDeviceInfo[]> GetPairedDevicesAsync()
        {
            // TODO: 使用 32feet.NET 库获取已配对设备
            // 这里是占位符实现
            await Task.Delay(100);
            return Array.Empty<BluetoothDeviceInfo>();
        }

        public async Task<ICommunicationChannel> ConnectAsync(string deviceAddress)
        {
            // TODO: 使用 InTheHand.Net.Bluetooth 连接 SPP
            // 这里是占位符实现
            throw new NotImplementedException("经典蓝牙连接实现待完成");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 蓝牙设备信息
    /// </summary>
    public class BluetoothDeviceInfo
    {
        public string DeviceAddress { get; set; }
        public string DeviceName { get; set; }
        public BluetoothDeviceType DeviceType { get; set; }
        public BleAddressType AddressType { get; set; }
        public int SignalStrength { get; set; } // RSSI (dBm)
        public bool IsPaired { get; set; }
        public DateTime DiscoveryTime { get; set; }
    }

    public enum BleAddressType
    {
        Unknown = 0,
        Public = 1,
        Random = 2,
    }

    /// <summary>
    /// 蓝牙设备类型
    /// </summary>
    public enum BluetoothDeviceType
    {
        Unknown = 0,
        BLE = 1,
        Classic = 2,
        Dual = 3  // 既支持 BLE 也支持经典蓝牙
    }
}
