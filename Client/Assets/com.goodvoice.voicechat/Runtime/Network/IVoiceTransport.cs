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
        event Action<int, byte[], int, uint, long> OnAudioReceived;    // (senderId, opusData, opusLength, sequenceNumber, sendTimestamp)
        event Action<double>           OnHandshakeAck;     // (rttMs)
        event Action<bool>             OnRoomJoinAck;      // (success)
        event Action                   OnDisconnected;
        event Action<double>           OnHeartbeatAck;
        event Action<int, string>      OnUserJoined;       // (clientId, metadata)
        event Action<int>              OnUserLeft;         // (clientId)

        // ── Control ──────────────────────────────────────────────────────
        Task<bool> ConnectAsync(CancellationToken cancellationToken = default);
        void JoinRoom(int roomId, string metadata = "");
        void SendAudio(byte[] opusData, int opusLength);
        void SendHeartbeat();
        void Disconnect();
    }
}
