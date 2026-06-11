using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace Client.Audio
{
    /// <summary>
    /// Manages low-latency audio playback of received voice streams.
    /// Each remote client gets its own looping AudioClip + AudioSource,
    /// backed by a CircularAudioClip for robust out-of-order buffer writing.
    /// </summary>
    public class AudioPlaybackManager : IDisposable
    {
        private class RemoteStream
        {
            public AudioSource Source;
            public CircularAudioClip Clip;
            public int         LatestAbsoluteIndex = -1;
            public ConcurrentQueue<(int absoluteIndex, float[] pcm)> FrameQueue = new();
            public long        LastActivityTimeMs;
            public bool        IsActive;
            public int         LastPlayPos;     // track the last playback position for clearing silence
            public bool        IsBuffering = true;
        }

        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly int _frameSizeInSamples;
        private readonly int _clipFrames;        // ring-buffer length in frames (2 seconds)
        private readonly Transform _audioParent;

        private readonly ConcurrentDictionary<int, RemoteStream> _streams = new();
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
        public void EnqueueAudio(int clientId, int absoluteIndex, float[] pcmFloats)
        {
            if (_streams.TryGetValue(clientId, out var s))
            {
                s.FrameQueue.Enqueue((absoluteIndex, pcmFloats));
                s.LastActivityTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                s.IsActive = true;
            }
        }

        /// <summary>
        /// Drain queued frames into each client's AudioClip ring buffer.
        /// MUST be called from the Unity main thread (e.g. MonoBehaviour.Update).
        /// </summary>
        public void Tick()
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
                            s.Clip.Clear();
                            s.LatestAbsoluteIndex = -1;
                            s.LastPlayPos = 0;
                            s.IsBuffering = true;
                            // Clear queued frames
                            while (s.FrameQueue.TryDequeue(out _)) { }
                        }
                        continue;
                    }

                    // Dequeue new frames and write them FIRST to get the most up-to-date write position
                    if (!s.IsBuffering)
                    {
                        while (s.FrameQueue.TryDequeue(out var item))
                        {
                            s.Clip.Write(item.absoluteIndex, item.pcm);
                            s.LatestAbsoluteIndex = item.absoluteIndex;
                        }
                    }

                    // Fixed target delay to absorb network jitter (e.g. 8 frames = 80ms at 10ms/frame)
                    // This provides a stable goalpost for the jitter buffer, preventing erratic pitch shifts.
                    int targetDelay = 8 * _frameSizeInSamples;

                    // Playout/Jitter Buffering state management
                    if (s.IsBuffering)
                    {
                        int bufferedInQueue = s.FrameQueue.Count * _frameSizeInSamples;
                        // Buffer at least targetDelay + 2 extra frames to absorb initial network arrival jitter
                        int minBufferingSamples = targetDelay + 2 * _frameSizeInSamples;
                        if (bufferedInQueue >= minBufferingSamples)
                        {
                            int playPos = s.Source.timeSamples;
                            s.LastPlayPos = playPos;

                            // We want to align the first frame in the queue to be targetDelay samples ahead of playPos
                            int targetWriteSamplePos = (playPos + targetDelay) % _clipFrames;
                            int targetLocalIndex = targetWriteSamplePos / _frameSizeInSamples;

                            if (s.FrameQueue.TryPeek(out var firstItem))
                            {
                                s.Clip.Reset(firstItem.absoluteIndex, targetLocalIndex);
                            }

                            // Drain all accumulated audio frames into the ring buffer
                            while (s.FrameQueue.TryDequeue(out var item))
                            {
                                s.Clip.Write(item.absoluteIndex, item.pcm);
                                s.LatestAbsoluteIndex = item.absoluteIndex;
                            }

                            s.IsBuffering = false;
                            s.Source.Play();
                            s.Source.pitch = 1.0f;
                        }
                        else
                        {
                            // Keep buffering, do not drain queue or play yet
                            continue;
                        }
                    }

                    if (!s.Source.isPlaying && s.IsActive && !s.IsBuffering)
                    {
                        s.Source.Play();
                        s.LastPlayPos = s.Source.timeSamples;
                    }

                    int playPosVal = s.Source.timeSamples;

                    // Calculate currently buffered samples *before* dequeuing new frames
                    int nextLocalIndex = (s.LatestAbsoluteIndex != -1)
                        ? s.Clip.GetNormalizedIndex(s.LatestAbsoluteIndex + 1)
                        : 0;
                    if (nextLocalIndex < 0) nextLocalIndex = 0;
                    int writeSamplePos = nextLocalIndex * _frameSizeInSamples;

                    int buffered = (writeSamplePos - playPosVal + _clipFrames) % _clipFrames;

                    // Starvation detection:
                    // If play position catches up to (or overtakes) the write position, we enter buffering state.
                    bool starved = (buffered < _frameSizeInSamples / 2) || (buffered > _clipFrames - _frameSizeInSamples);
                    if (starved)
                    {
                        s.Source.Pause();
                        s.IsBuffering = true;
                        s.Clip.Clear();
                        s.LatestAbsoluteIndex = -1;
                        continue;
                    }

                    // Pitch scaling and hard snap thresholds relative to targetDelay
                    int critLowThreshold  = targetDelay - 4 * _frameSizeInSamples;
                    int modLowThreshold   = targetDelay - 2 * _frameSizeInSamples;
                    int modHighThreshold  = targetDelay + 2 * _frameSizeInSamples;
                    int critHighThreshold = targetDelay + 4 * _frameSizeInSamples;
                    int snapThreshold     = targetDelay + 10 * _frameSizeInSamples;

                    // Lag correction (hard snap):
                    // If latency builds up too high (lag > snapThreshold), bring the write pointer back close to the playhead.
                    if (buffered > snapThreshold)
                    {
                        int newWritePos = (playPosVal + targetDelay) % _clipFrames;

                        // Clear the gap between the playhead and the new write pointer with silence
                        int start = playPosVal;
                        int end = newWritePos;
                        if (end > start)
                        {
                            s.Clip.AudioClip.SetData(GetSilenceArray((end - start) * _channels), start);
                        }
                        else
                        {
                            int len1 = _clipFrames - start;
                            s.Clip.AudioClip.SetData(GetSilenceArray(len1 * _channels), start);
                            if (end > 0)
                            {
                                s.Clip.AudioClip.SetData(GetSilenceArray(end * _channels), 0);
                            }
                        }

                        // Align the next frame to newWritePos
                        int targetLocalIndex = newWritePos / _frameSizeInSamples;
                        if (s.FrameQueue.TryPeek(out var nextItem))
                        {
                            s.Clip.Reset(nextItem.absoluteIndex, targetLocalIndex);
                        }
                        else if (s.LatestAbsoluteIndex != -1)
                        {
                            s.Clip.Reset(s.LatestAbsoluteIndex + 1, targetLocalIndex);
                        }

                        s.Source.pitch = 1.0f;
                        buffered = targetDelay;
                    }
                    else if (buffered < critLowThreshold)
                    {
                        // Critically low buffer: slow down slightly to allow recovery
                        s.Source.pitch = 0.98f;
                    }
                    else if (buffered < modLowThreshold)
                    {
                        // Moderately low buffer: slow down gently
                        s.Source.pitch = 0.99f;
                    }
                    else if (buffered > critHighThreshold)
                    {
                        // Critically high buffer: speed up slightly to drain latency
                        s.Source.pitch = 1.02f;
                    }
                    else if (buffered > modHighThreshold)
                    {
                        // Moderately high buffer: speed up gently
                        s.Source.pitch = 1.01f;
                    }
                    else
                    {
                        // Ideal buffer range: play at normal speed
                        s.Source.pitch = 1.0f;
                    }
                }
        }

        /// <summary>
        /// Register a new remote speaker. Must be called on the main Unity thread.
        /// </summary>
        public void AddClient(int clientId)
        {
            if (_streams.ContainsKey(clientId)) return;

                var go = new GameObject($"VoiceStream_{clientId}");
                go.transform.SetParent(_audioParent, false);

                var source = go.AddComponent<AudioSource>();
                source.spatialBlend = 0f; // 2D (non-spatial) voice
                source.loop         = true;
                source.volume       = 1f;
                source.priority     = 0;  // Highest priority to prevent being stolen by sound effects

                // Circular audio clip wraps the Unity AudioClip to handle out-of-order writes
                var circularClip = new CircularAudioClip(
                    _sampleRate,
                    _channels,
                    _frameSizeInSamples * _channels, // segDataLen (total floats)
                    _clipFrames / _frameSizeInSamples, // segCount
                    $"VoiceClip_{clientId}"
                );

                // Pre-fill with silence so Play() doesn't pop
                circularClip.Clear();

                source.clip = circularClip.AudioClip;
                source.Pause();

                _streams[clientId] = new RemoteStream
                {
                    Source        = source,
                    Clip          = circularClip,
                    LatestAbsoluteIndex = -1,
                    LastActivityTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    IsActive      = true,
                    LastPlayPos   = 0,
                    IsBuffering   = true,
                };

                Debug.Log($"[AudioPlaybackManager] Added playback stream for client {clientId}");
        }

        /// <summary>
        /// Remove a remote speaker. Must be called on the main Unity thread.
        /// </summary>
        public void RemoveClient(int clientId)
        {
            if (!_streams.TryRemove(clientId, out var stream)) return;
            stream.Source.Stop();
            UnityEngine.Object.Destroy(stream.Source.gameObject);
            Debug.Log($"[AudioPlaybackManager] Removed stream for client {clientId}");
        }

        public void Dispose()
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
