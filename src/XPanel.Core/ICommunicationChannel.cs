using System;
using System.Threading;
using System.Threading.Tasks;

namespace XPanel.Core.Communication
{
    /// <summary>
    /// 通信通道接口 - 定义所有通信驱动（COM、蓝牙、MQTT）的统一契约
    /// </summary>
    public interface ICommunicationChannel : IDisposable
    {
        /// <summary>
        /// 通道名称（COM3、BLE-Device、MQTT-Broker等）
        /// </summary>
        string ChannelName { get; }

        /// <summary>
        /// 当前连接状态
        /// </summary>
        ConnectionState State { get; }

        /// <summary>
        /// 异步连接到设备
        /// </summary>
        Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 异步断开连接
        /// </summary>
        Task<bool> DisconnectAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 发送消息到设备
        /// </summary>
        Task<bool> SendAsync(byte[] data, CancellationToken cancellationToken = default);

        /// <summary>
        /// 接收消息，通过事件回调返回
        /// </summary>
        Task StartReceivingAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 停止接收消息
        /// </summary>
        Task StopReceivingAsync();

        /// <summary>
        /// 连接状态变化事件
        /// </summary>
        event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;

        /// <summary>
        /// 消息接收事件
        /// </summary>
        event EventHandler<DataReceivedEventArgs> DataReceived;

        /// <summary>
        /// 错误事件
        /// </summary>
        event EventHandler<ErrorEventArgs> ErrorOccurred;
    }

    /// <summary>
    /// 连接状态枚举
    /// </summary>
    public enum ConnectionState
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Disconnecting = 3,
        Failed = 4
    }

    /// <summary>
    /// 连接状态变化事件参数
    /// </summary>
    public class ConnectionStateChangedEventArgs : EventArgs
    {
        public ConnectionState OldState { get; set; }
        public ConnectionState NewState { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// 数据接收事件参数
    /// </summary>
    public class DataReceivedEventArgs : EventArgs
    {
        public byte[] Data { get; set; }
        public DateTime ReceiveTime { get; set; }
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
