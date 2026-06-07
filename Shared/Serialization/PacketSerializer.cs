using System;
using System.Buffers.Binary;
using Shared.Models;

namespace Shared.Serialization
{
    public static class PacketSerializer
    {
        /// <summary>
        /// Attempts to deserialize a header from a raw byte buffer.
        /// </summary>
        public static bool TryDeserializeHeader(ReadOnlySpan<byte> buffer, out VoicePacketHeader header)
        {
            header = default;
            if (buffer.Length < VoicePacketHeader.HeaderSize)
            {
                return false;
            }

            int packetType = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(0, 4));
            int roomId = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4));
            int clientId = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(8, 4));
            uint seqNumber = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(12, 4));
            long timestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(16, 8));
            int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(24, 4));

            header = new VoicePacketHeader(packetType, roomId, clientId, seqNumber, timestamp, payloadLen);
            return true;
        }

        /// <summary>
        /// Attempts to serialize a packet header into the provided target buffer.
        /// </summary>
        public static bool TrySerializeHeader(in VoicePacketHeader header, Span<byte> target)
        {
            if (target.Length < VoicePacketHeader.HeaderSize)
            {
                return false;
            }

            BinaryPrimitives.WriteInt32LittleEndian(target.Slice(0, 4), header.PacketType);
            BinaryPrimitives.WriteInt32LittleEndian(target.Slice(4, 4), header.RoomId);
            BinaryPrimitives.WriteInt32LittleEndian(target.Slice(8, 4), header.ClientId);
            BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(12, 4), header.SequenceNumber);
            BinaryPrimitives.WriteInt64LittleEndian(target.Slice(16, 8), header.SendTimestamp);
            BinaryPrimitives.WriteInt32LittleEndian(target.Slice(24, 4), header.PayloadLength);

            return true;
        }
    }
}
