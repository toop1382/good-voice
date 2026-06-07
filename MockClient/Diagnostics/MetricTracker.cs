namespace MockClient.Diagnostics;

using System;
using System.Collections.Generic;
using global::MockClient.Models;

public class MetricTracker
{
    private readonly object _lock = new object();
    private uint? _lastSequenceNumber;
    private int _receivedPacketsCount;
    private int _outOfOrderCount;
    private int _duplicateCount;

    private double _averageRtt;
    private int _rttSampleCount;

    private double _jitter;
    private double? _lastTransitTime;

    private uint _minSequenceReceived = uint.MaxValue;
    private uint _maxSequenceReceived = uint.MinValue;
    private readonly HashSet<uint> _receivedSequences = new HashSet<uint>();

    public void RecordPacketReceived(uint sequenceNumber, long sendTimestamp, long receiveTimestamp)
    {
        lock (_lock)
        {
            _receivedPacketsCount++;

            if (sequenceNumber < _minSequenceReceived) _minSequenceReceived = sequenceNumber;
            if (sequenceNumber > _maxSequenceReceived) _maxSequenceReceived = sequenceNumber;

            bool isNew = _receivedSequences.Add(sequenceNumber);
            if (!isNew)
            {
                _duplicateCount++;
                return;
            }

            if (_lastSequenceNumber.HasValue && sequenceNumber < _lastSequenceNumber.Value)
            {
                _outOfOrderCount++;
            }
            _lastSequenceNumber = sequenceNumber;

            // Jitter calculation (RFC 3550)
            double currentTransitTime = receiveTimestamp - sendTimestamp;
            if (_lastTransitTime.HasValue)
            {
                double diff = Math.Abs(currentTransitTime - _lastTransitTime.Value);
                _jitter = _jitter + (diff - _jitter) / 16.0;
            }
            _lastTransitTime = currentTransitTime;
        }
    }

    public void RecordRttSample(long rttMs)
    {
        lock (_lock)
        {
            _rttSampleCount++;
            _averageRtt = _averageRtt + (rttMs - _averageRtt) / _rttSampleCount;
        }
    }

    public ClientMetrics GetMetrics()
    {
        lock (_lock)
        {
            double packetLossRate = 0;
            int lostPackets = 0;
            int totalExpected = 0;

            if (_receivedSequences.Count > 0)
            {
                totalExpected = (int)(_maxSequenceReceived - _minSequenceReceived + 1);
                lostPackets = totalExpected - _receivedSequences.Count;
                if (lostPackets < 0) lostPackets = 0;
                packetLossRate = totalExpected > 0 ? (double)lostPackets / totalExpected * 100.0 : 0;
            }

            return new ClientMetrics
            {
                ReceivedPackets = _receivedPacketsCount,
                DuplicatePackets = _duplicateCount,
                OutOfOrderPackets = _outOfOrderCount,
                LostPackets = lostPackets,
                PacketLossRate = packetLossRate,
                AverageRttMs = _averageRtt,
                JitterMs = _jitter
            };
        }
    }
}

public struct ClientMetrics
{
    public int ReceivedPackets { get; set; }
    public int DuplicatePackets { get; set; }
    public int OutOfOrderPackets { get; set; }
    public int LostPackets { get; set; }
    public double PacketLossRate { get; set; }
    public double AverageRttMs { get; set; }
    public double JitterMs { get; set; }
}
