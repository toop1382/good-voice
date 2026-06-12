using System;

namespace Client.Audio
{
    /// <summary>
    /// Platform-agnostic interface for low-latency audio capture.
    /// Implementations: WasapiRecorder (Windows), AndroidRecorderBridge (Android).
    /// </summary>
    public interface IAudioRecorder : IDisposable
    {
        /// <summary>Sample rate in Hz (e.g. 48000).</summary>
        int SampleRate { get; }

        /// <summary>Number of channels (1 = mono).</summary>
        int Channels { get; }

        /// <summary>
        /// Fired on the recording thread with a buffer of interleaved float PCM samples.
        /// The buffer is reused after the callback returns — copy if you need to retain it.
        /// </summary>
        event Action<float[]> OnAudioFrameCaptured;

        void StartRecording();
        void StopRecording();
        bool IsRecording { get; }

        /// <summary>
        /// Update method called from the main thread (MonoBehaviour.Update).
        /// Used by poll-based recorders like UnityMicRecorder.
        /// </summary>
        void Tick();
    }
}
