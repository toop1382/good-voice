using System;
using System.Threading;

namespace MockClient.Network
{
    public class LatencyHistogram
    {
        private readonly long[] _buckets = new long[202]; // Index 0 to 200 represent ms, 201 represents >200ms

        public void RecordLatency(double latencyMs)
        {
            int index = (int)Math.Round(latencyMs);
            if (index < 0) index = 0;
            if (index > 201) index = 201;
            Interlocked.Increment(ref _buckets[index]);
        }

        public double GetPercentile(double percentile)
        {
            long total = 0;
            for (int i = 0; i < _buckets.Length; i++)
            {
                total += Interlocked.Read(ref _buckets[i]);
            }

            if (total == 0) return 0.0;

            double target = total * (percentile / 100.0);
            long count = 0;
            for (int i = 0; i < _buckets.Length; i++)
            {
                count += Interlocked.Read(ref _buckets[i]);
                if (count >= target)
                {
                    return i;
                }
            }

            return 200.0;
        }

        public double GetAverage()
        {
            long totalSamples = 0;
            double sum = 0.0;
            for (int i = 0; i < _buckets.Length; i++)
            {
                long val = Interlocked.Read(ref _buckets[i]);
                totalSamples += val;
                sum += val * i;
            }

            return totalSamples > 0 ? sum / totalSamples : 0.0;
        }

        public long GetTotalSamples()
        {
            long total = 0;
            for (int i = 0; i < _buckets.Length; i++)
            {
                total += Interlocked.Read(ref _buckets[i]);
            }
            return total;
        }
    }
}
