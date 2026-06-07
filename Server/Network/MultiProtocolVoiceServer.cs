using System;
using System.Collections.Generic;
using Server.Core;

namespace Server.Network
{
    /// <summary>
    /// Composes multiple IVoiceServer instances and starts/stops them together.
    /// All three servers share the same RoomManager so clients on different
    /// protocols can coexist in the same voice room.
    /// </summary>
    public class MultiProtocolVoiceServer
    {
        private readonly List<IVoiceServer> _servers = new();

        public MultiProtocolVoiceServer Add(IVoiceServer server)
        {
            _servers.Add(server);
            return this;
        }

        public void StartAll()
        {
            foreach (var s in _servers)
            {
                try { s.Start(); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MultiProtocol] Failed to start {s.ProtocolName} server: {ex.Message}");
                }
            }
        }

        public void StopAll()
        {
            foreach (var s in _servers)
            {
                try { s.Stop(); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MultiProtocol] Error stopping {s.ProtocolName} server: {ex.Message}");
                }
            }
        }
    }
}
