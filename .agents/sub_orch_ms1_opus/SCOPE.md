# Scope: Milestone 1: Native Opus Codec Wrapper

## Architecture
- **Native Opus Libraries**:
  - Windows: x86_64 Opus DLL, to be placed under `Client/Assets/Plugins/Windows/`.
  - Android: `libopus.so` for arm64-v8a and armeabi-v7a architectures, to be placed under `Client/Assets/Plugins/Android/libs/`.
- **C# Wrapper**:
  - File(s) placed under `Client/Assets/Scripts/Codec/`.
  - Provides a unified API conforming to interface contracts below.
  - Handles marshalling between C# arrays and native memory buffers.
  - Implements initialization, encoding, decoding, and resource cleanup (implements `IDisposable`).

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| 1 | Integrate Native Libraries | Locate or install Opus native libraries for Windows and Android, and copy to plugin folders | None | PLANNED |
| 2 | C# Opus Wrapper API | Implement IOpusEncoder and IOpusDecoder with DllImport references to native Opus library | M1.1 | PLANNED |
| 3 | Verification | Compile/Build C# wrapper and run tests to ensure correct native binding and codec functionality | M1.2 | PLANNED |

## Interface Contracts
### Codec API
- `IOpusEncoder`: `int Encode(short[] pcm, byte[] output)`
  - Encodes a frame of PCM audio to an Opus packet.
  - Returns: Length of the encoded Opus packet in bytes.
- `IOpusDecoder`: `int Decode(byte[] packet, short[] output)`
  - Decodes an Opus packet back to PCM audio.
  - Returns: Number of decoded PCM samples per channel.
