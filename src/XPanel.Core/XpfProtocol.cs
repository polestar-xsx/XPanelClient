using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XPanel.Core.Protocol
{
    public enum XpfMessageType : byte
    {
        Cmd = 1,
        Resp = 2,
        Event = 3,
        Ack = 4,
        Error = 5,
    }

    public static class XpfProtocolConstants
    {
        public const ushort AppIdProtocolMgr = 106;
        public const ushort OpSessionHello = 0x0003;
        public const ushort OpSessionBye = 0x0004;
        public const ushort OpSessionKeepalive = 0x0005;

        public const byte TlvAckForMsgId = 0x01;
        public const byte TlvEndpointId = 0x06;
        public const byte TlvClientNonce = 0x0B;
        public const byte TlvServerNonce = 0x0C;
        public const byte TlvKeepaliveMs = 0x0D;
        public const byte TlvSessionId = 0x0E;
    }

    public sealed class XpfFrame
    {
        public byte VersionMajor { get; set; } = 0x01;
        public byte VersionMinor { get; set; } = 0x00;
        public XpfMessageType MessageType { get; set; }
        public byte Flags { get; set; }
        public byte QosLevel { get; set; }
        public byte Hop { get; set; }
        public ushort AppId { get; set; }
        public ushort OpCode { get; set; }
        public uint MsgId { get; set; }
        public uint TimestampSec { get; set; }
        public Dictionary<byte, byte[]> Tlvs { get; } = new();
    }

    public static class XpfCodec
    {
        private const byte Magic0 = 0x58; // X
        private const byte Magic1 = 0x50; // P
        private const int HeaderLength = 24;

        public static byte[] Serialize(XpfFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            byte[] body = EncodeTlvs(frame.Tlvs);
            if (body.Length > ushort.MaxValue)
            {
                throw new InvalidOperationException("TLV body 长度超过上限");
            }

            byte[] buffer = new byte[HeaderLength + body.Length];
            buffer[0] = Magic0;
            buffer[1] = Magic1;
            buffer[2] = frame.VersionMajor;
            buffer[3] = frame.VersionMinor;
            buffer[4] = (byte)frame.MessageType;
            buffer[5] = frame.Flags;
            buffer[6] = frame.QosLevel;
            buffer[7] = frame.Hop;

            WriteUInt16(buffer, 8, frame.AppId);
            WriteUInt16(buffer, 10, frame.OpCode);
            WriteUInt32(buffer, 12, frame.MsgId);
            WriteUInt32(buffer, 16, frame.TimestampSec);
            WriteUInt16(buffer, 20, (ushort)body.Length);

            ushort headerCrc = ComputeCrc16Ccitt(buffer, 0, 22);
            WriteUInt16(buffer, 22, headerCrc);

            if (body.Length > 0)
            {
                Buffer.BlockCopy(body, 0, buffer, HeaderLength, body.Length);
            }

            return buffer;
        }

        public static XpfFrame Deserialize(byte[] frameBytes)
        {
            if (frameBytes == null)
            {
                throw new ArgumentNullException(nameof(frameBytes));
            }

            if (frameBytes.Length < HeaderLength)
            {
                throw new InvalidDataException("XPF 帧长度不足");
            }

            if (frameBytes[0] != Magic0 || frameBytes[1] != Magic1)
            {
                throw new InvalidDataException("XPF magic 无效");
            }

            ushort expectedCrc = ReadUInt16(frameBytes, 22);
            ushort actualCrc = ComputeCrc16Ccitt(frameBytes, 0, 22);
            if (expectedCrc != actualCrc)
            {
                throw new InvalidDataException("XPF header CRC 校验失败");
            }

            ushort bodyLen = ReadUInt16(frameBytes, 20);
            if (frameBytes.Length != HeaderLength + bodyLen)
            {
                throw new InvalidDataException("XPF body_len 与实际长度不一致");
            }

            var result = new XpfFrame
            {
                VersionMajor = frameBytes[2],
                VersionMinor = frameBytes[3],
                MessageType = (XpfMessageType)frameBytes[4],
                Flags = frameBytes[5],
                QosLevel = frameBytes[6],
                Hop = frameBytes[7],
                AppId = ReadUInt16(frameBytes, 8),
                OpCode = ReadUInt16(frameBytes, 10),
                MsgId = ReadUInt32(frameBytes, 12),
                TimestampSec = ReadUInt32(frameBytes, 16),
            };

            if (bodyLen > 0)
            {
                var tlvs = DecodeTlvs(frameBytes, HeaderLength, bodyLen);
                foreach (var tlv in tlvs)
                {
                    result.Tlvs[tlv.Key] = tlv.Value;
                }
            }

            return result;
        }

        public static byte[] EncodeUtf8(string value)
        {
            return Encoding.UTF8.GetBytes(value ?? string.Empty);
        }

        public static byte[] EncodeUInt16(ushort value)
        {
            return new[] { (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF) };
        }

        public static byte[] EncodeUInt32(uint value)
        {
            return new[]
            {
                (byte)((value >> 24) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)(value & 0xFF),
            };
        }

        public static bool TryReadUInt16(Dictionary<byte, byte[]> tlvs, byte type, out ushort value)
        {
            value = default;
            if (!tlvs.TryGetValue(type, out var data) || data.Length != 2)
            {
                return false;
            }

            value = (ushort)((data[0] << 8) | data[1]);
            return true;
        }

        public static bool TryReadUInt32(Dictionary<byte, byte[]> tlvs, byte type, out uint value)
        {
            value = default;
            if (!tlvs.TryGetValue(type, out var data) || data.Length != 4)
            {
                return false;
            }

            value = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
            return true;
        }

        public static bool TryReadUtf8(Dictionary<byte, byte[]> tlvs, byte type, out string value)
        {
            value = string.Empty;
            if (!tlvs.TryGetValue(type, out var data))
            {
                return false;
            }

            value = Encoding.UTF8.GetString(data);
            return true;
        }

        private static byte[] EncodeTlvs(Dictionary<byte, byte[]> tlvs)
        {
            if (tlvs == null || tlvs.Count == 0)
            {
                return Array.Empty<byte>();
            }

            using var stream = new MemoryStream();
            foreach (var item in tlvs.OrderBy(x => x.Key))
            {
                byte[] value = item.Value ?? Array.Empty<byte>();
                if (value.Length > ushort.MaxValue)
                {
                    throw new InvalidOperationException($"TLV 0x{item.Key:X2} 长度超过上限");
                }

                stream.WriteByte(item.Key);
                stream.WriteByte((byte)((value.Length >> 8) & 0xFF));
                stream.WriteByte((byte)(value.Length & 0xFF));
                stream.Write(value, 0, value.Length);
            }

            return stream.ToArray();
        }

        private static Dictionary<byte, byte[]> DecodeTlvs(byte[] bytes, int offset, int length)
        {
            var result = new Dictionary<byte, byte[]>();
            int index = offset;
            int end = offset + length;

            while (index < end)
            {
                if (index + 3 > end)
                {
                    throw new InvalidDataException("TLV 头部长度不足");
                }

                byte type = bytes[index++];
                ushort valueLength = (ushort)((bytes[index++] << 8) | bytes[index++]);

                if (index + valueLength > end)
                {
                    throw new InvalidDataException($"TLV 0x{type:X2} 长度越界");
                }

                var value = new byte[valueLength];
                if (valueLength > 0)
                {
                    Buffer.BlockCopy(bytes, index, value, 0, valueLength);
                }

                result[type] = value;
                index += valueLength;
            }

            return result;
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 1] = (byte)(value & 0xFF);
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }

        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);
        }

        private static ushort ComputeCrc16Ccitt(byte[] data, int offset, int count)
        {
            ushort crc = 0xFFFF;
            int end = offset + count;

            for (int i = offset; i < end; i++)
            {
                crc ^= (ushort)(data[i] << 8);
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (ushort)(((crc & 0x8000) != 0) ? ((crc << 1) ^ 0x1021) : (crc << 1));
                }
            }

            return crc;
        }
    }
}
