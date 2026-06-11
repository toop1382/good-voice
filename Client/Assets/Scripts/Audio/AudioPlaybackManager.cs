using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace Client.Audio
{
    /// <summary>
    /// Manages low-latency audio playback of received voice streams.
    /// Each remote client gets its own looping AudioClip + AudioSource.
    ///
    /// Decoded PCM frames are queued from the receive thread and written into
    /// the AudioClip ring buffer via SetData() — called from Tick() on the main thread.
    /// This avoids the unstable PCMReaderCallback API and works on all Unity versions
    /// and all platforms including Android.
    /// </summary>
    public class AudioPlaybackManager : IDisposable
    {
        private class RemoteStream
        {
            public AudioSource Source;
            public AudioClip   Clip;
            public int         WriteFramePos;   // next write offset in frames
            public int         ClipFrames;      // total capacity in frames
            public ConcurrentQueue<float[]> FrameQueue = new();
            public long        LastActivityTimeMs;
            public bool        IsActive;
            public int         LastPlayPos;     // track the last playback position for clearing silence
        }

        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly int _frameSizeInSamples;
        private readonly int _clipFrames;        // ring-buffer length in frames (2 seconds)
        private readonly Transform _audioParent;

        private readonly Dictionary<int, RemoteStream> _streams = new();
        private readonly Dictionary<int, float[]> _silenceArrays = new();

        private float[] GetSilenceArray(int length)
        {
            if (!_silenceArrays.TryGetValue(length, out var array))
            {
                array = new float[length];
                _silenceArrays[length] = array;
            }
            return array;
        }

        /// <param name="sampleRate">e.g. 48000</param>
        /// <param name="channels">1 = mono</param>
        /// <param name="frameSizeInSamples">Frames per Opus packet, e.g. 480</param>
        /// <param name="audioParent">Parent Transform for AudioSource GameObjects</param>
        public AudioPlaybackManager(int sampleRate, int channels, int frameSizeInSamples, Transform audioParent)
        {
            _sampleRate         = sampleRate;
            _channels           = channels;
            _frameSizeInSamples = frameSizeInSamples;
            _clipFrames         = sampleRate * 2; // 2-second ring buffer per remote client
            _audioParent        = audioParent;
        }

        /// <summary>
        /// Queue decoded PCM audio from a remote client for playback.
        /// Thread-safe — call from the receive/decode thread.
        /// </summary>
        public void EnqueueAudio(int clientId, float[] pcmFloats)
        {
            lock (_streams)
            {
                if (_streams.TryGetValue(clientId, out var s))
                {
                    s.FrameQueue.Enqueue(pcmFloats);
                    s.LastActivityTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    s.IsActive = true;
                }
            }
        }

        /// <summary>
        /// Drain queued frames into each client's AudioClip ring buffer.
        /// MUST be called from the Unity main thread (e.g. MonoBehaviour.Update).
        /// </summary>
        public void Tick()
        {
            lock (_streams)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var kvp in _streams)
                {
                    var s = kvp.Value;
                    if (s.Source == null || s.Clip == null) continue;

                    long idleTime = now - s.LastActivityTimeMs;
                    if (idleTime > 1000) // 1 second timeout
                    {
                        if (s.IsActive)
                        {
                            s.IsActive = false;
                            s.Source.Stop();
                            // Clear clip buffer with silence
                            s.Clip.SetData(GetSilenceArray(s.ClipFrames * _channels), 0);
                            s.WriteFramePos = 0;
                            s.LastPlayPos = 0;
                            // Clear queued frames
                            while (s.FrameQueue.TryDequeue(out _)) { }
                        }
                        continue;
                    }

                    if (!s.Source.isPlaying && s.IsActive)
                    {
                        s.Source.Play();
                        s.LastPlayPos = s.Source.timeSamples;
                    }

                    int playPos = s.Source.timeSamples;

                    // 1. Clear played audio with silence to prevent repeat voice on starvation or wrap-around
                    int samplesPlayed = (playPos - s.LastPlayPos + s.ClipFrames) % s.ClipFrames;
                    if (samplesPlayed > 0)
                    {
                        int start = s.LastPlayPos;
                        int end = playPos;
                        if (end > start)
                        {
                            s.Clip.SetData(GetSilenceArray((end - start) * _channels), start);
                        }
                        else
                        {
                            int len1 = s.ClipFrames - start;
                            s.Clip.SetData(GetSilenceArray(len1 * _channels), start);
                            if (end > 0)
                            {
                                s.Clip.SetData(GetSilenceArray(end * _channels), 0);
                            }
                        }
                        s.LastPlayPos = playPos;
                    }

                    // Dynamic target delay based on current frame time (FPS) to prevent stutter on low/spiky frame rates
                    int frameSamples = (int)(Time.unscaledDeltaTime * _sampleRate);
                    int targetDelay = Mathf.Max(3 * _frameSizeInSamples, frameSamples + 2 * _frameSizeInSamples);

                    // Calculate currently buffered samples *before* dequeuing new frames
                    int buffered = (s.WriteFramePos - playPos + s.ClipFrames) % s.ClipFrames;

                    // Pitch scaling thresholds relative to dynamic targetDelay
                    int critLowThreshold  = targetDelay - 2 * _frameSizeInSamples;
                    int modLowThreshold   = targetDelay - _frameSizeInSamples;
                    int modHighThreshold  = targetDelay + _frameSizeInSamples;
                    int critHighThreshold = targetDelay + 3 * _frameSizeInSamples;
                    int snapThreshold     = targetDelay + 6 * _frameSizeInSamples;

                    // Buffer drift/starvation management:
                    // Only perform a hard snap if:
                    // 1. The play pointer overtook the write pointer (starvation: buffered is very large, close to ClipFrames).
                    // 2. The buffer level is too large (lag: buffered is > snapThreshold).
                    if (buffered > s.ClipFrames - _frameSizeInSamples || buffered > snapThreshold)
                    {
                        int newWritePos = (playPos + targetDelay) % s.ClipFrames;
                        int start = playPos;
                        int end = newWritePos;
                        if (end > start)
                        {
                            s.Clip.SetData(GetSilenceArray((end - start) * _channels), start);
                        }
                        else
                        {
                            int len1 = s.ClipFrames - start;
                            s.Clip.SetData(GetSilenceArray(len1 * _channels), start);
                            if (end > 0)
                            {
                                s.Clip.SetData(GetSilenceArray(end * _channels), 0);
                            }
                        }
                        s.WriteFramePos = newWritePos;
                        s.Source.pitch = 1.0f;
                    }
                    else if (buffered < critLowThreshold)
                    {
                        // Critically low buffer: slow down significantly (4%) to allow recovery
                        s.Source.pitch = 0.96f;
                    }
                    else if (buffered < modLowThreshold)
                    {
                        // Moderately low buffer: slow down gently (2%)
                        s.Source.pitch = 0.98f;
                    }
                    else if (buffered > critHighThreshold)
                    {
                        // Critically high buffer: speed up significantly (4%) to drain latency
                        s.Source.pitch = 1.04f;
                    }
                    else if (buffered > modHighThreshold)
                    {
                        // Moderately high buffer: speed up gently (2%)
                        s.Source.pitch = 1.02f;
                    }
                    else
                    {
                        // Ideal buffer range: play at normal speed
                        s.Source.pitch = 1.0f;
                    }

                    while (s.FrameQueue.TryDequeue(out float[] frame))
                    {
                        s.Clip.SetData(frame, s.WriteFramePos);
                        s.WriteFramePos = (s.WriteFramePos + _frameSizeInSamples) % s.ClipFrames;
                    }
                }
            }
        }

        /// <summary>
        /// Register a new remote speaker. Must be called on the main Unity thread.
        /// </summary>
        public void AddClient(int clientId)
        {
            lock (_streams)
            {
                if (_streams.ContainsKey(clientId)) return;

                var go = new GameObject($"VoiceStream_{clientId}");
                go.transform.SetParent(_audioParent, false);

                var source = go.AddComponent<AudioSource>();
                source.spatialBlend = 0f; // 2D (non-spatial) voice
                source.loop         = true;
                source.volume       = 1f;

                // Non-streaming looping clip — AudioClip.Create(name, samples, channels, freq, stream=false)
                // stream=false is universally supported; we manage the ring buffer ourselves via SetData.
                var clip = AudioClip.Create(
                    $"VoiceClip_{clientId}",
                    _clipFrames,
                    _channels,
                    _sampleRate,
                    false
                );

                // Pre-fill with silence so Play() doesn't pop
                clip.SetData(new float[_clipFrames * _channels], 0);

                source.clip = clip;
                source.Play();

                _streams[clientId] = new RemoteStream
                {
                    Source        = source,
                    Clip          = clip,
                    WriteFramePos = 0,
                    ClipFrames    = _clipFrames,
                    LastActivityTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    IsActive      = true,
                    LastPlayPos   = 0,
                };

                Debug.Log($"[AudioPlaybackManager] Added playback stream for client {clientId}");
            }
        }

        /// <summary>
        /// Remove a remote speaker. Must be called on the main Unity thread.
        /// </summary>
        public void RemoveClient(int clientId)
        {
            lock (_streams)
            {
                if (!_streams.TryGetValue(clientId, out var stream)) return;
                stream.Source.Stop();
                UnityEngine.Object.Destroy(stream.Source.gameObject);
                _streams.Remove(clientId);
                Debug.Log($"[AudioPlaybackManager] Removed stream for client {clientId}");
            }
        }

        public void Dispose()
        {
            lock (_streams)
            {
                foreach (var kvp in _streams)
                {
                    kvp.Value.Source?.Stop();
                    if (kvp.Value.Source != null)
                        UnityEngine.Object.Destroy(kvp.Value.Source.gameObject);
                }
                _streams.Clear();
            }
        }
    }
}
