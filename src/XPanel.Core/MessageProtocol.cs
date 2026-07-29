using System;
using System.Text;

namespace XPanel.Core.Protocol
{
    /// <summary>
    /// 消息协议基类 - 负责消息的序列化和反序列化
    /// </summary>
    public abstract class MessageProtocol
    {
        /// <summary>
        /// 协议版本
        /// </summary>
        public virtual byte ProtocolVersion => 1;

        /// <summary>
        /// 消息头长度（字节）
        /// </summary>
        public virtual int HeaderLength => 8;

        /// <summary>
        /// 消息头标志符（用于识别消息开始）
        /// </summary>
        public virtual byte[] HeaderMarker => new byte[] { 0xAA, 0x55 };

        /// <summary>
        /// 消息结尾标志符（用于识别消息结束）
        /// </summary>
        public virtual byte[] TailMarker => new byte[] { 0x55, 0xAA };

        /// <summary>
        /// 序列化消息
        /// </summary>
        /// <param name="message">待序列化的消息对象</param>
        /// <returns>序列化后的字节数组</returns>
        public virtual byte[] Serialize(Message message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            // 基础实现：消息格式为 [Header(2)] [Type(1)] [Length(2)] [Payload(N)] [CRC(2)] [Tail(2)]
            var payload = message.Payload ?? Array.Empty<byte>();
            var crc = CalculateCrc(payload);

            var buffer = new byte[HeaderMarker.Length + 3 + payload.Length + 2 + TailMarker.Length];
            int index = 0;

            // 写入消息头标志符
            Array.Copy(HeaderMarker, 0, buffer, index, HeaderMarker.Length);
            index += HeaderMarker.Length;

            // 写入消息类型
            buffer[index++] = message.MessageType;

            // 写入负载长度（大端序）
            buffer[index++] = (byte)((payload.Length >> 8) & 0xFF);
            buffer[index++] = (byte)(payload.Length & 0xFF);

            // 写入负载
            if (payload.Length > 0)
            {
                Array.Copy(payload, 0, buffer, index, payload.Length);
                index += payload.Length;
            }

            // 写入CRC校验（大端序）
            buffer[index++] = (byte)((crc >> 8) & 0xFF);
            buffer[index++] = (byte)(crc & 0xFF);

            // 写入消息尾标志符
            Array.Copy(TailMarker, 0, buffer, index, TailMarker.Length);

            return buffer;
        }

        /// <summary>
        /// 反序列化消息
        /// </summary>
        /// <param name="data">接收到的字节数组</param>
        /// <returns>反序列化后的消息对象</returns>
        public virtual Message Deserialize(byte[] data)
        {
            if (data == null || data.Length < HeaderMarker.Length + TailMarker.Length)
                throw new InvalidOperationException("数据长度不足");

            // 验证消息头
            for (int i = 0; i < HeaderMarker.Length; i++)
            {
                if (data[i] != HeaderMarker[i])
                    throw new InvalidOperationException("无效的消息头");
            }

            // 验证消息尾
            int tailStartIndex = data.Length - TailMarker.Length;
            for (int i = 0; i < TailMarker.Length; i++)
            {
                if (data[tailStartIndex + i] != TailMarker[i])
                    throw new InvalidOperationException("无效的消息尾");
            }

            int index = HeaderMarker.Length;

            // 读取消息类型
            byte messageType = data[index++];

            // 读取负载长度
            int payloadLength = ((data[index] << 8) | data[index + 1]);
            index += 2;

            // 读取负载
            byte[] payload = new byte[payloadLength];
            if (payloadLength > 0)
            {
                Array.Copy(data, index, payload, 0, payloadLength);
                index += payloadLength;
            }

            // 读取CRC校验
            ushort crc = (ushort)((data[index] << 8) | data[index + 1]);
            ushort calculatedCrc = CalculateCrc(payload);

            if (crc != calculatedCrc)
                throw new InvalidOperationException("CRC校验失败");

            return new Message
            {
                MessageType = messageType,
                Payload = payload,
                ReceiveTime = DateTime.Now
            };
        }

        /// <summary>
        /// 计算CRC16校验码（CCITT算法）
        /// </summary>
        protected virtual ushort CalculateCrc(byte[] data)
        {
            if (data == null || data.Length == 0)
                return 0;

            ushort crc = 0xFFFF;
            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x8000) != 0)
                        crc = (ushort)((crc << 1) ^ 0x1021);
                    else
                        crc = (ushort)(crc << 1);
                }
            }
            return crc;
        }
    }

    /// <summary>
    /// 消息类
    /// </summary>
    public class Message
    {
        /// <summary>
        /// 消息类型
        /// </summary>
        public byte MessageType { get; set; }

        /// <summary>
        /// 消息负载
        /// </summary>
        public byte[] Payload { get; set; }

        /// <summary>
        /// 接收时间
        /// </summary>
        public DateTime ReceiveTime { get; set; }

        /// <summary>
        /// 额外信息
        /// </summary>
        public string Description { get; set; }
    }

    /// <summary>
    /// 默认消息协议实现
    /// </summary>
    public class DefaultMessageProtocol : MessageProtocol
    {
        // 使用基类的默认实现
    }
}
