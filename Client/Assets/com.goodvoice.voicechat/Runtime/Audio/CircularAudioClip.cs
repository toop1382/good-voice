using UnityEngine;

namespace Client.Audio
{
    /// <summary>
    /// Used to arrange irregular, out of order and skipped audio segments for better playback.
    /// </summary>
    public class CircularAudioClip
    {
        public AudioClip AudioClip { get; private set; }
        public int SegCount { get; private set; }
        public int SegDataLen { get; private set; } // Total float count in a single segment (samples * channels)
        public int Channels { get; private set; }

        // Holds the first valid segment index received by the buffer to make sure that future
        // writes are not of older indices
        public int FirstIndex { get; private set; } = -1;

        private readonly float[] _singleSegZeroBuffer;
        private readonly float[] _allSegZeroBuffer;
        private readonly float[] _writeScratchBuffer;

        /// <summary>
        /// Create an instance
        /// </summary>
        public CircularAudioClip(
            int frequency,
            int channels,
            int segDataLen,
            int segCount = 3,
            string clipName = null
        ) {
            clipName = clipName ?? "clip";
            Channels = channels;
            SegDataLen = segDataLen;
            SegCount = segCount;
            FirstIndex = -1;

            _singleSegZeroBuffer = new float[segDataLen];
            _allSegZeroBuffer = new float[segDataLen * segCount];
            _writeScratchBuffer = new float[segDataLen];

            // In Unity, AudioClip.Create length is in sample frames (samples per channel)
            int totalSamples = segDataLen * segCount;
            int lengthSamples = totalSamples / channels;

            AudioClip = AudioClip.Create(
                clipName,
                lengthSamples,
                channels,
                frequency,
                false
            );
        }

        /// <summary>
        /// Resets the buffer's alignment so that the specified absoluteIndex maps to targetLocalIndex.
        /// </summary>
        public void Reset(int absoluteIndex, int targetLocalIndex)
        {
            // We want Mod(absoluteIndex - FirstIndex, SegCount) == targetLocalIndex
            // This is satisfied by setting FirstIndex = absoluteIndex - targetLocalIndex.
            FirstIndex = absoluteIndex - targetLocalIndex;
        }

        /// <summary>
        /// Feed an audio segment to the buffer.
        /// </summary>
        public bool Write(int absoluteIndex, float[] audioSegment) {
            // Reject if the segment is too small
            if (audioSegment.Length < SegDataLen) return false;

            // Reject if the index is older than our starting point
            if (absoluteIndex < 0 || (FirstIndex != -1 && absoluteIndex < FirstIndex)) return false;

            // If this is the first segment fed
            if (FirstIndex == -1) FirstIndex = absoluteIndex;

            // Convert the absolute index into a looped-around index
            var localIndex = GetNormalizedIndex(absoluteIndex);

            // Set the segment at the clip data at the right index
            if (localIndex >= 0) {
                int offsetSamples = localIndex * (SegDataLen / Channels);
                System.Array.Copy(audioSegment, 0, _writeScratchBuffer, 0, SegDataLen);
                AudioClip.SetData(_writeScratchBuffer, offsetSamples);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the index after looping around the buffer (0 to SegCount - 1)
        /// </summary>
        public int GetNormalizedIndex(int absoluteIndex) {
            if (FirstIndex == -1 || absoluteIndex < FirstIndex) return -1;
            return Mod(absoluteIndex - FirstIndex, SegCount);
        }

        /// <summary>
        /// Clears the buffer at the specified absolute index
        /// </summary>
        public bool Clear(int absoluteIndex) {
            if (absoluteIndex < 0) return false;

            int localIndex = GetNormalizedIndex(absoluteIndex);
            if (localIndex < 0) return false;

            int offsetSamples = localIndex * (SegDataLen / Channels);
            AudioClip.SetData(_singleSegZeroBuffer, offsetSamples);
            return true;
        }

        /// <summary>
        /// Clear the entire buffer
        /// </summary>
        public void Clear() {
            AudioClip.SetData(_allSegZeroBuffer, 0);
            FirstIndex = -1;
        }

        private int Mod(int a, int b) {
            int r = a % b;
            return r < 0 ? r + b : r;
        }
    }
}
