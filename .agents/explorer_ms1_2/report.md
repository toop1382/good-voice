# Opus Codec Native Wrapper Investigation Report

## 1. Executive Summary
This report details the C# P/Invoke design and memory marshaling patterns required to integrate the native Opus codec library (`opus.dll` on Windows and `libopus.so` on Android) into a Unity client application. Our primary objective is to achieve zero-copy marshaling for PCM audio frames (`short[]` or `float[]`) and encoded packets (`byte[]`) while maintaining a safe, stable, and highly performant pipeline.

---

## 2. Marshaling Patterns (Zero-Copy)

When interfacing managed C# memory with native C libraries, minimizing memory copying (allocations and garbage collection overhead) is critical for low-latency audio applications. Below are the three primary marshaling options, ranked by suitability.

### Option A: Unsafe C# Pointers with `fixed` Statement (Recommended)
This approach pins managed arrays in memory, prevents the GC from moving them, and passes raw pointers directly to the native code. It is scoped to the `fixed` block, ensuring automatic unpinning upon exit.

*   **Syntax Pattern:**
    ```csharp
    public unsafe int Encode(short[] pcm, byte[] output)
    {
        fixed (short* pcmPtr = pcm)
        fixed (byte* outPtr = output)
        {
            return NativeMethods.opus_encode(
                _encoderState,
                (IntPtr)pcmPtr,
                _frameSize,
                (IntPtr)outPtr,
                output.Length
            );
        }
    }
    ```
*   **Pros:**
    *   **True Zero-Copy:** Direct native access to managed memory addresses.
    *   **Extremely Low Overhead:** The CPU overhead of pinning via `fixed` is negligible.
    *   **Slice Support:** Allows offset math (e.g. `(IntPtr)(pcmPtr + offset)`) without array allocation.
*   **Cons:**
    *   Requires the C# project/assembly to enable the `unsafe` compiler flag.
    *   Pointers cannot escape the scope of the `fixed` block or cross `async/await` state machine boundaries.

### Option B: Automatic Blittable Array Pinning (Implicit Marshaler Pinning)
In C#, one-dimensional arrays of blittable types (types that have the same representation in managed and unmanaged memory, such as `byte`, `short`, `float`) are pinned automatically by the CLR/Mono marshaler for the duration of a synchronous P/Invoke call.

*   **Signature Pattern:**
    ```csharp
    [DllImport("opus", CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_encode(
        IntPtr st,
        [In] short[] pcm,
        int frame_size,
        [Out] byte[] data,
        int max_data_bytes
    );
    ```
*   **Pros:**
    *   Does not require `unsafe` blocks or compiler settings.
    *   Syntax is simple and clean.
*   **Cons:**
    *   Cannot specify arbitrary offsets or slice sizes without allocating sub-arrays or writing multiple overloads.
    *   Slightly less explicit; developer has less control over pinning.

### Option C: `GCHandle.Alloc` with `GCHandleType.Pinned` (For Asynchronous Operations)
If native code must write to or read from a C# array asynchronously (i.e. the native call returns immediately but executes a callback later), a `fixed` statement cannot be used because its scope is local. Instead, we pin the object globally.

*   **Syntax Pattern:**
    ```csharp
    GCHandle handle = GCHandle.Alloc(array, GCHandleType.Pinned);
    IntPtr ptr = handle.AddrOfPinnedObject();
    // Pass ptr to native code...
    // When done (e.g., in a callback or resource cleanup):
    handle.Free();
    ```
*   **Pros:**
    *   Survival of pin across methods, asynchronous boundaries, and threads.
*   **Cons:**
    *   Significant GC tracking overhead.
    *   Forgetting to call `Free()` results in a memory leak and pins objects permanently, causing heap fragmentation.
    *   *Note:* Opus encoding and decoding are synchronous blocking calls, so `GCHandle` is **unnecessary** and should be avoided in favor of `fixed`.

---

## 3. Opus Initialization Parameters & Configuration

### A. Core Creation Parameters
Creating an Opus encoder (`opus_encoder_create`) or decoder (`opus_decoder_create`) requires specific arguments:

1.  **Sample Rate (Fs)**: The frequency of the audio signal in Hz. Opus natively supports five specific sample rates:
    *   `8000` (Narrowband)
    *   `12000` (Mediumband)
    *   `16000` (Wideband)
    *   `24000` (Superwideband)
    *   `48000` (Fullband - **Standard choice for VoIP/High-Fidelity**)
2.  **Channels**: The number of audio channels. Must be:
    *   `1` (Mono)
    *   `2` (Stereo)
3.  **Application Type**: Specifies the encoding target mode (only needed for Encoder):
    *   `OPUS_APPLICATION_VOIP = 2048`: Optimizes for speech clarity, lowest latency, and network resilience.
    *   `OPUS_APPLICATION_AUDIO = 2049`: Optimizes for faithful music and high-fidelity broadcast.
    *   `OPUS_APPLICATION_RESTRICTED_LOWDELAY = 2051`: Bypasses speech-specific optimizations for the lowest possible algorithmic delay.

### B. Frame Sizes (Samples Per Channel)
Opus operates on discrete packet frames. The `frame_size` parameter passed to `opus_encode` or `opus_decode` represent the number of samples **per channel**. Valid frame sizes are defined by the duration of the audio slice. For a sample rate of **48000 Hz**, the mappings are:

| Frame Duration | Samples per Channel (`frame_size`) | Total Samples (Mono) | Total Samples (Stereo) |
| :--- | :--- | :--- | :--- |
| **2.5 ms** | 120 | 120 | 240 |
| **5.0 ms** | 240 | 240 | 480 |
| **10.0 ms** | 480 | 480 | 960 |
| **20.0 ms** (VoIP default) | 960 | 960 | 1920 |
| **40.0 ms** | 1920 | 1920 | 3840 |
| **60.0 ms** | 2880 | 2880 | 5760 |

*Crucial Implementation Gotcha:* When allocating the input PCM array for encoding, the array length must be `frame_size * channels`. However, the `frame_size` parameter passed to the P/Invoke function remains exactly `960` (for 20ms frames at 48kHz). Passing `frame_size * channels` as the argument to the native library will result in an error or audio distortion.

### C. Opus Error Codes
The API returns error codes as negative integers. A wrapper must check all return values against these codes:

| Constant Name | Value | Description |
| :--- | :--- | :--- |
| `OPUS_OK` | `0` | Success |
| `OPUS_BAD_ARG` | `-1` | One or more invalid/out-of-range arguments |
| `OPUS_BUFFER_TOOSMALL` | `-2` | Output buffer is too small to write the results |
| `OPUS_INTERNAL_ERROR` | `-3` | An internal native error occurred |
| `OPUS_INVALID_PACKET` | `-4` | The compressed data packet is corrupted or invalid |
| `OPUS_UNIMPLEMENTED` | `-5` | The requested request/CTL is unimplemented |
| `OPUS_INVALID_STATE` | `-6` | Encoder/Decoder structure is uninitialized or corrupted |
| `OPUS_ALLOC_FAIL` | `-7` | Memory allocation failed |

---

## 4. DllImport Signatures for Windows and Android

### A. Library Naming Resolution
*   **Windows**: Expects `opus.dll` under `Plugins/Windows/x86_64/`.
*   **Android**: Expects `libopus.so` under `Plugins/Android/libs/arm64-v8a/` and `Plugins/Android/libs/armeabi-v7a/`.
*   **Resolution Strategy**: By using `[DllImport("opus")]` without extensions or prefixes, the Mono/IL2CPP runtime automatically handles naming conventions:
    *   On Windows: resolves to `opus.dll`.
    *   On Android: resolves to `libopus.so`.

### B. Unsafe Pointer Signatures (Recommended)
This approach gives complete control over memory mapping and avoids managed marshaller translation overhead.

```csharp
using System;
using System.Runtime.InteropServices;

internal static unsafe class NativeMethods
{
    private const string LibName = "opus";

    // --- Error Handling Utility ---
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr opus_strerror(int error);

    // --- Encoder API ---
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr opus_encoder_create(
        int Fs,
        int channels,
        int application,
        out int error
    );

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void opus_encoder_destroy(IntPtr st);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_encode(
        IntPtr st,
        short* pcm,
        int frame_size,
        byte* data,
        int max_data_bytes
    );

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_encode_float(
        IntPtr st,
        float* pcm,
        int frame_size,
        byte* data,
        int max_data_bytes
    );

    // --- Decoder API ---
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr opus_decoder_create(
        int Fs,
        int channels,
        out int error
    );

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void opus_decoder_destroy(IntPtr st);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_decode(
        IntPtr st,
        byte* data,
        int len,
        short* pcm,
        int frame_size,
        int decode_fec
    );

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_decode_float(
        IntPtr st,
        byte* data,
        int len,
        float* pcm,
        int frame_size,
        int decode_fec
    );

    // --- Encoder CTL (Control) ---
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_encoder_ctl(IntPtr st, int request, int value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_encoder_ctl(IntPtr st, int request, out int value);

    // --- Decoder CTL (Control) ---
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_decoder_ctl(IntPtr st, int request, int value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_decoder_ctl(IntPtr st, int request, out int value);
}
```

### C. Safe/Array-Based Signatures (Alternative)
For codebases that strictly disallow the `unsafe` keyword, the following signatures use the standard CLR marshaler.

```csharp
using System;
using System.Runtime.InteropServices;

internal static class SafeNativeMethods
{
    private const string LibName = "opus";

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr opus_encoder_create(int Fs, int channels, int application, out int error);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void opus_encoder_destroy(IntPtr st);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_encode(
        IntPtr st,
        [In] short[] pcm,
        int frame_size,
        [Out] byte[] data,
        int max_data_bytes
    );

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr opus_decoder_create(int Fs, int channels, out int error);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void opus_decoder_destroy(IntPtr st);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_decode(
        IntPtr st,
        [In] byte[] data,
        int len,
        [Out] short[] pcm,
        int frame_size,
        int decode_fec
    );
}
```

### D. Encoder/Decoder CTL Constant Mappings
Since native Opus CTL calls use C varargs (`...`), C# requires overloaded P/Invoke signatures (as defined above). The required CTL operation constants are:

```csharp
// CTL Requests
public const int OPUS_SET_BITRATE_REQUEST = 4002;
public const int OPUS_GET_BITRATE_REQUEST = 4003;
public const int OPUS_SET_BANDWIDTH_REQUEST = 4008;
public const int OPUS_SET_COMPLEXITY_REQUEST = 4010;
public const int OPUS_SET_INBAND_FEC_REQUEST = 4012;
public const int OPUS_SET_PACKET_LOSS_PERC_REQUEST = 4014;
public const int OPUS_SET_SIGNAL_REQUEST = 4024;
public const int OPUS_RESET_STATE = 4028;

// Bitrate Constants
public const int OPUS_AUTO = -1000;
public const int OPUS_BITRATE_MAX = -1;

// Signal Type Constants
public const int OPUS_SIGNAL_AUTO = 3000;
public const int OPUS_SIGNAL_VOICE = 3001;
public const int OPUS_SIGNAL_MUSIC = 3002;
```

---

## 5. Architectural Recommendations

1.  **Bitrate & Complexity Management**:
    *   VoIP application should default to `OPUS_AUTO` bitrate or a standard VoIP range (e.g. `16000` to `32000` bps for Mono speech).
    *   Set Complexity using CTL to `OPUS_SET_COMPLEXITY_REQUEST`. For mobile (Android), a lower complexity (e.g. `2` or `3`) reduces CPU utilization, while on desktop (Windows), complexity `5` to `8` provides better audio quality at similar bitrates.
2.  **Forward Error Correction (FEC) & Packet Loss Resilience**:
    *   Enable inband FEC: `opus_encoder_ctl(encoderState, OPUS_SET_INBAND_FEC_REQUEST, 1)`.
    *   Dynamically update the packet loss estimate based on network conditions: `opus_encoder_ctl(encoderState, OPUS_SET_PACKET_LOSS_PERC_REQUEST, lossPercentage)`.
    *   During decode, if a packet is lost, pass `null` (or an empty pointer) and setting `decode_fec = 1` inside `opus_decode` to invoke PLC (Packet Loss Concealment) or FEC decoding.
3.  **State Safety / IDisposable Wrapper**:
    *   Wrap the `IntPtr` encoder/decoder handles in a class implementing `IDisposable`.
    *   Ensure that `opus_encoder_destroy` or `opus_decoder_destroy` is called in the `Dispose` method to prevent unmanaged native memory leaks.
