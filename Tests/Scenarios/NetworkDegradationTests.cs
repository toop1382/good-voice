namespace Tests.Scenarios;

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Tests.Fixtures;
using MockClient;
using MockClient.Network;
using Xunit;

public class NetworkDegradationTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _fixture;

    public NetworkDegradationTests(ServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PacketLossDegradation_ShouldDropPackets()
    {
        if (_fixture.ServerProcess == null) return;

        int roomId = 201;
        using var sender = new MockClient(10, _fixture.ServerEndPoint);
        using var receiver = new MockClient(11, _fixture.ServerEndPoint);

        bool c1Connected = await sender.ConnectAsync();
        bool c2Connected = await receiver.ConnectAsync();
        Assert.True(c1Connected);
        Assert.True(c2Connected);

        await sender.JoinRoomAsync(roomId);
        await receiver.JoinRoomAsync(roomId);
        await Task.Delay(200);

        // Configure sender with 100% loss (drop all outgoing packets)
        sender.Degradation = new DegradationConfig { PacketLossRate = 1.0, ArtificialDelayMs = 0 };

        sender.StartStreamingAudio(intervalMs: 20, payloadSize: 120);
        await Task.Delay(1000); // Attempt to stream for 1 second
        sender.StopStreamingAudio();

        var metrics = receiver.Tracker.GetMetrics();
        // Since all packets were dropped on send, receiver should have received 0 audio packets
        Assert.Equal(0, metrics.ReceivedPackets);
    }

    [Fact]
    public async Task LatencyDegradation_ShouldDelayPackets()
    {
        if (_fixture.ServerProcess == null) return;

        using var sender = new MockClient(20, _fixture.ServerEndPoint);
        
        // Configure sender with 100ms artificial delay
        sender.Degradation = new DegradationConfig { PacketLossRate = 0.0, ArtificialDelayMs = 100 };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        bool connected = await sender.ConnectAsync();
        stopwatch.Stop();

        Assert.True(connected, "Handshake should succeed despite delay.");
        
        var metrics = sender.Tracker.GetMetrics();
        // Handshake round trip time must be at least the 100ms artificial delay
        Assert.True(metrics.AverageRttMs >= 100.0, $"Expected RTT >= 100ms, got: {metrics.AverageRttMs}ms");
        Assert.True(stopwatch.ElapsedMilliseconds >= 100, $"Expected elapsed time >= 100ms, got: {stopwatch.ElapsedMilliseconds}ms");
    }
}
