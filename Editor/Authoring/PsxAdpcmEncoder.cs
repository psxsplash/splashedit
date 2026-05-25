/*

MIT License

Copyright (c) 2024 PCSX-Redux authors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/

using System;

namespace PCSX.ADPCM
{
    /// <summary>
    /// PlayStation SPU/XA ADPCM encoder. A C# port of the original C++ encoder from pcsx-redux,
    /// which re-creates Sony's ADPCM encoder from the Psy-Q development kit.
    /// The input is expected to be blocks of 28 samples of 16-bit signed PCM audio.
    /// </summary>
    public class Encoder
    {
        public enum Mode
        {
            Normal,
            XA,
            High,
            Low,
            FourBits,
        }

        public enum BlockAttribute
        {
            OneShot,     // Block flags 0x00
            OneShotEnd,  // Block flags 0x01
            LoopStart,   // Block flags 0x06
            LoopBody,    // Block flags 0x02
            LoopEnd,     // Block flags 0x03
        }

        public enum XAMode
        {
            FourBits,
            EightBits,
        }

        private readonly double[] m_factors = new double[10];
        private readonly double[][] m_lastBlockSamples = new double[2][] { new double[2], new double[2] };
        private readonly double[][] m_anomalies = new double[2][] { new double[2], new double[2] };

        private static readonly double[][] c_filters = new double[5][]
        {
            new double[] { 0.0, 0.0 },
            new double[] { -0.9375, 0.0 },
            new double[] { -1.796875, 0.8125 },
            new double[] { -1.53125, 0.859375 },
            new double[] { -1.90625, 0.9375 },
        };

        /// <summary>
        /// Initialize the encoder with the given mode. Must be called before encoding,
        /// and between different instruments.
        /// </summary>
        public void Reset(Mode mode = Mode.Normal)
        {
            m_lastBlockSamples[0][0] = 0.0;
            m_lastBlockSamples[0][1] = 0.0;
            m_lastBlockSamples[1][0] = 0.0;
            m_lastBlockSamples[1][1] = 0.0;
            m_anomalies[0][0] = 0.0;
            m_anomalies[0][1] = 0.0;
            m_anomalies[1][0] = 0.0;
            m_anomalies[1][1] = 0.0;
            for (int i = 0; i < 10; i++)
            {
                m_factors[i] = 1.0;
            }
            switch (mode)
            {
                case Mode.Normal:
                    break;
                case Mode.XA:
                    m_factors[4] = 1000.0;
                    break;
                case Mode.High:
                    m_factors[2] = 1000.0;
                    m_factors[3] = 1000.0;
                    break;
                case Mode.Low:
                    m_factors[2] = 1000.0;
                    m_factors[4] = 1000.0;
                    break;
                case Mode.FourBits:
                    m_factors[1] = 1000.0;
                    m_factors[2] = 1000.0;
                    m_factors[3] = 1000.0;
                    m_factors[4] = 1000.0;
                    break;
            }
        }

        /// <summary>
        /// Process a block of 28 samples per channel. Input is interleaved, output is NOT interleaved.
        /// filterOut and shiftOut must have at least `channels` elements each.
        /// Output must have at least 28 * channels elements.
        /// </summary>
        public void ProcessBlock(short[] input, int inputOffset, short[] output, int outputOffset,
                                 byte[] filterOut, int filterOffset, byte[] shiftOut, int shiftOffset,
                                 int channels = 1, XAMode xaMode = XAMode.FourBits)
        {
            if (channels > 2)
                throw new ArgumentException("Channels must be 1 or 2");

            double[][] converted = new double[2][];
            converted[0] = new double[28];
            converted[1] = new double[28];
            double[][] filtered = new double[2][];
            filtered[0] = new double[28];
            filtered[1] = new double[28];

            ConvertToDoubles(input, inputOffset, converted[0], channels);
            if (channels == 2)
            {
                ConvertToDoubles(input, inputOffset + 1, converted[1], channels);
            }
            for (int channel = 0; channel < channels; channel++)
            {
                byte filter = 0, shift = 0;
                FindFilterAndShift(converted[channel], filtered[channel], ref filter, ref shift, channel);
                filterOut[filterOffset + channel] = filter;
                shiftOut[shiftOffset + channel] = shift;
                Convert(filtered[channel], output, outputOffset + channel * 28, filter, shift, channel, xaMode);
            }
        }

        /// <summary>
        /// Pack 28 pre-processed samples into 14 bytes of 4-bit ADPCM.
        /// </summary>
        public void BlockTo4Bit(short[] input, int inputOffset, byte[] output, int outputOffset)
        {
            for (int i = 0; i < 14; i++)
            {
                int s1 = input[inputOffset + i * 2 + 0] >> 12;
                int s2 = input[inputOffset + i * 2 + 1] >> 12;
                output[outputOffset + i] = (byte)((s1 & 0x0f) | ((s2 & 0x0f) << 4));
            }
        }

        /// <summary>
        /// Pack 28 pre-processed samples into 28 bytes of 8-bit ADPCM.
        /// </summary>
        public void BlockTo8Bit(short[] input, int inputOffset, byte[] output, int outputOffset)
        {
            for (int i = 0; i < 28; i++)
            {
                output[outputOffset + i] = (byte)(input[inputOffset + i] >> 8);
            }
        }

        /// <summary>
        /// Process 28 samples into a 16-byte SPU block.
        /// </summary>
        public void ProcessSPUBlock(short[] input, int inputOffset, byte[] output, int outputOffset, BlockAttribute blockAttribute)
        {
            byte filter = 0;
            byte shift = 0;
            short[] encoded = new short[28];
            byte[] filterArr = new byte[1];
            byte[] shiftArr = new byte[1];
            ProcessBlock(input, inputOffset, encoded, 0, filterArr, 0, shiftArr, 0);
            filter = filterArr[0];
            shift = shiftArr[0];

            byte h1 = (byte)((shift & 0x0f) | ((filter & 0x0f) << 4));
            byte h2 = 0;

            switch (blockAttribute)
            {
                case BlockAttribute.OneShot:
                    break;
                case BlockAttribute.OneShotEnd:
                    h2 = 0x01;
                    break;
                case BlockAttribute.LoopStart:
                    h2 = 0x06;
                    break;
                case BlockAttribute.LoopBody:
                    h2 = 0x02;
                    break;
                case BlockAttribute.LoopEnd:
                    h2 = 0x03;
                    break;
            }

            output[outputOffset + 0] = h1;
            output[outputOffset + 1] = h2;

            BlockTo4Bit(encoded, 0, output, outputOffset + 2);
        }

        /// <summary>
        /// Write the final 16-byte SPU termination block (for one-shot audio).
        /// </summary>
        public void FinishSPU(byte[] output, int outputOffset)
        {
            output[outputOffset + 0] = 7;
            output[outputOffset + 1] = 0;
            for (int i = 0; i < 14; i++)
            {
                output[outputOffset + 2 + i] = 0x77;
            }
        }

        /// <summary>
        /// Process enough samples to fill a single 128-byte XA block.
        /// Input size depends on mode and channels:
        ///   4-bit mono:   224 samples (448 bytes)
        ///   4-bit stereo: 112 samples (448 bytes, interleaved)
        ///   8-bit mono:   112 samples (224 bytes)
        ///   8-bit stereo:  56 samples (224 bytes, interleaved)
        /// </summary>
        public void ProcessXABlock(short[] input, int inputOffset, byte[] output, int outputOffset, XAMode xaMode, int channels)
        {
            if (channels > 2)
                throw new ArgumentException("Channels must be 1 or 2");

            byte[] filterArr = new byte[2];
            byte[] shiftArr = new byte[2];

            if (channels == 1)
            {
                if (xaMode == XAMode.FourBits)
                {
                    short[] encoded = new short[28 * 8];
                    for (int b = 0; b < 8; b++)
                    {
                        ProcessBlock(input, inputOffset + b * 28, encoded, b * 28, filterArr, 0, shiftArr, 0, channels, xaMode);
                        byte h = (byte)((shiftArr[0] & 0x0f) | ((filterArr[0] & 0x0f) << 4));
                        int offset = (b & 3) + (b >> 2) * 8;
                        output[outputOffset + offset + 0] = h;
                        output[outputOffset + offset + 4] = h;
                    }
                    for (int s = 0; s < 28; s++)
                    {
                        for (int b = 0; b < 4; b++)
                        {
                            int s1 = (encoded[s + (b * 2 + 0) * 28] + 2048) >> 12;
                            int s2 = (encoded[s + (b * 2 + 1) * 28] + 2048) >> 12;
                            output[outputOffset + 16 + s * 4 + b] = (byte)((s1 & 0x0f) | ((s2 & 0x0f) << 4));
                        }
                    }
                }
                else
                {
                    short[] encoded = new short[28 * 4];
                    for (int b = 0; b < 4; b++)
                    {
                        ProcessBlock(input, inputOffset + b * 28, encoded, b * 28, filterArr, 0, shiftArr, 0, channels, xaMode);
                        int adjShift = Math.Max(0, (int)shiftArr[0] - 4);
                        byte h = (byte)((adjShift & 0x0f) | ((filterArr[0] & 0x0f) << 4));
                        output[outputOffset + b + 0] = h;
                        output[outputOffset + b + 4] = h;
                        output[outputOffset + b + 8] = h;
                        output[outputOffset + b + 12] = h;
                    }
                    for (int s = 0; s < 28; s++)
                    {
                        for (int b = 0; b < 4; b++)
                        {
                            output[outputOffset + 16 + s * 4 + b] = (byte)((encoded[s + b * 28] + 128) >> 8);
                        }
                    }
                }
            }
            else
            {
                if (xaMode == XAMode.FourBits)
                {
                    short[] encoded = new short[56 * 4];
                    for (int b = 0; b < 4; b++)
                    {
                        ProcessBlock(input, inputOffset + b * 56, encoded, b * 56, filterArr, 0, shiftArr, 0, channels, xaMode);
                        byte h0 = (byte)((shiftArr[0] & 0x0f) | ((filterArr[0] & 0x0f) << 4));
                        byte h1 = (byte)((shiftArr[1] & 0x0f) | ((filterArr[1] & 0x0f) << 4));
                        int offset = (b & 1) + (b >> 1) * 4;
                        output[outputOffset + offset * 2 + 0] = h0;
                        output[outputOffset + offset * 2 + 1] = h1;
                        output[outputOffset + offset * 2 + 4] = h0;
                        output[outputOffset + offset * 2 + 5] = h1;
                    }
                    for (int s = 0; s < 28; s++)
                    {
                        for (int b = 0; b < 4; b++)
                        {
                            int s1 = (encoded[s + (b * 2 + 0) * 28] + 2048) >> 12;
                            int s2 = (encoded[s + (b * 2 + 1) * 28] + 2048) >> 12;
                            output[outputOffset + 16 + s * 4 + b] = (byte)((s1 & 0x0f) | ((s2 & 0x0f) << 4));
                        }
                    }
                }
                else
                {
                    short[] encoded = new short[56 * 2];
                    for (int b = 0; b < 2; b++)
                    {
                        ProcessBlock(input, inputOffset + b * 56, encoded, b * 56, filterArr, 0, shiftArr, 0, channels, xaMode);
                        int adjShift0 = Math.Max(0, (int)shiftArr[0] - 4);
                        int adjShift1 = Math.Max(0, (int)shiftArr[1] - 4);
                        byte h0 = (byte)((adjShift0 & 0x0f) | ((filterArr[0] & 0x0f) << 4));
                        byte h1 = (byte)((adjShift1 & 0x0f) | ((filterArr[1] & 0x0f) << 4));
                        output[outputOffset + b * 2 + 0] = h0;
                        output[outputOffset + b * 2 + 1] = h1;
                        output[outputOffset + b * 2 + 4] = h0;
                        output[outputOffset + b * 2 + 5] = h1;
                        output[outputOffset + b * 2 + 8] = h0;
                        output[outputOffset + b * 2 + 9] = h1;
                        output[outputOffset + b * 2 + 12] = h0;
                        output[outputOffset + b * 2 + 13] = h1;
                    }
                    for (int s = 0; s < 28; s++)
                    {
                        for (int b = 0; b < 4; b++)
                        {
                            output[outputOffset + 16 + s * 4 + b] = (byte)((encoded[s + b * 28] + 128) >> 8);
                        }
                    }
                }
            }
        }

        private void ConvertToDoubles(short[] input, int inputOffset, double[] output, int channels)
        {
            for (int i = 0; i < 28; i++)
            {
                output[i] = (double)input[inputOffset + i * channels];
            }
        }

        private void FindFilterAndShift(double[] input, double[] output, ref byte filterOut, ref byte shiftOut, int channel)
        {
            double minMax = 1.8e+307;
            double[] filteredMax = new double[5];
            double[][] allFiltered = new double[5][];
            for (int i = 0; i < 5; i++)
                allFiltered[i] = new double[28];
            double[] samples = new double[2];

            filterOut = 0;

            for (int filter = 0; filter < 5; filter++)
            {
                samples[0] = m_lastBlockSamples[channel][0];
                samples[1] = m_lastBlockSamples[channel][1];
                filteredMax[filter] = 0.0;
                for (int i = 0; i < 28; i++)
                {
                    double next = input[i];
                    double f = samples[0] * c_filters[filter][0] + samples[1] * c_filters[filter][1] + next;
                    allFiltered[filter][i] = f;
                    double absF = f <= 0.0 ? -f : f;
                    if (filteredMax[filter] < absF) filteredMax[filter] = absF;
                    samples[1] = samples[0];
                    samples[0] = next;
                }
                double factorized = m_factors[filter] * filteredMax[filter];
                if (factorized < minMax)
                {
                    filterOut = (byte)filter;
                    minMax = factorized;
                }
                if ((filter == 0) && (filteredMax[0] <= 7.0)) break;
            }
            m_lastBlockSamples[channel][0] = samples[0];
            m_lastBlockSamples[channel][1] = samples[1];
            int bestFilter = filterOut;
            Array.Copy(allFiltered[bestFilter], 0, output, 0, 28);
            int maxI = (int)(filteredMax[bestFilter] * m_factors[bestFilter + 5]);
            if (maxI < -32768) maxI = -32768;
            if (maxI > 32767) maxI = 32767;
            int mask = 0x4000;
            for (shiftOut = 0; shiftOut < 12; shiftOut++)
            {
                int compare = maxI + (mask >> 3);
                if ((mask & compare) != 0) return;
                mask >>= 1;
            }
        }

        private void Convert(double[] input, short[] output, int outputOffset, byte filter, byte shift, int channel, XAMode xaMode)
        {
            double multiplier = 1 << shift;
            double[] anomalies = m_anomalies[channel];
            for (int i = 0; i < 28; i++)
            {
                double sample = anomalies[0] * c_filters[filter][0] + anomalies[1] * c_filters[filter][1] + input[i];
                int sampleI = (int)(sample * multiplier);
                if (xaMode == XAMode.FourBits)
                {
                    sampleI = (sampleI + 2048) & unchecked((int)0xfffff000);
                }
                else
                {
                    sampleI = (sampleI + 128) & unchecked((int)0xffffff00);
                }
                if (sampleI < -32768) sampleI = -32768;
                if (sampleI > 32767) sampleI = 32767;
                output[outputOffset + i] = (short)sampleI;
                anomalies[1] = anomalies[0];
                anomalies[0] = (double)(sampleI >> shift) - sample;
            }
        }
    }
}
