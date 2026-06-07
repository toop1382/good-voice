## 2026-06-07T00:27:42Z
You are a worker subagent for the E2E Testing Track.
Your task is to implement Milestone 2: Tier 1 & 2 Tests in C# (.NET 8.0).

Please use the folder `i:/projects/voice chat/.agents/sub_orch_e2e_testing/worker_ms2/` for your BRIEFING.md, progress.md, and all other metadata files. Do NOT write metadata to any other folder.

Specifically, you must:
1. Create `Tests/Scenarios/Tier1Tests.cs` implementing the following 25 Feature Coverage tests:
   - F1 (Room Session Management):
     * T1.1.1: Join Room (Success)
     * T1.1.2: Leave Room (Success)
     * T1.1.3: Re-join same Room
     * T1.1.4: Switch Room (Join Room A, then Join Room B)
     * T1.1.5: Multiple Clients Join Room
   - F2 (Audio Packet Broadcasting):
     * T1.2.1: Single Broadcaster, Single Receiver (Same room)
     * T1.2.2: Single Broadcaster, Multiple Receivers (Same room)
     * T1.2.3: Broadcaster does not receive their own voice packets
     * T1.2.4: Audio packets NOT broadcast to clients in other rooms
     * T1.2.5: Broadcast stops when sender leaves room
   - F3 (Packet Formatting & Validation):
     * T1.3.1: Valid Handshake packet structure
     * T1.3.2: Valid RoomJoin packet structure
     * T1.3.3: Valid Audio packet structure (correct fields, sequence, etc.)
     * T1.3.4: Server response contains correct Client ID assigned
     * T1.3.5: Packet parsing of minimum payload length
   - F4 (Latency Profiling & Diagnostics):
     * T1.4.1: Echo test to measure RTT
     * T1.4.2: Sequence number increments monotonically
     * T1.4.3: Timestamp in audio packet corresponds to send time
     * T1.4.4: Jitter estimation matches variance in arrival time
     * T1.4.5: Packet loss reporting under simulated drop
   - F5 (Robustness & Scale - Simple):
     * T1.5.1: 10 clients join room simultaneously
     * T1.5.2: Client disconnects abruptly (socket close)
     * T1.5.3: Server remains responsive after client leaves
     * T1.5.4: Message broadcast to 10 clients in the same room
     * T1.5.5: Simultaneous join of different rooms (Room 1, Room 2, Room 3)

2. Create `Tests/Scenarios/Tier2Tests.cs` implementing the following 25 Boundary & Corner cases:
   - F1 (Room Session Management):
     * T2.1.1: Join room with negative Room ID (expect failure/rejection or clean ignore)
     * T2.1.2: Join room with max integer Room ID
     * T2.1.3: Client joins room multiple times without leaving (duplicate join)
     * T2.1.4: Leave room without joining first
     * T2.1.5: Join room with empty/zero Room ID
   - F2 (Audio Packet Broadcasting):
     * T2.2.1: Send audio packet with empty/zero payload
     * T2.2.2: Send audio packet with maximum allowed payload size (e.g. 1024 bytes Opus limit)
     * T2.2.3: Send audio packet with payload larger than max allowed (oversized packet)
     * T2.2.4: Send audio packets to a room with no other clients (verify no broadcast, no crash)
     * T2.2.5: Send audio packet prior to joining any room
   - F3 (Packet Formatting & Validation):
     * T2.3.1: Send packet smaller than header size (malformed header)
     * T2.3.2: Send packet with invalid Packet Type
     * T2.3.3: Send packet with payload length field mismatching actual packet size (too small)
     * T2.3.4: Send packet with payload length field mismatching actual packet size (too large)
     * T2.3.5: Send packet with future timestamp
   - F4 (Latency & Jitter Boundaries):
     * T2.4.1: RTT measurement with delayed server response (simulated network lag)
     * T2.4.2: Out-of-order packets (sequence number: 3, then 2, then 4)
     * T2.4.3: Duplicate sequence number packets
     * T2.4.4: Packets sent at extremely high frequency (spamming packets)
     * T2.4.5: Timestamp in the past (clock drift simulator)
   - F5 (Robustness Boundaries):
     * T2.5.1: Send malformed random bytes (garbage packet)
     * T2.5.2: Rapid join/leave toggle (100 times in a loop)
     * T2.5.3: Server handles sudden mass disconnect of 20 clients
     * T2.5.4: Send packet with zero buffer length
     * T2.5.5: Port scanning simulation (send non-protocol UDP packets to server port)

3. Make sure all tests compile and build using:
   `dotnet build "voice chat.sln"`
4. Run `dotnet test "voice chat.sln"` to verify if they pass (note: some negative tests might fail or pass depending on server implementation. Implement appropriate assertions based on server behavior. E.g. server should ignore invalid packets or disconnect the client, but not crash).
5. Write your handoff and results report to `i:/projects/voice chat/.agents/sub_orch_e2e_testing/worker_ms2/handoff.md`.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A Forensic Auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

Please let me know once this is done by sending a message to my conversation ID.
