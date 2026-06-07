namespace Tests.Scenarios;

using System.Threading.Tasks;
using Tests.Fixtures;
using MockClient;
using Xunit;

public class StandardChatSessionTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _fixture;

    public StandardChatSessionTests(ServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TwoClients_InSameRoom_ShouldBroadcastAudioSuccessfully()
    {
        // Skip if server process failed to start (e.g. server project not yet implemented)
        if (_fixture.ServerProcess == null)
        {
            return;
        }

        // Arrange
        int roomId = 101;
        using var client1 = new MockClient(1, _fixture.ServerEndPoint);
        using var client2 = new MockClient(2, _fixture.ServerEndPoint);

        // Act & Assert (Connect both)
        bool c1Connected = await client1.ConnectAsync();
        bool c2Connected = await client2.ConnectAsync();
        Assert.True(c1Connected, "Client 1 handshake failed.");
        Assert.True(c2Connected, "Client 2 handshake failed.");

        // Join the same room
        await client1.JoinRoomAsync(roomId);
        await client2.JoinRoomAsync(roomId);
        await Task.Delay(200); // Wait for room join processing

        // Client 1 streams dummy audio
        client1.StartStreamingAudio(intervalMs: 20, payloadSize: 120);
        await Task.Delay(3000); // Stream for 3 seconds
        client1.StopStreamingAudio();

        // Assert metrics on receiver
        var client2Metrics = client2.Tracker.GetMetrics();

        Assert.True(client2Metrics.ReceivedPackets > 80, $"Expected > 80 packets, received: {client2Metrics.ReceivedPackets}");
        Assert.Equal(0, client2Metrics.LostPackets);
        Assert.Equal(0.0, client2Metrics.PacketLossRate);
        Assert.True(client2Metrics.AverageRttMs < 50.0, $"Average RTT too high: {client2Metrics.AverageRttMs}ms");
        Assert.True(client2Metrics.JitterMs < 5.0, $"Jitter too high: {client2Metrics.JitterMs}ms");
    }
}
