# Scope: E2E Testing Track

## Architecture
- **E2E Test Runner**: A .NET Console application or test runner located in `Tests/` that manages test execution. It spawns the Server process, initializes Mock Clients, sets up test assertions, and gathers execution logs.
- **Mock Client**: A lightweight .NET class/program in `MockClient/` or compiled into the test project. It communicates using the UDP packet protocol to simulate voice client behaviors (room join/leave, voice packet streaming, RTT/jitter calculation, packet reordering).
- **Network Protocol**: UDP-based packet format as specified in `PROJECT.md`.

## Milestones
| # | Name | Scope | Dependencies | Status | Conv ID |
|---|------|-------|-------------|--------|---------|
| 1 | Test Infra & Mock Client | Set up the C# test suite structure and implement the MockClient with custom UDP sockets and packet framing serialization. | None | DONE | e9067728-424a-4171-94f7-77e1c7065ade |
| 2 | Tier 1 & 2 Tests | Implement Feature Coverage (25 tests) and Boundary/Corner cases (25 tests). | M1 | IN_PROGRESS | |
| 3 | Tier 3 & 4 Tests | Implement Cross-Feature Combinations (5 tests) and Real-World Application Scenarios (5 tests). | M2 | PLANNED | |
| 4 | Verification & Handoff | Run all tests against the server implementation, verify all pass, publish TEST_READY.md and TEST_INFRA.md. | M3 | PLANNED | |

## Interface Contracts
### Client ↔ Server Packets
- Format:
  - Bytes 0-3: Packet Type (1=Handshake, 2=RoomJoin, 3=Audio)
  - Bytes 4-7: Room ID (int32)
  - Bytes 8-11: Client ID (int32)
  - Bytes 12-15: Sequence Number (uint32)
  - Bytes 16-23: Send Timestamp (int64, milliseconds)
  - Bytes 24-27: Payload length (int32)
  - Bytes 28+: Opus-encoded audio payload
