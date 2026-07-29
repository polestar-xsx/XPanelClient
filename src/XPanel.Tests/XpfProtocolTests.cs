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
    }
}
