# Handoff Report: Milestone 1 Codec Design & Verification

## 1. Observation
- The workspace root contains:
  - `voice chat.sln`
  - `PROJECT.md`
  - `TEST_INFRA.md`
  - `ORIGINAL_REQUEST.md`
- Running a file search for `.cs` or `.csproj` files returned no code files in the directory outside of these Markdown files and metadata.
- In `PROJECT.md`, the code layout is specified as:
  ```markdown
  - Client/: Unity Project folder.
    - Assets/Plugins/Windows/: Windows Opus DLL, WASAPI DLL (if native).
    - Assets/Plugins/Android/: Android Opus .so (arm64-v8a, armeabi-v7a), Android JNI AAR/Java files.
    - Assets/Scripts/Codec/: Opus encoding/decoding wrappers.
  ```
- The API contracts in `PROJECT.md` are defined as:
  ```markdown
  ### Codec API
  - IOpusEncoder: int Encode(short[] pcm, byte[] output)
  - IOpusDecoder: int Decode(byte[] packet, short[] output)
  ```

## 2. Logic Chain
- **Project Structure & Compilation**:
  - Since Unity handles the compilation of files under the `Assets/` directory automatically, we should create the `Client/Assets/Scripts/Codec/` structure and write the wrappers as `.cs` files there.
  - To compile the wrappers in non-Unity environments (such as in `Tests/Tests.csproj` for unit testing, and `MockClient/MockClient.csproj` for running simulated traffic) without duplicating the source code, we should link the source files using relative path compilation in the MSBuild files:
    `<Compile Include="..\Client\Assets\Scripts\Codec\**\*.cs" Link="Codec\%(RecursiveDir)%(Filename)%(Extension)" />`.
- **Memory Management & API Layout**:
  - The native Opus library is unmanaged C. We allocate native memory via `opus_encoder_create` and `opus_decoder_create`, which must be released via `opus_encoder_destroy` and `opus_decoder_destroy`.
  - To prevent native memory leaks when the unmanaged codec resources are allocated, we must implement `IDisposable` with a proper disposable pattern, including finalizers (`~OpusEncoder()` and `~OpusDecoder()`) that call `Dispose(false)`.
  - For high performance and zero-allocation in real-time low-latency audio capture/playback, we should extend the array-based contracts in `PROJECT.md` to support `ReadOnlySpan<short>` and `Span<byte>` overloads, utilizing C# `unsafe` fixed-pointer pinning.
- **Loopback Verification**:
  - To verify the correctness of the wrapper, we must perform an end-to-end loopback test.
  - We can construct a predictable sine wave in PCM (e.g. 440 Hz) and feed it into the encoder.
  - The resulting output from the encoder must have a valid length greater than 0, be smaller than the input size (successful compression), and not consist entirely of zero-bytes.
  - The decoder must take the compressed buffer and produce exactly the original sample count.
  - While lossy, the decoded output's Root-Mean-Square (RMS) amplitude must correlate closely with the input (within a defined ratio tolerance, e.g. 15%), and not be silent (all zeros).

## 3. Caveats
- We did not load or execute the native Windows `.dll` or Android `.so` libraries since we are in a read-only investigation mode and the native binary files themselves are not yet present in the workspace.
- The path and resolution rules for loading native `opus` dynamic libraries (such as placing `opus.dll` in the executable output directory or setting a DLL import resolver) are assumed to be handled by the implementer or environment deployment steps.

## 4. Conclusion
- We should establish the `Client/Assets/Scripts/Codec/` directory structure for the Unity-based wrappers and share these source files with `Tests/` and `MockClient/` via MSBuild relative file linkage.
- The `IOpusEncoder` and `IOpusDecoder` implementations must encapsulate unmanaged Opus native resources safely using standard IDisposable patterns and support Span overloads for zero GC allocation.
- Loopback tests verifying signal integrity (PCM -> encoded Opus -> decoded PCM) can be validated mathematically using sample counts, zero checks, and signal RMS energy boundaries.

## 5. Verification Method
- Code files to inspect:
  - `i:/projects/voice chat/.agents/explorer_ms1_3/report.md` (detailed layout and design code).
- Invalidation conditions:
  - If the native Opus library requires different P/Invoke function signatures than the ones defined (e.g., if utilizing custom wrapper DLL exports instead of standard libopus).
  - If sharing the source files via relative `<Compile>` paths fails inside specific IDEs or during custom cross-compilation configurations (in which case a shared `.NET Standard` library should be used).
