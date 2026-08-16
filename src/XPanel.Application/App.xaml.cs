using System.Collections.Generic;
using System;
using System.IO;
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
        private const int LocalKeepaliveIntervalMs = 3000;
        private static readonly TimeSpan SessionRecoveryRetryInterval = TimeSpan.FromSeconds(3);
        private DeviceManager _deviceManager;
        private readonly Dictionary<string, ICommunicationChannel> _connectedChannels = new();
        private readonly Dictionary<string, uint> _channelSessions = new();
        private readonly Dictionary<string, SessionKeepaliveContext> _keepaliveContexts = new();
        private readonly Dictionary<string, CancellationTokenSource> _sessionRecoveryContexts = new();
        private readonly object _channelLock = new();
        private readonly SemaphoreSlim _shutdownGate = new(1, 1);
        private Mutex? _singleInstanceMutex;
        private bool _shutdownCompleted;

        public event EventHandler<ChannelSessionStateChangedEventArgs>? ChannelSessionStateChanged;

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
            RaiseChannelSessionStateChanged(key, ChannelSessionState.Connected, channel, sessionId, keepaliveMs, "Channel registered");
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

            StopSessionRecoveryLoop(key);
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

            RaiseChannelSessionStateChanged(key, ChannelSessionState.Disconnected, channel, null, null, "Channel removed by user");

            return true;
        }

        public async Task<bool> SendTimeSyncAsync(
            string key,
            byte timeSource = 1,
            byte timeSetMode = 2,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            ICommunicationChannel? channel;
            uint sessionId;

            lock (_channelLock)
            {
                if (!_connectedChannels.TryGetValue(key, out channel) ||
                    !_channelSessions.TryGetValue(key, out sessionId))
                {
                    return false;
                }
            }

            if (channel == null)
            {
                return false;
            }

            DateTimeOffset now = DateTimeOffset.Now;
            int offsetMinutes = (int)now.Offset.TotalMinutes;
            if (offsetMinutes > short.MaxValue)
            {
                offsetMinutes = short.MaxValue;
            }
            else if (offsetMinutes < short.MinValue)
            {
                offsetMinutes = short.MinValue;
            }

            return await SendTimeSyncFrameAsync(
                channel,
                sessionId,
                (uint)now.ToUnixTimeSeconds(),
                (short)offsetMinutes,
                timeSource,
                timeSetMode,
                cancellationToken);
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
                foreach (var recoveryCts in _sessionRecoveryContexts.Values)
                {
                    try
                    {
                        recoveryCts.Cancel();
                    }
                    catch
                    {
                        // 忽略取消异常。
                    }
                }

                _sessionRecoveryContexts.Clear();
            }
        }

        private void StartSessionKeepaliveMonitor(string key, ICommunicationChannel channel, uint sessionId, ushort keepaliveMs)
        {
            int normalizedKeepaliveMs = LocalKeepaliveIntervalMs;

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

                    context.IncrementMisses();
                    StopSessionKeepaliveMonitor(context.Key);
                    RaiseChannelSessionStateChanged(
                        context.Key,
                        ChannelSessionState.Disconnected,
                        context.Channel,
                        null,
                        null,
                        "Keepalive ACK timeout");

                    StartSessionRecoveryLoop(context.Key, context.Channel, context.KeepaliveMs);
                    break;
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

        private void StartSessionRecoveryLoop(string key, ICommunicationChannel channel, int fallbackKeepaliveMs)
        {
            CancellationTokenSource recoveryCts;
            lock (_channelLock)
            {
                if (!_connectedChannels.TryGetValue(key, out var registered) || !ReferenceEquals(registered, channel))
                {
                    return;
                }

                if (_sessionRecoveryContexts.ContainsKey(key))
                {
                    return;
                }

                recoveryCts = new CancellationTokenSource();
                _sessionRecoveryContexts[key] = recoveryCts;
            }

            _ = RunSessionRecoveryLoopAsync(key, channel, fallbackKeepaliveMs, recoveryCts.Token);
        }

        private async Task RunSessionRecoveryLoopAsync(
            string key,
            ICommunicationChannel channel,
            int fallbackKeepaliveMs,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    bool stillRegistered;
                    lock (_channelLock)
                    {
                        stillRegistered = _connectedChannels.TryGetValue(key, out var registered)
                            && ReferenceEquals(registered, channel);
                    }

                    if (!stillRegistered)
                    {
                        return;
                    }

                    try
                    {
                        try
                        {
                            await channel.DisconnectAsync(cancellationToken);
                        }
                        catch
                        {
                            // 断开失败不阻断后续重连尝试。
                        }

                        bool connected = await channel.ConnectAsync(cancellationToken);
                        if (!connected)
                        {
                            throw new InvalidOperationException("Reconnect failed");
                        }

                        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        handshakeCts.CancelAfter(TimeSpan.FromSeconds(3));
                        SessionHandshakeResult handshake = await PerformSessionHelloHandshakeAsync(
                            channel,
                            endpointId: Environment.MachineName,
                            keepaliveMs: (ushort)Math.Max(1, fallbackKeepaliveMs),
                            cancellationToken: handshakeCts.Token);

                        lock (_channelLock)
                        {
                            if (!_connectedChannels.TryGetValue(key, out var registered) || !ReferenceEquals(registered, channel))
                            {
                                return;
                            }

                            _channelSessions[key] = handshake.SessionId;
                        }

                        StartSessionKeepaliveMonitor(key, channel, handshake.SessionId, handshake.KeepaliveMs);
                        RaiseChannelSessionStateChanged(
                            key,
                            ChannelSessionState.Connected,
                            channel,
                            handshake.SessionId,
                            handshake.KeepaliveMs,
                            "Session recovered");
                        return;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch
                    {
                        // 按固定周期持续重连重握手。
                    }

                    await Task.Delay(SessionRecoveryRetryInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消。
            }
            finally
            {
                CancellationTokenSource? ctsToDispose = null;
                lock (_channelLock)
                {
                    if (_sessionRecoveryContexts.TryGetValue(key, out var existing) && existing.Token == cancellationToken)
                    {
                        ctsToDispose = existing;
                        _sessionRecoveryContexts.Remove(key);
                    }
                }

                ctsToDispose?.Dispose();
            }
        }

        private void StopSessionRecoveryLoop(string key)
        {
            CancellationTokenSource? cts = null;
            lock (_channelLock)
            {
                if (_sessionRecoveryContexts.TryGetValue(key, out var existing))
                {
                    cts = existing;
                    _sessionRecoveryContexts.Remove(key);
                }
            }

            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch
            {
                // 忽略取消异常。
            }

            cts.Dispose();
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

                bool sent = await channel.SendAsync(XpfCodec.Serialize(helloFrame), cancellationToken);
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

        private void RaiseChannelSessionStateChanged(
            string key,
            ChannelSessionState state,
            ICommunicationChannel? channel,
            uint? sessionId,
            ushort? keepaliveMs,
            string reason)
        {
            ChannelSessionStateChanged?.Invoke(this, new ChannelSessionStateChangedEventArgs(
                key,
                state,
                channel,
                sessionId,
                keepaliveMs,
                reason));
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

        private static async Task<bool> SendTimeSyncFrameAsync(
            ICommunicationChannel channel,
            uint sessionId,
            uint unixSec,
            short timezoneOffsetMin,
            byte timeSource,
            byte timeSetMode,
            CancellationToken cancellationToken)
        {
            uint msgId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);

            var frame = new XpfFrame
            {
                MessageType = XpfMessageType.Cmd,
                Flags = 0x01,
                QosLevel = 1,
                Hop = 0,
                AppId = XpfProtocolConstants.AppIdRtcMgr,
                OpCode = XpfProtocolConstants.OpTimeSync,
                MsgId = msgId,
                TimestampSec = unixSec,
            };

            frame.Tlvs[XpfProtocolConstants.TlvSessionId] = XpfCodec.EncodeUInt32(sessionId);
            frame.Tlvs[XpfProtocolConstants.TlvTimeUnixSec] = XpfCodec.EncodeUInt32(unixSec);
            frame.Tlvs[XpfProtocolConstants.TlvTimeTzOffsetMin] = XpfCodec.EncodeInt16(timezoneOffsetMin);
            frame.Tlvs[XpfProtocolConstants.TlvTimeSource] = new[] { timeSource };
            frame.Tlvs[XpfProtocolConstants.TlvTimeSetMode] = new[] { timeSetMode };

            byte[] payload = XpfCodec.Serialize(frame);
            return await channel.SendAsync(payload, cancellationToken);
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

        public enum ChannelSessionState
        {
            Disconnected = 0,
            Connected = 1,
        }

        public sealed record ChannelSessionStateChangedEventArgs(
            string Key,
            ChannelSessionState State,
            ICommunicationChannel? Channel,
            uint? SessionId,
            ushort? KeepaliveMs,
            string Reason);

        private sealed record SessionHandshakeResult(uint SessionId, ushort KeepaliveMs);
    }
}
