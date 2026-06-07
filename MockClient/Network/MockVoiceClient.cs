using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Shared.Models;
using Shared.Serialization;

namespace MockClient.Network
{
    public class GlobalCounters
    {
        public long GlobalPacketsSent;
        public long GlobalBytesSent;
        public long GlobalPacketsReceived;
        public long GlobalBytesReceived;
    }

    public enum ClientState
    {
        Disconnected,
        Handshaking,
        Handshaked,
        Streaming,
        Failed
    }

    public class MockVoiceClient
    {
        public int ClientId { get; }
        public int RoomId { get; }
        private readonly string _serverIp;
        private readonly int _serverPort;
        private readonly int _packetIntervalMs;
        private readonly int _payloadSize;

        private readonly Socket _socket;
        private ClientState _state = ClientState.Disconnected;
        public ClientState State => _state;

        private long _clockOffset; // theta
        private uint _sequenceNumber;

        private readonly LatencyHistogram _globalHistogram;
        private readonly ConcurrentDictionary<int, PacketTracker> _peerTrackers = new();
        public ConcurrentDictionary<int, PacketTracker> PeerTrackers => _peerTrackers;

        private readonly GlobalCounters _globalCounters;

        private Task? _sendTask;
        private Task? _receiveTask;

        public MockVoiceClient(
            int clientId,
            int roomId,
            string serverIp,
            int serverPort,
            int packetIntervalMs,
            int payloadSize,
            LatencyHistogram globalHistogram,
            GlobalCounters globalCounters)
        {
            ClientId = clientId;
            RoomId = roomId;
            _serverIp = serverIp;
            _serverPort = serverPort;
            _packetIntervalMs = packetIntervalMs;
            _payloadSize = payloadSize;
            _globalHistogram = globalHistogram;
            _globalCounters = globalCounters;

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.ReceiveBufferSize = 1024 * 1024;
            _socket.SendBufferSize = 1024 * 1024;
        }

        public async Task<bool> StartAsync(CancellationToken ct)
        {
            try
            {
                _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client {ClientId}] Failed to bind socket: {ex.Message}");
                _state = ClientState.Failed;
                return false;
            }

            bool connected = await HandshakeAndJoinRoomAsync(ct);
            if (!connected)
            {
                return false;
            }

            // Start parallel receive and send loops
            _receiveTask = RunReceiveLoopAsync(ct);
            _sendTask = RunPreciseSendLoopAsync(ct);

            return true;
        }

        public void Stop()
        {
            _state = ClientState.Disconnected;
            try
            {
                _socket.Close();
            }
            catch
            {
                // Ignore
            }
        }

        private async Task<bool> HandshakeAndJoinRoomAsync(CancellationToken ct)
        {
            _state = ClientState.Handshaking;
            EndPoint serverEP = new IPEndPoint(IPAddress.Parse(_serverIp), _serverPort);
            byte[] buffer = new byte[VoicePacketHeader.HeaderSize + 16];

            // 1. Handshake Loop
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                long t0 = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                var handshakeHeader = new VoicePacketHeader(
                    packetType: 1,
                    roomId: 0,
                    clientId: ClientId,
                    sequenceNumber: 0,
                    sendTimestamp: t0,
                    payloadLength: 0
                );

                PacketSerializer.TrySerializeHeader(handshakeHeader, buffer);

                try
                {
                    await _socket.SendToAsync(new ReadOnlyMemory<byte>(buffer, 0, VoicePacketHeader.HeaderSize), SocketFlags.None, serverEP, ct);

                    Task<SocketReceiveFromResult> receiveTask = _socket.ReceiveFromAsync(new Memory<byte>(buffer), SocketFlags.None, serverEP).AsTask();
                    Task delayTask = Task.Delay(1000, ct);

                    Task completedTask = await Task.WhenAny(receiveTask, delayTask);
                    if (completedTask == receiveTask)
                    {
                        var result = await receiveTask;
                        long t1 = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

                        if (result.ReceivedBytes >= VoicePacketHeader.HeaderSize)
                        {
                            if (PacketSerializer.TryDeserializeHeader(buffer.AsSpan(0, result.ReceivedBytes), out var ackHeader))
                            {
                                if (ackHeader.PacketType == 1 && ackHeader.PayloadLength >= 16)
                                {
                                    long s0 = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(VoicePacketHeader.HeaderSize, 8));
                                    long s1 = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(VoicePacketHeader.HeaderSize + 8, 8));

                                    long rtt = (t1 - t0) - (s1 - s0);
                                    _clockOffset = ((s0 - t0) + (s1 - t1)) / 2;

                                    _state = ClientState.Handshaked;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Retry on network error
                }
            }

            if (_state != ClientState.Handshaked)
            {
                _state = ClientState.Failed;
                return false;
            }

            // 2. Room Join Loop
            var joinHeader = new VoicePacketHeader(
                packetType: 2,
                roomId: RoomId,
                clientId: ClientId,
                sequenceNumber: 0,
                sendTimestamp: DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond,
                payloadLength: 0
            );

            PacketSerializer.TrySerializeHeader(joinHeader, buffer);

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await _socket.SendToAsync(new ReadOnlyMemory<byte>(buffer, 0, VoicePacketHeader.HeaderSize), SocketFlags.None, serverEP, ct);

                    Task<SocketReceiveFromResult> receiveTask = _socket.ReceiveFromAsync(new Memory<byte>(buffer), SocketFlags.None, serverEP).AsTask();
                    Task delayTask = Task.Delay(1000, ct);

                    Task completedTask = await Task.WhenAny(receiveTask, delayTask);
                    if (completedTask == receiveTask)
                    {
                        var result = await receiveTask;
                        if (result.ReceivedBytes >= VoicePacketHeader.HeaderSize)
                        {
                            if (PacketSerializer.TryDeserializeHeader(buffer.AsSpan(0, result.ReceivedBytes), out var ackHeader))
                            {
                                if (ackHeader.PacketType == 2 && ackHeader.PayloadLength >= 4)
                                {
                                    int success = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(VoicePacketHeader.HeaderSize, 4));
                                    if (success == 1)
                                    {
                                        _state = ClientState.Streaming;
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Retry
                }
            }

            _state = ClientState.Failed;
            return false;
        }

        private async Task RunPreciseSendLoopAsync(CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            long intervalTicks = _packetIntervalMs * TimeSpan.TicksPerMillisecond;
            long nextTick = stopwatch.ElapsedTicks;

            byte[] sendBuffer = new byte[VoicePacketHeader.HeaderSize + _payloadSize];
            EndPoint serverEP = new IPEndPoint(IPAddress.Parse(_serverIp), _serverPort);

            // Populate dummy payload data
            for (int i = 0; i < _payloadSize; i++)
            {
                sendBuffer[VoicePacketHeader.HeaderSize + i] = (byte)(i % 256);
            }

            while (!ct.IsCancellationRequested && _state == ClientState.Streaming)
            {
                nextTick += intervalTicks;
                long currentTicks = stopwatch.ElapsedTicks;
                long waitTicks = nextTick - currentTicks;

                if (waitTicks > 0)
                {
                    int delayMs = (int)(waitTicks / TimeSpan.TicksPerMillisecond);
                    if (delayMs > 2)
                    {
                        try
                        {
                            await Task.Delay(delayMs - 1, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }

                    while (stopwatch.ElapsedTicks < nextTick)
                    {
                        Thread.SpinWait(1);
                    }
                }

                // Prepare SendTimestamp = local timestamp + clockOffset (server time normalized)
                long localTs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                long serverNormalizedSendTimestamp = localTs + _clockOffset;
                uint seq = _sequenceNumber++;

                var audioHeader = new VoicePacketHeader(
                    packetType: 3,
                    roomId: RoomId,
                    clientId: ClientId,
                    sequenceNumber: seq,
                    sendTimestamp: serverNormalizedSendTimestamp,
                    payloadLength: _payloadSize
                );

                PacketSerializer.TrySerializeHeader(audioHeader, sendBuffer);

                try
                {
                    await _socket.SendToAsync(
                        new ReadOnlyMemory<byte>(sendBuffer),
                        SocketFlags.None,
                        serverEP,
                        ct
                    );

                    Interlocked.Increment(ref _globalCounters.GlobalPacketsSent);
                    Interlocked.Add(ref _globalCounters.GlobalBytesSent, sendBuffer.Length);
                }
                catch (SocketException)
                {
                    // Handle transport disruption, client continues
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Ignore transient errors
                }
            }
        }

        private async Task RunReceiveLoopAsync(CancellationToken ct)
        {
            byte[] receiveBuffer = new byte[2048];
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

            while (!ct.IsCancellationRequested && _state == ClientState.Streaming)
            {
                try
                {
                    SocketReceiveFromResult result = await _socket.ReceiveFromAsync(
                        new Memory<byte>(receiveBuffer),
                        SocketFlags.None,
                        remoteEP,
                        ct
                    );

                    long tr = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                    int length = result.ReceivedBytes;

                    if (length < VoicePacketHeader.HeaderSize)
                    {
                        continue;
                    }

                    if (PacketSerializer.TryDeserializeHeader(receiveBuffer.AsSpan(0, length), out var header))
                    {
                        if (header.PacketType == 3) // Audio
                        {
                            long receiverServerTime = tr + _clockOffset;
                            double oneWayLatency = receiverServerTime - header.SendTimestamp;

                            if (oneWayLatency < 0)
                            {
                                oneWayLatency = 0; // Guard against minor OS clock variance
                            }

                            _globalHistogram.RecordLatency(oneWayLatency);

                            var tracker = _peerTrackers.GetOrAdd(header.ClientId, _ => new PacketTracker());
                            tracker.RecordPacket(header.SequenceNumber);

                            Interlocked.Increment(ref _globalCounters.GlobalPacketsReceived);
                            Interlocked.Add(ref _globalCounters.GlobalBytesReceived, length);
                        }
                    }
                }
                catch (SocketException)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    break;
                }
            }
        }
    }
}
