using UnityEngine;

namespace Client.Audio
{
    /// <summary>
    /// Factory that returns the best available IAudioRecorder for the current platform.
    ///
    /// Platform dispatch table:
    /// ┌──────────────────────────┬──────────────────────────────────────────┬────────────────┐
    /// │ Platform                 │ Implementation                           │ Approx latency │
    /// ├──────────────────────────┼──────────────────────────────────────────┼────────────────┤
    /// │ Windows (standalone)     │ WasapiRecorder   — native WASAPI C++ DLL │ ~5–10 ms       │
    /// │ Windows (Editor)         │ WasapiRecorder   — native WASAPI C++ DLL │ ~5–10 ms       │
    /// │ Android (device)         │ AndroidRecorderBridge → Java AudioRecord  │ ~10–20 ms      │
    /// │ macOS (standalone+Editor)│ UnityMicRecorder — Unity Microphone API  │ ~40–80 ms      │
    /// │ iOS                      │ UnityMicRecorder — Unity Microphone API  │ ~40–80 ms      │
    /// │ Linux                    │ UnityMicRecorder — Unity Microphone API  │ ~40–80 ms      │
    /// │ WebGL                    │ UnityMicRecorder — Unity Microphone API  │ browser-limited│
    /// │ tvOS / Other             │ UnityMicRecorder — Unity Microphone API  │ ~40–80 ms      │
    /// └──────────────────────────┴──────────────────────────────────────────┴────────────────┘
    /// </summary>
    public static class AudioRecorderFactory
    {
        /// <param name="sampleRate">Sample rate in Hz — 48000 recommended for Opus VoIP.</param>
        /// <param name="channels">Output channel count. 1 = mono (recommended for voice).</param>
        /// <param name="frameSizeInSamples">
        ///   Samples per Opus frame (per channel).
        ///   Common values: 120 (2.5 ms), 240 (5 ms), 480 (10 ms), 960 (20 ms) at 48 kHz.
        /// </param>
        public static IAudioRecorder Create(int sampleRate = 48000, int channels = 1, int frameSizeInSamples = 480, bool enableAec = true, bool enableNs = true, bool enableAgc = true)
        {
            // ── Android device ────────────────────────────────────────────
#if UNITY_ANDROID && !UNITY_EDITOR
            Debug.Log($"[AudioRecorderFactory] Android → AndroidRecorderBridge (native AudioRecord, AEC={enableAec}, NS={enableNs}, AGC={enableAgc}).");
            return new AndroidRecorderBridge(sampleRate, channels, frameSizeInSamples, enableAec, enableNs, enableAgc);

            // ── Windows standalone or Windows Editor ──────────────────────
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                      return new UnityMicRecorder(sampleRate, channels, frameSizeInSamples);
#elif UNITY_STANDALONE_OSX
            Debug.Log("[AudioRecorderFactory] macOS → UnityMicRecorder (Unity Microphone API).");
            return new UnityMicRecorder(sampleRate, channels, frameSizeInSamples);

            // ── macOS Editor (non-Windows) ────────────────────────────────
#elif UNITY_EDITOR_OSX
            Debug.Log("[AudioRecorderFactory] macOS Editor → UnityMicRecorder (Unity Microphone API).");
            return new UnityMicRecorder(sampleRate, channels, frameSizeInSamples);

            // ── iOS ───────────────────────────────────────────────────────
#elif UNITY_IOS
            Debug.Log("[AudioRecorderFactory] iOS → UnityMicRecorder (Unity Microphone API).");
            // Note: NSMicrophoneUsageDescription must be set in Info.plist
            return new UnityMicRecorder(sampleRate, channels, frameSizeInSamples);

            // ── Linux standalone ──────────────────────────────────────────
#elif UNITY_STANDALONE_LINUX
            Debug.Log("[AudioRecorderFactory] Linux → UnityMicRecorder (Unity Microphone API via PulseAudio/ALSA).");
            return new UnityMicRecorder(sampleRate, channels, frameSizeInSamples);

            // ── WebGL ─────────────────────────────────────────────────────
#elif UNITY_WEBGL
            Debug.Log("[AudioRecorderFactory] WebGL → UnityMicRecorder (Unity Microphone API — browser mic permission required).");
            // WebGL mic requires user gesture to grant permission before calling StartRecording().
            return new UnityMicRecorder(sampleRate, channels, frameSizeInSamples);

            // ── tvOS / visionOS / Other ───────────────────────────────────
#else
            Debug.LogWarning("[AudioRecorderFactory] Unknown/unsupported platform → UnityMicRecorder (Unity Microphone API fallback).");
            return new UnityMicRecorder(sampleRate, channels, frameSizeInSamples);
#endif
        }
    }
}
