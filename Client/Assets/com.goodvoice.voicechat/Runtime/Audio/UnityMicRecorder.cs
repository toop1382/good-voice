using System;
using System.Threading;
using UnityEngine;

namespace Client.Audio
{
    /// <summary>
    /// Fallback recorder using Unity's built-in Microphone API.
    /// Supports macOS, Linux, iOS, WebGL (where supported), tvOS, and
    /// the Unity Editor on any platform.
    ///
    /// Latency is higher than WASAPI/AudioRecord (~40-80ms typical) but
    /// fully functional and cross-platform out-of-the-box.
    ///
    /// Handles:
    ///  - Device enumeration with graceful fallback to the default device
    ///  - Mono downmix when the mic clip has more channels than requested
    ///  - Sample-accurate ring-buffer polling on a background thread
    ///  - Proper cleanup on StopRecording / Dispose
    /// </summary>
    public class UnityMicRecorder : IAudioRecorder
    {
        // ── Public interface ──────────────────────────────────────────────
        public int SampleRate { get; }
        public int Channels { get; }
        public bool IsRecording { get; private set; }

        public event Action<float[]> OnAudioFrameCaptured;

        // ── Internal state ────────────────────────────────────────────────
        private readonly int _frameSizeInSamples;

        private AudioClip _micClip;
        private string _deviceName;
        private int _clipChannels; // actual channels the clip was created with
        private int _clipTotalSamples; // clip.samples * clip.channels (ring-buffer length)
        private int _lastReadSample; // read-head in clip-sample space

        private float[] _rawBuffer; // scratch for multi-channel read
        private float[] _frameBuffer; // mono (or requested-channel) output frame
        private int _micFrameSize;
        private float[] _downmixBuffer;



        // ─────────────────────────────────────────────────────────────────
        /// <param name="sampleRate">Sample rate Hz — 48000 recommended for Opus</param>
        /// <param name="channels">Output channels (1=mono). Input is downmixed if needed.</param>
        /// <param name="frameSizeInSamples">
        ///   Samples per output frame (per channel).
        ///   e.g. 480 = 10 ms at 48 kHz.
        /// </param>
        public UnityMicRecorder(int sampleRate = 48000, int channels = 1, int frameSizeInSamples = 480)
        {
            SampleRate = sampleRate;
            Channels = channels;
            _frameSizeInSamples = frameSizeInSamples;
            _frameBuffer = new float[frameSizeInSamples * channels];
        }

        // ── StartRecording ────────────────────────────────────────────────
        public void StartRecording()
        {
            if (IsRecording) return;

            // Pick best available device
            string[] devices = Microphone.devices;
            if (devices == null || devices.Length == 0)
            {
                Debug.LogError("[UnityMicRecorder] No microphone devices found.");
                return;
            }

            _deviceName = devices[0]; // first device is usually the default
            Debug.Log($"[UnityMicRecorder] Available mics: {string.Join(", ", devices)}");
            Debug.Log($"[UnityMicRecorder] Using: '{_deviceName}'");

            // Clamp sample rate to what the device supports
            Microphone.GetDeviceCaps(_deviceName, out int minFreq, out int maxFreq);
            int actualRate = SampleRate;
            if (maxFreq > 0) // 0 means "supports any rate"
                actualRate = Mathf.Clamp(SampleRate, minFreq, maxFreq);
            if (actualRate != SampleRate)
                Debug.LogWarning(
                    $"[UnityMicRecorder] Requested {SampleRate} Hz but device supports {minFreq}–{maxFreq} Hz. Using {actualRate} Hz.");

            // 2-second ring buffer; loop=true
            _micClip = Microphone.Start(_deviceName, true, 2, actualRate);
            if (_micClip == null)
            {
                Debug.LogError("[UnityMicRecorder] Microphone.Start returned null.");
                return;
            }

            // Wait until the mic actually starts recording (can take a few frames)
            float timeout = Time.realtimeSinceStartup + 1f;
            while (!Microphone.IsRecording(_deviceName) && Time.realtimeSinceStartup < timeout)
                System.Threading.Thread.Sleep(5);

            if (!Microphone.IsRecording(_deviceName))
            {
                Debug.LogError("[UnityMicRecorder] Microphone failed to start within 1 second.");
                Microphone.End(_deviceName);
                return;
            }

            _clipChannels = _micClip.channels;
            _clipTotalSamples = _micClip.samples * _clipChannels; // total interleaved samples in the ring

            _micFrameSize = (int)Math.Round(_frameSizeInSamples * (double)actualRate / SampleRate);

            // Raw scratch buffer: one frame worth of interleaved mic samples (at actualRate)
            _rawBuffer = new float[_micFrameSize * _clipChannels];

            // Downmix buffer: one frame of downmixed mic samples (at actualRate)
            _downmixBuffer = new float[_micFrameSize * Channels];

            // Output frame: one frame of resampled/final samples (at SampleRate)
            _frameBuffer = new float[_frameSizeInSamples * Channels];

            _lastReadSample = 0;
            IsRecording = true;


            Debug.Log(
                $"[UnityMicRecorder] Recording started — {actualRate} Hz, mic-channels={_clipChannels}, output-channels={Channels}, frame={_frameSizeInSamples} samples.");
        }

        // ── StopRecording ─────────────────────────────────────────────────
        public void StopRecording()
        {
            if (!IsRecording) return;
            IsRecording = false;
            if (_deviceName != null)
                Microphone.End(_deviceName);
            _micClip = null;
            Debug.Log("[UnityMicRecorder] Stopped.");
        }

        // ── Main Thread Tick ──────────────────────────────────────────────
        public void Tick()
        {
            if (!IsRecording || _micClip == null) return;

            int writeHead = Microphone.GetPosition(_deviceName); // in frames
            int writeSample = writeHead * _clipChannels; // in clip-samples

            int available = (writeSample - _lastReadSample + _clipTotalSamples) % _clipTotalSamples;
            int frameSamples = _micFrameSize * _clipChannels;

            while (available >= frameSamples)
            {
                int readFrame = _lastReadSample / _clipChannels;

                _micClip.GetData(_rawBuffer, readFrame);

                _lastReadSample = (_lastReadSample + frameSamples) % _clipTotalSamples;

                // 1. Downmix from _clipChannels to Channels (at actualRate)
                BuildOutputFrame(_rawBuffer, _downmixBuffer, _micFrameSize, _clipChannels, Channels);

                // 2. Resample from actualRate to SampleRate (linear interpolation)
                int actualRate = _micClip.frequency;
                if (actualRate != SampleRate)
                {
                    Resample(_downmixBuffer, _frameBuffer, actualRate, SampleRate, Channels);
                }
                else
                {
                    Array.Copy(_downmixBuffer, _frameBuffer, _frameBuffer.Length);
                }

                OnAudioFrameCaptured?.Invoke(_frameBuffer);

                // Recalculate available samples after advancing
                available = (writeSample - _lastReadSample + _clipTotalSamples) % _clipTotalSamples;
            }
        }

        private static void Resample(float[] src, float[] dst, int srcRate, int dstRate, int channels)
        {
            int srcFrames = src.Length / channels;
            int dstFrames = dst.Length / channels;
            double ratio = (double)dstRate / srcRate;

            for (int c = 0; c < channels; c++)
            {
                for (int f = 0; f < dstFrames; f++)
                {
                    double srcFrameIndex = f / ratio;
                    int indexA = (int)Math.Floor(srcFrameIndex);
                    int indexB = indexA + 1;

                    if (indexB >= srcFrames)
                    {
                        dst[f * channels + c] = src[indexA * channels + c];
                    }
                    else
                    {
                        double weight = srcFrameIndex - indexA;
                        float valA = src[indexA * channels + c];
                        float valB = src[indexB * channels + c];
                        dst[f * channels + c] = (float)((1.0 - weight) * valA + weight * valB);
                    }
                }
            }
        }

        // ── Channel downmix helper ─────────────────────────────────────────
        /// <summary>
        /// Copy/downmix <paramref name="srcChannels"/>-channel interleaved source
        /// into <paramref name="dstChannels"/>-channel interleaved destination.
        /// Currently supports: passthrough (src==dst) and stereo→mono.
        /// </summary>
        private static void BuildOutputFrame(float[] src, float[] dst,
            int frames, int srcChannels, int dstChannels)
        {
            if (srcChannels == dstChannels)
            {
                // Direct copy — no downmix needed
                int count = frames * dstChannels;
                Array.Copy(src, dst, count);
                return;
            }

            if (dstChannels == 1)
            {
                // Average all source channels → mono
                float inv = 1f / srcChannels;
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0f;
                    for (int c = 0; c < srcChannels; c++)
                        sum += src[f * srcChannels + c];
                    dst[f] = sum * inv;
                }

                return;
            }

            // Generic: fill each dst channel from the nearest src channel
            for (int f = 0; f < frames; f++)
            {
                for (int dc = 0; dc < dstChannels; dc++)
                {
                    int sc = Mathf.Min(dc, srcChannels - 1);
                    dst[f * dstChannels + dc] = src[f * srcChannels + sc];
                }
            }
        }

        // ── IDisposable ───────────────────────────────────────────────────
        public void Dispose()
        {
            StopRecording();
        }
    }
}