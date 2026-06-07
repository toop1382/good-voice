using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Client.Network
{
    /// <summary>
    /// Async UDP client for the voice chat server.
    /// Handles: Handshake (Type=1), RoomJoin (Type=2), Audio send (Type=3), and audio receive.
    ///
    /// Packet format (28-byte header + payload):
    ///   [0-3]   PacketType (int32)
    ///   [4-7]   RoomId     (int32)
    ///   [8-11]  ClientId   (int32)
    ///   [12-15] SeqNumber  (uint32)
    ///   [16-23] Timestamp  (int64, ms)
    ///   [24-27] PayloadLen (int32)
    ///   [28+]   Opus payload
    /// </summary>
    public class UdpVoiceClient : IDisposable
    {
        private const int HeaderSize = 28;
        private const int MaxPacketSize = 2048;

        // --- Events ---
        /// <summary>Fired on the receive thread with (senderId, opusPacket, opusLength).</summary>
        public event Action<int, byte[], int> OnAudioReceived;
        /// <summary>Fired when a Handshake ACK is received with the measured RTT.</summary>
        public event Action<double> OnHandshakeAck;
        /// <summary>Fired when RoomJoin ACK is received with success flag.</summary>
        public event Action<bool> OnRoomJoinAck;
        /// <summary>Fired when the socket disconnects or errors out.</summary>
        public event Action OnDisconnected;

        // --- State ---
        public int ClientId { get; }
        public int RoomId { get; private set; }
        public bool IsConnected { get; private set; }

        private readonly string _serverHost;
        private readonly int _serverPort;
        private Socket _socket;
        private IPEndPoint _serverEndPoint;
        private CancellationTokenSource _cts;

        private uint _outboundSeq;

        // RTT calculation
        private long _lastHandshakeSendMs;

        public UdpVoiceClient(string serverHost, int serverPort, int clientId)
        {
            _serverHost = serverHost;
            _serverPort = serverPort;
            ClientId = clientId;
        }

        /// <summary>Connect and perform handshake with the server.</summary>
        public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(_serverHost);
                _serverEndPoint = new IPEndPoint(addresses[0], _serverPort);

                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _socket.ReceiveBufferSize = 512 * 1024;
                _socket.SendBufferSize = 512 * 1024;
                _socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);

                // Send handshake
                await SendHandshakeAsync();
                IsConnected = true;
                Debug.Log($"[UdpVoiceClient] Connected to {_serverHost}:{_serverPort} as ClientId={ClientId}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UdpVoiceClient] Connect failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Join a voice room on the server.</summary>
        public void JoinRoom(int roomId)
        {
            RoomId = roomId;
            SendRoomJoin(roomId);
        }

        /// <summary>Send an encoded Opus audio packet.</summary>
        public void SendAudio(byte[] opusData, int opusLength)
        {
            if (!IsConnected || _socket == null) return;

            int totalSize = HeaderSize + opusLength;
            byte[] buf = new byte[totalSize];

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            uint seq = _outboundSeq++;

            // Write header and payload using array-based helpers (no Span in this sync method)
            WriteHeader(buf, packetType: 3, RoomId, ClientId, seq, now, opusLength);
            Buffer.BlockCopy(opusData, 0, buf, HeaderSize, opusLength);

            try
            {
                _socket.SendTo(buf, 0, totalSize, SocketFlags.None, _serverEndPoint);
            }
            catch (SocketException ex)
            {
                Debug.LogWarning($"[UdpVoiceClient] SendAudio error: {ex.Message}");
            }
        }

        // --- Private ---

        private async Task SendHandshakeAsync()
        {
            byte[] buf = new byte[HeaderSize];
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastHandshakeSendMs = now;
            WriteHeader(buf, packetType: 1, roomId: 0, ClientId, seq: 0, now, payloadLen: 0);
            await _socket.SendToAsync(new ArraySegment<byte>(buf), SocketFlags.None, _serverEndPoint);
        }

        private void SendRoomJoin(int roomId)
        {
            byte[] buf = new byte[HeaderSize];
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            WriteHeader(buf, packetType: 2, roomId, ClientId, _outboundSeq++, now, payloadLen: 0);
            try
            {
                _socket.SendTo(buf, 0, HeaderSize, SocketFlags.None, _serverEndPoint);
            }
            catch (SocketException ex)
            {
                Debug.LogWarning($"[UdpVoiceClient] SendRoomJoin error: {ex.Message}");
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            byte[] recvBuf = new byte[MaxPacketSize];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    SocketReceiveFromResult result = await _socket.ReceiveFromAsync(
                        new ArraySegment<byte>(recvBuf),
                        SocketFlags.None,
                        remote
                    );

                    int received = result.ReceivedBytes;
                    if (received < HeaderSize) continue;

                    // Parse in a synchronous helper — ReadOnlySpan<T> cannot be a
                    // local variable in an async method (C# compiler restriction).
                    ParseInboundPacket(recvBuf, received);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    if (!token.IsCancellationRequested)
                        Debug.LogWarning($"[UdpVoiceClient] ReceiveLoop SocketException: {ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[UdpVoiceClient] ReceiveLoop error: {ex.Message}");
                }
            }

            IsConnected = false;
            OnDisconnected?.Invoke();
        }

        /// <summary>
        /// Parse a raw inbound packet. Must be a synchronous method so that
        /// ReadOnlySpan locals are stack-safe (no async state machine).
        /// </summary>
        private void ParseInboundPacket(byte[] buf, int received)
        {
            int pktType    = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(buf, 0,  4));
            int senderId   = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(buf, 8,  4));
            int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(buf, 24, 4));

            if (payloadLen < 0 || HeaderSize + payloadLen > received) return;

            switch (pktType)
            {
                case 1: // Handshake ACK
                    double rtt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastHandshakeSendMs;
                    OnHandshakeAck?.Invoke(rtt);
                    break;

                case 2: // RoomJoin ACK
                    bool success = payloadLen >= 4 &&
                        BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(buf, HeaderSize, 4)) == 1;
                    OnRoomJoinAck?.Invoke(success);
                    break;

                case 3: // Audio from another client
                    if (payloadLen > 0 && senderId != ClientId)
                    {
                        byte[] opus = new byte[payloadLen];
                        Buffer.BlockCopy(buf, HeaderSize, opus, 0, payloadLen);
                        OnAudioReceived?.Invoke(senderId, opus, payloadLen);
                    }
                    break;
            }
        }

        private static void WriteHeader(byte[] buf, int packetType, int roomId, int clientId,
            uint seq, long timestamp, int payloadLen)
        {
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(buf, 0,  4), packetType);
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(buf, 4,  4), roomId);
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(buf, 8,  4), clientId);
            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(buf, 12, 4), seq);
            BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(buf, 16, 8), timestamp);
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(buf, 24, 4), payloadLen);
        }

        public void Disconnect()
        {
            IsConnected = false;
            _cts?.Cancel();
            try { _socket?.Close(); } catch { }
        }

        public void Dispose()
        {
            Disconnect();
            _cts?.Dispose();
            _socket?.Dispose();
        }
    }
}
