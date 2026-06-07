# Handoff Report — Explorer 2

## 1. Observation
- Workspace contents list: `ORIGINAL_REQUEST.md`, `PROJECT.md`, `TEST_INFRA.md`, `voice chat.sln`.
- Verified no active C# files currently exist in the repository under `Client/` or `Server/` folders (directories are not yet initialized).
- From `PROJECT.md` line 42:
  ```markdown
  ### Codec API
  - `IOpusEncoder`: `int Encode(short[] pcm, byte[] output)`
  - `IOpusDecoder`: `int Decode(byte[] packet, short[] output)`
  ```
- Detailed investigation into unmanaged marshalling and Opus native library signatures was conducted and recorded in `i:/projects/voice chat/.agents/explorer_ms1_2/report.md`.

## 2. Logic Chain
- To implement `IOpusEncoder` and `IOpusDecoder` on Windows (`opus.dll`) and Android (`libopus.so`), the C# compiler needs `DllImport` bindings.
- Using `[DllImport("opus")]` resolves to `opus.dll` on Windows and `libopus.so` on Android due to Mono/IL2CPP's automatic library naming resolution.
- C# arrays are managed and can be moved by the GC. Passing them to C/C++ requires pinning.
- Synchronous calls like `opus_encode` and `opus_decode` execute immediately on the calling thread. Therefore, `fixed` pointers are the most efficient, zero-copy method because pinning is scoped and carries zero GC handle tracking overhead.
- Valid Opus frame size calculations depend on sample rate (48000 Hz) and duration (e.g. 20ms = 960 samples per channel). The total length of the PCM array for Stereo is `1920` but the `frame_size` parameter passed to the library must remain `960`.
- The Opus CTL functions use C varargs (`...`), which cannot be cleanly mapped to a single C# signature. Overloaded P/Invoke signatures must be declared for different CTL requests (such as setting bitrate, complexity, and packet loss).

## 3. Caveats
- Since the source files and native library files are not yet created on disk (to be done in future steps by implementation agents), the library paths and build behavior were not verified via compiler tests.
- Platform behavior differences on iOS/macOS were not studied as only Windows and Android are in-scope.

## 4. Conclusion
- The standard marshaling pattern for passing PCM `short[]` and Opus packet `byte[]` arrays to native C libraries without copy overhead is to use C# `unsafe` pointer syntax inside a `fixed` block.
- Opus requires sample rate (8kHz to 48kHz), channel count (1 or 2), application type (2048 for VoIP), and uses negative integers for error codes.
- The correct C# DllImport signatures have been successfully designed, along with CTL overloads, and written to `i:/projects/voice chat/.agents/explorer_ms1_2/report.md`.

## 5. Verification Method
- Independent verification can be achieved by:
  1. Inspecting the generated report in `i:/projects/voice chat/.agents/explorer_ms1_2/report.md` to review the designed `NativeMethods` signatures.
  2. Compiling the proposed C# wrapper against `opus.dll` / `libopus.so` during the Worker phase and running the test suite to verify no stack imbalances or memory violation errors (`AccessViolationException`) occur.
