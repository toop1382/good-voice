## 2026-06-07T00:17:43Z
You are Worker 1 (archetype: teamwork_preview_worker).
Working directory: i:/projects/voice chat/.agents/worker_ms1/
Workspace: i:/projects/voice chat/
Parent Conversation ID: fec73b90-f3d7-45e1-9229-a03f3657c5c2 (Milestone 1 Sub-orchestrator)

Task Description:
Implement Milestone 1: Native Opus Codec Wrapper.

Detailed Steps:
1. Create the Unity Client directories:
   - `Client/Assets/Plugins/Windows/`
   - `Client/Assets/Plugins/Android/libs/arm64-v8a/`
   - `Client/Assets/Plugins/Android/libs/armeabi-v7a/`
   - `Client/Assets/Scripts/Codec/`

2. Copy the native library files:
   - Copy `I:/projects/Sonario/Sonario/Assets/Sonario/Plugins/x64/UnityOpus.dll` to `Client/Assets/Plugins/Windows/UnityOpus.dll`.
   - Extract `jni/arm64-v8a/libunityopus.so` from `I:/projects/SonarioClient/Sonario/Assets/Sonario/Plugins/Android/unityopus.aar` and write it to `Client/Assets/Plugins/Android/libs/arm64-v8a/libunityopus.so`.
   - Extract `jni/armeabi-v7a/libunityopus.so` from `I:/projects/SonarioClient/Sonario/Assets/Sonario/Plugins/Android/unityopus.aar` and write it to `Client/Assets/Plugins/Android/libs/armeabi-v7a/libunityopus.so`.

3. Write the C# wrapper files under `Client/Assets/Scripts/Codec/`:
   - `IOpusEncoder.cs`: interface defining `int Encode(short[] pcm, byte[] output)` and `int Encode(ReadOnlySpan<short> pcm, Span<byte> output)`.
   - `IOpusDecoder.cs`: interface defining `int Decode(byte[] packet, short[] output)` and `int Decode(ReadOnlySpan<byte> packet, Span<short> output)`.
   - `UnityOpusLibrary.cs`: contains the `DllImport` definitions for the native `UnityOpus` library. It must match the entry points of `UnityOpus` exactly (e.g. `OpusEncoderCreate`, `OpusEncode`, `OpusEncoderDestroy`, `OpusDecoderCreate`, `OpusDecode`, `OpusDecoderDestroy`). Declare the library name dynamically using:
     #if UNITY_ANDROID
         const string dllName = "unityopus";
     #else
         const string dllName = "UnityOpus";
     #endif
   - `OpusEncoder.cs`: implements `IOpusEncoder` and `IDisposable`. Pin arrays using `fixed` statement in `unsafe` block for high-performance zero-copy P/Invoke. Make sure to call `OpusEncoderDestroy` in `Dispose` and the finalizer.
   - `OpusDecoder.cs`: implements `IOpusDecoder` and `IDisposable`. Pin arrays using `fixed` statement in `unsafe` block. Make sure to call `OpusDecoderDestroy` in `Dispose` and the finalizer.

4. Set up C# project files:
   - Create `Tests/Tests.csproj` targetting `.NET 8.0`. Add xUnit references.
   - Link the C# wrapper source files in `Tests/Tests.csproj` so they are compiled:
     `<Compile Include="..\Client\Assets\Scripts\Codec\**\*.cs" Link="Codec\%(RecursiveDir)%(Filename)%(Extension)" />`
   - Create a basic `voice chat.sln` or update it to include the `Tests` project.
   - Create `Tests/OpusLoopbackTests.cs` implementing the loopback test with a synthetic sine wave and RMS validation (decoded signal RMS should be within 15% of the input signal RMS, and not silent or zero).
   
5. Verify build and run tests:
   - Ensure the native library `UnityOpus.dll` is placed in the build output folder of the tests (or in the execution folder) so it can be loaded at runtime during test execution. E.g., copy `Client/Assets/Plugins/Windows/UnityOpus.dll` to the bin folder or configure `Tests.csproj` to copy it to output directory:
     `<None Include="..\Client\Assets\Plugins\Windows\UnityOpus.dll" Link="UnityOpus.dll" CopyToOutputDirectory="PreserveNewest" />`
   - Run `dotnet test` to execute the loopback test and ensure it passes successfully.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A Forensic Auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

Submit your findings and test output in your handoff report.

## 2026-06-07T00:15:22Z
You are a worker subagent for the E2E Testing Track.
Your task is to implement Milestone 1: Test Infra & Mock Client in C# (.NET 8.0).

Specifically, you must:
1. Create the directories:
   - `MockClient/`
   - `MockClient/Models/`
   - `MockClient/Diagnostics/`
   - `MockClient/Network/`
   - `Tests/`
   - `Tests/Fixtures/`
   - `Tests/Scenarios/`
2. Create `MockClient/MockClient.csproj` and `Tests/Tests.csproj` with the exact content described in `i:/projects/voice chat/.agents/explorer_ms1_1/explorer_ms1_report.md`.
3. Implement `MockClient/Models/VoicePacket.cs` for serialization/deserialization.
4. Implement `MockClient/Diagnostics/MetricTracker.cs` for packet statistics, RTT, loss, and jitter tracking.
5. Implement `MockClient/Network/MockClientUdpSocket.cs` using asynchronous UDP sockets and built-in simulated network degradation.
6. Implement `MockClient/MockClient.cs` to tie socket and metrics together.
7. Implement `Tests/Fixtures/ServerFixture.cs` to manage the background Server process.
8. Add both projects to `voice chat.sln` by executing:
   `dotnet sln "voice chat.sln" add "MockClient/MockClient.csproj" "Tests/Tests.csproj"`
9. Run `dotnet build "voice chat.sln"` to verify that the projects compile successfully.
10. Write your handoff and results report to `i:/projects/voice chat/.agents/worker_ms1/handoff.md`.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A Forensic Auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

Please let me know once this is done by sending a message to my conversation ID.

