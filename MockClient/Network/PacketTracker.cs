using System;

namespace MockClient.Network
{
    public class PacketTracker
    {
        private readonly object _lock = new();
        private uint _maxSeq;
        private uint _minSeq;
        private ulong _bitmask;
        private bool _initialized;

        public int ReceivedCount { get; private set; }
        public int OutOfOrderCount { get; private set; }
        public int DuplicateCount { get; private set; }
        public int TooLateCount { get; private set; }

        public int ExpectedCount
        {
            get
            {
                lock (_lock)
                {
                    return _initialized ? (int)(_maxSeq - _minSeq + 1) : 0;
                }
            }
        }

        public int LostCount
        {
            get
            {
                lock (_lock)
                {
                    int expected = ExpectedCount;
                    // LostCount is expected minus actual unique received count
                    // Let's calculate actual unique received count which is expected - lost
                    // Note that our ReceivedCount includes duplicates and out-of-order packets.
                    // Let's compute unique received count directly from the bitmask or keep a count.
                    // Wait, let's keep a unique received count! That is much simpler and more accurate!
                    return expected - UniqueReceivedCount;
                }
            }
        }

        public int UniqueReceivedCount { get; private set; }

        public double LossRate
        {
            get
            {
                lock (_lock)
                {
                    int expected = ExpectedCount;
                    return expected > 0 ? (double)LostCount / expected : 0.0;
                }
            }
        }

        public void RecordPacket(uint seq)
        {
            lock (_lock)
            {
                if (!_initialized)
                {
                    _maxSeq = seq;
                    _minSeq = seq;
                    _bitmask = 1UL;
                    _initialized = true;
                    ReceivedCount = 1;
                    UniqueReceivedCount = 1;
                    return;
                }

                if (seq > _maxSeq)
                {
                    uint diff = seq - _maxSeq;
                    if (diff < 64)
                    {
                        _bitmask = (_bitmask << (int)diff) | 1UL;
                    }
                    else
                    {
                        _bitmask = 1UL; // Window jumped too far, reset mask
                    }
                    _maxSeq = seq;
                    ReceivedCount++;
                    UniqueReceivedCount++;
                }
                else if (seq < _minSeq)
                {
                    _minSeq = seq;
                    ReceivedCount++;
                    UniqueReceivedCount++;
                    OutOfOrderCount++;
                }
                else
                {
                    uint diff = _maxSeq - seq;
                    if (diff < 64)
                    {
                        ulong bit = 1UL << (int)diff;
                        if ((_bitmask & bit) != 0)
                        {
                            DuplicateCount++;
                        }
                        else
                        {
                            _bitmask |= bit;
                            ReceivedCount++;
                            UniqueReceivedCount++;
                            OutOfOrderCount++;
                        }
                    }
                    else
                    {
                        // Too old to track in the 64-bit window
                        TooLateCount++;
                        ReceivedCount++;
                        UniqueReceivedCount++;
                        OutOfOrderCount++;
                    }
                }
            }
        }
    }
}
