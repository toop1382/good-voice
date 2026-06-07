using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace Client.Diagnostics
{
    /// <summary>
    /// Collects real-time voice chat metrics: RTT, jitter, packet loss, encode time, bandwidth.
    /// Thread-safe — can be updated from receive/encode threads and read from the main thread.
    /// </summary>
    public class DiagnosticsCollector
    {
        private const int SampleWindow = 50; // Rolling window for latency/jitter smoothing

        // --- RTT ---
        private double _latestRttMs;
        private readonly ConcurrentQueue<double> _rttSamples = new();
        public double AverageRttMs { get; private set; }

        // --- Jitter ---
        private long _lastReceiveMs;
        private readonly ConcurrentQueue<double> _jitterSamples = new();
        public double AverageJitterMs { get; private set; }

        // --- Packet Loss ---
        private long _packetsExpected;
        private long _packetsReceived;
        public float PacketLossPercent => _packetsExpected == 0 ? 0f
            : 100f * (1f - (float)_packetsReceived / _packetsExpected);

        // --- Encode Latency ---
        private readonly ConcurrentQueue<double> _encodeTimeSamples = new();
        public double AverageEncodeMs { get; private set; }

        // --- Bandwidth ---
        private long _bytesSentThisSecond;
        private long _bytesReceivedThisSecond;
        private long _lastBandwidthResetMs;
        public float SendKbps { get; private set; }
        public float RecvKbps { get; private set; }

        // --- Audio level ---
        public float InputLevelRms { get; private set; }

        // --- Per-client stats ---
        private readonly ConcurrentDictionary<int, long> _perClientLastSeq = new();

        public DiagnosticsCollector()
        {
            _lastBandwidthResetMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>Record an RTT measurement from a handshake echo.</summary>
        public void RecordRtt(double rttMs)
        {
            _latestRttMs = rttMs;
            _rttSamples.Enqueue(rttMs);
            if (_rttSamples.Count > SampleWindow) _rttSamples.TryDequeue(out _);
            AverageRttMs = Average(_rttSamples);
        }

        /// <summary>Record reception of an audio packet from a remote client.</summary>
        public void RecordPacketReceived(int clientId, long sequenceNumber, int payloadBytes)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Jitter: inter-arrival time variance
            if (_lastReceiveMs > 0)
            {
                double interArrival = nowMs - _lastReceiveMs;
                _jitterSamples.Enqueue(interArrival);
                if (_jitterSamples.Count > SampleWindow) _jitterSamples.TryDequeue(out _);
                AverageJitterMs = Average(_jitterSamples);
            }
            _lastReceiveMs = nowMs;

            // Packet loss estimation based on sequence gaps per client
            if (_perClientLastSeq.TryGetValue(clientId, out long lastSeq))
            {
                long expected = sequenceNumber - lastSeq;
                if (expected > 0)
                {
                    System.Threading.Interlocked.Add(ref _packetsExpected, expected);
                    System.Threading.Interlocked.Add(ref _packetsReceived, 1);
                }
            }
            _perClientLastSeq[clientId] = sequenceNumber;

            // Bandwidth
            System.Threading.Interlocked.Add(ref _bytesReceivedThisSecond, payloadBytes);
        }

        /// <summary>Record time spent encoding one audio frame (in ms).</summary>
        public void RecordEncodeTime(double encodeMs)
        {
            _encodeTimeSamples.Enqueue(encodeMs);
            if (_encodeTimeSamples.Count > SampleWindow) _encodeTimeSamples.TryDequeue(out _);
            AverageEncodeMs = Average(_encodeTimeSamples);
        }

        /// <summary>Record audio bytes sent.</summary>
        public void RecordBytesSent(int bytes)
        {
            System.Threading.Interlocked.Add(ref _bytesSentThisSecond, bytes);
        }

        /// <summary>Record RMS audio level from a captured frame [0..1].</summary>
        public void RecordInputLevel(float[] pcm)
        {
            double sumSq = 0;
            foreach (var s in pcm) sumSq += s * s;
            InputLevelRms = (float)Math.Sqrt(sumSq / pcm.Length);
        }

        /// <summary>Call once per second (e.g. from a coroutine) to compute bandwidth stats.</summary>
        public void Tick()
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            double elapsed = (nowMs - _lastBandwidthResetMs) / 1000.0;
            if (elapsed < 0.001) return;

            SendKbps = (float)(_bytesSentThisSecond * 8 / elapsed / 1000.0);
            RecvKbps = (float)(_bytesReceivedThisSecond * 8 / elapsed / 1000.0);

            System.Threading.Interlocked.Exchange(ref _bytesSentThisSecond, 0);
            System.Threading.Interlocked.Exchange(ref _bytesReceivedThisSecond, 0);
            _lastBandwidthResetMs = nowMs;
        }

        private static double Average(ConcurrentQueue<double> queue)
        {
            double sum = 0;
            int count = 0;
            foreach (var v in queue) { sum += v; count++; }
            return count > 0 ? sum / count : 0;
        }

        public DiagnosticsSnapshot GetSnapshot() => new DiagnosticsSnapshot
        {
            RttMs = _latestRttMs,
            AvgRttMs = AverageRttMs,
            AvgJitterMs = AverageJitterMs,
            PacketLossPercent = PacketLossPercent,
            AvgEncodeMs = AverageEncodeMs,
            SendKbps = SendKbps,
            RecvKbps = RecvKbps,
            InputLevelRms = InputLevelRms
        };
    }

    public struct DiagnosticsSnapshot
    {
        public double RttMs;
        public double AvgRttMs;
        public double AvgJitterMs;
        public float PacketLossPercent;
        public double AvgEncodeMs;
        public float SendKbps;
        public float RecvKbps;
        public float InputLevelRms;
    }
}
