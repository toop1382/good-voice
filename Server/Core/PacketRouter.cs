using System;
using System.Buffers.Binary;
using System.Net;
using Shared.Models;
using Shared.Serialization;

namespace Server.Core
{
    /// <summary>
    /// Routes inbound voice packets to the appropriate handler.
    /// Protocol-agnostic — callers supply an ISendChannel for the sender
    /// so responses can be sent back over the originating transport (UDP/TCP/WS).
    /// </summary>
    public class PacketRouter
    {
        private readonly RoomManager _roomManager;

        public PacketRouter(RoomManager roomManager)
        {
            _roomManager = roomManager;
        }

        /// <summary>
        /// Route a single packet.
        /// </summary>
        /// <param name="buffer">Raw packet bytes (header + payload).</param>
        /// <param name="length">Valid bytes in buffer.</param>
        /// <param name="senderEndPoint">Logical endpoint of the sender.</param>
        /// <param name="senderChannel">Send channel back to the originating client.</param>
        /// <param name="serverReceiveTimestampMs">Server-side receive wall-clock time.</param>
        public void RoutePacket(
            byte[] buffer,
            int length,
            IPEndPoint senderEndPoint,
            ISendChannel senderChannel,
            long serverReceiveTimestampMs)
        {
            if (length < VoicePacketHeader.HeaderSize) return;

            if (!PacketSerializer.TryDeserializeHeader(buffer.AsSpan(0, length), out var header))
                return;

            int actualPayloadSize = length - VoicePacketHeader.HeaderSize;
            if (header.PayloadLength < 0 || header.PayloadLength != actualPayloadSize || header.PayloadLength > 1500)
                return;

            switch (header.PacketType)
            {
                case 1: HandleHandshake(header, buffer, length, senderEndPoint, senderChannel, serverReceiveTimestampMs); break;
                case 2: HandleRoomJoin(header, buffer, length, senderEndPoint, senderChannel); break;
                case 3: HandleAudio(header, buffer, length, senderEndPoint); break;
                case 4: HandleHeartbeat(header, senderEndPoint, senderChannel); break;
            }
        }

        // ── Handshake ────────────────────────────────────────────────────
        private void HandleHandshake(
            in VoicePacketHeader header,
            byte[] buffer,
            int length,
            IPEndPoint senderEndPoint,
            ISendChannel senderChannel,
            long serverReceiveTimestampMs)
        {
            int clientId = header.ClientId;
            if (clientId == 0)
            {
                clientId = Random.Shared.Next(100000, 999999999);
            }
            var session = _roomManager.RegisterClient(clientId, senderEndPoint, senderChannel);
            if (header.PayloadLength > 0 && length >= VoicePacketHeader.HeaderSize + header.PayloadLength)
            {
                session.Metadata = System.Text.Encoding.UTF8.GetString(buffer, VoicePacketHeader.HeaderSize, header.PayloadLength);
            }

            long serverSendTimestampMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

            // ACK: Type=1, Payload=16 bytes (S0=server receive time, S1=server send time)
            byte[] ackBuf = new byte[VoicePacketHeader.HeaderSize + 16];
            var ackHeader = new VoicePacketHeader(
                packetType: 1, roomId: 0, clientId: clientId,
                sequenceNumber: 0, sendTimestamp: header.SendTimestamp, // echo client T0
                payloadLength: 16);

            PacketSerializer.TrySerializeHeader(ackHeader, ackBuf.AsSpan());
            BinaryPrimitives.WriteInt64LittleEndian(ackBuf.AsSpan(VoicePacketHeader.HeaderSize, 8), serverReceiveTimestampMs);
            BinaryPrimitives.WriteInt64LittleEndian(ackBuf.AsSpan(VoicePacketHeader.HeaderSize + 8, 8), serverSendTimestampMs);

            senderChannel.Send(ackBuf, ackBuf.Length);
            Console.WriteLine($"[{senderChannel.Protocol}] Client {clientId} handshake from {senderEndPoint}");
        }

        // ── Room Join ────────────────────────────────────────────────────
        private void HandleRoomJoin(
            in VoicePacketHeader header,
            byte[] buffer,
            int length,
            IPEndPoint senderEndPoint,
            ISendChannel senderChannel)
        {
            int clientId = header.ClientId;
            int roomId = header.RoomId;

            if (!_roomManager.TryGetSession(clientId, out var session) || session == null
                || !session.EndPoint.Equals(senderEndPoint))
                return;

            session.LastActivityTicks = DateTime.UtcNow.Ticks;

            if (header.PayloadLength > 0 && length >= VoicePacketHeader.HeaderSize + header.PayloadLength)
            {
                session.Metadata = System.Text.Encoding.UTF8.GetString(buffer, VoicePacketHeader.HeaderSize, header.PayloadLength);
            }

            bool success = _roomManager.JoinRoom(clientId, roomId, out var room);

            byte[] ackBuf = new byte[VoicePacketHeader.HeaderSize + 4];
            var ackHeader = new VoicePacketHeader(
                packetType: 2, roomId: roomId, clientId: clientId,
                sequenceNumber: 0,
                sendTimestamp: DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond,
                payloadLength: 4);

            PacketSerializer.TrySerializeHeader(ackHeader, ackBuf.AsSpan());
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(ackBuf.AsSpan(VoicePacketHeader.HeaderSize, 4), success ? 1 : 0);

            senderChannel.Send(ackBuf, ackBuf.Length);

            if (success && room != null)
            {
                room.SendExistingUsersTo(session);
                room.BroadcastUserJoined(session);
            }
            Console.WriteLine($"[{senderChannel.Protocol}] Client {clientId} joined room {roomId} (success={success})");
        }

        // ── Audio ────────────────────────────────────────────────────────
        private void HandleAudio(
            in VoicePacketHeader header,
            byte[] buffer,
            int length,
            IPEndPoint senderEndPoint)
        {
            int clientId = header.ClientId;

            if (!_roomManager.TryGetSession(clientId, out var session) || session == null
                || !session.EndPoint.Equals(senderEndPoint))
                return;

            session.LastActivityTicks = DateTime.UtcNow.Ticks;

            int trustedRoomId = session.RoomId;
            if (trustedRoomId != 0 && _roomManager.TryGetRoom(trustedRoomId, out var room) && room != null)
                room.Broadcast(buffer, length, clientId);
        }

        // ── Heartbeat ────────────────────────────────────────────────────
        private void HandleHeartbeat(
            in VoicePacketHeader header,
            IPEndPoint senderEndPoint,
            ISendChannel senderChannel)
        {
            int clientId = header.ClientId;
            if (!_roomManager.TryGetSession(clientId, out var session) || session == null
                || !session.EndPoint.Equals(senderEndPoint))
                return;

            session.LastActivityTicks = DateTime.UtcNow.Ticks;

            byte[] ackBuf = new byte[VoicePacketHeader.HeaderSize];
            var ackHeader = new VoicePacketHeader(
                packetType: 4,
                roomId: header.RoomId,
                clientId: clientId,
                sequenceNumber: 0,
                sendTimestamp: header.SendTimestamp, // echo client T0 for RTT measurement
                payloadLength: 0);

            PacketSerializer.TrySerializeHeader(ackHeader, ackBuf.AsSpan());
            senderChannel.Send(ackBuf, ackBuf.Length);
        }
    }
}
