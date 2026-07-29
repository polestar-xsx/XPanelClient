using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace XPanel.Core.Device
{
    /// <summary>
    /// 设备管理器 - 负责设备的连接、状态管理和命令转发
    /// </summary>
    public class DeviceManager : IDisposable
    {
        private readonly Dictionary<string, ConnectedDevice> _devices = new();
        private bool _disposed = false;

        public event EventHandler<DeviceEventArgs> DeviceConnected;
        public event EventHandler<DeviceEventArgs> DeviceDisconnected;
        public event EventHandler<DeviceEventArgs> DeviceStateChanged;
        public event EventHandler<ErrorEventArgs> ErrorOccurred;

        public DeviceManager()
        {
        }

        /// <summary>
        /// 获取所有已连接的设备
        /// </summary>
        public IReadOnlyList<ConnectedDevice> GetConnectedDevices()
        {
            lock (_devices)
            {
                return _devices.Values.ToList();
            }
        }

        /// <summary>
        /// 获取指定设备
        /// </summary>
        public ConnectedDevice GetDevice(string deviceId)
        {
            lock (_devices)
            {
                return _devices.TryGetValue(deviceId, out var device) ? device : null;
            }
        }

        /// <summary>
        /// 注册设备
        /// </summary>
        public void RegisterDevice(string deviceId, ConnectedDevice device)
        {
            if (string.IsNullOrEmpty(deviceId))
                throw new ArgumentNullException(nameof(deviceId));

            lock (_devices)
            {
                if (_devices.ContainsKey(deviceId))
                    throw new InvalidOperationException($"设备 {deviceId} 已注册");

                _devices[deviceId] = device ?? throw new ArgumentNullException(nameof(device));
            }

            DeviceConnected?.Invoke(this, new DeviceEventArgs { DeviceId = deviceId });
        }

        /// <summary>
        /// 注销设备
        /// </summary>
        public void UnregisterDevice(string deviceId)
        {
            lock (_devices)
            {
                if (_devices.Remove(deviceId))
                {
                    DeviceDisconnected?.Invoke(this, new DeviceEventArgs { DeviceId = deviceId });
                }
            }
        }

        /// <summary>
        /// 向设备发送命令
        /// </summary>
        public async Task<bool> SendCommandAsync(string deviceId, Command command)
        {
            var device = GetDevice(deviceId);
            if (device == null)
                throw new InvalidOperationException($"设备 {deviceId} 未找到");

            return await device.SendCommandAsync(command);
        }

        public void Dispose()
        {
            if (_disposed) return;

            lock (_devices)
            {
                foreach (var device in _devices.Values)
                {
                    device?.Dispose();
                }
                _devices.Clear();
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 已连接的设备信息
    /// </summary>
    public class ConnectedDevice : IDisposable
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public DeviceType DeviceType { get; set; }
        public DateTime ConnectedTime { get; set; }
        public DeviceStatus Status { get; set; }

        private bool _disposed = false;

        public event EventHandler<DeviceEventArgs> StatusChanged;

        /// <summary>
        /// 向设备发送命令
        /// </summary>
        public async Task<bool> SendCommandAsync(Command command)
        {
            // 具体实现由通信管理器完成
            await Task.Delay(100); // 占位符
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 设备类型
    /// </summary>
    public enum DeviceType
    {
        Unknown = 0,
        Panel = 1,
        Sensor = 2,
        Controller = 3
    }

    /// <summary>
    /// 设备状态
    /// </summary>
    public enum DeviceStatus
    {
        Offline = 0,
        Online = 1,
        Idle = 2,
        Busy = 3,
        Error = 4
    }

    /// <summary>
    /// 命令基类
    /// </summary>
    public class Command
    {
        public string CommandId { get; set; }
        public string CommandName { get; set; }
        public byte[] Payload { get; set; }
        public DateTime CreatedTime { get; set; }
    }

    /// <summary>
    /// 设备事件参数
    /// </summary>
    public class DeviceEventArgs : EventArgs
    {
        public string DeviceId { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// 错误事件参数
    /// </summary>
    public class ErrorEventArgs : EventArgs
    {
        public Exception Exception { get; set; }
        public string ErrorMessage { get; set; }
    }
}
