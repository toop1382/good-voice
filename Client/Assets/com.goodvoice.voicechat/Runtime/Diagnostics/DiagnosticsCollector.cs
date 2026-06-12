using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace Client.Diagnostics
{
    /// <summary>
    /// Helper to track rolling packet loss in a sliding window of expected packets.
    /// Thread-safe when accessed under a lock.
    /// </summary>
    public class RollingPacketLossTracker
    {
        private const int WindowSize = 100;
        private long _minSeq = -1;
        private long _maxSeq = -1;
        private readonly HashSet<long> _receivedSeqs = new();

        public void RecordPacket(long seq)
        {
            if (_minSeq == -1)
            {
                _minSeq = seq;
                _maxSeq = seq;
                _receivedSeqs.Add(seq);
                return;
            }

            if (seq > _maxSeq)
            {
                _maxSeq = seq;
                _receivedSeqs.Add(seq);
                // Evict sequences older than the sliding window
                _receivedSeqs.RemoveWhere(s => s <= _maxSeq - WindowSize);
            }
            else if (seq >= _maxSeq - WindowSize && seq >= _minSeq)
            {
                _receivedSeqs.Add(seq);
            }
        }

        public float GetPacketLossPercent()
        {
            if (_maxSeq == -1) return 0f;
            
            long range = _maxSeq - _minSeq + 1;
            long expected = Math.Min(range, WindowSize);
            long received = _receivedSeqs.Count;
            
            long lost = expected - received;
            if (lost < 0) lost = 0;
            
            return 100f * lost / expected;
        }
    }

    /// <summary>
    /// Tracks per-client metrics like RFC 3550 Jitter and Rolling Packet Loss.
    /// </summary>
    public class ClientDiagnostics
    {
        public RollingPacketLossTracker LossTracker { get; } = new();
        public double Jitter { get; set; } = 0.0;
        public long LastReceiveTimestamp { get; set; } = -1;
        public long LastSendTimestamp { get; set; } = -1;
    }

    /// <summary>
    /// Collects real-time voice chat metrics: RTT, jitter, packet loss, encode time, bandwidth.
    /// Thread-safe — can be updated from receive/encode threads and read from the main thread.
    /// </summary>
    public class DiagnosticsCollector
    {
        private const int SampleWindow = 50; // Rolling window for latency smoothing

        // --- RTT ---
        private double _latestRttMs;
        private readonly ConcurrentQueue<double> _rttSamples = new();
        public double AverageRttMs { get; private set; }

        // --- Per-Client Stats ---
        private readonly ConcurrentDictionary<int, ClientDiagnostics> _clientStats = new();

        public float PacketLossPercent
        {
            get
            {
                if (_clientStats.IsEmpty) return 0f;
                float sum = 0f;
                int count = 0;
                foreach (var stats in _clientStats.Values)
                {
                    lock (stats)
                    {
                        sum += stats.LossTracker.GetPacketLossPercent();
                        count++;
                    }
                }
                return count > 0 ? sum / count : 0f;
            }
        }

        public double AverageJitterMs
        {
            get
            {
                if (_clientStats.IsEmpty) return 0.0;
                double sum = 0.0;
                int count = 0;
                foreach (var stats in _clientStats.Values)
                {
                    lock (stats)
                    {
                        sum += stats.Jitter;
                        count++;
                    }
                }
                return count > 0 ? sum / count : 0.0;
            }
        }

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

        public DiagnosticsCollector()
        {
            _lastBandwidthResetMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>Record an RTT measurement from a handshake/heartbeat echo.</summary>
        public void RecordRtt(double rttMs)
        {
            _latestRttMs = rttMs;
            _rttSamples.Enqueue(rttMs);
            if (_rttSamples.Count > SampleWindow) _rttSamples.TryDequeue(out _);
            AverageRttMs = Average(_rttSamples);
        }

        /// <summary>Record reception of an audio packet from a remote client.</summary>
        public void RecordPacketReceived(int clientId, long sequenceNumber, int payloadBytes, long sendTimestamp)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var stats = _clientStats.GetOrAdd(clientId, _ => new ClientDiagnostics());
            lock (stats)
            {
                // 1. Packet Loss
                stats.LossTracker.RecordPacket(sequenceNumber);

                // 2. RFC 3550 Jitter
                if (stats.LastReceiveTimestamp != -1 && stats.LastSendTimestamp != -1)
                {
                    double transitCurrent = nowMs - sendTimestamp;
                    double transitPrevious = stats.LastReceiveTimestamp - stats.LastSendTimestamp;
                    double d = transitCurrent - transitPrevious;
                    stats.Jitter = stats.Jitter + (Math.Abs(d) - stats.Jitter) / 16.0;
                }

                stats.LastReceiveTimestamp = nowMs;
                stats.LastSendTimestamp = sendTimestamp;
            }

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

        /// <summary>Removes a client's tracked diagnostic stats when they disconnect/timeout.</summary>
        public void RemoveClient(int clientId)
        {
            _clientStats.TryRemove(clientId, out _);
        }

        /// <summary>Resets all metrics.</summary>
        public void Reset()
        {
            _clientStats.Clear();
            _rttSamples.Clear();
            _encodeTimeSamples.Clear();
            AverageRttMs = 0;
            AverageEncodeMs = 0;
            _bytesSentThisSecond = 0;
            _bytesReceivedThisSecond = 0;
            SendKbps = 0;
            RecvKbps = 0;
            InputLevelRms = 0;
        }

        /// <summary>Call once per second to compute bandwidth stats.</summary>
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
