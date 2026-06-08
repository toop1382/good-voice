using System;

namespace Client.Codec
{
    public class OpusDecoder : IOpusDecoder
    {
        public const int maximumPacketDuration = UnityOpusLibrary.maximumPacketDuration;

        private readonly object _lock = new object();
        private IntPtr decoder;
        private readonly NumChannels channels;
        private readonly float[] softclipMem;

        public OpusDecoder(
            SamplingFrequency samplingFrequency,
            NumChannels channels)
        {
            this.channels = channels;
            ErrorCode error;
            decoder = UnityOpusLibrary.OpusDecoderCreate(
                samplingFrequency,
                channels,
                out error);
            if (error != ErrorCode.OK)
            {
#if UNITY_5_3_OR_NEWER || UNITY_2017_1_OR_NEWER
                UnityEngine.Debug.LogError("[UnityOpus] Failed to create Decoder. Error code is " + error.ToString());
#else
                Console.WriteLine("[UnityOpus] Failed to create Decoder. Error code is " + error.ToString());
#endif
                decoder = IntPtr.Zero;
            }
            softclipMem = new float[(int)channels];
        }

        public int Decode(
            byte[] data,
            int dataLength,
            float[] pcm,
            int decodeFec = 0)
        {
            lock (_lock)
            {
                if (decoder == IntPtr.Zero)
                {
                    return 0;
                }
                var decodedLength = UnityOpusLibrary.OpusDecodeFloat(
                    decoder,
                    data,
                    dataLength,
                    pcm,
                    pcm.Length / (int)channels,
                    decodeFec);
                
                UnityOpusLibrary.OpusPcmSoftClip(
                    pcm,
                    decodedLength / (int)channels,
                    channels,
                    softclipMem);
                
                return decodedLength;
            }
        }

        #region IDisposable Support
        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (!disposedValue)
                {
                    if (decoder != IntPtr.Zero)
                    {
                        UnityOpusLibrary.OpusDecoderDestroy(decoder);
                        decoder = IntPtr.Zero;
                    }
                    disposedValue = true;
                }
            }
        }

        ~OpusDecoder()
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
