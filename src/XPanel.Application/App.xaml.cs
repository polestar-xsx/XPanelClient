using System.Collections.Generic;
using System;
using System.Linq;
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
        private readonly Dictionary<string, uint> _channelSessions = new();
        private readonly Dictionary<string, SessionKeepaliveContext> _keepaliveContexts = new();
        private readonly object _channelLock = new();
        private readonly SemaphoreSlim _shutdownGate = new(1, 1);
        private Mutex? _singleInstanceMutex;
        private bool _shutdownCompleted;

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
                _channelSessions[key] = sessionId;
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
                _channelSessions.Remove(key);
            }

            return true;
        }

        public async Task ShutdownConnectionsAsync()
        {
            await _shutdownGate.WaitAsync();
            try
            {
                if (_shutdownCompleted)
                {
                    return;
                }

                await ShutdownAllChannelsGracefullyAsync();
                _shutdownCompleted = true;
            }
            finally
            {
                _shutdownGate.Release();
            }
        }

        private async Task ShutdownAllChannelsGracefullyAsync()
        {
            List<(string Key, ICommunicationChannel Channel, uint? SessionId)> channelsToClose;
            lock (_channelLock)
            {
                channelsToClose = _connectedChannels
                    .Select(item =>
                    {
                        uint? sessionId = _channelSessions.TryGetValue(item.Key, out uint id) ? id : null;
                        return (item.Key, item.Value, sessionId);
                    })
                    .ToList();
            }

            foreach (var entry in channelsToClose)
            {
                StopSessionKeepaliveMonitor(entry.Key);

                if (entry.SessionId.HasValue)
                {
                    try
                    {
                        bool byeOk = false;
                        for (int retry = 0; retry < 2 && !byeOk; retry++)
                        {
                            byeOk = await SendSessionByeAsync(entry.Channel, entry.SessionId.Value);
                            if (!byeOk)
                            {
                                await Task.Delay(250);
                            }
                        }

                        // 给底层传输一点时间完成发送队列冲刷。
                        await Task.Delay(150);
                    }
                    catch
                    {
                        // 退出阶段忽略 BYE 失败，继续执行断开和释放。
                    }
                }

                try
                {
                    await entry.Channel.DisconnectAsync();
                }
                catch
                {
                    // 退出阶段忽略断开异常。
                }

                try
                {
                    entry.Channel.Dispose();
                }
                catch
                {
                    // 退出阶段忽略释放异常。
                }
            }

            lock (_channelLock)
            {
                _connectedChannels.Clear();
                _channelSessions.Clear();
                _keepaliveContexts.Clear();
            }
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
                    if (frame.OpCode != XpfProtocolConstants.OpSessionBye)
                    {
                        return;
                    }

                    if (XpfCodec.TryReadUInt32(frame.Tlvs, XpfProtocolConstants.TlvAckForMsgId, out uint ackForMsgId) && ackForMsgId != msgId)
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
                    // 忽略非 XPF 包
                }
            }

            channel.DataReceived += OnDataReceived;

            try
            {
                await channel.StartReceivingAsync();

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

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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
            const string singleInstanceMutexName = @"Global\XPanelClient.SingleInstance";
            _singleInstanceMutex = new Mutex(initiallyOwned: true, singleInstanceMutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                MessageBox.Show("XPanelClient is already running on this PC.", "XPanelClient", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // 初始化设备管理器
            _deviceManager = new DeviceManager();

            // TODO: 初始化通信管理器
            // TODO: 初始化消息监测服务
            // TODO: 初始化日志系统
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                ShutdownConnectionsAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // 兜底清理，避免退出被阻塞。
                lock (_channelLock)
                {
                    foreach (var channel in _connectedChannels.Values)
                    {
                        try
                        {
                            channel?.Dispose();
                        }
                        catch
                        {
                            // 忽略异常。
                        }
                    }

                    _connectedChannels.Clear();
                    _channelSessions.Clear();
                    _keepaliveContexts.Clear();
                }
            }

            _deviceManager?.Dispose();

            if (_singleInstanceMutex != null)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch
                {
                    // Ignore release failure during shutdown.
                }

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

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
