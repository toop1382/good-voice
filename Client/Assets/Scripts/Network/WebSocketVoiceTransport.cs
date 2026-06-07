using System;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Client.Network
{
    /// <summary>
    /// WebSocket voice transport — works through HTTP proxies and firewalls.
    /// Each WebSocket message = one voice packet (no length prefix needed).
    ///
    /// Ideal for WebGL Unity builds and browser-based clients.
    /// Connects to ws://host:{port}/voice/
    /// Uses ClientWebSocket from System.Net.WebSockets (available in Unity 2021.2+).
    /// </summary>
    public class WebSocketVoiceTransport : IVoiceTransport
    {
        private const int HeaderSize    = 28;
        private const int MaxPacketSize = 4096;

        public string Protocol    => "WebSocket";
        public bool   IsConnected { get; private set; }
        public int    ClientId    { get; }
        public int    RoomId      { get; private set; }

        public event Action<int, byte[], int> OnAudioReceived;
        public event Action<double>           OnHandshakeAck;
        public event Action<bool>             OnRoomJoinAck;
        public event Action                   OnDisconnected;
        public event Action                   OnHeartbeatAck;

        private readonly string _url; // e.g. "ws://127.0.0.1:50007/voice/"
        private ClientWebSocket      _ws;
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private uint  _outSeq;
        private long  _lastHandshakeMs;

        public WebSocketVoiceTransport(string host, int port, int clientId)
        {
            _url = $"ws://{host}:{port}/voice/";
            ClientId = clientId;
        }

        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            try
            {
                _ws = new ClientWebSocket();
                _ws.Options.SetBuffer(receiveBufferSize: 256 * 1024, sendBufferSize: 256 * 1024);
                await _ws.ConnectAsync(new Uri(_url), ct);
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
                await SendHandshakeAsync();
                IsConnected = true;
                Debug.Log($"[WsTransport] Connected to {_url} as client {ClientId}");
                return true;
            }
            catch (Exception ex) { Debug.LogError($"[WsTransport] Connect failed: {ex.Message}"); return false; }
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
            try
            {
                if (_ws?.State == WebSocketState.Open)
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                       .GetAwaiter().GetResult();
            }
            catch { }
        }

        private async Task SendHandshakeAsync()
        {
            _lastHandshakeMs = NowMs();
            byte[] buf = new byte[HeaderSize];
            WriteHeader(buf, 1, 0, ClientId, 0, _lastHandshakeMs, 0);
            await SendAsync(buf, HeaderSize);
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            byte[] buf = new byte[MaxPacketSize];
            try
            {
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Binary && result.EndOfMessage)
                        ParsePacket(buf, result.Count);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex) { Debug.LogWarning($"[WsTransport] Recv: {ex.Message}"); }
            catch (Exception ex)          { Debug.LogError($"[WsTransport] Recv error: {ex.Message}"); }

            IsConnected = false;
            OnDisconnected?.Invoke();
        }

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
                    OnHandshakeAck?.Invoke(NowMs() - _lastHandshakeMs);
                    break;
                case 2:
                    bool ok = payloadLen >= 4 && BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(HeaderSize, 4)) == 1;
                    OnRoomJoinAck?.Invoke(ok);
                    break;
                case 3 when payloadLen > 0 && senderId != ClientId:
                    byte[] opus = new byte[payloadLen];
                    Buffer.BlockCopy(buf, HeaderSize, opus, 0, payloadLen);
                    OnAudioReceived?.Invoke(senderId, opus, payloadLen);
                    break;
                case 4:
                    OnHeartbeatAck?.Invoke();
                    break;
            }
        }

        private void TrySend(byte[] data, int len)
        {
            _ = Task.Run(async () =>
            {
                try { await SendAsync(data, len); }
                catch (Exception ex) { Debug.LogWarning($"[WsTransport] Send: {ex.Message}"); }
            });
        }

        private async Task SendAsync(byte[] data, int len)
        {
            if (_ws?.State != WebSocketState.Open) return;
            await _sendGate.WaitAsync();
            try
            {
                await _ws.SendAsync(new ArraySegment<byte>(data, 0, len),
                    WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
            }
            finally { _sendGate.Release(); }
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

        public void Dispose() { Disconnect(); _cts?.Dispose(); _ws?.Dispose(); _sendGate.Dispose(); }
    }
}
