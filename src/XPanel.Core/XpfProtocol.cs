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
        public const ushort AppIdClock = 2;
        public const ushort AppIdPaint = 10;
        public const ushort AppIdProtocolMgr = 106;
        public const ushort AppIdRtcMgr = 107;
        public const ushort AppIdNotificationMgr = 101;
        public const ushort AppIdNvmMgr = 102;
        public const ushort AppIdNetworkMgr = 100;
        public const ushort AppIdDisplayMgr = 108;
        public const ushort OpSessionHello = 0x0003;
        public const ushort OpSessionBye = 0x0004;
        public const ushort OpSessionKeepalive = 0x0005;
        public const ushort OpTimeSync = 0x0051;
        public const ushort OpNotifyPush = 0x0020;
        public const ushort OpNotifyAssetBegin = 0x0021;
        public const ushort OpNotifyAssetChunk = 0x0022;
        public const ushort OpNotifyAssetEnd = 0x0023;
        public const ushort OpNotifyCancel = 0x0024;
        public const ushort OpNvmWrite = 0x0040;
        public const ushort OpNvmRead = 0x0041;
        public const ushort OpWeatherUpdate = 0x0030;
        public const ushort OpDisplayScreenshot = 0x0070;
        public const ushort OpDisplayShotChunk = 0x0071;
        public const ushort OpDisplayShotEnd = 0x0072;
        public const ushort OpClockConfigSet = 0x0080;
        public const ushort OpClockBgChunk = 0x0081;
        public const ushort OpClockConfigCommit = 0x0082;
        public const ushort OpPaintBegin = 0x0090;
        public const ushort OpPaintStroke = 0x0091;
        public const ushort OpPaintFill = 0x0092;
        public const ushort OpPaintFrameBegin = 0x0093;
        public const ushort OpPaintFrameChunk = 0x0094;
        public const ushort OpPaintFrameEnd = 0x0095;
        public const ushort OpPaintSync = 0x0096;
        public const ushort OpPaintEnd = 0x0097;

        // 天气同步模式（wx_mode，见协议第 12 章）
        public const byte WxModeDataSync = 0x01;

        // 统一配置作用域与配置项 ID（见协议第 13 章）
        public const byte CfgScopeDeviceNvm = 0x01;

        // cfg_value_type 枚举（见协议 §13 TLV 0x26）
        public const byte CfgValueTypeBool = 0x01;
        public const byte CfgValueTypeInt32 = 0x02;
        public const byte CfgValueTypeFloat32 = 0x03;
        public const byte CfgValueTypeUtf8 = 0x04;
        public const byte CfgValueTypeBytes = 0x05;

        public const ushort CfgIdWifiSsid = 0x0002;
        public const ushort CfgIdWifiPassword = 0x0003;
        public const ushort CfgIdWeatherProvince = 0x0004;
        public const ushort CfgIdWeatherCity = 0x0005;
        public const ushort CfgIdClockFont = 0x0201;
        public const ushort CfgIdClockBackgroundMode = 0x0202;
        public const ushort CfgIdClockColorRgb = 0x0203;
        public const ushort CfgIdClockPositionX = 0x0204;
        public const ushort CfgIdClockPositionY = 0x0205;

        public const byte TlvAckForMsgId = 0x01;
        public const byte TlvErrCode = 0x07;
        public const byte TlvReqId = 0x0A;
        public const byte TlvEndpointId = 0x06;
        public const byte TlvClientNonce = 0x0B;
        public const byte TlvServerNonce = 0x0C;
        public const byte TlvKeepaliveMs = 0x0D;
        public const byte TlvSessionId = 0x0E;
        public const byte TlvTimeUnixSec = 0x10;
        public const byte TlvTimeTzOffsetMin = 0x11;
        public const byte TlvTimeSource = 0x12;
        public const byte TlvTimeSetMode = 0x13;
        public const byte TlvNotifyId = 0x14;
        public const byte TlvNotifyTitle = 0x15;
        public const byte TlvNotifyText = 0x16;
        public const byte TlvNotifyPriority = 0x17;
        public const byte TlvNotifyTtlMs = 0x18;
        public const byte TlvNotifyChannel = 0x19;
        public const byte TlvNotifyImageMode = 0x1A;
        public const byte TlvNotifyImageFormat = 0x1B;
        public const byte TlvNotifyImageUri = 0x1C;
        public const byte TlvNotifyImageData = 0x1D;
        public const byte TlvNotifyImageSize = 0x1E;
        public const byte TlvNotifyAssetId = 0x1F;
        public const byte TlvChunkIndex = 0x20;
        public const byte TlvChunkTotal = 0x21;
        public const byte TlvChunkCrc32 = 0x22;
        public const byte TlvCfgScope = 0x23;
        public const byte TlvCfgCount = 0x24;
        public const byte TlvCfgId = 0x25;
        public const byte TlvCfgValueType = 0x26;
        public const byte TlvCfgValue = 0x27;
        public const byte TlvCfgItemStatus = 0x28;
        public const byte TlvCfgFlags = 0x29;

        // Screenshot TLVs (see protocol section 14).
        public const byte TlvShotFormat = 0x50;
        public const byte TlvShotWidth = 0x51;
        public const byte TlvShotHeight = 0x52;
        public const byte TlvShotPixelOrder = 0x53;
        public const byte TlvShotBytesPerPixel = 0x54;
        public const byte TlvShotTotalSize = 0x55;
        public const byte TlvShotFrameId = 0x56;
        public const byte TlvShotData = 0x57;
        public const byte TlvShotChunkSize = 0x58;

        // 时钟显示与背景图片传输 TLV（见协议第 15 章）
        public const byte TlvClockFontIndex = 0x60;
        public const byte TlvClockX = 0x61;
        public const byte TlvClockY = 0x62;
        public const byte TlvClockColorRgb = 0x63;
        public const byte TlvClockBgMode = 0x64;
        public const byte TlvClockImageFormat = 0x65;
        public const byte TlvClockImageSize = 0x66;
        public const byte TlvClockTransferId = 0x67;
        public const byte TlvClockImageData = 0x68;
        public const byte TlvClockImageWidth = 0x69;
        public const byte TlvClockImageHeight = 0x6A;

        // 画板实时同步 TLV（见协议第 16 章）
        public const byte TlvPaintSeq = 0x70;
        public const byte TlvPaintColor = 0x71;
        public const byte TlvPaintBrushShape = 0x72;
        public const byte TlvPaintBrushSize = 0x73;
        public const byte TlvPaintStrokeFlags = 0x74;
        public const byte TlvPaintPoints = 0x75;
        public const byte TlvPaintRect = 0x76;
        public const byte TlvPaintFrameId = 0x77;
        public const byte TlvPaintPixels = 0x78;
        public const byte TlvPaintTotalSize = 0x79;
        public const byte TlvPaintCanvasCrc32 = 0x7A;
        public const byte TlvPaintSyncFlags = 0x7B;
        public const byte TlvPaintWidth = 0x7C;
        public const byte TlvPaintHeight = 0x7D;
        public const byte TlvPaintEndReason = 0x7F;

        // 天气同步 TLV（见协议第 12.2 节）
        public const byte TlvWxMode = 0x30;
        public const byte TlvWxValid = 0x31;
        public const byte TlvWxHasTemp = 0x32;
        public const byte TlvWxHasCode = 0x33;
        public const byte TlvWxCity = 0x34;
        public const byte TlvWxTempCx10 = 0x35;
        public const byte TlvWxCode = 0x36;
        public const byte TlvWxFutureCount = 0x37;
        public const byte TlvWxDay1MinCx10 = 0x38;
        public const byte TlvWxDay1MaxCx10 = 0x39;
        public const byte TlvWxDay1Code = 0x3A;
        public const byte TlvWxDay2MinCx10 = 0x3B;
        public const byte TlvWxDay2MaxCx10 = 0x3C;
        public const byte TlvWxDay2Code = 0x3D;
        public const byte TlvWxUpdateUnixSec = 0x3E;
        public const byte TlvWxErrorCode = 0x3F;
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

        public static byte[] EncodeInt16(short value)
        {
            unchecked
            {
                return EncodeUInt16((ushort)value);
            }
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

        public static bool TryReadInt16(Dictionary<byte, byte[]> tlvs, byte type, out short value)
        {
            value = default;
            if (!TryReadUInt16(tlvs, type, out ushort raw))
            {
                return false;
            }

            unchecked
            {
                value = (short)raw;
            }

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
