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
        private readonly Dictionary<string, SessionKeepaliveContext> _keepaliveContexts = new();
        private readonly object _channelLock = new();

        public bool RegisterConnectedChannel(string key, ICommunicationChannel channel, uint sessionId, ushort keepaliveMs)
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
            }

            StartSessionKeepaliveMonitor(key, channel, sessionId, keepaliveMs);
            return true;
        }

        public async Task<bool> DisconnectAndRemoveChannelAsync(string key, uint sessionId)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            ICommunicationChannel? channel;
            SessionKeepaliveContext? keepaliveContext;
            lock (_channelLock)
            {
                if (!_connectedChannels.TryGetValue(key, out channel))
                {
                    return false;
                }

                _keepaliveContexts.TryGetValue(key, out keepaliveContext);
            }

            if (channel == null)
            {
                return false;
            }

            StopSessionKeepaliveMonitor(key);

            bool byeOk = await SendSessionByeAsync(channel, sessionId);
            if (!byeOk)
            {
                if (keepaliveContext != null)
                {
                    StartSessionKeepaliveMonitor(
                        key,
                        keepaliveContext.Channel,
                        keepaliveContext.SessionId,
                        (ushort)keepaliveContext.KeepaliveMs);
                }

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

        private void StartSessionKeepaliveMonitor(string key, ICommunicationChannel channel, uint sessionId, ushort keepaliveMs)
        {
            int normalizedKeepaliveMs = keepaliveMs <= 0 ? 25000 : keepaliveMs;

            StopSessionKeepaliveMonitor(key);

            var context = new SessionKeepaliveContext(key, channel, sessionId, normalizedKeepaliveMs);
            EventHandler<DataReceivedEventArgs> onDataReceived = (sender, args) =>
            {
                if (args.Data == null || args.Data.Length == 0)
                {
                    return;
                }

                try
                {
                    _ = XpfCodec.Deserialize(args.Data);
                    context.TouchInbound();
                }
                catch
                {
                    // 忽略非 XPF 数据包。
                }
            };

            context.DataReceivedHandler = onDataReceived;
            channel.DataReceived += onDataReceived;

            lock (_channelLock)
            {
                _keepaliveContexts[key] = context;
            }

            _ = RunSessionKeepaliveLoopAsync(context);
        }

        private async Task RunSessionKeepaliveLoopAsync(SessionKeepaliveContext context)
        {
            try
            {
                while (!context.TokenSource.IsCancellationRequested)
                {
                    await Task.Delay(context.KeepaliveMs, context.TokenSource.Token);

                    if (context.TokenSource.IsCancellationRequested)
                    {
                        break;
                    }

                    if (!context.ShouldSendKeepalive())
                    {
                        continue;
                    }

                    bool ok = await SendSessionKeepaliveAsync(
                        context.Channel,
                        context.SessionId,
                        context.TokenSource.Token);

                    if (ok)
                    {
                        context.ResetMisses();
                        continue;
                    }

                    if (context.IncrementMisses() >= 3)
                    {
                        // 连续丢失保活响应后停止保活任务，等待上层执行重连/重握手。
                        StopSessionKeepaliveMonitor(context.Key);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出。
            }
        }

        private void StopSessionKeepaliveMonitor(string key)
        {
            SessionKeepaliveContext? context = null;
            lock (_channelLock)
            {
                if (_keepaliveContexts.TryGetValue(key, out var existing))
                {
                    context = existing;
                    _keepaliveContexts.Remove(key);
                }
            }

            if (context == null)
            {
                return;
            }

            try
            {
                context.TokenSource.Cancel();
            }
            catch
            {
                // 忽略取消异常。
            }

            if (context.DataReceivedHandler != null)
            {
                context.Channel.DataReceived -= context.DataReceivedHandler;
            }

            context.TokenSource.Dispose();
        }

        private static async Task<bool> SendSessionKeepaliveAsync(
            ICommunicationChannel channel,
            uint sessionId,
            CancellationToken cancellationToken)
        {
            var responseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            uint msgId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);

            void OnDataReceived(object? sender, DataReceivedEventArgs args)
            {
                if (args.Data == null || args.Data.Length == 0)
                {
                    return;
                }

                try
                {
                    var frame = XpfCodec.Deserialize(args.Data);
                    if (frame.OpCode != XpfProtocolConstants.OpSessionKeepalive)
                    {
                        return;
                    }

                    if (!XpfCodec.TryReadUInt32(frame.Tlvs, XpfProtocolConstants.TlvAckForMsgId, out uint ackForMsgId) || ackForMsgId != msgId)
                    {
                        return;
                    }

                    if (frame.MessageType == XpfMessageType.Ack || frame.MessageType == XpfMessageType.Resp)
                    {
                        responseTcs.TrySetResult(true);
                        return;
                    }

                    if (frame.MessageType == XpfMessageType.Error)
                    {
                        responseTcs.TrySetResult(false);
                    }
                }
                catch
                {
                    // 忽略非 XPF 数据包。
                }
            }

            channel.DataReceived += OnDataReceived;

            try
            {
                await channel.StartReceivingAsync(cancellationToken);

                var keepaliveFrame = new XpfFrame
                {
                    MessageType = XpfMessageType.Cmd,
                    Flags = 0x01,
                    QosLevel = 1,
                    Hop = 0,
                    AppId = XpfProtocolConstants.AppIdProtocolMgr,
                    OpCode = XpfProtocolConstants.OpSessionKeepalive,
                    MsgId = msgId,
                    TimestampSec = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };

                keepaliveFrame.Tlvs[XpfProtocolConstants.TlvSessionId] = XpfCodec.EncodeUInt32(sessionId);

                bool sent = await channel.SendAsync(XpfCodec.Serialize(keepaliveFrame), cancellationToken);
                if (!sent)
                {
                    return false;
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
                using var reg = timeoutCts.Token.Register(() => responseTcs.TrySetResult(false));
                return await responseTcs.Task;
            }
            finally
            {
                channel.DataReceived -= OnDataReceived;
            }
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
            List<string> keepaliveKeys;
            lock (_channelLock)
            {
                keepaliveKeys = new List<string>(_keepaliveContexts.Keys);

                foreach (var channel in _connectedChannels.Values)
                {
                    channel?.Dispose();
                }

                _connectedChannels.Clear();
            }

            foreach (var key in keepaliveKeys)
            {
                StopSessionKeepaliveMonitor(key);
            }

            _deviceManager?.Dispose();
            base.OnExit(e);
        }

        private sealed class SessionKeepaliveContext
        {
            private int _consecutiveMisses;
            private DateTime _lastInboundUtc = DateTime.UtcNow;

            public SessionKeepaliveContext(string key, ICommunicationChannel channel, uint sessionId, int keepaliveMs)
            {
                Key = key;
                Channel = channel;
                SessionId = sessionId;
                KeepaliveMs = keepaliveMs;
            }

            public string Key { get; }
            public ICommunicationChannel Channel { get; }
            public uint SessionId { get; }
            public int KeepaliveMs { get; }
            public CancellationTokenSource TokenSource { get; } = new();
            public EventHandler<DataReceivedEventArgs>? DataReceivedHandler { get; set; }

            public void TouchInbound()
            {
                _lastInboundUtc = DateTime.UtcNow;
            }

            public bool ShouldSendKeepalive()
            {
                return (DateTime.UtcNow - _lastInboundUtc).TotalMilliseconds >= KeepaliveMs;
            }

            public int IncrementMisses()
            {
                _consecutiveMisses++;
                return _consecutiveMisses;
            }

            public void ResetMisses()
            {
                _consecutiveMisses = 0;
            }
        }
    }
}
