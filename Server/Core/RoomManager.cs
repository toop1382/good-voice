using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;

namespace Server.Core
{
    public class RoomManager
    {
        private readonly ConcurrentDictionary<int, VoiceRoom> _rooms = new();
        private readonly ConcurrentDictionary<int, ClientSession> _sessions = new();
        private readonly ConcurrentDictionary<string, ClientSession> _endpointSessions = new();
        private int _nextClientId = 100000;

        public int GenerateUniqueClientId()
        {
            while (true)
            {
                int id = System.Threading.Interlocked.Increment(ref _nextClientId);
                if (!_sessions.ContainsKey(id))
                {
                    return id;
                }
            }
        }

        // ── Room helpers ──────────────────────────────────────────────────
        public VoiceRoom GetOrCreateRoom(int roomId) =>
            _rooms.GetOrAdd(roomId, id => new VoiceRoom(id));

        public bool TryGetRoom(int roomId, out VoiceRoom? room) =>
            _rooms.TryGetValue(roomId, out room);

        public ICollection<VoiceRoom> GetAllRooms() => _rooms.Values;

        // ── Session management ────────────────────────────────────────────
        /// <summary>
        /// Register or refresh a client session. For UDP, called per-handshake (may move endpoint).
        /// For TCP/WebSocket, called once at connection time.
        /// </summary>
        public ClientSession RegisterClient(int clientId, IPEndPoint endPoint, ISendChannel? sendChannel = null)
        {
            string epKey = endPoint.ToString();

            if (_sessions.TryGetValue(clientId, out var existing))
            {
                lock (existing)
                {
                    string oldKey = existing.EndPoint.ToString();
                    if (oldKey != epKey)
                    {
                        // NAT roam or protocol reconnect — update endpoint mapping
                        existing.EndPoint = endPoint;
                        lock (_endpointSessions)
                        {
                            _endpointSessions.TryRemove(oldKey, out _);
                            _endpointSessions[epKey] = existing;
                        }
                    }
                    if (sendChannel != null)
                    {
                        if (existing.SendChannel is IDisposable oldDisposable && existing.SendChannel != sendChannel)
                        {
                            try { oldDisposable.Dispose(); } catch { }
                        }
                        existing.SendChannel = sendChannel;
                    }
                    existing.LastActivityTicks = DateTime.UtcNow.Ticks;
                    return existing;
                }
            }

            var session = new ClientSession(clientId, endPoint, sendChannel);
            if (_sessions.TryAdd(clientId, session))
            {
                lock (_endpointSessions)
                {
                    _endpointSessions[epKey] = session;
                }
                return session;
            }
            // Race: another thread won — recurse once to retrieve it
            return RegisterClient(clientId, endPoint, sendChannel);
        }

        public bool JoinRoom(int clientId, int roomId, out VoiceRoom? joinedRoom)
        {
            joinedRoom = null;
            if (!_sessions.TryGetValue(clientId, out var session)) return false;
            
            lock (session)
            {
                if (session.RoomId != 0) LeaveRoom(session);

                joinedRoom = GetOrCreateRoom(roomId);
                if (joinedRoom.TryAdd(session))
                {
                    session.RoomId = roomId;
                    return true;
                }
                return false;
            }
        }

        public bool LeaveRoom(int clientId)
        {
            if (!_sessions.TryGetValue(clientId, out var session)) return false;
            lock (session)
            {
                return LeaveRoom(session);
            }
        }

        public bool LeaveRoom(ClientSession session)
        {
            lock (session)
            {
                int currentRoomId = session.RoomId;
                if (currentRoomId == 0) return false;

                if (_rooms.TryGetValue(currentRoomId, out var room))
                {
                    room.TryRemove(session.ClientId, out _);
                    room.BroadcastUserLeft(session.ClientId);
                    session.RoomId = 0;
                    if (room.ClientCount == 0) _rooms.TryRemove(currentRoomId, out _);
                    return true;
                }
                return false;
            }
        }

        private void TryRemoveEndpointSession(ClientSession session)
        {
            string epKey = session.EndPoint.ToString();
            lock (_endpointSessions)
            {
                if (_endpointSessions.TryGetValue(epKey, out var current) && current == session)
                {
                    _endpointSessions.TryRemove(epKey, out _);
                }
            }
        }

        public void UnregisterClient(int clientId, long currentTicks, long thresholdTicks)
        {
            if (!_sessions.TryGetValue(clientId, out var session)) return;
            if (currentTicks - session.LastActivityTicks > thresholdTicks)
            {
                lock (session)
                {
                    // Re-check under lock
                    if (currentTicks - session.LastActivityTicks <= thresholdTicks)
                        return;

                    if (_sessions.TryRemove(clientId, out var removed))
                    {
                        LeaveRoom(removed);
                        TryRemoveEndpointSession(removed);
                        if (removed.SendChannel is IDisposable disposableChannel)
                        {
                            try { disposableChannel.Dispose(); } catch { }
                        }
                    }
                }
            }
        }

        public void UnregisterClient(int clientId, ISendChannel? expectedChannel)
        {
            if (!_sessions.TryGetValue(clientId, out var session)) return;
            
            // Fast path check before lock
            if (expectedChannel != null && session.SendChannel != expectedChannel)
                return;

            lock (session)
            {
                if (expectedChannel != null && session.SendChannel != expectedChannel)
                {
                    // Client has reconnected on a different channel/socket. Do not unregister.
                    return;
                }

                if (_sessions.TryRemove(clientId, out var removed))
                {
                    LeaveRoom(removed);
                    TryRemoveEndpointSession(removed);
                    if (removed.SendChannel is IDisposable disposableChannel)
                    {
                        try { disposableChannel.Dispose(); } catch { }
                    }
                }
            }
        }

        public bool TryGetSession(int clientId, out ClientSession? session) =>
            _sessions.TryGetValue(clientId, out session);

        public bool TryGetSessionByEndPoint(IPEndPoint endPoint, out ClientSession? session) =>
            _endpointSessions.TryGetValue(endPoint.ToString(), out session);

        public ICollection<ClientSession> GetAllSessions() => _sessions.Values;
    }
}
