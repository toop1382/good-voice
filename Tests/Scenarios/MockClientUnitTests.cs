namespace Tests.Scenarios;

using System;
using MockClient.Models;
using MockClient.Diagnostics;
using Xunit;

public class MockClientUnitTests
{
    [Fact]
    public void VoicePacket_SerializationDeserialization_ShouldMatch()
    {
        // Arrange
        var original = new VoicePacket
        {
            Type = PacketType.Audio,
            RoomId = 42,
            ClientId = 1001,
            SequenceNumber = 999,
            SendTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PayloadLength = 5,
            Payload = new byte[] { 1, 2, 3, 4, 5 }
        };

        var buffer = new byte[VoicePacket.HeaderSize + original.PayloadLength];

        // Act
        original.Serialize(buffer);
        var deserialized = VoicePacket.Deserialize(buffer);

        // Assert
        Assert.Equal(original.Type, deserialized.Type);
        Assert.Equal(original.RoomId, deserialized.RoomId);
        Assert.Equal(original.ClientId, deserialized.ClientId);
        Assert.Equal(original.SequenceNumber, deserialized.SequenceNumber);
        Assert.Equal(original.SendTimestamp, deserialized.SendTimestamp);
        Assert.Equal(original.PayloadLength, deserialized.PayloadLength);
        Assert.Equal(original.Payload, deserialized.Payload);
    }

    [Fact]
    public void MetricTracker_LossAndJitterCalculations_ShouldBeCorrect()
    {
        // Arrange
        var tracker = new MetricTracker();

        // Act & Assert RTT
        tracker.RecordRttSample(10);
        tracker.RecordRttSample(20);
        var metrics = tracker.GetMetrics();
        Assert.Equal(15.0, metrics.AverageRttMs);

        // Act & Assert Loss/Jitter/Out-of-order
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        tracker.RecordPacketReceived(1, now - 10, now);
        tracker.RecordPacketReceived(2, now - 20, now);
        tracker.RecordPacketReceived(3, now - 30, now);
        tracker.RecordPacketReceived(5, now - 50, now);

        metrics = tracker.GetMetrics();
        Assert.Equal(4, metrics.ReceivedPackets);
        Assert.Equal(1, metrics.LostPackets);
        Assert.Equal(20.0, metrics.PacketLossRate);

        // Record duplicate
        tracker.RecordPacketReceived(2, now - 20, now);
        metrics = tracker.GetMetrics();
        Assert.Equal(1, metrics.DuplicatePackets);

        // Record out of order
        tracker.RecordPacketReceived(4, now - 40, now);
        metrics = tracker.GetMetrics();
        Assert.Equal(1, metrics.OutOfOrderPackets);
    }
}
