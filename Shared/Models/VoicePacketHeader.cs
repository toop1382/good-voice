using System;

namespace Shared.Models
{
    public readonly struct VoicePacketHeader
    {
        public const int HeaderSize = 28;

        public int PacketType { get; }
        public int RoomId { get; }
        public int ClientId { get; }
        public uint SequenceNumber { get; }
        public long SendTimestamp { get; }
        public int PayloadLength { get; }

        public VoicePacketHeader(int packetType, int roomId, int clientId, uint sequenceNumber, long sendTimestamp, int payloadLength)
        {
            PacketType = packetType;
            RoomId = roomId;
            ClientId = clientId;
            SequenceNumber = sequenceNumber;
            SendTimestamp = sendTimestamp;
            PayloadLength = payloadLength;
        }
    }
}
