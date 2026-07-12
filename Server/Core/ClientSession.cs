using System;
using System.Net;
using System.Threading;

namespace Server.Core
{
    public class ClientSession
    {
        public int ClientId { get; }

        private volatile IPEndPoint _endPoint;
        public IPEndPoint EndPoint
        {
            get => _endPoint;
            set => _endPoint = value ?? throw new ArgumentNullException(nameof(value));
        }

        private volatile int _roomId;
        public int RoomId
        {
            get => _roomId;
            set => _roomId = value;
        }

        private long _lastActivityTicks;
        public long LastActivityTicks
        {
            get => Interlocked.Read(ref _lastActivityTicks);
            set => Interlocked.Exchange(ref _lastActivityTicks, value);
        }

        /// <summary>
        /// Protocol-specific send channel — set once at registration and never changed.
        /// For UDP: re-created per-packet using the latest remote endpoint.
        /// For TCP/WebSocket: set once at connection time.
        /// </summary>
        public volatile ISendChannel? SendChannel;

        public string Metadata { get; set; } = string.Empty;

        public ClientSession(int clientId, IPEndPoint endPoint, ISendChannel? sendChannel = null)
        {
            ClientId = clientId;
            _endPoint = endPoint ?? throw new ArgumentNullException(nameof(endPoint));
            _roomId = 0; // 0 = not in any room
            _lastActivityTicks = DateTime.UtcNow.Ticks;
            SendChannel = sendChannel;
        }
    }
}
