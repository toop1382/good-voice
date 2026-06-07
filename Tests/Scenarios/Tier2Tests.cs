namespace Tests.Scenarios;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Tests.Fixtures;
using MockClient;
using MockClient.Network;
using MockClient.Diagnostics;
using Xunit;

public class Tier2Tests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _fixture;

    public Tier2Tests(ServerFixture fixture)
    {
        _fixture = fixture;
    }

    #region Helper Classes & Methods

    private class RawTestClient : IDisposable
    {
        public Socket Socket { get; }
        public IPEndPoint ServerEndPoint { get; }
        public int ClientId { get; }

        public RawTestClient(int clientId, IPEndPoint serverEndPoint)
        {
            ClientId = clientId;
            ServerEndPoint = serverEndPoint;
            Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            Socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        }

        public async Task<byte[]> SendAndReceiveAsync(byte[] sendBuffer, int timeoutMs = 2000)
        {
            await Socket.SendToAsync(sendBuffer, SocketFlags.None, ServerEndPoint);
            var receiveBuffer = new byte[2048];
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                var result = await Socket.ReceiveFromAsync(receiveBuffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
                var response = new byte[result.ReceivedBytes];
                Array.Copy(receiveBuffer, response, result.ReceivedBytes);
                return response;
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Receive timed out.");
            }
        }

        public async Task SendAsync(byte[] sendBuffer)
        {
            await Socket.SendToAsync(sendBuffer, SocketFlags.None, ServerEndPoint);
        }

        public async Task<byte[]> ReceiveAsync(int timeoutMs = 2000)
        {
            var receiveBuffer = new byte[2048];
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                var result = await Socket.ReceiveFromAsync(receiveBuffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
                var response = new byte[result.ReceivedBytes];
                Array.Copy(receiveBuffer, response, result.ReceivedBytes);
                return response;
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Receive timed out.");
            }
        }

        public void Dispose()
        {
            try
            {
                Socket.Close();
            }
            catch { }
            Socket.Dispose();
        }
    }

    private static byte[] CreatePacket(int packetType, int roomId, int clientId, uint sequenceNumber, long timestamp, byte[]? payload = null)
    {
        int payloadLength = payload?.Length ?? 0;
        byte[] buffer = new byte[28 + payloadLength];
        var header = new Shared.Models.VoicePacketHeader(packetType, roomId, clientId, sequenceNumber, timestamp, payloadLength);
        Shared.Serialization.PacketSerializer.TrySerializeHeader(header, buffer.AsSpan());
        if (payloadLength > 0 && payload != null)
        {
            Array.Copy(payload, 0, buffer, 28, payloadLength);
        }
        return buffer;
    }

    private static async Task<(bool handshakeSuccess, bool joinSuccess)> PerformHandshakeAndJoinAsync(RawTestClient client, int roomId)
    {
        try
        {
            // Handshake
            var handshakeBytes = CreatePacket(1, 0, client.ClientId, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var handshakeResponse = await client.SendAndReceiveAsync(handshakeBytes);
            if (handshakeResponse.Length < 28) return (false, false);
            Shared.Serialization.PacketSerializer.TryDeserializeHeader(handshakeResponse, out var hsHeader);
            bool handshakeSuccess = hsHeader.PacketType == 1 && hsHeader.ClientId == client.ClientId;

            // Join Room
            var joinBytes = CreatePacket(2, roomId, client.ClientId, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var joinResponse = await client.SendAndReceiveAsync(joinBytes);
            if (joinResponse.Length < 28 + 4) return (handshakeSuccess, false);
            Shared.Serialization.PacketSerializer.TryDeserializeHeader(joinResponse, out var joinHeader);
            int successVal = BinaryPrimitives.ReadInt32LittleEndian(joinResponse.AsSpan(28, 4));
            bool joinSuccess = joinHeader.PacketType == 2 && joinHeader.RoomId == roomId && joinHeader.ClientId == client.ClientId && successVal == 1;

            return (handshakeSuccess, joinSuccess);
        }
        catch
        {
            return (false, false);
        }
    }

    private class RawTcpTestClient : IDisposable
    {
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public int ClientId { get; }

        public RawTcpTestClient(int clientId, IPEndPoint serverEndPoint)
        {
            ClientId = clientId;
            Client = new TcpClient();
            Client.Connect(serverEndPoint);
            Stream = Client.GetStream();
        }

        public async Task SendFramedAsync(byte[] data, int length, CancellationToken ct = default)
        {
            byte[] lenBuf = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)length);
            await Stream.WriteAsync(lenBuf, 0, 4, ct);
            await Stream.WriteAsync(data, 0, length, ct);
        }

        public async Task<byte[]> ReceiveFramedAsync(int timeoutMs = 2000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            byte[] lenBuf = new byte[4];
            if (!await ReadExactAsync(Stream, lenBuf, 4, cts.Token))
                throw new TimeoutException("TCP read exact length prefix failed.");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
            byte[] body = new byte[length];
            if (!await ReadExactAsync(Stream, body, (int)length, cts.Token))
                throw new TimeoutException("TCP read exact body failed.");
            return body;
        }

        private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
        {
            int read = 0;
            while (read < count)
            {
                int n = await s.ReadAsync(buf, read, count - read, ct);
                if (n == 0) return false;
                read += n;
            }
            return true;
        }

        public void Dispose()
        {
            Stream?.Dispose();
            Client?.Dispose();
        }
    }

    private static async Task<(bool handshakeSuccess, bool joinSuccess)> PerformTcpHandshakeAndJoinAsync(RawTcpTestClient client, int roomId)
    {
        try
        {
            // Handshake
            var handshakeBytes = CreatePacket(1, 0, client.ClientId, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await client.SendFramedAsync(handshakeBytes, handshakeBytes.Length);
            var handshakeResponse = await client.ReceiveFramedAsync();
            if (handshakeResponse.Length < 28) return (false, false);
            Shared.Serialization.PacketSerializer.TryDeserializeHeader(handshakeResponse, out var hsHeader);
            bool handshakeSuccess = hsHeader.PacketType == 1 && hsHeader.ClientId == client.ClientId;

            // Join Room
            var joinBytes = CreatePacket(2, roomId, client.ClientId, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await client.SendFramedAsync(joinBytes, joinBytes.Length);
            var joinResponse = await client.ReceiveFramedAsync();
            if (joinResponse.Length < 28 + 4) return (handshakeSuccess, false);
            Shared.Serialization.PacketSerializer.TryDeserializeHeader(joinResponse, out var joinHeader);
            int successVal = BinaryPrimitives.ReadInt32LittleEndian(joinResponse.AsSpan(28, 4));
            bool joinSuccess = joinHeader.PacketType == 2 && joinHeader.RoomId == roomId && joinHeader.ClientId == client.ClientId && successVal == 1;

            return (handshakeSuccess, joinSuccess);
        }
        catch
        {
            return (false, false);
        }
    }

    #endregion

    #region F1: Room Session Management Boundaries

    [Fact]
    public async Task T2_1_1_JoinRoom_NegativeRoomId()
    {
        if (_fixture.ServerProcess == null) return;
        using var client = new RawTestClient(20101, _fixture.ServerEndPoint);
        
        // Joining with a negative Room ID should not crash the server.
        // Based on the server implementation, this succeeds since there's no restriction on roomId value.
        var (hs, join) = await PerformHandshakeAndJoinAsync(client, -5);
        Assert.True(hs);
        Assert.True(join);
    }

    [Fact]
    public async Task T2_1_2_JoinRoom_MaxIntegerRoomId()
    {
        if (_fixture.ServerProcess == null) return;
        using var client = new RawTestClient(20102, _fixture.ServerEndPoint);
        var (hs, join) = await PerformHandshakeAndJoinAsync(client, int.MaxValue);
        Assert.True(hs && join);
    }

    [Fact]
    public async Task T2_1_3_DuplicateRoomJoin()
    {
        if (_fixture.ServerProcess == null) return;
        using var client = new RawTestClient(20103, _fixture.ServerEndPoint);
        var (hs, join1) = await PerformHandshakeAndJoinAsync(client, 20203);
        Assert.True(hs && join1);

        // Join again
        var join2 = CreatePacket(2, 20203, client.ClientId, 2, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var res2 = await client.SendAndReceiveAsync(join2);
        int val2 = BinaryPrimitives.ReadInt32LittleEndian(res2.AsSpan(28, 4));
        Assert.Equal(1, val2);

        // Join a third time
        var join3 = CreatePacket(2, 20203, client.ClientId, 3, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var res3 = await client.SendAndReceiveAsync(join3);
        int val3 = BinaryPrimitives.ReadInt32LittleEndian(res3.AsSpan(28, 4));
        Assert.Equal(1, val3);
    }

    [Fact]
    public async Task T2_1_4_LeaveRoom_WithoutJoiningFirst()
    {
        if (_fixture.ServerProcess == null) return;
        using var client = new RawTestClient(20104, _fixture.ServerEndPoint);
        
        // Handshake first to register
        var hs = CreatePacket(1, 0, client.ClientId, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await client.SendAndReceiveAsync(hs);

        // Send JoinRoom with room 0 (Leave) without having joined any room first.
        var leave = CreatePacket(2, 0, client.ClientId, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var res = await client.SendAndReceiveAsync(leave);
        
        // Server will try to LeaveRoom, but ClientSession.RoomId is 0.
        // Let's verify it returns either success (1) or failure (0) and doesn't crash.
        int val = BinaryPrimitives.ReadInt32LittleEndian(res.AsSpan(28, 4));
        Assert.True(val == 0 || val == 1);
    }

    [Fact]
    public async Task T2_1_5_JoinRoom_EmptyZeroRoomId()
    {
        if (_fixture.ServerProcess == null) return;
        using var client = new RawTestClient(20105, _fixture.ServerEndPoint);
        var (hs, join) = await PerformHandshakeAndJoinAsync(client, 0);
        // Joining room 0 acts as a leave/null room, but let's assert it is processed and doesn't crash.
        Assert.True(hs);
        Assert.True(join);
    }

    #endregion

    #region F2: Audio Packet Broadcasting Boundaries

    [Fact]
    public async Task T2_2_1_SendAudio_EmptyZeroPayload()
    {
        if (_fixture.ServerProcess == null) return;
        using var sender = new RawTestClient(20210, _fixture.ServerEndPoint);
        using var receiver = new RawTestClient(20211, _fixture.ServerEndPoint);

        await PerformHandshakeAndJoinAsync(sender, 20300);
        await PerformHandshakeAndJoinAsync(receiver, 20300);

        // Send audio packet with empty payload
        var audio = CreatePacket(3, 20300, sender.ClientId, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Array.Empty<byte>());
        await sender.SendAsync(audio);

        var recv = await receiver.ReceiveAsync();
        Assert.Equal(28, recv.Length);
        Shared.Serialization.PacketSerializer.TryDeserializeHeader(recv, out var header);
        Assert.Equal(0, header.PayloadLength);
    }

    [Fact]
    public async Task T2_2_2_SendAudio_MaxAllowedPayload()
    {
        if (_fixture.ServerProcess == null) return;
        using var sender = new RawTestClient(20212, _fixture.ServerEndPoint);
        using var receiver = new RawTestClient(20213, _fixture.ServerEndPoint);

        await PerformHandshakeAndJoinAsync(sender, 20301);
        await PerformHandshakeAndJoinAsync(receiver, 20301);

        // Max payload size is 1024 bytes (Opus limit commonly used)
        byte[] payload = new byte[1024];
        new Random().NextBytes(payload);

        var audio = CreatePacket(3, 20301, sender.ClientId, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), payload);
        await sender.SendAsync(audio);

        var recv = await receiver.ReceiveAsync();
        Assert.Equal(28 + 1024, recv.Length);
        Shared.Serialization.PacketSerializer.TryDeserializeHeader(recv, out var header);
        Assert.Equal(1024, header.PayloadLength);
        Assert.Equal(payload, recv.AsSpan(28).ToArray());
    }

    [Fact]
    public async Task T2_2_3_SendAudio_OversizedPayload()
    {
        if (_fixture.ServerProcess == null) return;
        using var sender = new RawTestClient(20214, _fixture.ServerEndPoint);
        using var receiver = new RawTestClient(20215, _fixture.ServerEndPoint);

        await PerformHandshakeAndJoinAsync(sender, 20302);
        await PerformHandshakeAndJoinAsync(receiver, 20302);

        // Server has max 1500 bytes payload limit in PacketRouter.cs:
        // if (header.PayloadLength < 0 || header.PayloadLength != actualPayloadSize || header.PayloadLength > 1500)
        byte[] oversizedPayload = new byte[1501];
        var audio = CreatePacket(3, 20302, sender.ClientId, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), oversizedPayload);
        await sender.SendAsync(audio);

        // Receiver should not receive it since it gets dropped
        await Assert.ThrowsAnyAsync<Exception>(async () => await receiver.ReceiveAsync(500));
    }

    [Fact]
    public async Task T2_2_4_SendAudio_NoOtherClientsInRoom()
    {
        if (_fixture.ServerProcess == null) return;
        using var sender = new RawTestClient(20216, _fixture.ServerEndPoint);
        await PerformHandshakeAndJoinAsync(sender, 20303);

        for (int i = 0; i < 5; i++)
        {
            var audio = CreatePacket(3, 20303, sender.ClientId, (uint)i, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new byte[] { 1, 2, 3 });
            await sender.SendAsync(audio);
        }

        // Verify sender doesn't crash and remains registered
        var join0 = CreatePacket(2, 0, sender.ClientId, 10, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var res = await sender.SendAndReceiveAsync(join0);
        Assert.True(res.Length >= 28);
    }

    [Fact]
    public async Task T2_2_5_SendAudio_PriorToJoiningAnyRoom()
    {
        if (_fixture.ServerProcess == null) return;
        using var sender = new RawTestClient(20217, _fixture.ServerEndPoint);
        using var receiver = new RawTestClient(20218, _fixture.ServerEndPoint);

        // Handshake sender but do not join room
        var hs = CreatePacket(1, 0, sender.ClientId, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await sender.SendAndReceiveAsync(hs);

        // Join receiver to room 20304
        await PerformHandshakeAndJoinAsync(receiver, 20304);

        // Sender tries to send audio packet with RoomId = 20304
        var audio = CreatePacket(3, 20304, sender.ClientId, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new byte[] { 1 });
        await sender.SendAsync(audio);

        // Receiver should receive nothing because sender session has RoomId = 0
        await Assert.ThrowsAnyAsync<Exception>(async () => await receiver.ReceiveAsync(500));
    }

    #endregion

    #region F3: Packet Formatting & Validation Boundaries

    [Fact]
    public async Task T2_3_1_SendPacket_SmallerThanHeaderSize()
    {
        if (_fixture.ServerProcess == null) return;
        using var client = new RawTestClient(20310, _fixture.ServerEndPoint);
        
        // Handshake
        var hs = CreatePacket(1, 0, client.ClientId, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await client.SendAndReceiveAsync(hs);

        // Send a 10-byte packet (malformed header)
        byte[] malformed = new byte[10];
        new Random().NextBytes(malformed);
        await client.SendAsync(malformed);

        // Verify client remains handshaked by joining room
        var join = CreatePacket(2, 20310, client.ClientId, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var res = await client.SendAndReceiveAsync(join);
        int success = BinaryPrimitives.ReadInt32LittleEndian(res.AsSpan(28, 4));
        Assert.Equal(1, success);
    }

    [Fact]
    public async Task T2_3_2_SendPacket_InvalidPacketType()
    {
        if (_fixture.ServerProcess == null) return;
        using var client = new RawTestClient(20311, _fixture.ServerEndPoint);
        
        // Handshake
        var hs = CreatePacket(1, 0, client.ClientId, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await client.SendAndReceiveAsync(hs);

        // Send packet with type 99 (invalid)
        var invalidPacket = CreatePacket(99, 20311, client.ClientId, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await client.SendAsync(invalidPacket);

        // Verify client remains functional
        var join = CreatePacket(2, 20311, client.ClientId, 2, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var res = await client.SendAndReceiveAsync(join);
        int success = BinaryPrimitives.ReadInt32LittleEndian(res.AsSpan(28, 4));
        Assert.Equal(1, success);
    }

    [Fact]
    public async Task T2_3_3_PayloadLength_Mismatch_TooSmall()
    {
        if (_fixture.ServerProcess == null) return;
        using var sender = new RawTestClient(20312, _fixture.ServerEndPoint);
        using var receiver = new RawTestClient(20313, _fixture.ServerEndPoint);

        await PerformHandshakeAndJoinAsync(sender, 20312);
        await PerformHandshakeAndJoinAsync(receiver, 20312);

        // Set header PayloadLength = 5, but actual payload is 10 bytes (actual packet size = 38 bytes)
        byte[] buffer = new byte[38];
        var header = new Shared.Models.VoicePacketHeader(3, 20312, sender.ClientId, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 5);
        Shared.Serialization.PacketSerializer.TrySerializeHeader(header, buffer);
        await sender.SendAsync(buffer);

        // Server should drop it due to mismatch: actualPayloadSize (10) != header.PayloadLength (5)
        await Assert.ThrowsAnyAsync<Exception>(async () => await receiver.ReceiveAsync(500));
    }

    [Fact]
    public async Task T2_3_4_PayloadLength_Mismatch_TooLarge()
    {
        if (_fixture.ServerProcess == null) return;
        using var sender = new RawTestClient(20314, _fixture.ServerEndPoint);
        using var receiver = new RawTestClient(20315, _fixture.ServerEndPoint);

        await PerformHandshakeAndJoinAsync(sender, 20313);
        await PerformHandshakeAndJoinAsync(receiver, 20313);

        // Set header PayloadLength = 10, but actual payload is 5 bytes (actual packet size = 33 bytes)
        byte[] buffer = new byte[33];
        var header = new Shared.Models.VoicePacketHeader(3, 20313, sender.ClientId, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 10);
        Shared.Serialization.PacketSerializer.TrySerializeHeader(header, buffer);
        await sender.SendAsync(buffer);

        // Server drops it due to mismatch: actualPayloadSize (5) != header.PayloadLength (10)
        await Assert.ThrowsAnyAsync<Exception>(async () => await receiver.ReceiveAsync(500));
    }

    [Fact]
    public async Task T2_3_5_SendPacket_FutureTimestamp()
    {
        if (_fixture.ServerProcess == null) return;
        using var sender = new RawTestClient(20316, _fixture.ServerEndPoint);
        using var receiver = new RawTestClient(20317, _fixture.ServerEndPoint);

        await PerformHandshakeAndJoinAsync(sender, 20314);
        await PerformHandshakeAndJoinAsync(receiver, 20314);

        long futureTs = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds();
        var audio = CreatePacket(3, 20314, sender.ClientId, 1, futureTs, new byte[] { 1 });
        await sender.SendAsync(audio);

        var recv = await receiver.ReceiveAsync();
        Shared.Serialization.PacketSerializer.TryDeserializeHeader(recv, out var header);
        Assert.Equal(futureTs, header.SendTimestamp);
    }

    #endregion

    #region F4: Latency & Jitter Boundaries

    [Fact]
    public async Task T2_4_1_RTT_DelayedResponse()
    {
        if (_fixture.ServerProcess == null) return;
        using var client = new MockClient(20400, _fixture.ServerEndPoint);
        
        // Set artificial delay to 100ms
        client.Degradation = new DegradationConfig { PacketLossRate = 0, ArtificialDelayMs = 100 };
        
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        bool connected = await client.ConnectAsync();
        stopwatch.Stop();

        Assert.True(connected);
        var metrics = client.Tracker.GetMetrics();
        Assert.True(metrics.AverageRttMs >= 100.0);
    }

    [Fact]
    public void T2_4_2_OutOfOrderPackets()
    {
        var tracker = new MetricTracker();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        tracker.RecordPacketReceived(3, now - 30, now);
        tracker.RecordPacketReceived(2, now - 20, now); // Out of order!
        tracker.RecordPacketReceived(4, now - 40, now); // Monotonic compared to 3 (which was the highest before)

        var metrics = tracker.GetMetrics();
        Assert.Equal(1, metrics.OutOfOrderPackets);
    }

    [Fact]
    public void T2_4_3_DuplicateSequencePackets()
    {
        var tracker = new MetricTracker();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        tracker.RecordPacketReceived(1, now - 10, now);
        tracker.RecordPacketReceived(2, now - 20, now);
        tracker.RecordPacketReceived(2, now - 20, now); // Duplicate!
        tracker.RecordPacketReceived(3, now - 30, now);

        var metrics = tracker.GetMetrics();
        Assert.Equal(1, metrics.DuplicatePackets);
    }

    [Fact]
    public async Task T2_4_4_SpammingPackets()
    {
        if (_fixture.ServerProcess == null) return;
        using var client = new RawTestClient(20401, _fixture.ServerEndPoint);
        await PerformHandshakeAndJoinAsync(client, 20400);

        // Spam 500 audio packets in a tight loop
        for (uint i = 0; i < 500; i++)
        {
            var audio = CreatePacket(3, 20400, client.ClientId, i, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new byte[] { 1 });
            await client.SendAsync(audio);
        }

        // Verify server still responsive
        var join0 = CreatePacket(2, 0, client.ClientId, 1000, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var res = await client.SendAndReceiveAsync(join0);
        Assert.True(res.Length >= 28);
    }

    [Fact]
    public async Task T2_4_5_TimestampInPast()
    {
        if (_fixture.ServerProcess == null) return;
        using var sender = new RawTestClient(20402, _fixture.ServerEndPoint);
        using var receiver = new RawTestClient(20403, _fixture.ServerEndPoint);

        await PerformHandshakeAndJoinAsync(sender, 20401);
        await PerformHandshakeAndJoinAsync(receiver, 20401);

        long pastTs = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
        var audio = CreatePacket(3, 20401, sender.ClientId, 1, pastTs, new byte[] { 1 });
        await sender.SendAsync(audio);

        var recv = await receiver.ReceiveAsync();
        Shared.Serialization.PacketSerializer.TryDeserializeHeader(recv, out var header);
        Assert.Equal(pastTs, header.SendTimestamp);
    }

    #endregion

    #region F5: Robustness Boundaries

    [Fact]
    public async Task T2_5_1_SendMalformedRandomBytes()
    {
        if (_fixture.ServerProcess == null) return;
        using var client = new RawTestClient(20500, _fixture.ServerEndPoint);
        
        // Send garbage
        byte[] garbage = new byte[100];
        new Random().NextBytes(garbage);
        await client.SendAsync(garbage);

        // Verify server still works
        var (hs, join) = await PerformHandshakeAndJoinAsync(client, 20500);
        Assert.True(hs && join);
    }

    [Fact]
    public async Task T2_5_2_RapidJoinLeaveToggle()
    {
        if (_fixture.ServerProcess == null) return;
        using var client = new RawTestClient(20501, _fixture.ServerEndPoint);

        // Handshake
        var hs = CreatePacket(1, 0, client.ClientId, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await client.SendAndReceiveAsync(hs);

        for (uint i = 1; i <= 100; i++)
        {
            var join = CreatePacket(2, 20501, client.ClientId, i * 2, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await client.SendAsync(join);

            var leave = CreatePacket(2, 0, client.ClientId, i * 2 + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await client.SendAsync(leave);
        }

        // Give the network loop a moment to drain all processed packets
        await Task.Delay(500);

        // Verify client can still join successfully
        var joinFinal = CreatePacket(2, 20501, client.ClientId, 300, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var res = await client.SendAndReceiveAsync(joinFinal);
        int success = BinaryPrimitives.ReadInt32LittleEndian(res.AsSpan(28, 4));
        Assert.Equal(1, success);
    }

    [Fact]
    public async Task T2_5_3_MassDisconnect_20Clients()
    {
        if (_fixture.ServerProcess == null) return;
        int count = 20;
        var clients = new List<RawTestClient>();

        for (int i = 0; i < count; i++)
        {
            var c = new RawTestClient(20600 + i, _fixture.ServerEndPoint);
            clients.Add(c);
            await PerformHandshakeAndJoinAsync(c, 20600);
        }

        // Mass disconnect concurrently
        foreach (var c in clients)
        {
            c.Dispose();
        }

        await Task.Delay(500);

        // Verify server handles a new client handshake successfully
        using var testClient = new RawTestClient(20700, _fixture.ServerEndPoint);
        var (hs, join) = await PerformHandshakeAndJoinAsync(testClient, 20600);
        Assert.True(hs && join);
    }

    [Fact]
    public async Task T2_5_4_SendPacket_ZeroBufferLength()
    {
        if (_fixture.ServerProcess == null) return;
        using var client = new RawTestClient(20800, _fixture.ServerEndPoint);
        
        // Send a 0-byte packet
        await client.SendAsync(Array.Empty<byte>());

        // Verify server remains responsive
        var (hs, join) = await PerformHandshakeAndJoinAsync(client, 20800);
        Assert.True(hs && join);
    }

    [Fact]
    public async Task T2_5_5_PortScanningSimulation()
    {
        if (_fixture.ServerProcess == null) return;
        
        using var client = new RawTestClient(20900, _fixture.ServerEndPoint);
        
        // Send various non-protocol random UDP packets to server port
        var random = new Random();
        for (int i = 0; i < 15; i++)
        {
            byte[] scanBytes = new byte[random.Next(1, 512)];
            random.NextBytes(scanBytes);
            await client.SendAsync(scanBytes);
        }

        // Verify server still processes valid clients correctly
        var (hs, join) = await PerformHandshakeAndJoinAsync(client, 20900);
        Assert.True(hs && join);
    }

    [Fact]
    public async Task T2_5_6_TcpHandshakeAndJoin_Success()
    {
        if (_fixture.ServerProcess == null) return;
        
        IPEndPoint tcpEndPoint = new IPEndPoint(_fixture.ServerEndPoint.Address, 50006);
        using var client = new RawTcpTestClient(22222, tcpEndPoint);
        
        var (handshakeSuccess, joinSuccess) = await PerformTcpHandshakeAndJoinAsync(client, 222);
        Assert.True(handshakeSuccess, "TCP Handshake failed.");
        Assert.True(joinSuccess, "TCP Join room failed.");
    }

    [Fact]
    public async Task T2_5_7_TcpReconnectSameClientId_Success()
    {
        if (_fixture.ServerProcess == null) return;
        
        IPEndPoint tcpEndPoint = new IPEndPoint(_fixture.ServerEndPoint.Address, 50006);
        int clientId = 33333;
        int roomId = 333;
        
        // 1. Connect and join with client1
        using (var client1 = new RawTcpTestClient(clientId, tcpEndPoint))
        {
            var (hs1, join1) = await PerformTcpHandshakeAndJoinAsync(client1, roomId);
            Assert.True(hs1 && join1, "Initial connection failed.");
        } // client1 is disposed here, closing the socket
        
        // 2. Immediately reconnect with client2 using the SAME client ID
        using (var client2 = new RawTcpTestClient(clientId, tcpEndPoint))
        {
            var (hs2, join2) = await PerformTcpHandshakeAndJoinAsync(client2, roomId);
            Assert.True(hs2, "Reconnection Handshake failed.");
            Assert.True(join2, "Reconnection Join room failed.");
            
            // Wait 500ms to ensure the old connection's cleanup task had time to run
            await Task.Delay(500);
            
            // 3. Verify client2 is still alive and can send a heartbeat and receive an ACK
            var heartbeatBytes = CreatePacket(4, roomId, clientId, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await client2.SendFramedAsync(heartbeatBytes, heartbeatBytes.Length);
            
            var heartbeatResponse = await client2.ReceiveFramedAsync();
            Assert.True(heartbeatResponse.Length >= 28, "Did not receive heartbeat ACK after cleanup.");
            Shared.Serialization.PacketSerializer.TryDeserializeHeader(heartbeatResponse, out var hbHeader);
            Assert.Equal(4, hbHeader.PacketType);
        }
    }

    #endregion
}
