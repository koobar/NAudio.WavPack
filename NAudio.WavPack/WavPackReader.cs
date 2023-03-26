/* NAudio.WavPack
 * Copyright (c) 2023 koobar.
 * ================================================================================
 * This library was created to make it possible to load WavPack files from NAudio. 
 * wavpackdll.dll must be provided separately.*/
using NAudio.Wave;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NAudio.WavPack
{
    public class WavPackReader : WaveStream
    {
        // Constant fields.
        private const int WAVPACK_BYTES_PER_SAMPLE = 4;
        private const int WAVPACK_OPEN_WVC = 0x01;
        private const int WAVPACK_OPEN_MAX2CH = 0x08;
        private const int WAVPACK_OPEN_NORMALIZE = 0x10;
        private const int WAVPACK_OPEN_DSD_AS_PCM = 0x200;

        // private fields.
        private IntPtr wavPackContext;
        private readonly WaveFormat waveFormat;
        private readonly long bytesPerSample;
        private readonly long sizeOfSample;
        private readonly byte[] readBuffer;
        private byte[] decodeBuffer = new byte[0];

        // constructor
        public WavPackReader(string fileName)
        {
            StringBuilder error = new StringBuilder(1024);

            if (LibraryVersion.Major >= 5)
            {
                this.wavPackContext = WavpackOpenFileInput(fileName, error, WAVPACK_OPEN_NORMALIZE | WAVPACK_OPEN_WVC | WAVPACK_OPEN_MAX2CH | WAVPACK_OPEN_DSD_AS_PCM, 0);
            }
            else
            {
                this.wavPackContext = WavpackOpenFileInput(fileName, error, WAVPACK_OPEN_NORMALIZE | WAVPACK_OPEN_WVC | WAVPACK_OPEN_MAX2CH, 0);
            }

            if (this.wavPackContext == IntPtr.Zero)
            {
                throw new Exception(error.ToString());
            }

            this.bytesPerSample = WavpackGetBytesPerSample(this.wavPackContext);
            this.waveFormat = new WaveFormat((int)WavpackGetSampleRate(this.wavPackContext), WavpackGetBitsPerSample(this.wavPackContext), WavpackGetNumChannels(this.wavPackContext));
            this.sizeOfSample = this.waveFormat.Channels * WAVPACK_BYTES_PER_SAMPLE;
            this.readBuffer = new byte[this.sizeOfSample];
        }

        // destructor
        ~WavPackReader()
        {
            Dispose(false);
        }


        #region Properties

        /// <summary>
        /// Version of the WavPack library.
        /// </summary>
        public static Version LibraryVersion
        {
            get
            {
                int version = (int)WavpackGetLibraryVersion();
                return new Version((version >> 16) & 0xFF, (version >> 8) & 0xFF, version & 0xFF);
            }
        }

        /// <summary>
        /// Version of the format.
        /// </summary>
        public Version Version
        {
            get
            {
                return new Version(WavpackGetVersion(wavPackContext), 0);
            }
        }

        /// <summary>
        /// The current position within the stream.
        /// </summary>
        public override long Position
        {
            set
            {
                WavpackSeekSample(this.wavPackContext, (uint)(value / this.WaveFormat.BlockAlign));
            }
            get
            {
                return WavpackGetSampleIndex(this.wavPackContext) * this.WaveFormat.BlockAlign;
            }
        }

        /// <summary>
        /// The length of data that can be read from the Read method (in bytes).
        /// </summary>
        public override long Length
        {
            get
            {
                return WavpackGetNumSamples(this.wavPackContext) * this.WaveFormat.BlockAlign;
            }
        }

        /// <summary>
        /// Get the WaveFormat for this stream.
        /// </summary>
        public override WaveFormat WaveFormat
        {
            get
            {
                return this.waveFormat;
            }
        }

        #endregion

        /// <summary>
        /// Dispose this stream.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (IntPtr.Zero != this.wavPackContext)
            {
                this.wavPackContext = WavpackCloseFile(this.wavPackContext);
            }

            GC.SuppressFinalize(this);
            GC.Collect();
        }

        /// <summary>
        /// Close this stream.
        /// </summary>
        public override void Close()
        {
            Dispose();
        }

        /// <summary>
        /// Reads decoded PCM data from WavPack format file.
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalBytesRead = 0;
            int actualRead = 0;
            int bufferOffset = 0;

            while (actualRead + totalBytesRead < count)
            {
                actualRead = FillBuffer(this.readBuffer);

                // Write to output buffer.
                for (int i = 0; i < this.readBuffer.Length; ++i)
                {
                    int newIndex = bufferOffset + 1;

                    if (newIndex >= buffer.Length)
                    {
                        break;
                    }

                    buffer[bufferOffset++] = this.readBuffer[i];
                }

                // Update total bytes read.
                totalBytesRead += actualRead;

                if (actualRead == 0)
                    break;
            }

            return totalBytesRead;
        }

        /// <summary>
        /// Fills the given buffer.
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private int FillBuffer(byte[] buffer)
        {
            if (this.bytesPerSample == WAVPACK_BYTES_PER_SAMPLE)
            {
                // Decode directly in destination buffer.
                uint sampleCount = (uint)(buffer.Length / this.sizeOfSample);
                sampleCount = WavpackUnpackSamples(this.wavPackContext, buffer, sampleCount);

                return (int)(sampleCount * this.sizeOfSample);
            }
            else
            {
                // Compute need buffer size.
                int maxWaveBufferLength = Math.Min(this.waveFormat.AverageBytesPerSecond, buffer.Length);
                int wavPackBufferLength = (int)(maxWaveBufferLength * WAVPACK_BYTES_PER_SAMPLE / this.bytesPerSample);

                // If need more buffer, resize buffer size.
                if (wavPackBufferLength > this.decodeBuffer.Length)
                {
                    Array.Resize(ref this.decodeBuffer, wavPackBufferLength);
                }

                // Read the sample into a buffer allocated separately for decoding.
                uint sampleCount = WavpackUnpackSamples(this.wavPackContext, this.decodeBuffer, (uint)(wavPackBufferLength / this.sizeOfSample));

                // Write to the output buffer according to the number of bytes per sample.
                int read = 0;
                int count = (int)(sampleCount * this.sizeOfSample);
                switch (this.bytesPerSample)
                {
                    case 1:
                        for (int src = 0; src < count; src += WAVPACK_BYTES_PER_SAMPLE)
                        {
                            buffer[read++] = (byte)(this.decodeBuffer[src] + 128);
                        }
                        break;
                    case 2:
                        for (int src = 0; src < count; src += WAVPACK_BYTES_PER_SAMPLE)
                        {
                            buffer[read++] = this.decodeBuffer[src];
                            buffer[read++] = this.decodeBuffer[src + 1];
                        }
                        break;
                    case 3:
                        for (int src = 0; src < count; src += WAVPACK_BYTES_PER_SAMPLE)
                        {
                            buffer[read++] = this.decodeBuffer[src];
                            buffer[read++] = this.decodeBuffer[src + 1];
                            buffer[read++] = this.decodeBuffer[src + 2];
                        }
                        break;
                    default:
                        throw new Exception("Unsupported format.");
                }

                return read;
            }
        }

        #region Native methods.

        [DllImport("wavpackdll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WavpackOpenFileInput(string infilename, StringBuilder error, int flags, int norm_offset);
        
        [DllImport("wavpackdll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WavpackCloseFile(IntPtr wpc);
        
        [DllImport("wavpackdll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int WavpackGetBitsPerSample(IntPtr wpc);
        
        [DllImport("wavpackdll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int WavpackGetBytesPerSample(IntPtr wpc);
        
        [DllImport("wavpackdll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint WavpackGetLibraryVersion();
        
        [DllImport("wavpackdll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int WavpackGetVersion(IntPtr wpc);

        [DllImport("wavpackdll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int WavpackGetNumChannels(IntPtr wpc);

        [DllImport("wavpackdll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint WavpackGetSampleRate(IntPtr wpc);

        [DllImport("wavpackdll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint WavpackGetNumSamples(IntPtr wpc);

        [DllImport("wavpackdll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint WavpackGetSampleIndex(IntPtr wpc);

        [DllImport("wavpackdll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool WavpackSeekSample(IntPtr wpc, uint sample);

        [DllImport("wavpackdll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint WavpackUnpackSamples(IntPtr wpc, [In, Out] byte[] buffer, uint samples);

        #endregion
    }
}
