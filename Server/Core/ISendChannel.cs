using System;

namespace Server.Core
{
    /// <summary>
    /// Protocol-agnostic send abstraction.
    /// Each connected client owns one ISendChannel regardless of transport (UDP/TCP/WebSocket).
    /// Implementations must be thread-safe — Broadcast calls it from multiple worker threads.
    /// </summary>
    public interface ISendChannel
    {
        /// <summary>The transport protocol name for logging.</summary>
        string Protocol { get; }

        /// <summary>
        /// Send raw bytes to the remote client.
        /// Implementations must not throw on transient errors — log and swallow instead.
        /// </summary>
        void Send(byte[] data, int offset, int length);

        /// <summary>Convenience overload for sending an entire buffer.</summary>
        void Send(byte[] data, int length) => Send(data, 0, length);

        /// <summary>Returns true if the underlying connection is still alive.</summary>
        bool IsAlive { get; }
    }
}
