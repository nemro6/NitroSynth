using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace NitroSynth.App
{
    public sealed class SYMB
    {
        public uint Size { get; private set; }

        public IReadOnlyDictionary<int, string> Sseq { get; }
        public IReadOnlyDictionary<int, string> Ssar { get; }
        public IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>> SsarSequenceNames { get; }
        public IReadOnlyDictionary<int, string> Sbnk { get; }
        public IReadOnlyDictionary<int, string> Swar { get; }
        public IReadOnlyDictionary<int, string> Player { get; }
        public IReadOnlyDictionary<int, string> Group { get; }
        public IReadOnlyDictionary<int, string> StrmPlayer { get; }
        public IReadOnlyDictionary<int, string> Strm { get; }

        private SYMB(
            uint size,
            Dictionary<int, string> sseq,
            Dictionary<int, string> ssar,
            Dictionary<int, IReadOnlyDictionary<int, string>> ssarSequenceNames,
            Dictionary<int, string> sbnk,
            Dictionary<int, string> swar,
            Dictionary<int, string> player,
            Dictionary<int, string> group,
            Dictionary<int, string> strmPlayer,
            Dictionary<int, string> strm)
        {
            Size = size;
            Sseq = sseq;
            Ssar = ssar;
            SsarSequenceNames = ssarSequenceNames;
            Sbnk = sbnk;
            Swar = swar;
            Player = player;
            Group = group;
            StrmPlayer = strmPlayer;
            Strm = strm;
        }

        public static SYMB Load(Stream stream, uint symbOffset, uint symbSize)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            var blockStart = (long)symbOffset;
            var blockEnd = blockStart + symbSize;

            stream.Seek(blockStart, SeekOrigin.Begin);

            var fourcc = ReadFourCc(reader);
            if (fourcc != "SYMB")
                throw new InvalidDataException($"SYMB fourcc mismatch: '{fourcc}'");

            var sizeFromHeader = reader.ReadUInt32();

            uint sseqTableOfs = reader.ReadUInt32();
            uint ssarTableOfs = reader.ReadUInt32();
            uint sbnkTableOfs = reader.ReadUInt32();
            uint swarTableOfs = reader.ReadUInt32();
            uint playerTableOfs = reader.ReadUInt32();
            uint groupTableOfs = reader.ReadUInt32();
            uint strmPlayerTableOfs = reader.ReadUInt32();
            uint strmTableOfs = reader.ReadUInt32();

            const int PaddingSize = 0x18;
            if (stream.Position + PaddingSize > blockEnd)
                throw new InvalidDataException("SYMB padding overrun.");
            stream.Seek(PaddingSize, SeekOrigin.Current);

            var sseq = ReadNameTable(reader, blockStart, blockEnd, sseqTableOfs);
            var (ssar, ssarSequenceNames) = ReadSsarNameTable(reader, blockStart, blockEnd, ssarTableOfs);
            var sbnk = ReadNameTable(reader, blockStart, blockEnd, sbnkTableOfs);
            var swar = ReadNameTable(reader, blockStart, blockEnd, swarTableOfs);
            var player = ReadNameTable(reader, blockStart, blockEnd, playerTableOfs);
            var group = ReadNameTable(reader, blockStart, blockEnd, groupTableOfs);
            var strmP = ReadNameTable(reader, blockStart, blockEnd, strmPlayerTableOfs);
            var strm = ReadNameTable(reader, blockStart, blockEnd, strmTableOfs);

            return new SYMB(symbSize, sseq, ssar, ssarSequenceNames, sbnk, swar, player, group, strmP, strm);
        }

        private static Dictionary<int, string> ReadNameTable(BinaryReader reader, long blockStart, long blockEnd, uint tableRelOfs)
        {
            var result = new Dictionary<int, string>();
            if (tableRelOfs == 0) return result; 

            var stream = reader.BaseStream;
            var tableAbs = blockStart + tableRelOfs;
            if (tableAbs + 4 > blockEnd) return result; 

            var save = stream.Position;
            stream.Seek(tableAbs, SeekOrigin.Begin);

            try
            {
                uint count = reader.ReadUInt32();
                if (count == 0) return result;
                if (count > 0x100000) 
                    throw new InvalidDataException($"SYMB name table too large: {count}");

                if (stream.Position + count * 4L > blockEnd)
                    throw new InvalidDataException("SYMB name table offsets out of range.");
                var rels = new uint[count];
                for (int i = 0; i < count; i++)
                    rels[i] = reader.ReadUInt32();

                for (int i = 0; i < count; i++)
                {
                    uint rel = rels[i];
                    if (rel == 0 || rel == 0xFFFFFFFF) continue;
                    long pos = blockStart + rel;
                    if (pos < blockStart || pos >= blockEnd) continue;

                    stream.Seek(pos, SeekOrigin.Begin);
                    var name = ReadNullTerminatedString(reader, blockEnd);
                    if (!string.IsNullOrWhiteSpace(name))
                        result[i] = name;
                }
            }
            finally
            {
                stream.Seek(save, SeekOrigin.Begin);
            }

            return result;
        }

        private static (Dictionary<int, string> names, Dictionary<int, IReadOnlyDictionary<int, string>> sequenceNames)
            ReadSsarNameTable(BinaryReader reader, long blockStart, long blockEnd, uint tableRelOfs)
        {
            var result = new Dictionary<int, string>();
            var sequenceNameTables = new Dictionary<int, IReadOnlyDictionary<int, string>>();
            if (tableRelOfs == 0) return (result, sequenceNameTables);

            var stream = reader.BaseStream;
            var tableAbs = blockStart + tableRelOfs;
            if (tableAbs + 4 > blockEnd) return (result, sequenceNameTables);

            var save = stream.Position;
            stream.Seek(tableAbs, SeekOrigin.Begin);

            try
            {
                uint count = reader.ReadUInt32();
                if (count == 0) return (result, sequenceNameTables);
                if (count > 0x100000)
                    throw new InvalidDataException($"SYMB SSAR table too large: {count}");

                long entriesStart = stream.Position;
                bool canBePairTable = entriesStart + count * 8L <= blockEnd;

                if (canBePairTable)
                {
                    var pairNameOffsets = new uint[count];
                    var pairSequenceTableOffsets = new uint[count];
                    for (int i = 0; i < count; i++)
                    {
                        pairNameOffsets[i] = reader.ReadUInt32();
                        pairSequenceTableOffsets[i] = reader.ReadUInt32();
                    }

                    var pairParsed = ParseNameOffsets(reader, blockStart, blockEnd, pairNameOffsets);
                    for (int i = 0; i < pairSequenceTableOffsets.Length; i++)
                    {
                        if (TryReadNestedNameTable(reader, blockStart, blockEnd, pairSequenceTableOffsets[i], out var nestedNames))
                            sequenceNameTables[i] = nestedNames;
                    }

                    if (pairParsed.Count > 0 || sequenceNameTables.Count > 0)
                        return (pairParsed, sequenceNameTables);
                }

                if (entriesStart + count * 4L > blockEnd)
                    throw new InvalidDataException("SYMB SSAR table offsets out of range.");

                stream.Seek(entriesStart, SeekOrigin.Begin);
                var rels = new uint[count];
                for (int i = 0; i < count; i++)
                    rels[i] = reader.ReadUInt32();

                return (ParseNameOffsets(reader, blockStart, blockEnd, rels), sequenceNameTables);
            }
            finally
            {
                stream.Seek(save, SeekOrigin.Begin);
            }
        }

        private static bool TryReadNestedNameTable(
            BinaryReader reader,
            long blockStart,
            long blockEnd,
            uint tableRelOfs,
            out IReadOnlyDictionary<int, string> names)
        {
            names = new Dictionary<int, string>();
            if (tableRelOfs == 0 || tableRelOfs == 0xFFFFFFFF)
                return false;

            var stream = reader.BaseStream;
            var tableAbs = blockStart + tableRelOfs;
            if (tableAbs + 4 > blockEnd)
                return false;

            var save = stream.Position;
            try
            {
                stream.Seek(tableAbs, SeekOrigin.Begin);

                uint count = reader.ReadUInt32();
                if (count == 0 || count > 0x100000)
                    return false;

                if (stream.Position + count * 4L > blockEnd)
                    return false;

                var rels = new uint[count];
                for (int i = 0; i < count; i++)
                    rels[i] = reader.ReadUInt32();

                var parsed = ParseNameOffsets(reader, blockStart, blockEnd, rels);
                if (parsed.Count == 0)
                    return false;

                names = parsed;
                return true;
            }
            finally
            {
                stream.Seek(save, SeekOrigin.Begin);
            }
        }

        private static Dictionary<int, string> ParseNameOffsets(BinaryReader reader, long blockStart, long blockEnd, IReadOnlyList<uint> rels)
        {
            var result = new Dictionary<int, string>(rels.Count);
            var stream = reader.BaseStream;

            for (int i = 0; i < rels.Count; i++)
            {
                uint rel = rels[i];
                if (rel == 0 || rel == 0xFFFFFFFF)
                    continue;

                long pos = blockStart + rel;
                if (pos < blockStart || pos >= blockEnd)
                    continue;

                stream.Seek(pos, SeekOrigin.Begin);
                var name = ReadNullTerminatedString(reader, blockEnd);
                if (!string.IsNullOrWhiteSpace(name))
                    result[i] = name;
            }

            return result;
        }

        private static string ReadNullTerminatedString(BinaryReader reader, long limit)
        {
            var buf = new List<byte>();
            while (reader.BaseStream.Position < limit)
            {
                int b = reader.Read();
                if (b < 0 || b == 0) break;
                buf.Add((byte)b);
            }
            return Encoding.ASCII.GetString(buf.ToArray());
        }

        private static string ReadFourCc(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);
            return Encoding.ASCII.GetString(bytes);
        }
        
    }
}

