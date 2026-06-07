using System;
using System.Threading;
using System.Threading.Tasks;

namespace Client.Network
{
    /// <summary>
    /// Protocol-agnostic Unity voice transport interface.
    /// Implementations: UdpVoiceTransport, TcpVoiceTransport, WebSocketVoiceTransport.
    /// </summary>
    public interface IVoiceTransport : IDisposable
    {
        /// <summary>Human-readable protocol name for logging.</summary>
        string Protocol { get; }

        bool IsConnected { get; }
        int ClientId { get; }
        int RoomId { get; }

        // ── Events (fired on receive thread — marshal to main thread as needed) ──
        event Action<int, byte[], int> OnAudioReceived;    // (senderId, opusData, opusLength)
        event Action<double>           OnHandshakeAck;     // (rttMs)
        event Action<bool>             OnRoomJoinAck;      // (success)
        event Action                   OnDisconnected;
        event Action                   OnHeartbeatAck;

        // ── Control ──────────────────────────────────────────────────────
        Task<bool> ConnectAsync(CancellationToken cancellationToken = default);
        void JoinRoom(int roomId);
        void SendAudio(byte[] opusData, int opusLength);
        void SendHeartbeat();
        void Disconnect();
    }
}
