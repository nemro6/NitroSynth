using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace NitroSynth.App
{
    public sealed class FAT
    {
        public uint Size { get; }
        public IReadOnlyList<FatEntry> Entries { get; }

        public readonly record struct FatEntry(uint Offset, uint Size)
        {
            public string OffsetHex => $"0x{Offset:X8}";
            public string SizeHex => $"0x{Size:X8}";
        }

        private FAT(uint size, List<FatEntry> entries)
        {
            Size = size;
            Entries = entries;
        }

        public static FAT Load(Stream stream, uint fatOffset, uint fatSize)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            var blockStart = (long)fatOffset;
            var blockEnd = blockStart + fatSize;

            stream.Seek(blockStart, SeekOrigin.Begin);

            var fourcc = ReadFourCc(reader);
            if (fourcc != "FAT ")
                throw new InvalidDataException($"FAT fourcc mismatch: '{fourcc}'");

            var sizeFromHeader = reader.ReadUInt32();
            if (sizeFromHeader != fatSize)
                throw new InvalidDataException($"FAT size mismatch: header={sizeFromHeader} actual={fatSize}");

            if (reader.BaseStream.Position + 4 > blockEnd)
                throw new InvalidDataException("FAT fileCount field out of range.");
            var fileCount = reader.ReadUInt32();

            var entries = new List<FatEntry>(fileCount > int.MaxValue ? int.MaxValue : (int)fileCount);
            for (uint i = 0; i < fileCount; i++)
            {
                if (reader.BaseStream.Position + 16 > blockEnd)
                    throw new InvalidDataException($"FAT entry[{i}] truncated.");

                uint dataOffset = reader.ReadUInt32(); 
                uint dataSize = reader.ReadUInt32(); 
                _ = reader.ReadUInt32();              
                _ = reader.ReadUInt32();              

                entries.Add(new FatEntry(dataOffset, dataSize));
            }

            return new FAT(fatSize, entries);
        }

        private static string ReadFourCc(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);
            return Encoding.ASCII.GetString(bytes);
        }
    }
}

