using System;
using System.IO;

namespace NitroSynth.App
{
    public enum SwavEncoding : byte { Pcm8 = 0, Pcm16 = 1, ImaAdpcm = 2 }

    public sealed class SWAV
    {
        public SwavEncoding Encoding { get; private set; }
        public bool Loop { get; private set; }
        public int SampleRate { get; private set; }
        public int LoopStartSample { get; private set; }  
        public int LoopEndSample { get; private set; }    
        public short[] PCM16 { get; private set; } = Array.Empty<short>();
        public static SWAV Parse(byte[] swavInfoPlusData)
        {
            if (swavInfoPlusData == null || swavInfoPlusData.Length < 12)
                throw new InvalidDataException("SWAV block too small (needs 12B+).");

            var w = new SWAV();

            w.Encoding = (SwavEncoding)swavInfoPlusData[0];     
            w.Loop = swavInfoPlusData[1] != 0;              
            w.SampleRate = BitConverter.ToUInt16(swavInfoPlusData, 2); 
            
            _ = BitConverter.ToUInt16(swavInfoPlusData, 4); 
            ushort loopStartOff = BitConverter.ToUInt16(swavInfoPlusData, 6); 
            uint loopEndOff = BitConverter.ToUInt32(swavInfoPlusData, 8); 

            int dataOff = 12;
            int dataLen = swavInfoPlusData.Length - dataOff;
            if (dataLen <= 0) throw new InvalidDataException("SWAV has no audio data.");

            w.PCM16 = w.Encoding switch
            {
                SwavEncoding.Pcm8 => DecodePcm8(swavInfoPlusData, dataOff, dataLen),
                SwavEncoding.Pcm16 => DecodePcm16(swavInfoPlusData, dataOff, dataLen),
                SwavEncoding.ImaAdpcm => DecodeImaAdpcm(swavInfoPlusData, dataOff, dataLen),
                _ => throw new NotSupportedException($"Unsupported encoding: {(byte)w.Encoding}")
            };



            int totalFromBuffer = w.PCM16.Length;

            ulong startBytes = (ulong)loopStartOff * 4UL;  
            ulong lenBytes = (ulong)loopEndOff * 4UL;  

            static int StartBytesToSamples(SwavEncoding enc, ulong bytes)
            {
                return enc switch
                {
                    SwavEncoding.Pcm8 => (int)Math.Min(bytes, (ulong)int.MaxValue),
                    SwavEncoding.Pcm16 => (int)Math.Min(bytes / 2UL, (ulong)int.MaxValue),
                    SwavEncoding.ImaAdpcm => (int)Math.Max(0L, Math.Min((long)bytes * 2L - 8L, int.MaxValue)),
                    _ => 0
                };
            }
            static int LengthBytesToTotalSamples(SwavEncoding enc, ulong bytes)
            {
                return enc switch
                {
                    SwavEncoding.Pcm8 => (int)Math.Min(bytes, (ulong)int.MaxValue),
                    SwavEncoding.Pcm16 => (int)Math.Min(bytes / 2UL, (ulong)int.MaxValue),
                    SwavEncoding.ImaAdpcm => (int)Math.Min((long)bytes * 2L, int.MaxValue),
                    _ => 0
                };
            }

            int totalFromHeader = LengthBytesToTotalSamples(w.Encoding, lenBytes);

            int total = Math.Max(totalFromBuffer, totalFromHeader);
            w.LoopStartSample = 0;
            w.LoopEndSample = 0;

            if (!w.Loop)
            {
                return w;
            }

            int s = StartBytesToSamples(w.Encoding, startBytes);
            int e = total;

            if (w.Encoding == SwavEncoding.ImaAdpcm)
            {
                s &= ~1;
                e &= ~1;
            }

            s = Math.Clamp(s, 0, total);
            e = Math.Clamp(e, 0, total);

            if (e <= s)
            {
                s = Math.Max(0, Math.Min(s, total - 8)); 
                e = total;
            }

            w.LoopStartSample = s;
            w.LoopEndSample = e;   
            return w;
        }


        private static short[] DecodePcm8(byte[] src, int off, int len)
        {
            var dst = new short[len];
            for (int i = 0; i < len; i++)
                dst[i] = (short)(unchecked((sbyte)src[off + i]) << 8);
            return dst;
        }

        private static short[] DecodePcm16(byte[] src, int off, int len)
        {
            if ((len & 1) != 0) len--; 
            int n = len >> 1;
            var dst = new short[n];
            Buffer.BlockCopy(src, off, dst, 0, n * 2);
            return dst;
        }

        private static readonly int[] ImaIndexTable = { -1, -1, -1, -1, 2, 4, 6, 8 };
        private static readonly int[] ImaStepTable =
        {
            0x0007,0x0008,0x0009,0x000A,0x000B,0x000C,0x000D,0x000E,0x0010,0x0011,0x0013,0x0015,
            0x0017,0x0019,0x001C,0x001F,0x0022,0x0025,0x0029,0x002D,0x0032,0x0037,0x003C,0x0042,
            0x0049,0x0050,0x0058,0x0061,0x006B,0x0076,0x0082,0x008F,0x009D,0x00AD,0x00BE,0x00D1,
            0x00E6,0x00FD,0x0117,0x0133,0x0151,0x0173,0x0198,0x01C1,0x01EE,0x0220,0x0256,0x0292,
            0x02D4,0x031C,0x036C,0x03C3,0x0424,0x048E,0x0502,0x0583,0x0610,0x06AB,0x0756,0x0812,
            0x08E0,0x09C3,0x0ABD,0x0BD0,0x0CFF,0x0E4C,0x0FBA,0x114C,0x1307,0x14EE,0x1706,0x1954,
            0x1BDC,0x1EA5,0x21B6,0x2515,0x28CA,0x2CDF,0x315B,0x364B,0x3BB9,0x41B2,0x4844,0x4F7E,
            0x5771,0x602F,0x69CE,0x7462,0x7FFF
        };

        private static short[] DecodeImaAdpcm(byte[] src, int off, int len)
        {
            if (len < 4) return Array.Empty<short>();

            uint head = BitConverter.ToUInt32(src, off);
            int sample = (short)(head & 0xFFFF);
            int index = (int)((head >> 16) & 0x7F);
            if (index < 0) index = 0; else if (index > 88) index = 88;

            int adpcmBytes = len - 4;
            int totalSamples = Math.Max(0, adpcmBytes * 2); 

            var dst = new short[totalSamples];
            int di = 0;
            int p = off + 4;
            int end = off + len;

            while (p < end && di < totalSamples)
            {
                int b = src[p++];

                for (int rep = 0; rep < 2 && di < totalSamples; rep++)
                {
                    int code = (rep == 0) ? (b & 0x0F) : (b >> 4);
                    int step = ImaStepTable[index];

                    int diff = step >> 3;
                    if ((code & 1) != 0) diff += step >> 2;
                    if ((code & 2) != 0) diff += step >> 1;
                    if ((code & 4) != 0) diff += step;

                    if ((code & 8) != 0) sample -= diff;
                    else sample += diff;

                    if (sample > 32767) sample = 32767;
                    else if (sample < -32768) sample = -32768;

                    index += ImaIndexTable[code & 7];
                    if (index < 0) index = 0;
                    else if (index > 88) index = 88;

                    dst[di++] = (short)sample;
                }
            }
            return dst;
        }
    }
}

