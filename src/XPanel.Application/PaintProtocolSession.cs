using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using XPanel.Core.Communication;
using XPanel.Core.Protocol;
using MediaColor = System.Windows.Media.Color;

namespace XPanel.Application
{
    internal sealed record PaintConnection(string ChannelKey, ICommunicationChannel Channel, uint SessionId, string DeviceName);

    internal sealed record PaintSyncInfo(ushort Seq, uint CanvasCrc32, bool OutOfSync, bool FrameTransferActive);

    internal sealed class PaintProtocolException : Exception
    {
        public PaintProtocolException(string message) : base(message)
        {
        }
    }

    // 协议第 16 章控制端实现：所有操作经单一队列按序发送，保证关键帧传输期间不会插入 stroke/fill。
    internal sealed class PaintProtocolSession : IDisposable
    {
        public const byte BrushSquare = 1;
        public const byte BrushRound = 2;
        public const byte StrokeStart = 0x01;
        public const byte StrokeEnd = 0x02;
        public const int MaxStrokePoints = 64;

        private const int FrameChunkSize = 180;
        private const int MaxConsecutiveKeyframeFailures = 3;
        private const ushort ErrNotHandshaked = 4010;
        private const ushort ErrInvalidSession = 4011;
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(5);

        private readonly ICommunicationChannel _channel;
        private readonly List<byte> _receiveBuffer = new(512);
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<XpfFrame>> _pendingResponses = new();
        private readonly Channel<Func<CancellationToken, Task>> _operations =
            Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _cts = new();
        private int _pendingOperations;
        private int _pendingKeyframes;
        private int _lastSentSeq;
        private uint _lastFrameId;
        private int _keyframeFailures;
        private int _closed;
        private int _endedRaised;

        private PaintProtocolSession(PaintConnection connection)
        {
            _channel = connection.Channel;
            ChannelKey = connection.ChannelKey;
            SessionId = connection.SessionId;
            DeviceName = connection.DeviceName;
        }

        public string ChannelKey { get; }

        public uint SessionId { get; }

        public string DeviceName { get; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public ushort LastSentSeq => (ushort)Volatile.Read(ref _lastSentSeq);

        public bool HasPendingOperations => Volatile.Read(ref _pendingOperations) > 0;

        public bool KeyframePending => Volatile.Read(ref _pendingKeyframes) > 0;

        // 在接收线程上触发，订阅方需自行切换到 UI 线程。
        public event EventHandler<PaintSyncInfo>? SyncReceived;

        public event EventHandler<string>? Ended;

        public static async Task<PaintProtocolSession> StartAsync(PaintConnection connection, CancellationToken cancellationToken)
        {
            var session = new PaintProtocolSession(connection);
            try
            {
                await session.BeginAsync(cancellationToken);
                _ = Task.Run(session.ProcessOperationsAsync);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        public void QueueStroke(MediaColor color, byte shape, byte size, byte flags, IReadOnlyList<(byte X, byte Y)> points)
        {
            if (points.Count == 0 || points.Count > MaxStrokePoints)
            {
                throw new ArgumentOutOfRangeException(nameof(points));
            }

            var encodedPoints = new byte[points.Count * 2];
            for (int index = 0; index < points.Count; index++)
            {
                encodedPoints[index * 2] = points[index].X;
                encodedPoints[index * 2 + 1] = points[index].Y;
            }

            byte[] encodedColor = EncodeColor(color);
            Enqueue(async cancellationToken =>
            {
                var frame = CreateFrame(XpfProtocolConstants.OpPaintStroke, needAck: false);
                frame.Tlvs[XpfProtocolConstants.TlvPaintSeq] = XpfCodec.EncodeUInt16(NextSeq());
                frame.Tlvs[XpfProtocolConstants.TlvPaintColor] = encodedColor;
                frame.Tlvs[XpfProtocolConstants.TlvPaintBrushShape] = new[] { shape };
                frame.Tlvs[XpfProtocolConstants.TlvPaintBrushSize] = new[] { size };
                frame.Tlvs[XpfProtocolConstants.TlvPaintStrokeFlags] = new[] { flags };
                frame.Tlvs[XpfProtocolConstants.TlvPaintPoints] = encodedPoints;
                await SendAsync(frame, cancellationToken);
            });
        }

        // rect 为空时填充整个画布（清屏即黑色全幅填充）。
        public void QueueFill(MediaColor color, (byte X, byte Y, byte W, byte H)? rect = null)
        {
            byte[] encodedColor = EncodeColor(color);
            Enqueue(async cancellationToken =>
            {
                var frame = CreateFrame(XpfProtocolConstants.OpPaintFill, needAck: false);
                frame.Tlvs[XpfProtocolConstants.TlvPaintSeq] = XpfCodec.EncodeUInt16(NextSeq());
                frame.Tlvs[XpfProtocolConstants.TlvPaintColor] = encodedColor;
                if (rect is { } r)
                {
                    frame.Tlvs[XpfProtocolConstants.TlvPaintRect] = new[] { r.X, r.Y, r.W, r.H };
                }

                await SendAsync(frame, cancellationToken);
            });
        }

        public void QueueKeyframe(byte[] canvasRgb)
        {
            QueueKeyframe(canvasRgb, (0, 0, Width, Height));
        }

        // 只发送 rect 区域的像素；canvasRgb 始终为整幅画布。
        public void QueueKeyframe(byte[] canvasRgb, (int X, int Y, int W, int H) rect)
        {
            if (canvasRgb.Length != Width * Height * 3)
            {
                throw new ArgumentException("Canvas size does not match the device canvas.", nameof(canvasRgb));
            }

            if (rect.W < 1 || rect.H < 1 || rect.X < 0 || rect.Y < 0 || rect.X + rect.W > Width || rect.Y + rect.H > Height)
            {
                throw new ArgumentOutOfRangeException(nameof(rect));
            }

            var region = new byte[rect.W * rect.H * 3];
            for (int row = 0; row < rect.H; row++)
            {
                Buffer.BlockCopy(canvasRgb, ((rect.Y + row) * Width + rect.X) * 3, region, row * rect.W * 3, rect.W * 3);
            }

            bool full = rect.W == Width && rect.H == Height;
            if (full)
            {
                Interlocked.Increment(ref _pendingKeyframes);
            }

            if (!Enqueue(cancellationToken => SendKeyframeAsync(region, rect, full, cancellationToken)) && full)
            {
                Interlocked.Decrement(ref _pendingKeyframes);
            }
        }

        public void QuerySync()
        {
            Enqueue(async cancellationToken =>
            {
                XpfFrame? response = await SendAwaitResponseAsync(CreateFrame(XpfProtocolConstants.OpPaintSync, needAck: true), cancellationToken);
                if (response?.MessageType == XpfMessageType.Resp && TryParseSync(response, out var info))
                {
                    SyncReceived?.Invoke(this, info);
                }
            });
        }

        public async Task EndAsync()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool queued = Enqueue(async cancellationToken =>
            {
                try
                {
                    var frame = CreateFrame(XpfProtocolConstants.OpPaintEnd, needAck: true);
                    Log($"paint.end sending: msgId={frame.MsgId}");
                    XpfFrame? response = await SendAwaitResponseAsync(frame, cancellationToken);
                    Log(response == null
                        ? $"paint.end timeout: msgId={frame.MsgId}"
                        : $"paint.end {response.MessageType} received: msgId={frame.MsgId}, err={ReadErrorCode(response)}");
                }
                finally
                {
                    done.TrySetResult();
                }
            });

            Volatile.Write(ref _closed, 1);
            if (queued)
            {
                await Task.WhenAny(done.Task, Task.Delay(EndTimeout));
            }
            else
            {
                Log("paint.end not sent: session already closed");
            }

            Dispose();
        }

        public void Dispose()
        {
            Volatile.Write(ref _closed, 1);
            _channel.DataReceived -= OnDataReceived;
            _operations.Writer.TryComplete();
            _cts.Cancel();
            foreach (var pending in _pendingResponses.Values)
            {
                pending.TrySetCanceled();
            }
        }

        private async Task BeginAsync(CancellationToken cancellationToken)
        {
            _channel.DataReceived += OnDataReceived;
            await _channel.StartReceivingAsync(cancellationToken);

            XpfFrame response = await SendAwaitResponseAsync(CreateFrame(XpfProtocolConstants.OpPaintBegin, needAck: true), cancellationToken)
                ?? throw new PaintProtocolException("paint.begin timed out.");
            if (response.MessageType == XpfMessageType.Error)
            {
                throw new PaintProtocolException($"paint.begin rejected (err={ReadErrorCode(response)}).");
            }

            // paint_rect 的宽高为 uint8，画布边长不能超过 255。
            if (!XpfCodec.TryReadUInt16(response.Tlvs, XpfProtocolConstants.TlvPaintWidth, out ushort width) ||
                !XpfCodec.TryReadUInt16(response.Tlvs, XpfProtocolConstants.TlvPaintHeight, out ushort height) ||
                width is 0 or > 255 || height is 0 or > 255)
            {
                throw new PaintProtocolException("paint.begin response has an invalid canvas size.");
            }

            Width = width;
            Height = height;
            Volatile.Write(ref _lastSentSeq, 0);
            Log($"paint.begin ok: {width}x{height}, device={DeviceName}");
        }

        private async Task ProcessOperationsAsync()
        {
            try
            {
                await foreach (var operation in _operations.Reader.ReadAllAsync(_cts.Token))
                {
                    try
                    {
                        await operation(_cts.Token);
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log($"operation failed: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pendingOperations);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private bool Enqueue(Func<CancellationToken, Task> operation)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return false;
            }

            Interlocked.Increment(ref _pendingOperations);
            if (_operations.Writer.TryWrite(operation))
            {
                return true;
            }

            Interlocked.Decrement(ref _pendingOperations);
            return false;
        }

        private async Task SendKeyframeAsync(byte[] canvasRgb, (int X, int Y, int W, int H) rect, bool full, CancellationToken cancellationToken)
        {
            try
            {
                ushort seq = NextSeq();
                uint frameId = ++_lastFrameId;
                uint crc32 = MainWindow.ComputeCrc32(canvasRgb);
                int chunkTotal = (canvasRgb.Length + FrameChunkSize - 1) / FrameChunkSize;

                var begin = CreateFrame(XpfProtocolConstants.OpPaintFrameBegin, needAck: true);
                begin.Tlvs[XpfProtocolConstants.TlvPaintSeq] = XpfCodec.EncodeUInt16(seq);
                begin.Tlvs[XpfProtocolConstants.TlvPaintFrameId] = XpfCodec.EncodeUInt32(frameId);
                begin.Tlvs[XpfProtocolConstants.TlvPaintRect] = new[] { (byte)rect.X, (byte)rect.Y, (byte)rect.W, (byte)rect.H };
                begin.Tlvs[XpfProtocolConstants.TlvPaintTotalSize] = XpfCodec.EncodeUInt32((uint)canvasRgb.Length);
                begin.Tlvs[XpfProtocolConstants.TlvChunkTotal] = XpfCodec.EncodeUInt16((ushort)chunkTotal);
                begin.Tlvs[XpfProtocolConstants.TlvChunkCrc32] = XpfCodec.EncodeUInt32(crc32);

                XpfFrame? beginResponse = await SendAwaitResponseAsync(begin, cancellationToken);
                if (beginResponse?.MessageType != XpfMessageType.Resp)
                {
                    if (beginResponse != null)
                    {
                        // frame_begin 被拒绝时序号未被消耗。
                        Volatile.Write(ref _lastSentSeq, (ushort)(seq - 1));
                    }

                    OnKeyframeFailed($"frame_begin {(beginResponse == null ? "timeout" : $"err={ReadErrorCode(beginResponse)}")}");
                    return;
                }

                for (int index = 0; index < chunkTotal; index++)
                {
                    int offset = index * FrameChunkSize;
                    int length = Math.Min(FrameChunkSize, canvasRgb.Length - offset);
                    var pixels = new byte[length];
                    Buffer.BlockCopy(canvasRgb, offset, pixels, 0, length);

                    var chunk = CreateFrame(XpfProtocolConstants.OpPaintFrameChunk, needAck: false);
                    chunk.Tlvs[XpfProtocolConstants.TlvPaintFrameId] = XpfCodec.EncodeUInt32(frameId);
                    chunk.Tlvs[XpfProtocolConstants.TlvChunkIndex] = XpfCodec.EncodeUInt16((ushort)index);
                    chunk.Tlvs[XpfProtocolConstants.TlvPaintPixels] = pixels;
                    if (!await SendAsync(chunk, cancellationToken))
                    {
                        OnKeyframeFailed($"frame_chunk {index} send failed");
                        return;
                    }
                }

                var end = CreateFrame(XpfProtocolConstants.OpPaintFrameEnd, needAck: true);
                end.Tlvs[XpfProtocolConstants.TlvPaintFrameId] = XpfCodec.EncodeUInt32(frameId);
                end.Tlvs[XpfProtocolConstants.TlvChunkTotal] = XpfCodec.EncodeUInt16((ushort)chunkTotal);
                end.Tlvs[XpfProtocolConstants.TlvChunkCrc32] = XpfCodec.EncodeUInt32(crc32);

                XpfFrame? endResponse = await SendAwaitResponseAsync(end, cancellationToken);
                if (endResponse?.MessageType != XpfMessageType.Resp)
                {
                    OnKeyframeFailed($"frame_end {(endResponse == null ? "timeout" : $"err={ReadErrorCode(endResponse)}")}");
                    return;
                }

                Interlocked.Exchange(ref _keyframeFailures, 0);
                Log($"keyframe {frameId} applied: seq={seq}, chunks={chunkTotal}");
            }
            finally
            {
                if (full)
                {
                    Interlocked.Decrement(ref _pendingKeyframes);
                }
            }
        }

        // 失败后主动查询一次状态，由同步规则决定是否重发；连续失败时停止，避免无限重试。
        private void OnKeyframeFailed(string reason)
        {
            int failures = Interlocked.Increment(ref _keyframeFailures);
            Log($"keyframe failed ({failures}): {reason}");
            if (failures < MaxConsecutiveKeyframeFailures)
            {
                QuerySync();
            }
        }

        private ushort NextSeq()
        {
            ushort seq = (ushort)(Volatile.Read(ref _lastSentSeq) + 1);
            Volatile.Write(ref _lastSentSeq, seq);
            return seq;
        }

        private XpfFrame CreateFrame(ushort opCode, bool needAck)
        {
            var frame = new XpfFrame
            {
                MessageType = XpfMessageType.Cmd,
                Flags = needAck ? (byte)0x01 : (byte)0x00,
                QosLevel = needAck ? (byte)1 : (byte)0,
                AppId = XpfProtocolConstants.AppIdPaint,
                OpCode = opCode,
                MsgId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue),
                TimestampSec = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            frame.Tlvs[XpfProtocolConstants.TlvSessionId] = XpfCodec.EncodeUInt32(SessionId);
            return frame;
        }

        private Task<bool> SendAsync(XpfFrame frame, CancellationToken cancellationToken)
        {
            return _channel.SendAsync(XpfCodec.Serialize(frame), cancellationToken);
        }

        // 返回 RESP/ERROR；超时返回 null。
        private async Task<XpfFrame?> SendAwaitResponseAsync(XpfFrame frame, CancellationToken cancellationToken)
        {
            var responseTcs = new TaskCompletionSource<XpfFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingResponses[frame.MsgId] = responseTcs;
            try
            {
                if (!await SendAsync(frame, cancellationToken))
                {
                    return null;
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(ResponseTimeout);
                using var registration = timeoutCts.Token.Register(() => responseTcs.TrySetCanceled());
                try
                {
                    return await responseTcs.Task;
                }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return null;
                }
            }
            finally
            {
                _pendingResponses.TryRemove(frame.MsgId, out _);
            }
        }

        private void OnDataReceived(object? sender, DataReceivedEventArgs args)
        {
            if (args.Data == null || args.Data.Length == 0)
            {
                return;
            }

            var frames = new List<XpfFrame>();
            lock (_receiveBuffer)
            {
                _receiveBuffer.AddRange(args.Data);
                while (MainWindow.TryExtractFirstXpfFrame(_receiveBuffer, out var frameBytes))
                {
                    try
                    {
                        frames.Add(XpfCodec.Deserialize(frameBytes));
                    }
                    catch
                    {
                        // 忽略损坏或非 XPF 数据。
                    }
                }
            }

            foreach (var frame in frames)
            {
                HandleFrame(frame);
            }
        }

        private void HandleFrame(XpfFrame frame)
        {
            if (XpfCodec.TryReadUInt32(frame.Tlvs, XpfProtocolConstants.TlvAckForMsgId, out uint ackForMsgId) &&
                _pendingResponses.TryGetValue(ackForMsgId, out var pending))
            {
                if (frame.MessageType is XpfMessageType.Resp or XpfMessageType.Error)
                {
                    pending.TrySetResult(frame);
                }

                return;
            }

            if (frame.AppId != XpfProtocolConstants.AppIdPaint)
            {
                return;
            }

            if (XpfCodec.TryReadUInt32(frame.Tlvs, XpfProtocolConstants.TlvSessionId, out uint sessionId) && sessionId != SessionId)
            {
                return;
            }

            if (frame.MessageType == XpfMessageType.Event && frame.OpCode == XpfProtocolConstants.OpPaintSync)
            {
                if (TryParseSync(frame, out var info))
                {
                    SyncReceived?.Invoke(this, info);
                }
            }
            else if (frame.MessageType == XpfMessageType.Event && frame.OpCode == XpfProtocolConstants.OpPaintEnd)
            {
                byte reason = frame.Tlvs.TryGetValue(XpfProtocolConstants.TlvPaintEndReason, out var value) && value.Length == 1 ? value[0] : (byte)0;
                RaiseEnded(reason switch
                {
                    1 => "Paint was closed on the device.",
                    2 => "Paint was stopped by the device system.",
                    _ => "Paint was ended by the device.",
                });
            }
            else if (frame.MessageType == XpfMessageType.Error &&
                     ReadErrorCode(frame) is ErrNotHandshaked or ErrInvalidSession)
            {
                RaiseEnded("Device session is no longer valid.");
            }
        }

        private void RaiseEnded(string reason)
        {
            if (Interlocked.Exchange(ref _endedRaised, 1) == 0)
            {
                Log(reason);
                Ended?.Invoke(this, reason);
            }
        }

        private static bool TryParseSync(XpfFrame frame, out PaintSyncInfo info)
        {
            info = new PaintSyncInfo(0, 0, false, false);
            if (!XpfCodec.TryReadUInt16(frame.Tlvs, XpfProtocolConstants.TlvPaintSeq, out ushort seq) ||
                !XpfCodec.TryReadUInt32(frame.Tlvs, XpfProtocolConstants.TlvPaintCanvasCrc32, out uint crc32) ||
                !frame.Tlvs.TryGetValue(XpfProtocolConstants.TlvPaintSyncFlags, out var flags) ||
                flags.Length != 1)
            {
                return false;
            }

            info = new PaintSyncInfo(seq, crc32, (flags[0] & 0x01) != 0, (flags[0] & 0x02) != 0);
            return true;
        }

        private static ushort ReadErrorCode(XpfFrame frame)
        {
            return XpfCodec.TryReadUInt16(frame.Tlvs, XpfProtocolConstants.TlvErrCode, out ushort code) ? code : (ushort)0;
        }

        private static byte[] EncodeColor(MediaColor color)
        {
            return new[] { color.R, color.G, color.B };
        }

        private static void Log(string message)
        {
            MainWindow.WriteAppLog(message, "Paint");
        }
    }
}
