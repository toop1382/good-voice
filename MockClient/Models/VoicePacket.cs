namespace MockClient.Models;

using System;
using System.Buffers.Binary;

public enum PacketType : int
{
    Handshake = 1,
    RoomJoin = 2,
    Audio = 3
}

public struct VoicePacket
{
    public PacketType Type { get; set; }
    public int RoomId { get; set; }
    public int ClientId { get; set; }
    public uint SequenceNumber { get; set; }
    public long SendTimestamp { get; set; }
    public int PayloadLength { get; set; }
    public byte[] Payload { get; set; }

    public const int HeaderSize = 28;

    public void Serialize(Span<byte> destination)
    {
        if (destination.Length < HeaderSize + PayloadLength)
            throw new ArgumentException("Destination buffer too small.");

        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(0, 4), (int)Type);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(4, 4), RoomId);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(8, 4), ClientId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12, 4), SequenceNumber);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(16, 8), SendTimestamp);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(24, 4), PayloadLength);

        if (PayloadLength > 0 && Payload != null)
        {
            Payload.AsSpan(0, PayloadLength).CopyTo(destination.Slice(HeaderSize, PayloadLength));
        }
    }

    public static VoicePacket Deserialize(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize)
            throw new ArgumentException("Source buffer smaller than header size.");

        var packet = new VoicePacket();
        packet.Type = (PacketType)BinaryPrimitives.ReadInt32LittleEndian(source.Slice(0, 4));
        packet.RoomId = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(4, 4));
        packet.ClientId = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(8, 4));
        packet.SequenceNumber = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(12, 4));
        packet.SendTimestamp = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(16, 8));
        packet.PayloadLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(24, 4));

        if (packet.PayloadLength > 0)
        {
            if (source.Length < HeaderSize + packet.PayloadLength)
                throw new ArgumentException("Source buffer does not contain full payload.");
            packet.Payload = source.Slice(HeaderSize, packet.PayloadLength).ToArray();
        }
        else
        {
            packet.Payload = Array.Empty<byte>();
        }

        return packet;
    }
}
