using System;

namespace Client.Codec
{
    public class OpusEncoder : IOpusEncoder
    {
        private int bitrate;
        public int Bitrate
        {
            get => bitrate;
            set
            {
                if (encoder != IntPtr.Zero)
                {
                    UnityOpusLibrary.OpusEncoderSetBitrate(encoder, value);
                    bitrate = value;
                }
            }
        }

        private int complexity;
        public int Complexity
        {
            get => complexity;
            set
            {
                if (encoder != IntPtr.Zero)
                {
                    UnityOpusLibrary.OpusEncoderSetComplexity(encoder, value);
                    complexity = value;
                }
            }
        }

        private OpusSignal signal;
        public OpusSignal Signal
        {
            get => signal;
            set
            {
                if (encoder != IntPtr.Zero)
                {
                    UnityOpusLibrary.OpusEncoderSetSignal(encoder, value);
                    signal = value;
                }
            }
        }

        private IntPtr encoder;
        private readonly NumChannels channels;

        public OpusEncoder(
            SamplingFrequency samplingFrequency,
            NumChannels channels,
            OpusApplication application)
        {
            this.channels = channels;
            ErrorCode error;
            encoder = UnityOpusLibrary.OpusEncoderCreate(
                samplingFrequency,
                channels,
                application,
                out error);
            if (error != ErrorCode.OK)
            {
#if UNITY_5_3_OR_NEWER || UNITY_2017_1_OR_NEWER
                UnityEngine.Debug.LogError("[UnityOpus] Failed to init encoder. Error code: " + error.ToString());
#else
                Console.WriteLine("[UnityOpus] Failed to init encoder. Error code: " + error.ToString());
#endif
                encoder = IntPtr.Zero;
            }
        }

        public int Encode(float[] pcm, byte[] output)
        {
            if (encoder == IntPtr.Zero)
            {
                return 0;
            }
            return UnityOpusLibrary.OpusEncodeFloat(
                encoder,
                pcm,
                pcm.Length / (int)channels,
                output,
                output.Length
            );
        }

        #region IDisposable Support
        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (encoder != IntPtr.Zero)
                {
                    UnityOpusLibrary.OpusEncoderDestroy(encoder);
                    encoder = IntPtr.Zero;
                }
                disposedValue = true;
            }
        }

        ~OpusEncoder()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
