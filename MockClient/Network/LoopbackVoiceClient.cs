using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Shared.Models;
using Shared.Serialization;

namespace MockClient.Network
{
    public class LoopbackVoiceClient
    {
        public int ClientId { get; }
        public int RoomId { get; }
        private readonly string _serverIp;
        private readonly int _serverPort;
        private readonly string _protocol; // "udp", "tcp", "ws"

        private Socket? _udpSocket;
        private TcpClient? _tcpClient;
        private NetworkStream? _tcpStream;
        private ClientWebSocket? _wsClient;

        private uint _sequenceNumber;
        private CancellationTokenSource? _cts;
        private Task? _receiveTask;

        private long _packetsReceived;
        private long _packetsLooped;
        private long _bytesReceived;
        private long _bytesLooped;

        public LoopbackVoiceClient(int clientId, int roomId, string serverIp, int serverPort, string protocol)
        {
            ClientId = clientId;
            RoomId = roomId;
            _serverIp = serverIp;
            _serverPort = serverPort;
            _protocol = protocol.ToLower();
        }

        public async Task<bool> StartAsync(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Console.WriteLine($"[LoopbackClient {ClientId}] Starting connection to {_serverIp}:{_serverPort} via {_protocol.ToUpper()} in room {RoomId}...");

            try
            {
                if (_protocol == "udp")
                {
                    _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    _udpSocket.ReceiveBufferSize = 1024 * 1024;
                    _udpSocket.SendBufferSize = 1024 * 1024;
                    _udpSocket.Bind(new IPEndPoint(IPAddress.Any, 0));

                    bool connected = await UdpHandshakeAndJoinAsync(_cts.Token);
                    if (!connected) return false;

                    _receiveTask = RunUdpReceiveLoopAsync(_cts.Token);
                }
                else if (_protocol == "tcp")
                {
                    _tcpClient = new TcpClient { NoDelay = true };
                    _tcpClient.ReceiveBufferSize = 256 * 1024;
                    _tcpClient.SendBufferSize = 256 * 1024;

                    await _tcpClient.ConnectAsync(_serverIp, _serverPort, _cts.Token);
                    _tcpStream = _tcpClient.GetStream();

                    bool connected = await TcpHandshakeAndJoinAsync(_cts.Token);
                    if (!connected) return false;

                    _receiveTask = RunTcpReceiveLoopAsync(_cts.Token);
                }
                else if (_protocol == "ws" || _protocol == "websocket")
                {
                    _wsClient = new ClientWebSocket();
                    _wsClient.Options.SetBuffer(receiveBufferSize: 256 * 1024, sendBufferSize: 256 * 1024);

                    string url = $"ws://{_serverIp}:{_serverPort}/voice/";
                    await _wsClient.ConnectAsync(new Uri(url), _cts.Token);

                    bool connected = await WsHandshakeAndJoinAsync(_cts.Token);
                    if (!connected) return false;

                    _receiveTask = RunWsReceiveLoopAsync(_cts.Token);
                }
                else
                {
                    Console.WriteLine($"[LoopbackClient] Unsupported protocol: {_protocol}");
                    return false;
                }

                _ = RunStatsLoggingLoopAsync(_cts.Token);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoopbackClient {ClientId}] Failed to start: {ex.Message}");
                return false;
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _udpSocket?.Close(); } catch {}
            try { _tcpClient?.Close(); } catch {}
            try { _wsClient?.CloseAsync(WebSocketCloseStatus.NormalClosure, "stopping", CancellationToken.None).GetAwaiter().GetResult(); } catch {}
        }

        // --- UDP HANDSHAKE & JOIN ---
        private async Task<bool> UdpHandshakeAndJoinAsync(CancellationToken ct)
        {
            EndPoint serverEP = new IPEndPoint(IPAddress.Parse(_serverIp), _serverPort);
            byte[] buffer = new byte[VoicePacketHeader.HeaderSize + 16];

            // Handshake
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var header = new VoicePacketHeader(packetType: 1, roomId: 0, clientId: ClientId, sequenceNumber: 0, sendTimestamp: t0, payloadLength: 0);
                PacketSerializer.TrySerializeHeader(header, buffer);

                try
                {
                    await _udpSocket!.SendToAsync(new ReadOnlyMemory<byte>(buffer, 0, VoicePacketHeader.HeaderSize), SocketFlags.None, serverEP, ct);
                    
                    var receiveTask = _udpSocket.ReceiveFromAsync(new Memory<byte>(buffer), SocketFlags.None, serverEP).AsTask();
                    var delayTask = Task.Delay(1000, ct);

                    if (await Task.WhenAny(receiveTask, delayTask) == receiveTask)
                    {
                        var result = await receiveTask;
                        if (result.ReceivedBytes >= VoicePacketHeader.HeaderSize && PacketSerializer.TryDeserializeHeader(buffer.AsSpan(0, result.ReceivedBytes), out var ack))
                        {
                            if (ack.PacketType == 1 && ack.ClientId == ClientId)
                            {
                                Console.WriteLine($"[LoopbackClient {ClientId}] UDP Handshake successful.");
                                break;
                            }
                        }
                    }
                }
                catch {}
            }

            // Room Join
            var joinHeader = new VoicePacketHeader(packetType: 2, roomId: RoomId, clientId: ClientId, sequenceNumber: 0, sendTimestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), payloadLength: 0);
            PacketSerializer.TrySerializeHeader(joinHeader, buffer);

            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    await _udpSocket!.SendToAsync(new ReadOnlyMemory<byte>(buffer, 0, VoicePacketHeader.HeaderSize), SocketFlags.None, serverEP, ct);

                    var receiveTask = _udpSocket.ReceiveFromAsync(new Memory<byte>(buffer), SocketFlags.None, serverEP).AsTask();
                    var delayTask = Task.Delay(1000, ct);

                    if (await Task.WhenAny(receiveTask, delayTask) == receiveTask)
                    {
                        var result = await receiveTask;
                        if (result.ReceivedBytes >= VoicePacketHeader.HeaderSize && PacketSerializer.TryDeserializeHeader(buffer.AsSpan(0, result.ReceivedBytes), out var ack))
                        {
                            if (ack.PacketType == 2 && ack.PayloadLength >= 4)
                            {
                                int success = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(VoicePacketHeader.HeaderSize, 4));
                                if (success == 1)
                                {
                                    Console.WriteLine($"[LoopbackClient {ClientId}] UDP Room Join successful.");
                                    return true;
                                }
                            }
                        }
                    }
                }
                catch {}
            }

            return false;
        }

        // --- TCP HANDSHAKE & JOIN ---
        private async Task<bool> TcpHandshakeAndJoinAsync(CancellationToken ct)
        {
            byte[] frameBuf = new byte[4 + VoicePacketHeader.HeaderSize + 16];

            // Handshake
            long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var header = new VoicePacketHeader(packetType: 1, roomId: 0, clientId: ClientId, sequenceNumber: 0, sendTimestamp: t0, payloadLength: 0);
            BinaryPrimitives.WriteUInt32LittleEndian(frameBuf.AsSpan(0, 4), VoicePacketHeader.HeaderSize);
            PacketSerializer.TrySerializeHeader(header, frameBuf.AsSpan(4));

            await _tcpStream!.WriteAsync(new ReadOnlyMemory<byte>(frameBuf, 0, 4 + VoicePacketHeader.HeaderSize), ct);

            // Read Handshake ACK
            byte[] lenBuf = new byte[4];
            if (!await ReadExactAsync(_tcpStream, lenBuf, 4, ct)) return false;
            uint msgLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
            byte[] bodyBuf = new byte[msgLen];
            if (!await ReadExactAsync(_tcpStream, bodyBuf, (int)msgLen, ct)) return false;

            if (PacketSerializer.TryDeserializeHeader(bodyBuf, out var ack) && ack.PacketType == 1)
            {
                Console.WriteLine($"[LoopbackClient {ClientId}] TCP Handshake successful.");
            }
            else
            {
                return false;
            }

            // Room Join
            var joinHeader = new VoicePacketHeader(packetType: 2, roomId: RoomId, clientId: ClientId, sequenceNumber: 0, sendTimestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), payloadLength: 0);
            BinaryPrimitives.WriteUInt32LittleEndian(frameBuf.AsSpan(0, 4), VoicePacketHeader.HeaderSize);
            PacketSerializer.TrySerializeHeader(joinHeader, frameBuf.AsSpan(4));

            await _tcpStream.WriteAsync(new ReadOnlyMemory<byte>(frameBuf, 0, 4 + VoicePacketHeader.HeaderSize), ct);

            // Read Room Join ACK
            if (!await ReadExactAsync(_tcpStream, lenBuf, 4, ct)) return false;
            msgLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
            bodyBuf = new byte[msgLen];
            if (!await ReadExactAsync(_tcpStream, bodyBuf, (int)msgLen, ct)) return false;

            if (PacketSerializer.TryDeserializeHeader(bodyBuf, out var joinAck) && joinAck.PacketType == 2)
            {
                int success = BinaryPrimitives.ReadInt32LittleEndian(bodyBuf.AsSpan(VoicePacketHeader.HeaderSize, 4));
                if (success == 1)
                {
                    Console.WriteLine($"[LoopbackClient {ClientId}] TCP Room Join successful.");
                    return true;
                }
            }

            return false;
        }

        // --- WS HANDSHAKE & JOIN ---
        private async Task<bool> WsHandshakeAndJoinAsync(CancellationToken ct)
        {
            byte[] buffer = new byte[VoicePacketHeader.HeaderSize + 16];

            // Handshake
            long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var header = new VoicePacketHeader(packetType: 1, roomId: 0, clientId: ClientId, sequenceNumber: 0, sendTimestamp: t0, payloadLength: 0);
            PacketSerializer.TrySerializeHeader(header, buffer);

            await _wsClient!.SendAsync(new ArraySegment<byte>(buffer, 0, VoicePacketHeader.HeaderSize), WebSocketMessageType.Binary, true, ct);

            // Read Handshake ACK
            var result = await _wsClient.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Binary && result.Count >= VoicePacketHeader.HeaderSize)
            {
                if (PacketSerializer.TryDeserializeHeader(buffer.AsSpan(0, result.Count), out var ack) && ack.PacketType == 1)
                {
                    Console.WriteLine($"[LoopbackClient {ClientId}] WS Handshake successful.");
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            // Room Join
            var joinHeader = new VoicePacketHeader(packetType: 2, roomId: RoomId, clientId: ClientId, sequenceNumber: 0, sendTimestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), payloadLength: 0);
            PacketSerializer.TrySerializeHeader(joinHeader, buffer);

            await _wsClient.SendAsync(new ArraySegment<byte>(buffer, 0, VoicePacketHeader.HeaderSize), WebSocketMessageType.Binary, true, ct);

            // Read Room Join ACK
            result = await _wsClient.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Binary && result.Count >= VoicePacketHeader.HeaderSize)
            {
                if (PacketSerializer.TryDeserializeHeader(buffer.AsSpan(0, result.Count), out var joinAck) && joinAck.PacketType == 2)
                {
                    int success = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(VoicePacketHeader.HeaderSize, 4));
                    if (success == 1)
                    {
                        Console.WriteLine($"[LoopbackClient {ClientId}] WS Room Join successful.");
                        return true;
                    }
                }
            }

            return false;
        }

        // --- UDP RECEIVE AND LOOPBACK LOOP ---
        private async Task RunUdpReceiveLoopAsync(CancellationToken ct)
        {
            byte[] receiveBuffer = new byte[2048];
            byte[] sendBuffer = new byte[2048];
            EndPoint serverEP = new IPEndPoint(IPAddress.Parse(_serverIp), _serverPort);
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var result = await _udpSocket!.ReceiveFromAsync(new Memory<byte>(receiveBuffer), SocketFlags.None, remoteEP, ct);
                    int length = result.ReceivedBytes;

                    if (length >= VoicePacketHeader.HeaderSize)
                    {
                        if (PacketSerializer.TryDeserializeHeader(receiveBuffer.AsSpan(0, length), out var header))
                        {
                            if (header.PacketType == 3 && header.ClientId != ClientId)
                            {
                                Interlocked.Increment(ref _packetsReceived);
                                Interlocked.Add(ref _bytesReceived, length);

                                // Prepare Loopback Packet
                                var loopbackHeader = new VoicePacketHeader(
                                    packetType: 3,
                                    roomId: RoomId,
                                    clientId: ClientId,
                                    sequenceNumber: ++_sequenceNumber,
                                    sendTimestamp: header.SendTimestamp, // PRESERVE sender's timestamp for RTT
                                    payloadLength: header.PayloadLength
                                );

                                PacketSerializer.TrySerializeHeader(loopbackHeader, sendBuffer);
                                Buffer.BlockCopy(receiveBuffer, VoicePacketHeader.HeaderSize, sendBuffer, VoicePacketHeader.HeaderSize, header.PayloadLength);

                                await _udpSocket.SendToAsync(new ReadOnlyMemory<byte>(sendBuffer, 0, length), SocketFlags.None, serverEP, ct);

                                Interlocked.Increment(ref _packetsLooped);
                                Interlocked.Add(ref _bytesLooped, length);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) {}
            catch (Exception ex)
            {
                Console.WriteLine($"[LoopbackClient] UDP Loop error: {ex.Message}");
            }
        }

        // --- TCP RECEIVE AND LOOPBACK LOOP ---
        private async Task RunTcpReceiveLoopAsync(CancellationToken ct)
        {
            byte[] lenBuf = new byte[4];
            byte[] packetBuf = new byte[2048];
            byte[] sendBuffer = new byte[2048];

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(_tcpStream!, lenBuf, 4, ct)) break;
                    uint msgLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);

                    if (msgLen > packetBuf.Length)
                        packetBuf = new byte[msgLen];

                    if (!await ReadExactAsync(_tcpStream!, packetBuf, (int)msgLen, ct)) break;

                    if (msgLen >= VoicePacketHeader.HeaderSize)
                    {
                        if (PacketSerializer.TryDeserializeHeader(packetBuf.AsSpan(0, (int)msgLen), out var header))
                        {
                            if (header.PacketType == 3 && header.ClientId != ClientId)
                            {
                                Interlocked.Increment(ref _packetsReceived);
                                Interlocked.Add(ref _bytesReceived, msgLen);

                                // Prepare Loopback Packet
                                var loopbackHeader = new VoicePacketHeader(
                                    packetType: 3,
                                    roomId: RoomId,
                                    clientId: ClientId,
                                    sequenceNumber: ++_sequenceNumber,
                                    sendTimestamp: header.SendTimestamp, // PRESERVE
                                    payloadLength: header.PayloadLength
                                );

                                BinaryPrimitives.WriteUInt32LittleEndian(sendBuffer.AsSpan(0, 4), msgLen);
                                PacketSerializer.TrySerializeHeader(loopbackHeader, sendBuffer.AsSpan(4));
                                Buffer.BlockCopy(packetBuf, VoicePacketHeader.HeaderSize, sendBuffer, 4 + VoicePacketHeader.HeaderSize, header.PayloadLength);

                                await _tcpStream!.WriteAsync(new ReadOnlyMemory<byte>(sendBuffer, 0, (int)(4 + msgLen)), ct);

                                Interlocked.Increment(ref _packetsLooped);
                                Interlocked.Add(ref _bytesLooped, msgLen);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) {}
            catch (Exception ex)
            {
                Console.WriteLine($"[LoopbackClient] TCP Loop error: {ex.Message}");
            }
        }

        // --- WS RECEIVE AND LOOPBACK LOOP ---
        private async Task RunWsReceiveLoopAsync(CancellationToken ct)
        {
            byte[] receiveBuffer = new byte[2048];
            byte[] sendBuffer = new byte[2048];

            try
            {
                while (!ct.IsCancellationRequested && _wsClient!.State == WebSocketState.Open)
                {
                    var result = await _wsClient.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), ct);
                    int length = result.Count;

                    if (result.MessageType == WebSocketMessageType.Binary && length >= VoicePacketHeader.HeaderSize)
                    {
                        if (PacketSerializer.TryDeserializeHeader(receiveBuffer.AsSpan(0, length), out var header))
                        {
                            if (header.PacketType == 3 && header.ClientId != ClientId)
                            {
                                Interlocked.Increment(ref _packetsReceived);
                                Interlocked.Add(ref _bytesReceived, length);

                                // Prepare Loopback Packet
                                var loopbackHeader = new VoicePacketHeader(
                                    packetType: 3,
                                    roomId: RoomId,
                                    clientId: ClientId,
                                    sequenceNumber: ++_sequenceNumber,
                                    sendTimestamp: header.SendTimestamp, // PRESERVE
                                    payloadLength: header.PayloadLength
                                );

                                PacketSerializer.TrySerializeHeader(loopbackHeader, sendBuffer);
                                Buffer.BlockCopy(receiveBuffer, VoicePacketHeader.HeaderSize, sendBuffer, VoicePacketHeader.HeaderSize, header.PayloadLength);

                                await _wsClient.SendAsync(new ArraySegment<byte>(sendBuffer, 0, length), WebSocketMessageType.Binary, true, ct);

                                Interlocked.Increment(ref _packetsLooped);
                                Interlocked.Add(ref _bytesLooped, length);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) {}
            catch (Exception ex)
            {
                Console.WriteLine($"[LoopbackClient] WS Loop error: {ex.Message}");
            }
        }

        // --- HELPERS ---
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

        private async Task RunStatsLoggingLoopAsync(CancellationToken ct)
        {
            long lastReceived = 0;
            long lastLooped = 0;
            var stopwatch = Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                stopwatch.Restart();

                long currentReceived = Volatile.Read(ref _packetsReceived);
                long currentLooped = Volatile.Read(ref _packetsLooped);

                double recvPps = elapsedSec > 0 ? (currentReceived - lastReceived) / elapsedSec : 0;
                double loopPps = elapsedSec > 0 ? (currentLooped - lastLooped) / elapsedSec : 0;

                lastReceived = currentReceived;
                lastLooped = currentLooped;

                long rxBytes = Volatile.Read(ref _bytesReceived);
                long txBytes = Volatile.Read(ref _bytesLooped);

                Console.WriteLine($"[LoopbackClient {ClientId}] Room: {RoomId} | Protocol: {_protocol.ToUpper()} | Active");
                Console.WriteLine($"    Packets RX: {currentReceived:N0} ({recvPps:F1} pps) | TX: {currentLooped:N0} ({loopPps:F1} pps)");
                Console.WriteLine($"    Data RX: {rxBytes / 1024.0:F1} KB | TX: {txBytes / 1024.0:F1} KB");
            }
        }
    }
}
