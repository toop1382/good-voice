using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;

namespace Server.Core
{
    public class VoiceRoom
    {
        public int RoomId { get; }
        private readonly ConcurrentDictionary<int, ClientSession> _clients = new();

        public VoiceRoom(int roomId)
        {
            RoomId = roomId;
        }

        public ICollection<ClientSession> Clients => _clients.Values;
        public int ClientCount => _clients.Count;

        public bool TryAdd(ClientSession session) => _clients.TryAdd(session.ClientId, session);

        public bool TryRemove(int clientId, out ClientSession? session) =>
            _clients.TryRemove(clientId, out session);

        public bool Contains(int clientId) => _clients.ContainsKey(clientId);

        /// <summary>
        /// Broadcast a packet to every client in the room except the sender.
        /// Uses each client's ISendChannel — works for UDP, TCP, and WebSocket clients equally.
        /// Allocation-free inner loop; send errors on individual clients are swallowed.
        /// </summary>
        public void Broadcast(byte[] packetBuffer, int length, int senderClientId)
        {
            foreach (var kvp in _clients)
            {
                ClientSession client = kvp.Value;
                if (client.ClientId == senderClientId) continue;

                ISendChannel? channel = client.SendChannel;
                if (channel == null || !channel.IsAlive) continue;

                channel.Send(packetBuffer, 0, length);
            }
        }
    }
}
