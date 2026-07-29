using System;
using System.Threading.Tasks;
using Xunit;
using XPanel.Core.Protocol;

namespace XPanel.Tests
{
    /// <summary>
    /// 消息协议测试
    /// </summary>
    public class MessageProtocolTests
    {
        [Fact]
        public void Serialize_WithValidMessage_ReturnsCorrectBytes()
        {
            // Arrange
            var protocol = new DefaultMessageProtocol();
            var message = new Message
            {
                MessageType = 0x01,
                Payload = new byte[] { 0x12, 0x34, 0x56, 0x78 }
            };

            // Act
            var result = protocol.Serialize(message);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.Length > message.Payload.Length);
            // 验证消息头
            Assert.Equal(0xAA, result[0]);
            Assert.Equal(0x55, result[1]);
        }

        [Fact]
        public void Deserialize_WithValidData_ReturnsCorrectMessage()
        {
            // Arrange
            var protocol = new DefaultMessageProtocol();
            var originalMessage = new Message
            {
                MessageType = 0x02,
                Payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }
            };

            // Act
            var serialized = protocol.Serialize(originalMessage);
            var deserialized = protocol.Deserialize(serialized);

            // Assert
            Assert.NotNull(deserialized);
            Assert.Equal(originalMessage.MessageType, deserialized.MessageType);
            Assert.Equal(originalMessage.Payload, deserialized.Payload);
        }

        [Fact]
        public void Deserialize_WithInvalidHeader_ThrowsException()
        {
            // Arrange
            var protocol = new DefaultMessageProtocol();
            var invalidData = new byte[] { 0xFF, 0xFF, 0x01, 0x00, 0x04, 0x12, 0x34, 0x55, 0xAA };

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => protocol.Deserialize(invalidData));
        }
    }
}
