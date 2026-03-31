using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace NitroSynth.App
{
    public sealed class SBNK
    {

        public HeaderInfo Header { get; }
        public IReadOnlyList<InstrumentRecord> Records { get; }

        public sealed class HeaderInfo
        {
            public uint Magic { get; init; }               
            public ushort ByteOrder { get; init; }         
            public ushort Version { get; init; }           
            public uint FileSize { get; init; }            
            public ushort HeaderSize { get; init; }        
            public ushort BlockCount { get; init; }        
            public uint DataMagic { get; init; }           
            public uint DataSizeMinus10h { get; init; }    
            public int InstrumentCount { get; init; }      
        }

        public enum InstrumentType : byte
        {
            Null = 0x00,
            Pcm = 0x01,
            Psg = 0x02,
            Noise = 0x03,
            DirectPcm = 0x04,
            NullInstrument = 0x05,
            DrumSet = 0x10,
            KeySplit = 0x11,
            Unknown = 0xFF,
        }

        public sealed class InstrumentRecord
        {
            public InstrumentType Type { get; init; }
            public ushort ArticulationOffset { get; init; } 
            public object? Articulation { get; set; }       
        }

        public sealed class SingleInst
        {
            public ushort SwavId { get; init; }
            public ushort SwarId { get; init; }
            public byte BaseKey { get; init; }         
            public byte Attack { get; init; }
            public byte Decay { get; init; }
            public byte Sustain { get; init; }
            public byte Release { get; init; }
            public byte Pan { get; init; }
        }

        public sealed class DrumSet
        {
            public byte LowKey { get; init; }
            public byte HighKey { get; init; }
            public List<DrumEntry> Entries { get; } = new();
            public sealed class DrumEntry
            {
                public InstrumentType Type { get; init; }   
                public byte Reserved { get; init; }
                public ushort SwavId { get; init; }
                public ushort SwarId { get; init; }
                public byte Key { get; init; }              
                public byte Attack { get; init; }
                public byte Decay { get; init; }
                public byte Sustain { get; init; }
                public byte Release { get; init; }
                public byte Pan { get; init; }
            }
        }

        public sealed class KeySplit
        {
            public byte[] SplitKeys { get; init; } = new byte[8]; 
            public List<KeySplitEntry> Entries { get; } = new();
            public sealed class KeySplitEntry
            {
                public InstrumentType Type { get; init; }   
                public byte Reserved { get; init; }
                public ushort SwavId { get; init; }
                public ushort SwarId { get; init; }
                public byte BaseKey { get; init; }
                public byte Attack { get; init; }
                public byte Decay { get; init; }
                public byte Sustain { get; init; }
                public byte Release { get; init; }
                public byte Pan { get; init; }
            }
        }

        public sealed class BankOption
        {
            public int Id { get; }
            public string Name { get; }
            public BankOption(int id, string name)
            {
                Id = id;
                Name = name;
            }

            public string Display => $"{Id:D3}: {Name}";
            public override string ToString() => Display;
        }



        private SBNK(HeaderInfo header, List<InstrumentRecord> records)
        {
            Header = header;
            Records = records;
        }

        public static SBNK Read(Stream s, long sbnkOffset, uint sbnkSize)
        => Read(s, sbnkOffset, sbnkSize, null);


        public static SBNK Read(Stream s, long sbnkOffset, uint sbnkSize, int? instrumentCountOverride)
        {
            if (sbnkOffset < 0 || sbnkSize < 60)
                throw new InvalidDataException("SBNK block is too short.");

            var br = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);

            var basePos = sbnkOffset;
            var endPos = sbnkOffset + sbnkSize;

            s.Seek(basePos, SeekOrigin.Begin);
            var magic = br.ReadUInt32();
            if (magic != 0x4B4E4253) throw new InvalidDataException("Invalid SBNK magic.");

            var byteOrder = br.ReadUInt16();
            var version = br.ReadUInt16();
            var fileSize = br.ReadUInt32();
            var headerSize = br.ReadUInt16();
            var blockCount = br.ReadUInt16();
            var dataMagic = br.ReadUInt32();
            var dataSizeMinus10h = br.ReadUInt32();

            s.Seek(basePos + 0x38, SeekOrigin.Begin);
            int countFromHeader = br.ReadByte(); 

            s.Seek(basePos + 0x3C, SeekOrigin.Begin);

            int remaining = (int)Math.Max(0, endPos - s.Position);
            int maxBySize = remaining / 4; 
            int maxSafe = maxBySize;

            int n = instrumentCountOverride.HasValue
                    ? Math.Clamp(instrumentCountOverride.Value, 0, maxSafe)
                    : Math.Clamp(countFromHeader, 0, maxSafe);

            var header = new HeaderInfo
            {
                Magic = magic,
                ByteOrder = byteOrder,
                Version = version,
                FileSize = fileSize,
                HeaderSize = headerSize,
                BlockCount = blockCount,
                DataMagic = dataMagic,
                DataSizeMinus10h = dataSizeMinus10h,
                InstrumentCount = n
            };

            var records = new List<InstrumentRecord>(n);
            for (int i = 0; i < n; i++)
            {
                if (s.Position + 4 > endPos) break;
                byte typeByte = br.ReadByte();
                ushort artRel = br.ReadUInt16();
                _ = br.ReadByte(); 
                records.Add(new InstrumentRecord
                {
                    Type = ToType(typeByte),
                    ArticulationOffset = artRel,
                    Articulation = null
                });
            }

            foreach (var rec in records)
            {
                if (rec.Type == InstrumentType.Null) continue;
                long artPos = basePos + rec.ArticulationOffset;       
                if (artPos < basePos || artPos + 0x0A > endPos) continue;

                try
                {
                    s.Seek(artPos, SeekOrigin.Begin);
                    switch (rec.Type)
                    {
                        case InstrumentType.Pcm:
                        case InstrumentType.Psg:
                        case InstrumentType.Noise:
                        case InstrumentType.DirectPcm:
                        case InstrumentType.NullInstrument:
                            rec.Articulation = ReadSingle(br, endPos);
                            break;
                        case InstrumentType.DrumSet:
                            rec.Articulation = ReadDrumSet(br, endPos);
                            break;
                        case InstrumentType.KeySplit:
                            rec.Articulation = ReadKeySplit(br, endPos);
                            break;
                    }
                }
                catch { rec.Articulation = null; }
            }

            return new SBNK(header, records);
        }

        private static InstrumentType ToType(byte b) =>
            b switch
            {
                0x00 => InstrumentType.Null,
                0x01 => InstrumentType.Pcm,
                0x02 => InstrumentType.Psg,
                0x03 => InstrumentType.Noise,
                0x04 => InstrumentType.DirectPcm,
                0x05 => InstrumentType.NullInstrument,
                0x10 => InstrumentType.DrumSet,
                0x11 => InstrumentType.KeySplit,
                _ => InstrumentType.Unknown
            };

        private static SingleInst? ReadSingle(BinaryReader br, long endPos)
        {
            var s = br.BaseStream;
            if (s.Position + 10 > endPos) return null;

            var sv = new SingleInst
            {
                SwavId = br.ReadUInt16(),
                SwarId = br.ReadUInt16(),
                BaseKey = br.ReadByte(),
                Attack = br.ReadByte(),
                Decay = br.ReadByte(),
                Sustain = br.ReadByte(),
                Release = br.ReadByte(),
                Pan = br.ReadByte(),
            };
            return sv;
        }

        private static DrumSet? ReadDrumSet(BinaryReader br, long endPos)
        {
            var s = br.BaseStream;
            if (s.Position + 2 > endPos) return null;

            var low = br.ReadByte();
            var high = br.ReadByte();

            var ds = new DrumSet { LowKey = low, HighKey = high };

            int count = Math.Max(0, high - low + 1);

            for (int i = 0; i < count; i++)
            {
                if (s.Position + 12 > endPos) break;

                var type = ToType(br.ReadByte());
                var reserved = br.ReadByte();
                var swav = br.ReadUInt16();
                var swar = br.ReadUInt16();
                var key = br.ReadByte();
                var a = br.ReadByte();
                var d = br.ReadByte();
                var sus = br.ReadByte();
                var r = br.ReadByte();
                var pan = br.ReadByte();

                ds.Entries.Add(new DrumSet.DrumEntry
                {
                    Type = type,
                    Reserved = reserved,
                    SwavId = swav,
                    SwarId = swar,
                    Key = key,
                    Attack = a,
                    Decay = d,
                    Sustain = sus,
                    Release = r,
                    Pan = pan
                });
            }

            return ds;
        }

        private static KeySplit? ReadKeySplit(BinaryReader br, long endPos)
        {
            var s = br.BaseStream;
            if (s.Position + 8 > endPos) return null;

            var ks = new KeySplit();
            for (int i = 0; i < 8; i++) ks.SplitKeys[i] = br.ReadByte();

            for (int i = 0; i < 8; i++)
            {
                byte splitKey = ks.SplitKeys[i];
                if (splitKey == 0) continue;               

                if (s.Position + 12 > endPos) break;

                var type = ToType(br.ReadByte());
                var reserved = br.ReadByte();
                var swav = br.ReadUInt16();
                var swar = br.ReadUInt16();
                var baseKey = br.ReadByte();
                var a = br.ReadByte();
                var d = br.ReadByte();
                var sus = br.ReadByte();
                var r = br.ReadByte();
                var pan = br.ReadByte();

                ks.Entries.Add(new KeySplit.KeySplitEntry
                {
                    Type = type,
                    Reserved = reserved,
                    SwavId = swav,
                    SwarId = swar,
                    BaseKey = baseKey,
                    Attack = a,
                    Decay = d,
                    Sustain = sus,
                    Release = r,
                    Pan = pan
                });
            }

            return ks;
        }

        public bool TryGetPcmArticulationBytes(int instIndex, Stream s, long sbnkBaseOffset, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if ((uint)instIndex >= (uint)Records.Count) return false;

            var rec = Records[instIndex];
            if (rec.Type != InstrumentType.Pcm && rec.Type != InstrumentType.DirectPcm)
                return false;

            if (rec.Articulation is SingleInst sv)
            {
                var buf = new byte[0x0A];
                BitConverter.TryWriteBytes(buf.AsSpan(0x00, 2), sv.SwavId);
                BitConverter.TryWriteBytes(buf.AsSpan(0x02, 2), sv.SwarId);
                buf[0x04] = sv.BaseKey;
                buf[0x05] = sv.Attack;
                buf[0x06] = sv.Decay;
                buf[0x07] = sv.Sustain;
                buf[0x08] = sv.Release;
                buf[0x09] = sv.Pan;
                bytes = buf;
                return true;
            }

            long dataBase = sbnkBaseOffset;
            long artPos = dataBase + rec.ArticulationOffset;

            if (artPos < 0) return false;

            s.Seek(artPos, SeekOrigin.Begin);
            var raw = new byte[0x0A];
            int read = s.Read(raw, 0, raw.Length);
            if (read != raw.Length) return false;

            bytes = raw;
            return true;
        }

        public bool TryGetPsgArticulationBytes(int instIndex, Stream s, long sbnkBaseOffset, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if ((uint)instIndex >= (uint)Records.Count) return false;

            var rec = Records[instIndex];
            if (rec.Type != InstrumentType.Psg) return false;

            if (rec.Articulation is SingleInst sv)
            {
                var buf = new byte[0x0A];
                BitConverter.TryWriteBytes(buf.AsSpan(0x00, 2), sv.SwavId); 
                BitConverter.TryWriteBytes(buf.AsSpan(0x02, 2), (ushort)0); 
                buf[0x04] = sv.BaseKey;
                buf[0x05] = sv.Attack;
                buf[0x06] = sv.Decay;
                buf[0x07] = sv.Sustain;
                buf[0x08] = sv.Release;
                buf[0x09] = sv.Pan;
                bytes = buf;
                return true;
            }

            long artPos = sbnkBaseOffset + rec.ArticulationOffset;
            long sbnkEnd = sbnkBaseOffset + Header.FileSize;
            if (artPos < sbnkBaseOffset || artPos + 0x0A > sbnkEnd) return false;

            s.Seek(artPos, SeekOrigin.Begin);
            var raw = new byte[0x0A];
            if (s.Read(raw, 0, raw.Length) != raw.Length) return false;

            bytes = raw;
            return true;
        }

        public bool TryGetNoiseArticulationBytes(int instIndex, Stream s, long sbnkBaseOffset, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if ((uint)instIndex >= (uint)Records.Count) return false;

            var rec = Records[instIndex];
            if (rec.Type != InstrumentType.Noise) return false;

            if (rec.Articulation is SingleInst sv)
            {
                var buf = new byte[0x0A];

                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x00, 2), sv.SwavId);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x02, 2), sv.SwarId);

                buf[0x04] = sv.BaseKey;
                buf[0x05] = sv.Attack;
                buf[0x06] = sv.Decay;
                buf[0x07] = sv.Sustain;
                buf[0x08] = sv.Release;
                buf[0x09] = sv.Pan;

                bytes = buf;
                return true;
            }

            long artPos = sbnkBaseOffset + rec.ArticulationOffset;
            long sbnkEnd = sbnkBaseOffset + Header.FileSize;
            if (artPos < sbnkBaseOffset || artPos + 0x0A > sbnkEnd) return false;

            long oldPos = 0;
            bool canRestore = s.CanSeek;
            if (canRestore) oldPos = s.Position;

            try
            {
                s.Seek(artPos, SeekOrigin.Begin);
                var raw = new byte[0x0A];
                if (s.Read(raw, 0, raw.Length) != raw.Length) return false;

                bytes = raw;
                return true;
            }
            finally
            {
                if (canRestore) s.Seek(oldPos, SeekOrigin.Begin);
            }
        }
        public bool TryGetDrumSet(int instIndex, out DrumSet ds)
        {
            ds = null!;
            if ((uint)instIndex >= (uint)Records.Count) return false;
            var rec = Records[instIndex];
            if (rec.Type != InstrumentType.DrumSet) return false;
            if (rec.Articulation is DrumSet x) { ds = x; return true; }
            return false;
        }

        public bool TryGetKeySplit(int instIndex, out KeySplit ks)
        {
            ks = null!;
            if ((uint)instIndex >= (uint)Records.Count) return false;
            var rec = Records[instIndex];
            if (rec.Type != InstrumentType.KeySplit) return false;
            if (rec.Articulation is KeySplit x) { ks = x; return true; }
            return false;
        }



    }
}


