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
    /// UDP voice transport — lowest latency, fire-and-forget.
    /// Refactored from UdpVoiceClient to implement IVoiceTransport.
    /// </summary>
    public class UdpVoiceTransport : IVoiceTransport
    {
        private const int HeaderSize    = 28;
        private const int MaxPacketSize = 2048;

        public string Protocol      => "UDP";
        public bool   IsConnected   { get; private set; }
        public int    ClientId      { get; }
        public int    RoomId        { get; private set; }

        public event Action<int, byte[], int> OnAudioReceived;
        public event Action<double>           OnHandshakeAck;
        public event Action<bool>             OnRoomJoinAck;
        public event Action                   OnDisconnected;
        public event Action<double>           OnHeartbeatAck;

        private readonly string _host;
        private readonly int    _port;
        private Socket          _socket;
        private IPEndPoint      _serverEP;
        private CancellationTokenSource _cts;
        private uint  _outSeq;
        private long  _lastHandshakeMs;

        public UdpVoiceTransport(string host, int port, int clientId)
        {
            _host = host; _port = port; ClientId = clientId;
        }

        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(_host);
                _serverEP = new IPEndPoint(addrs[0], _port);
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _socket.ReceiveBufferSize = 512 * 1024;
                _socket.SendBufferSize    = 512 * 1024;
                _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
                await SendHandshakeAsync();
                IsConnected = true;
                Debug.Log($"[UdpTransport] Connected to {_host}:{_port} as client {ClientId}");
                return true;
            }
            catch (Exception ex) { Debug.LogError($"[UdpTransport] Connect failed: {ex.Message}"); return false; }
        }

        public void JoinRoom(int roomId)
        {
            RoomId = roomId;
            byte[] buf = new byte[HeaderSize];
            WriteHeader(buf, 2, roomId, ClientId, _outSeq++, NowMs(), 0);
            TrySend(buf, HeaderSize);
        }

        public void SendAudio(byte[] opus, int len)
        {
            if (!IsConnected) return;
            byte[] buf = new byte[HeaderSize + len];
            WriteHeader(buf, 3, RoomId, ClientId, _outSeq++, NowMs(), len);
            Buffer.BlockCopy(opus, 0, buf, HeaderSize, len);
            TrySend(buf, buf.Length);
        }

        public void SendHeartbeat()
        {
            if (!IsConnected) return;
            byte[] buf = new byte[HeaderSize];
            WriteHeader(buf, 4, RoomId, ClientId, _outSeq++, NowMs(), 0);
            TrySend(buf, buf.Length);
        }

        public void Disconnect()
        {
            IsConnected = false;
            _cts?.Cancel();
            try { _socket?.Close(); } catch { }
        }

        private async Task SendHandshakeAsync()
        {
            byte[] buf = new byte[HeaderSize];
            _lastHandshakeMs = NowMs();
            WriteHeader(buf, 1, 0, ClientId, 0, _lastHandshakeMs, 0);
            await _socket.SendToAsync(new ArraySegment<byte>(buf), SocketFlags.None, _serverEP);
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            byte[] buf = new byte[MaxPacketSize];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var r = await _socket.ReceiveFromAsync(new ArraySegment<byte>(buf), SocketFlags.None, remote);
                    ProcessPacket(buf, r.ReceivedBytes);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex) { if (!ct.IsCancellationRequested) Debug.LogWarning($"[UdpTransport] Recv: {ex.Message}"); break; }
                catch (Exception ex) { Debug.LogError($"[UdpTransport] Recv error: {ex.Message}"); }
            }
            IsConnected = false;
            OnDisconnected?.Invoke();
        }

        private void ProcessPacket(byte[] buf, int len) => ParsePacket(buf, len);

        private void ParsePacket(byte[] buf, int len)
        {
            if (len < HeaderSize) return;
            int pktType    = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
            int senderId   = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8, 4));
            int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(24, 4));
            if (payloadLen < 0 || HeaderSize + payloadLen > len) return;

            switch (pktType)
            {
                case 1:
                    double rtt = NowMs() - _lastHandshakeMs;
                    OnHandshakeAck?.Invoke(rtt);
                    break;
                case 2:
                    bool ok = payloadLen >= 4 && BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(HeaderSize, 4)) == 1;
                    OnRoomJoinAck?.Invoke(ok);
                    break;
                case 3:
                    if (payloadLen > 0 && senderId != ClientId)
                    {
                        byte[] opus = new byte[payloadLen];
                        Buffer.BlockCopy(buf, HeaderSize, opus, 0, payloadLen);
                        OnAudioReceived?.Invoke(senderId, opus, payloadLen);
                    }
                    break;
                case 4:
                    long heartbeatTs = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16, 8));
                    double heartbeatRtt = NowMs() - heartbeatTs;
                    OnHeartbeatAck?.Invoke(heartbeatRtt);
                    break;
            }
        }

        private void TrySend(byte[] data, int len)
        {
            try { _socket.SendTo(data, 0, len, SocketFlags.None, _serverEP); }
            catch (SocketException ex) { Debug.LogWarning($"[UdpTransport] Send: {ex.Message}"); }
        }

        private static void WriteHeader(byte[] buf, int type, int room, int client, uint seq, long ts, int payloadLen)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4),  type);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4),  room);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4),  client);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), seq);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16, 8), ts);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(24, 4), payloadLen);
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public void Dispose() { Disconnect(); _cts?.Dispose(); _socket?.Dispose(); }
    }
}
