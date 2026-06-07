## 2026-06-06T20:45:24Z
You are the Worker (Software Engineer) implementing Milestone 3: Network Transport & Room Server.
Working directory: i:/projects/voice chat/.agents/worker_ms3_implementation/

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A Forensic Auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

Objective:
Implement the transport layer, server logic, and mock client as outlined in PROJECT.md, SCOPE.md, and the Explorer design analyses.

Inputs & Reference Designs:
- PROJECT.md at i:/projects/voice chat/PROJECT.md
- SCOPE.md at i:/projects/voice chat/.agents/sub_orch_ms3_server/SCOPE.md
- Explorer 1 (Socket/Serialization): i:/projects/voice chat/.agents/teamwork_preview_explorer_ms3_1/analysis.md
- Explorer 2 (Room/Session Management): i:/projects/voice chat/.agents/teamwork_preview_explorer_ms3_2/analysis.md
- Explorer 3 (Mock Client/Simulator): i:/projects/voice chat/.agents/teamwork_preview_explorer_ms3_3/analysis.md

Tasks to execute:
1. Create a Standard Shared Project (`Shared/`) with standard TargetFramework netstandard2.1.
   - Implement `VoicePacketHeader` containing packet metadata.
   - Implement `PacketSerializer` handling zero-allocation serialize/deserialize with Little Endian primitives.
2. Create the Voice Server console application (`Server/`) targeting net8.0.
   - Implement `UdpVoiceServer` which binds to a configurable UDP socket, listens asynchronously using `ReceiveFromAsync(Memory<byte>, ...)` without allocating new buffers per packet, and routes packets to a Channel queue.
   - Implement `PacketRouter` to route incoming packets: Handshake (Type 1), RoomJoin (Type 2), Audio (Type 3).
   - Implement `RoomManager` and `VoiceRoom` classes. Use thread-safe collections (`ConcurrentDictionary`) to support concurrent clients/rooms without global lock contention. Broadcast received audio packets to all other clients in the same room. Use struct enumerators to avoid heap allocations.
   - Implement `SessionPruningService` to scan sessions periodically and prune clients inactive for >10 seconds.
   - Implement `Program.cs` to bind to port 50005 (or CLI parameter), start the server, start the pruning service, and log activity to the console.
3. Create the Mock Client console application (`MockClient/`) targeting net8.0.
   - Implement a simulator capable of running multiple concurrent clients asynchronously.
   - Implement connection handshake, NTP-like clock sync (sharing server timestamps), room join, and audio packet sending.
   - Implement the hybrid coarse-delay + stopwatch fine-spin timing loop to achieve accurate 20ms audio frame intervals.
   - Implement the 64-bit sliding window bitmask packet tracker for out-of-order, duplicate, and loss calculations.
   - Implement an atomic bucketed latency histogram for P50/P90/P95/P99 latency calculations.
   - Implement a real-time console dashboard updated once per second.
4. Add all three projects (`Shared/Shared.csproj`, `Server/Server.csproj`, `MockClient/MockClient.csproj`) to `voice chat.sln` using `dotnet sln add`.
5. Compile the entire solution in Release mode. Make sure there are no compiler warnings or errors.
6. Verify locally that the server runs and that the Mock Client can successfully connect, join rooms, and stream simulated audio with low latency and 0% loss.

Output Requirements:
- Write a handoff report at `i:/projects/voice chat/.agents/worker_ms3_implementation/handoff.md` detailing all implemented files, compilation logs, verification commands, and test output.
- Once complete, send a message back to the orchestrator (conversation ID: 98e3da8d-a700-42a9-807e-a06ca0c4705d) referencing the handoff path.
