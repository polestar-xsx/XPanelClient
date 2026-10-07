using Xunit;
using XPanel.Core.Protocol;

namespace XPanel.Tests
{
    public class XpfProtocolTests
    {
        [Fact]
        public void SerializeAndDeserialize_HelloFrame_RoundTrips()
        {
            var frame = new XpfFrame
            {
                MessageType = XpfMessageType.Cmd,
                Flags = 0x01,
                QosLevel = 1,
                Hop = 0,
                AppId = XpfProtocolConstants.AppIdProtocolMgr,
                OpCode = XpfProtocolConstants.OpSessionHello,
                MsgId = 123456,
                TimestampSec = 1722153600,
            };

            frame.Tlvs[XpfProtocolConstants.TlvEndpointId] = XpfCodec.EncodeUtf8("PC-TEST");
            frame.Tlvs[XpfProtocolConstants.TlvClientNonce] = XpfCodec.EncodeUInt32(99887766);
            frame.Tlvs[XpfProtocolConstants.TlvKeepaliveMs] = XpfCodec.EncodeUInt16(25000);

            byte[] serialized = XpfCodec.Serialize(frame);
            XpfFrame parsed = XpfCodec.Deserialize(serialized);

            Assert.Equal(frame.MessageType, parsed.MessageType);
            Assert.Equal(frame.Flags, parsed.Flags);
            Assert.Equal(frame.AppId, parsed.AppId);
            Assert.Equal(frame.OpCode, parsed.OpCode);
            Assert.Equal(frame.MsgId, parsed.MsgId);
            Assert.Equal(frame.TimestampSec, parsed.TimestampSec);

            Assert.True(XpfCodec.TryReadUtf8(parsed.Tlvs, XpfProtocolConstants.TlvEndpointId, out var endpointId));
            Assert.Equal("PC-TEST", endpointId);

            Assert.True(XpfCodec.TryReadUInt32(parsed.Tlvs, XpfProtocolConstants.TlvClientNonce, out var nonce));
            Assert.Equal((uint)99887766, nonce);

            Assert.True(XpfCodec.TryReadUInt16(parsed.Tlvs, XpfProtocolConstants.TlvKeepaliveMs, out var keepalive));
            Assert.Equal((ushort)25000, keepalive);
        }

        [Fact]
        public void SerializeAndDeserialize_TimeSyncFrame_RoundTrips()
        {
            const uint unixSec = 1764500000;
            const short timezoneOffsetMin = 480;

            var frame = new XpfFrame
            {
                MessageType = XpfMessageType.Cmd,
                Flags = 0x01,
                QosLevel = 1,
                Hop = 0,
                AppId = XpfProtocolConstants.AppIdRtcMgr,
                OpCode = XpfProtocolConstants.OpTimeSync,
                MsgId = 654321,
                TimestampSec = unixSec,
            };

            frame.Tlvs[XpfProtocolConstants.TlvSessionId] = XpfCodec.EncodeUInt32(1234);
            frame.Tlvs[XpfProtocolConstants.TlvTimeUnixSec] = XpfCodec.EncodeUInt32(unixSec);
            frame.Tlvs[XpfProtocolConstants.TlvTimeTzOffsetMin] = XpfCodec.EncodeInt16(timezoneOffsetMin);
            frame.Tlvs[XpfProtocolConstants.TlvTimeSource] = new byte[] { 1 };
            frame.Tlvs[XpfProtocolConstants.TlvTimeSetMode] = new byte[] { 2 };

            byte[] serialized = XpfCodec.Serialize(frame);
            XpfFrame parsed = XpfCodec.Deserialize(serialized);

            Assert.Equal(XpfProtocolConstants.AppIdRtcMgr, parsed.AppId);
            Assert.Equal(XpfProtocolConstants.OpTimeSync, parsed.OpCode);

            Assert.True(XpfCodec.TryReadUInt32(parsed.Tlvs, XpfProtocolConstants.TlvTimeUnixSec, out var parsedUnixSec));
            Assert.Equal(unixSec, parsedUnixSec);

            Assert.True(XpfCodec.TryReadInt16(parsed.Tlvs, XpfProtocolConstants.TlvTimeTzOffsetMin, out var parsedOffset));
            Assert.Equal(timezoneOffsetMin, parsedOffset);

            Assert.Equal((byte)1, parsed.Tlvs[XpfProtocolConstants.TlvTimeSource][0]);
            Assert.Equal((byte)2, parsed.Tlvs[XpfProtocolConstants.TlvTimeSetMode][0]);
        }

        [Fact]
        public void SerializeAndDeserialize_CopilotUsageUpdate_RoundTrips()
        {
            var frame = new XpfFrame
            {
                MessageType = XpfMessageType.Cmd,
                Flags = 0x01,
                QosLevel = 1,
                AppId = XpfProtocolConstants.AppIdCopilot,
                OpCode = XpfProtocolConstants.OpCopilotUsageUpdate,
                MsgId = 1234,
                TimestampSec = 1760000000,
            };
            frame.Tlvs[XpfProtocolConstants.TlvSessionId] = XpfCodec.EncodeUInt32(0x12345678);
            frame.Tlvs[XpfProtocolConstants.TlvCopilotUsagePercent] = new byte[] { 75 };

            XpfFrame parsed = XpfCodec.Deserialize(XpfCodec.Serialize(frame));

            Assert.Equal((ushort)11, parsed.AppId);
            Assert.Equal((ushort)0x00A0, parsed.OpCode);
            Assert.Equal(XpfMessageType.Cmd, parsed.MessageType);
            Assert.True(XpfCodec.TryReadUInt32(parsed.Tlvs, XpfProtocolConstants.TlvSessionId, out uint sessionId));
            Assert.Equal(0x12345678u, sessionId);
            Assert.Equal(new byte[] { 75 }, parsed.Tlvs[XpfProtocolConstants.TlvCopilotUsagePercent]);
        }
    }
}
