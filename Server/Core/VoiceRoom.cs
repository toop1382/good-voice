using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using Shared.Models;
using Shared.Serialization;

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

        private readonly object _joinLock = new object();

        public void BroadcastUserJoined(ClientSession joinedSession)
        {
            lock (_joinLock)
            {
                byte[] metadataBytes = Encoding.UTF8.GetBytes(joinedSession.Metadata ?? string.Empty);
            byte[] packetBuffer = new byte[VoicePacketHeader.HeaderSize + metadataBytes.Length];

            var header = new VoicePacketHeader(
                packetType: 5,
                roomId: RoomId,
                clientId: joinedSession.ClientId,
                sequenceNumber: 0,
                sendTimestamp: DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond,
                payloadLength: metadataBytes.Length
            );

            PacketSerializer.TrySerializeHeader(header, packetBuffer.AsSpan());
            Buffer.BlockCopy(metadataBytes, 0, packetBuffer, VoicePacketHeader.HeaderSize, metadataBytes.Length);

            Broadcast(packetBuffer, packetBuffer.Length, joinedSession.ClientId);
            }
        }

        public void BroadcastUserLeft(int clientId)
        {
            byte[] packetBuffer = new byte[VoicePacketHeader.HeaderSize];

            var header = new VoicePacketHeader(
                packetType: 6,
                roomId: RoomId,
                clientId: clientId,
                sequenceNumber: 0,
                sendTimestamp: DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond,
                payloadLength: 0
            );

            PacketSerializer.TrySerializeHeader(header, packetBuffer.AsSpan());

            Broadcast(packetBuffer, packetBuffer.Length, clientId);
        }

        public void SendExistingUsersTo(ClientSession newClient)
        {
            ISendChannel? channel = newClient.SendChannel;
            if (channel == null || !channel.IsAlive) return;

            foreach (var kvp in _clients)
            {
                ClientSession existingClient = kvp.Value;
                if (existingClient.ClientId == newClient.ClientId) continue;

                byte[] metadataBytes = Encoding.UTF8.GetBytes(existingClient.Metadata ?? string.Empty);
                byte[] packetBuffer = new byte[VoicePacketHeader.HeaderSize + metadataBytes.Length];

                var header = new VoicePacketHeader(
                    packetType: 5,
                    roomId: RoomId,
                    clientId: existingClient.ClientId,
                    sequenceNumber: 0,
                    sendTimestamp: DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond,
                    payloadLength: metadataBytes.Length
                );

                PacketSerializer.TrySerializeHeader(header, packetBuffer.AsSpan());
                Buffer.BlockCopy(metadataBytes, 0, packetBuffer, VoicePacketHeader.HeaderSize, metadataBytes.Length);

                channel.Send(packetBuffer, 0, packetBuffer.Length);
            }
        }
    }
}
