using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MockClient.Models;
using MockClient.Network;
using MockClient.Diagnostics;

namespace MockClient
{
    public class MockClient : IDisposable
    {
        private readonly int _clientId;
        private readonly MockClientUdpSocket _socket;
        private readonly MetricTracker _tracker;
        private int _roomId;
        private uint _sequenceCounter;
        private TaskCompletionSource<bool>? _handshakeTcs;
        private CancellationTokenSource? _streamingCts;

        public int ClientId => _clientId;
        public int RoomId => _roomId;
        public MetricTracker Tracker => _tracker;

        public DegradationConfig? Degradation
        {
            get => _socket.Degradation;
            set => _socket.Degradation = value;
        }

        public MockClient(int clientId, IPEndPoint serverEndPoint)
        {
            _clientId = clientId;
            _socket = new MockClientUdpSocket(serverEndPoint);
            _tracker = new MetricTracker();
            _socket.OnPacketReceived += HandlePacketReceived;
        }

        public async Task<bool> ConnectAsync(int maxRetries = 3, int timeoutMs = 1500)
        {
            _socket.Start();

            for (int retry = 0; retry < maxRetries; retry++)
            {
                _handshakeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var stopwatch = Stopwatch.StartNew();

                var handshakePacket = new VoicePacket
                {
                    Type = PacketType.Handshake,
                    RoomId = 0,
                    ClientId = _clientId,
                    SequenceNumber = 0,
                    SendTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    PayloadLength = 0,
                    Payload = Array.Empty<byte>()
                };

                await _socket.SendPacketAsync(handshakePacket);

                var delayTask = Task.Delay(timeoutMs);
                var completedTask = await Task.WhenAny(_handshakeTcs.Task, delayTask);

                if (completedTask == _handshakeTcs.Task && await _handshakeTcs.Task)
                {
                    stopwatch.Stop();
                    _tracker.RecordRttSample(stopwatch.ElapsedMilliseconds);
                    return true;
                }
            }

            return false;
        }

        public async Task JoinRoomAsync(int roomId)
        {
            _roomId = roomId;
            var joinPacket = new VoicePacket
            {
                Type = PacketType.RoomJoin,
                RoomId = roomId,
                ClientId = _clientId,
                SequenceNumber = ++_sequenceCounter,
                SendTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PayloadLength = 0,
                Payload = Array.Empty<byte>()
            };
            await _socket.SendPacketAsync(joinPacket);
        }

        public void StartStreamingAudio(int intervalMs = 20, int payloadSize = 120)
        {
            _streamingCts?.Cancel();
            _streamingCts = new CancellationTokenSource();
            var token = _streamingCts.Token;

            Task.Run(async () =>
            {
                var random = new Random();
                var dummyPayload = new byte[payloadSize];
                random.NextBytes(dummyPayload);

                var stopwatch = Stopwatch.StartNew();
                long intervalTicks = intervalMs * TimeSpan.TicksPerMillisecond;
                long nextTick = stopwatch.ElapsedTicks;

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var audioPacket = new VoicePacket
                        {
                            Type = PacketType.Audio,
                            RoomId = _roomId,
                            ClientId = _clientId,
                            SequenceNumber = ++_sequenceCounter,
                            SendTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            PayloadLength = payloadSize,
                            Payload = dummyPayload
                        };

                        await _socket.SendPacketAsync(audioPacket);

                        nextTick += intervalTicks;
                        long currentTicks = stopwatch.ElapsedTicks;
                        long waitTicks = nextTick - currentTicks;

                        if (waitTicks > 0)
                        {
                            int delayMs = (int)(waitTicks / TimeSpan.TicksPerMillisecond);
                            if (delayMs > 30)
                            {
                                await Task.Delay(delayMs - 10, token);
                            }

                            while (stopwatch.ElapsedTicks < nextTick)
                            {
                                if (token.IsCancellationRequested) break;
                                Thread.SpinWait(10);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Ignore transient network errors during streaming
                    }
                }
            }, token);
        }

        public void StopStreamingAudio()
        {
            _streamingCts?.Cancel();
        }

        private void HandlePacketReceived(VoicePacket packet, IPEndPoint sender)
        {
            long receiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (packet.Type == PacketType.Handshake)
            {
                if (packet.ClientId == _clientId)
                {
                    _handshakeTcs?.TrySetResult(true);
                }
            }
            else if (packet.Type == PacketType.Audio)
            {
                _tracker.RecordPacketReceived(packet.SequenceNumber, packet.SendTimestamp, receiveTimestamp);
            }
        }

        public void Dispose()
        {
            _streamingCts?.Cancel();
            _socket.Dispose();
        }
    }
}
