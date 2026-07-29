using System.Collections.Generic;
using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using XPanel.Core.Communication;
using XPanel.Core.Device;
using XPanel.Core.Protocol;

namespace XPanel.Application
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private DeviceManager _deviceManager;
        private readonly Dictionary<string, ICommunicationChannel> _connectedChannels = new();
        private readonly object _channelLock = new();

        public bool RegisterConnectedChannel(string key, ICommunicationChannel channel)
        {
            if (string.IsNullOrWhiteSpace(key) || channel == null)
            {
                return false;
            }

            lock (_channelLock)
            {
                if (_connectedChannels.ContainsKey(key))
                {
                    return false;
                }

                _connectedChannels[key] = channel;
                return true;
            }
        }

        public async Task<bool> DisconnectAndRemoveChannelAsync(string key, uint sessionId)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            ICommunicationChannel? channel;
            lock (_channelLock)
            {
                if (!_connectedChannels.TryGetValue(key, out channel))
                {
                    return false;
                }
            }

            if (channel == null)
            {
                return false;
            }

            bool byeOk = await SendSessionByeAsync(channel, sessionId);
            if (!byeOk)
            {
                return false;
            }

            try
            {
                await channel.DisconnectAsync();
            }
            catch
            {
                // 忽略断开异常，确保继续释放资源。
            }

            try
            {
                channel.Dispose();
            }
            catch
            {
                // 忽略释放异常。
            }

            lock (_channelLock)
            {
                _connectedChannels.Remove(key);
            }

            return true;
        }

        private static async Task<bool> SendSessionByeAsync(ICommunicationChannel channel, uint sessionId)
        {
            var responseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnDataReceived(object? sender, DataReceivedEventArgs args)
            {
                if (args.Data == null || args.Data.Length == 0)
                {
                    return;
                }

                try
                {
                    var frame = XpfCodec.Deserialize(args.Data);
                    if (frame.OpCode != XpfProtocolConstants.OpSessionBye)
                    {
                        return;
                    }

                    if (frame.MessageType == XpfMessageType.Ack || frame.MessageType == XpfMessageType.Resp || frame.MessageType == XpfMessageType.Error)
                    {
                        responseTcs.TrySetResult(true);
                    }
                }
                catch
                {
                    // 忽略非 XPF 包
                }
            }

            channel.DataReceived += OnDataReceived;

            try
            {
                await channel.StartReceivingAsync();

                uint msgId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                var byeFrame = new XpfFrame
                {
                    MessageType = XpfMessageType.Cmd,
                    Flags = 0x01,
                    QosLevel = 1,
                    Hop = 0,
                    AppId = XpfProtocolConstants.AppIdProtocolMgr,
                    OpCode = XpfProtocolConstants.OpSessionBye,
                    MsgId = msgId,
                    TimestampSec = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };

                byeFrame.Tlvs[XpfProtocolConstants.TlvSessionId] = XpfCodec.EncodeUInt32(sessionId);
                byte[] byePayload = XpfCodec.Serialize(byeFrame);

                bool sent = await channel.SendAsync(byePayload);
                if (!sent)
                {
                    return false;
                }

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var reg = timeoutCts.Token.Register(() => responseTcs.TrySetResult(false));
                return await responseTcs.Task;
            }
            finally
            {
                channel.DataReceived -= OnDataReceived;
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 初始化设备管理器
            _deviceManager = new DeviceManager();

            // TODO: 初始化通信管理器
            // TODO: 初始化消息监测服务
            // TODO: 初始化日志系统
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 清理资源
            lock (_channelLock)
            {
                foreach (var channel in _connectedChannels.Values)
                {
                    channel?.Dispose();
                }

                _connectedChannels.Clear();
            }

            _deviceManager?.Dispose();
            base.OnExit(e);
        }
    }
}
