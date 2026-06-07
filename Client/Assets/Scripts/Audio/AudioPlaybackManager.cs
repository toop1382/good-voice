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
        }

        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly int _frameSizeInSamples;
        private readonly int _clipFrames;        // ring-buffer length in frames (2 seconds)
        private readonly Transform _audioParent;

        private readonly Dictionary<int, RemoteStream> _streams = new();

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
                            s.Clip.SetData(new float[s.ClipFrames * _channels], 0);
                            s.WriteFramePos = 0;
                            // Clear queued frames
                            while (s.FrameQueue.TryDequeue(out _)) { }
                        }
                        continue;
                    }

                    if (!s.Source.isPlaying && s.IsActive)
                        s.Source.Play();

                    int playPos = s.Source.timeSamples;
                    int targetDelay = 3 * _frameSizeInSamples; // Jitter buffer delay (e.g. 30ms)

                    while (s.FrameQueue.TryDequeue(out float[] frame))
                    {
                        // Calculate currently buffered samples between write position and play head
                        int buffered = (s.WriteFramePos - playPos + s.ClipFrames) % s.ClipFrames;

                        // Drift/Starvation detection:
                        // If buffer is too small (< 1 frame) or too large (> 8 frames), snap write position
                        if (buffered < _frameSizeInSamples || buffered > 8 * _frameSizeInSamples)
                        {
                            s.WriteFramePos = (playPos + targetDelay) % s.ClipFrames;
                        }

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
