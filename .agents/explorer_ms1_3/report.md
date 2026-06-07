# Milestone 1 Codec Wrapper Design Report

This report documents the architectural design, compilation structure, and wrapper details for integrating the native Opus audio codec into the low-latency voice chat system.

---

## 1. Project & Compilation Structure

### Solution Layout (`voice chat.sln`)
Currently, `voice chat.sln` is empty. To build and compile the C# codebase using MSBuild, we will add the following standard .NET Core project files:
- **`Server/Server.csproj`**: A .NET Console Application targetting `.NET 8.0` or `.NET 9.0`. Handles packet broadcasting.
- **`MockClient/MockClient.csproj`**: A .NET Console Application / Client Simulator targetting `.NET 8.0` or `.NET 9.0`.
- **`Tests/Tests.csproj`**: A C# Unit Test project (using xUnit) targetting `.NET 8.0` or `.NET 9.0`.

### Unity Client Compilation & Folder Layout
- We **should** create the `Client/Assets/Scripts/Codec/` folder structure. This aligns with the path specified in `PROJECT.md` and houses the C# wrapper source files.
- In Unity, files placed in `Assets/` are automatically compiled by Unity's internal compiler. Keeping them as source scripts (`.cs` files) under `Client/Assets/Scripts/Codec/` makes debugging and cross-compiling (especially for Android IL2CPP) much easier than using pre-compiled DLLs.

### Sharing Code between Unity & MSBuild
To run unit tests or client simulations outside of Unity without duplicating code:
- **Recommended Approach (Shared Source files)**:
  In `Tests/Tests.csproj` and `MockClient/MockClient.csproj`, configure MSBuild to compile the source files located inside the Unity project folder:
  ```xml
  <ItemGroup>
    <Compile Include="..\Client\Assets\Scripts\Codec\**\*.cs" Link="Codec\%(RecursiveDir)%(Filename)%(Extension)" />
  </ItemGroup>
  ```
  This links the files at compile time. Any change made to the Opus wrapper in the Unity folder will be immediately tested and run by MSBuild, eliminating duplication and synchronization issues.
- **Alternative Approach (Class Library)**:
  Compile a `.NET Standard 2.1` class library (`Codec.csproj`) containing the wrappers. Build the library and place the compiled DLL into `Client/Assets/Plugins/` (or reference it). While clean for MSBuild, this creates dependency-management overhead inside Unity and complicates IL2CPP native debugging.

---

## 2. Loopback Test Design

To verify the Opus encoding and decoding wrapper works correctly, we will implement an automated **loopback test**. The test generates a synthetic PCM audio signal (a sine wave), encodes it using the `IOpusEncoder` implementation, decodes the resulting packet using `IOpusDecoder`, and runs structural and mathematical assertions on the result.

### Verification Criteria
1. **Compression Check**: The encoded byte buffer must have a size greater than `0` and significantly smaller than the input PCM size (raw PCM size = `sampleCount * 2` bytes).
2. **Data Integrity Check**: The encoded byte buffer must not be all zeros.
3. **Reconstruction Check**: The decoder must return the exact expected number of decoded samples (e.g. `960` samples for a 20ms frame at 48kHz).
4. **Signal Verification**:
   - The decoded PCM must not be all zeros.
   - Since Opus is a lossy codec, the input and output PCM will not match bit-for-bit. However, the root-mean-square (RMS) energy of the output must be close to the input signal (within a 15% tolerance), verifying that the audio structure and amplitude are preserved.

### Verification Code (xUnit)

```csharp
using System;
using Xunit;

namespace VoiceChat.Tests
{
    public class OpusLoopbackTests
    {
        private const int SampleRate = 48000;
        private const int Channels = 1;
        private const int FrameDurationMs = 20; // 2.5, 5, 10, 20, 40, or 60 ms
        private const int FrameSize = (SampleRate * FrameDurationMs) / 1000; // 960 samples

        [Fact]
        public void EncodeDecode_Loopback_ShouldMatchSignalProperties()
        {
            // 1. Arrange: Generate input PCM (440Hz Sine Wave at -6dB)
            short[] inputPcm = new short[FrameSize];
            double frequency = 440.0;
            double amplitude = 0.5 * short.MaxValue;
            for (int i = 0; i < FrameSize; i++)
            {
                double time = (double)i / SampleRate;
                inputPcm[i] = (short)(amplitude * Math.Sin(2 * Math.PI * frequency * time));
            }

            using IOpusEncoder encoder = new OpusEncoder(SampleRate, Channels, OpusApplication.Voip);
            using IOpusDecoder decoder = new OpusDecoder(SampleRate, Channels);

            byte[] encodedBuffer = new byte[FrameSize * sizeof(short)]; // Safe upper bound
            short[] outputPcm = new short[FrameSize];

            // 2. Act: Encode
            int encodedLength = encoder.Encode(inputPcm, encodedBuffer);

            // 3. Act: Decode
            int decodedSamples = decoder.Decode(encodedBuffer.AsSpan(0, encodedLength), outputPcm);

            // 4. Assert: Structural Properties
            Assert.True(encodedLength > 0, "Encoded payload length must be greater than zero.");
            Assert.True(encodedLength < inputPcm.Length * sizeof(short), "Encoded payload must be compressed.");
            Assert.Equal(FrameSize, decodedSamples);

            // Ensure encoded packet is not all zeros
            bool encodedHasData = false;
            for (int i = 0; i < encodedLength; i++)
            {
                if (encodedBuffer[i] != 0) { encodedHasData = true; break; }
            }
            Assert.True(encodedHasData, "Encoded output packet is empty or all zeros.");

            // Ensure decoded PCM is not all zeros
            bool decodedHasData = false;
            for (int i = 0; i < outputPcm.Length; i++)
            {
                if (outputPcm[i] != 0) { decodedHasData = true; break; }
            }
            Assert.True(decodedHasData, "Decoded PCM output is empty or all zeros.");

            // 5. Assert: Audio Quality (RMS Validation)
            double inputRms = CalculateRms(inputPcm);
            double outputRms = CalculateRms(outputPcm);

            Assert.True(inputRms > 0, "Input signal RMS must be greater than zero.");
            Assert.True(outputRms > 0, "Decoded signal RMS must be greater than zero.");

            // Bounded ratio: Decoded RMS should be within 15% of input RMS
            double rmsRatio = outputRms / inputRms;
            Assert.True(rmsRatio >= 0.85 && rmsRatio <= 1.15, 
                $"Decoded RMS energy deviates too much from input. Ratio: {rmsRatio:P2}");
        }

        private static double CalculateRms(short[] signal)
        {
            double sumSquare = 0;
            for (int i = 0; i < signal.Length; i++)
            {
                double normalized = signal[i] / (double)short.MaxValue;
                sumSquare += normalized * normalized;
            }
            return Math.Sqrt(sumSquare / signal.Length);
        }
    }
}
```

---

## 3. Draft Layout of `IOpusEncoder` & `IOpusDecoder`

### Interface Contracts
We extend the contracts in `PROJECT.md` to support both the standard array-based interfaces and high-performance `Span`-based/zero-allocation interfaces.

```csharp
using System;

namespace VoiceChat.Codec
{
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
        /// Encodes 16-bit PCM audio to Opus payload (zero allocation).
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
        /// Decodes an Opus packet to 16-bit PCM audio (zero allocation).
        /// </summary>
        int Decode(ReadOnlySpan<byte> packet, Span<short> output);
    }
}
```

### Enumerations & Types
```csharp
namespace VoiceChat.Codec
{
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
}
```

### P/Invoke Definitions (Native Opus Bridge)
We map native entry points from the Opus shared library. The P/Invoke compiler automatically loads `opus.dll` on Windows and `libopus.so` on Android.

```csharp
using System;
using System.Runtime.InteropServices;

namespace VoiceChat.Codec
{
    internal static class NativeOpus
    {
        private const string DllName = "opus";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_encoder_create")]
        public static extern IntPtr opus_encoder_create(int Fs, int channels, int application, out int error);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_encoder_destroy")]
        public static extern void opus_encoder_destroy(IntPtr encoder);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_encode")]
        public static unsafe extern int opus_encode(IntPtr st, short* pcm, int frame_size, byte* data, int max_data_bytes);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_decoder_create")]
        public static extern IntPtr opus_decoder_create(int Fs, int channels, out int error);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_decoder_destroy")]
        public static extern void opus_decoder_destroy(IntPtr decoder);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_decode")]
        public static unsafe extern int opus_decode(IntPtr st, byte* data, int len, short* pcm, int frame_size, int decode_fec);
    }
}
```

### `OpusEncoder` Implementation
This wrapper encapsulates the native encoder state pointer, implements double-dispose protection, and uses standard finalizers to prevent native memory leaks.

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

            // Opus expects frame size as samples per channel
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

### `OpusDecoder` Implementation
Supports normal decoding as well as Packet Loss Concealment (PLC) / Forward Error Correction (FEC) by passing an empty or null buffer to `opus_decode`.

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

            // Packet can be null in case of packet loss (PLC/FEC recovery)
            ReadOnlySpan<byte> packetSpan = packet != null ? packet.AsSpan() : ReadOnlySpan<byte>.Empty;
            return Decode(packetSpan, output.AsSpan());
        }

        public int Decode(ReadOnlySpan<byte> packet, Span<short> output)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(OpusDecoder));

            // Frame size of the decoded signal must match the sample capacity per channel of the output buffer
            int frameSize = output.Length / _channels;

            unsafe
            {
                fixed (short* pOut = output)
                {
                    int result;
                    if (packet.IsEmpty)
                    {
                        // Pass NULL and length 0 to request Packet Loss Concealment (PLC)
                        result = NativeOpus.opus_decode(_decoderState, null, 0, pOut, frameSize, 0);
                    }
                    else
                    {
                        fixed (byte* pPacket = packet)
                        {
                            // Decodes using normal path (fec = 0)
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
