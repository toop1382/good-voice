# Scope: Network Transport & Room Server (Milestone 3)

## Architecture
- **Server Module (`Server/`)**: A .NET 8.0 Console Application.
  - `UdpVoiceServer`: Manages the socket binding, asynchronous packet receiving loop, and packet parsing/routing.
  - `RoomManager`: Coordinates voice rooms (`VoiceRoom` objects), client sessions, room joins, and broadcasting.
  - `ClientSession`: Holds remote endpoint, client ID, room association, and last activity timestamp (for heartbeat/pruning).
- **Mock Client (`MockClient/`)**: A .NET 8.0 Console Application used to simulate multiple client connections, send dummy audio streams, measure latency/packet loss, and perform load/scalability testing.
- **Solution Layout**:
  - `voice chat.sln` will contain the `Server` project and `MockClient` project.

## Milestones
| # | Name | Scope | Dependencies | Status | Conv ID |
|---|------|-------|-------------|--------|---------|
| 1 | Network Socket & Packet Protocol | Implement `Server` project boilerplate, custom packet serialization/deserialization, UDP listener loop, and packet routing. | None | PLANNED | |
| 2 | Room Management & Broadcasting | Implement `RoomManager`, room association, multi-room broadcasting, client disconnect/cleanup/pruning, and console logs. | MS 3.1 | PLANNED | |
| 3 | Mock Client & Verification | Implement `MockClient` load generator to join rooms, send/receive packets, measure round-trip time, and verify server scalability. | MS 3.2 | PLANNED | |

## Interface Contracts
### Client ↔ Server UDP Packet Layout
Packets are sent over UDP and structured as follows:
- **Bytes 0-3**: Packet Type (int32):
  - `1`: Handshake (Client initiates handshake; Server responds with handshake acknowledgment)
  - `2`: RoomJoin (Client requests to join a specific Room ID)
  - `3`: Audio (Client sends Opus-encoded audio payload; Server broadcasts to others in the room)
- **Bytes 4-7**: Room ID (int32)
- **Bytes 8-11**: Client ID (int32)
- **Bytes 12-15**: Sequence Number (uint32)
- **Bytes 16-23**: Send Timestamp (int64, milliseconds)
- **Bytes 24-27**: Payload Length (int32)
- **Bytes 28+**: Opus-encoded audio payload or extra handshake/metadata
