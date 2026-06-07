# Opus Integration & C# Wrapper Design Report

## Executive Summary
This report presents the findings of Explorer 1 regarding the presence of Opus binaries in the workspace and proposes the architecture and P/Invoke signatures for the C# native Opus wrappers. 

Key results:
1. **Workspace Audit**: There are currently **no** Opus library binaries, compilation scripts, or source files in the workspace.
2. **Project Structure**: No directories for `Server`, `Client`, `MockClient`, or `Tests` exist on disk yet. `voice chat.sln` is empty.
3. **P/Invoke Signatures**: We propose high-performance `DllImport` signatures targeting the standard Opus library interface, compatible with both Windows (`opus.dll`) and Android (`libopus.so`).
4. **Wrapper Class Structure**: We outline classes implementing `IOpusEncoder` and `IOpusDecoder` that conform to the contracts in `PROJECT.md` while incorporating best practices like `IDisposable`, finalizers, and zero-allocation `Span`-based overloads.

---

## 1. Workspace Search Results

We performed a comprehensive recursive search of the workspace, including hidden files and files ignored by git (e.g., inside `.idea` and `.agents`).

### Opus Binary Search
- **Query**: Searched for patterns such as `*opus*`, `*.dll`, `*.so`, `*.a`, `*.aar`, `*.lib`.
- **Result**: **No existing Opus binaries, packaging files, compile scripts, or source code were found.**
- **Implication**: Native Opus binaries for Windows x86_64 (`opus.dll`) and Android arm64-v8a/armeabi-v7a (`libopus.so`) must be obtained/compiled and integrated into the project layout during Milestone 1 implementation.

### Directories and Projects
- **Query**: Checked for directories like `Client/`, `Server/`, `MockClient/`, and `Tests/`.
- **Result**: **None of these directories exist under the workspace root.**
- **Current Workspace Contents**:
  - `.agents/` (agent metadata folders)
  - `.idea/` (IDE settings)
  - `ORIGINAL_REQUEST.md` (original user request)
  - `PROJECT.md` (architecture, layout, and interfaces)
  - `TEST_INFRA.md` (testing specifications)
  - `voice chat.sln` (empty solution file with no project configurations)

---

## 2. Proposed C# Wrapper Structure

To interface with the native Opus library, we will implement the `IOpusEncoder` and `IOpusDecoder` interfaces specified in `PROJECT.md`. We also propose extending them to support zero-allocation `Span`-based operations for high-frequency runtime loops.

### Interface Definitions (from `PROJECT.md`)
```csharp
public interface IOpusEncoder : IDisposable
{
    /// <summary>
    /// Encodes 16-bit PCM audio to Opus payload.
    /// </summary>
    /// <param name="pcm">The input raw PCM buffer.</param>
    /// <param name="output">The output buffer for the compressed packet.</param>
    /// <returns>The number of bytes written to the output buffer.</returns>
    int Encode(short[] pcm, byte[] output);

    /// <summary>
    /// Zero-allocation overload using Spans.
    /// </summary>
    int Encode(ReadOnlySpan<short> pcm, Span<byte> output);
}

public interface IOpusDecoder : IDisposable
{
    /// <summary>
    /// Decodes an Opus packet to 16-bit PCM audio.
    /// </summary>
    /// <param name="packet">The compressed Opus packet.</param>
    /// <param name="output">The output buffer to store decoded PCM samples.</param>
    /// <returns>The number of decoded samples written to the output buffer.</returns>
    int Decode(byte[] packet, short[] output);

    /// <summary>
    /// Zero-allocation overload using Spans.
    /// </summary>
    int Decode(ReadOnlySpan<byte> packet, Span<short> output);
}
```

### Supporting Enums
```csharp
public enum OpusApplication
{
    Voip = 2048,
    Audio = 2049,
    RestrictedLowDelay = 2051
}

public enum OpusError
{
    OK = 0,
    BadArg = -1,
    BufferTooSmall = -2,
    InternalError = -3,
    InvalidPacket = -4,
    Unimplemented = -5,
    InvalidState = -6,
    AllocFail = -7
}
```

---

## 3. P/Invoke Signatures for Native Opus

Since the wrapper must run on both Windows Standalone (x86_64) and Android (arm64/armeabi-v7a) in Unity, we leverage Unity's automatic library loading. We specify the library name as `"opus"`. Unity resolves this to:
- `opus.dll` on Windows (placed in `Assets/Plugins/Windows/x86_64/`)
- `libopus.so` on Android (placed in `Assets/Plugins/Android/libs/`)

### Function Signatures (NativeOpus Bridge)

We define two P/Invoke approaches:
1. **Unsafe Pointer-Based** (Recommended for performance and zero-allocation `Span` integration)
2. **Safe Array-Based** (Easier for simple array usage, though incurs Marshalling/Pinning overhead)

#### Option A: Unsafe Pointer-Based (Recommended)
This approach allows direct pinned pointers from `Span` and avoids marshalling copying or pinning overhead in C#.
```csharp
using System;
using System.Runtime.InteropServices;

internal static class NativeOpus
{
    private const string LibName = "opus";

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_encoder_create")]
    public static extern IntPtr opus_encoder_create(int Fs, int channels, int application, out int error);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_encoder_destroy")]
    public static extern void opus_encoder_destroy(IntPtr encoder);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_encode")]
    public static unsafe extern int opus_encode(IntPtr st, short* pcm, int frame_size, byte* data, int max_data_bytes);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_decoder_create")]
    public static extern IntPtr opus_decoder_create(int Fs, int channels, out int error);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_decoder_destroy")]
    public static extern void opus_decoder_destroy(IntPtr decoder);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_decode")]
    public static unsafe extern int opus_decode(IntPtr st, byte* data, int len, short* pcm, int frame_size, int decode_fec);
}
```

#### Option B: Safe Array-Based
This uses CLR array pinning automatically.
```csharp
using System;
using System.Runtime.InteropServices;

internal static class SafeNativeOpus
{
    private const string LibName = "opus";

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_encoder_create")]
    public static extern IntPtr opus_encoder_create(int Fs, int channels, int application, out int error);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_encoder_destroy")]
    public static extern void opus_encoder_destroy(IntPtr encoder);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_encode")]
    public static extern int opus_encode(IntPtr st, [In] short[] pcm, int frame_size, [Out] byte[] data, int max_data_bytes);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_decoder_create")]
    public static extern IntPtr opus_decoder_create(int Fs, int channels, out int error);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_decoder_destroy")]
    public static extern void opus_decoder_destroy(IntPtr decoder);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_decode")]
    public static extern int opus_decode(IntPtr st, [In] byte[] data, int len, [Out] short[] pcm, int frame_size, int decode_fec);
}
```

### Parameter and Memory Management Details

1. **State Lifecycle**:
   - `opus_encoder_create` and `opus_decoder_create` allocate native memory. The returned `IntPtr` points to the native state.
   - If creation fails, the returned `IntPtr` is `IntPtr.Zero` and the `error` parameter receives a negative `OpusError` value.
   - These state handles **must** be explicitly freed using `opus_encoder_destroy` and `opus_decoder_destroy` respectively. We enforce this in C# by implementing `IDisposable` with a finalizer in the wrapper classes.

2. **Frame Size Calculation**:
   - For encoding, `frame_size` is the number of samples **per channel** in the input PCM. For mono, `frame_size` equals `pcm.Length`. For stereo, it is `pcm.Length / 2`.
   - Opus supports only specific frame sizes: 2.5, 5, 10, 20, 40, or 60 ms. For 48kHz sampling rate, the valid frame sizes are 120, 240, 480, 960, 1920, and 2880 samples per channel. Passing any other size will cause `opus_encode` to return `OPUS_BAD_ARG` (-1).
   - For decoding, `frame_size` is the capacity of the output PCM buffer per channel (e.g. `pcm.Length / channels`).

3. **Packet Loss Concealment (PLC) / Forward Error Correction (FEC)**:
   - In `opus_decode`, if packet loss occurs, we pass a `null` pointer (or `IntPtr.Zero`) for the input data, with `len` set to `0`. This prompts the decoder to generate concealment comfort noise or reconstruct missing samples using standard PLC/FEC if `decode_fec` is set to `1` and the previous packet contained FEC payload.

4. **Encoder/Decoder Configuration (CTL Codes)**:
   - The native Opus library provides `opus_encoder_ctl` and `opus_decoder_ctl` to adjust internal settings (e.g. bitrate, VBR, complexity, packet loss percentage, FEC).
   - In C#, since CTL functions are variadic, we define type-safe P/Invoke overloads for the specific options we require:
     ```csharp
     // Examples for configuring Encoder settings
     [DllImport("opus", CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_encoder_ctl")]
     public static extern int opus_encoder_ctl(IntPtr st, int request, int value);

     [DllImport("opus", CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_encoder_ctl")]
     public static extern int opus_encoder_ctl(IntPtr st, int request, out int value);
     ```

---

## 4. Implementation Structure

Below is the concrete implementation structure for the `OpusEncoder` and `OpusDecoder` wrappers incorporating standard C# patterns:

### `OpusEncoder` Wrapper Class
```csharp
using System;

namespace VoiceChat.Codec
{
    public sealed class OpusEncoder : IOpusEncoder
    {
        private IntPtr _encoderState;
        private readonly int _channels;
        private bool _isDisposed;

        public OpusEncoder(int sampleRate, int channels, OpusApplication application)
        {
            if (channels != 1 && channels != 2)
                throw new ArgumentException("Opus only supports 1 (mono) or 2 (stereo) channels.", nameof(channels));

            _channels = channels;
            _encoderState = NativeOpus.opus_encoder_create(sampleRate, channels, (int)application, out int error);

            if (error != (int)OpusError.OK || _encoderState == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Failed to create Opus Encoder. Native Error Code: {(OpusError)error}");
            }
        }

        public int Encode(short[] pcm, byte[] output)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(OpusEncoder));
            if (pcm == null) throw new ArgumentNullException(nameof(pcm));
            if (output == null) throw new ArgumentNullException(nameof(output));

            return Encode(pcm.AsSpan(), output.AsSpan());
        }

        public int Encode(ReadOnlySpan<short> pcm, Span<byte> output)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(OpusEncoder));

            int frameSize = pcm.Length / _channels;

            unsafe
            {
                fixed (short* pPcm = pcm)
                fixed (byte* pOut = output)
                {
                    int result = NativeOpus.opus_encode(_encoderState, pPcm, frameSize, pOut, output.Length);
                    if (result < 0)
                    {
                        throw new InvalidOperationException($"Opus encoding failed. Native Error Code: {(OpusError)result}");
                    }
                    return result;
                }
            }
        }

        private void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (_encoderState != IntPtr.Zero)
                {
                    NativeOpus.opus_encoder_destroy(_encoderState);
                    _encoderState = IntPtr.Zero;
                }
                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~OpusEncoder()
        {
            Dispose(false);
        }
    }
}
```

### `OpusDecoder` Wrapper Class
```csharp
using System;

namespace VoiceChat.Codec
{
    public sealed class OpusDecoder : IOpusDecoder
    {
        private IntPtr _decoderState;
        private readonly int _channels;
        private bool _isDisposed;

        public OpusDecoder(int sampleRate, int channels)
        {
            if (channels != 1 && channels != 2)
                throw new ArgumentException("Opus only supports 1 (mono) or 2 (stereo) channels.", nameof(channels));

            _channels = channels;
            _decoderState = NativeOpus.opus_decoder_create(sampleRate, channels, out int error);

            if (error != (int)OpusError.OK || _decoderState == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Failed to create Opus Decoder. Native Error Code: {(OpusError)error}");
            }
        }

        public int Decode(byte[] packet, short[] output)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(OpusDecoder));
            if (output == null) throw new ArgumentNullException(nameof(output));

            ReadOnlySpan<byte> packetSpan = packet != null ? packet.AsSpan() : ReadOnlySpan<byte>.Empty;
            return Decode(packetSpan, output.AsSpan());
        }

        public int Decode(ReadOnlySpan<byte> packet, Span<short> output)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(OpusDecoder));

            int frameSize = output.Length / _channels;

            unsafe
            {
                fixed (short* pOut = output)
                {
                    int result;
                    if (packet.IsEmpty)
                    {
                        // Request Packet Loss Concealment (PLC)
                        result = NativeOpus.opus_decode(_decoderState, null, 0, pOut, frameSize, 0);
                    }
                    else
                    {
                        fixed (byte* pPacket = packet)
                        {
                            result = NativeOpus.opus_decode(_decoderState, pPacket, packet.Length, pOut, frameSize, 0);
                        }
                    }

                    if (result < 0)
                    {
                        throw new InvalidOperationException($"Opus decoding failed. Native Error Code: {(OpusError)result}");
                    }
                    return result; // Number of decoded samples
                }
            }
        }

        private void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (_decoderState != IntPtr.Zero)
                {
                    NativeOpus.opus_decoder_destroy(_decoderState);
                    _decoderState = IntPtr.Zero;
                }
                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~OpusDecoder()
        {
            Dispose(false);
        }
    }
}
```

---

## 5. Verification & Testing Strategy

To verify this proposed wrapper behaves as expected:
1. **Loopback Test**: Build a unit test (e.g. `OpusLoopbackTests` in `Tests/`) that generates a sine wave, encodes it, decodes it, and calculates the Root-Mean-Square (RMS) of the input vs output signals. The output RMS must be within a 15% tolerance of the input, confirming audio fidelity.
2. **Project Linkage**: Implement the source code in `Client/Assets/Scripts/Codec/` and link the file via `<Compile Include="..." Link="..." />` inside the xUnit `Tests.csproj` and `MockClient.csproj` to test the wrapper in a standalone .NET environment without duplicating code.
