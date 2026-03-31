using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NitroSynth.App
{
    public sealed class SSAR
    {
        private const int SequenceRecordSize = 12;

        public sealed class HeaderInfo
        {
            public string Magic { get; init; } = string.Empty;
            public ushort ByteOrder { get; init; }
            public ushort Version { get; init; }
            public uint FileSize { get; init; }
            public ushort HeaderSize { get; init; }
            public ushort BlockCount { get; init; }
            public string DataMagic { get; init; } = string.Empty;
            public uint DataSize { get; init; }
            public uint DataOffset { get; init; }
            public uint SequenceCount { get; init; }
        }

        public sealed class SequenceInfo
        {
            public int Index { get; init; }
            public uint SequenceOffsetRaw { get; init; }
            public int SequenceOffset { get; init; }
            public ushort SbnkId { get; init; }
            public byte Volume { get; init; }
            public byte ChannelPriority { get; init; }
            public byte PlayerPriority { get; init; }
            public byte PlayerId { get; init; }
            public ushort Reserved { get; init; }
        }

        public HeaderInfo Header { get; }
        public IReadOnlyList<SequenceInfo> Sequences { get; }

        private SSAR(HeaderInfo header, List<SequenceInfo> sequences)
        {
            Header = header;
            Sequences = sequences;
        }

        public static SSAR Read(byte[] fileBytes)
        {
            if (fileBytes is null)
                throw new ArgumentNullException(nameof(fileBytes));
            if (fileBytes.Length < 0x20)
                throw new InvalidDataException("SSAR file is too short.");

            using var ms = new MemoryStream(fileBytes, writable: false);
            using var br = new BinaryReader(ms, Encoding.ASCII, leaveOpen: true);

            string magic = ReadFourCc(br);
            if (!string.Equals(magic, "SSAR", StringComparison.Ordinal))
                throw new InvalidDataException($"Invalid SSAR magic: {magic}");

            ushort byteOrder = br.ReadUInt16();
            ushort version = br.ReadUInt16();
            uint fileSize = br.ReadUInt32();
            ushort headerSize = br.ReadUInt16();
            ushort blockCount = br.ReadUInt16();

            string dataMagic = ReadFourCc(br);
            uint dataSize = br.ReadUInt32();
            uint dataOffset = br.ReadUInt32();
            uint sequenceCount = br.ReadUInt32();

            if (!string.Equals(dataMagic, "DATA", StringComparison.Ordinal))
                throw new InvalidDataException($"Invalid SSAR DATA magic: {dataMagic}");

            uint effectiveFileSize = fileSize;
            if (effectiveFileSize == 0 || effectiveFileSize > fileBytes.Length)
                effectiveFileSize = (uint)fileBytes.Length;

            int tableStart = 0x20;
            if (tableStart > fileBytes.Length)
                throw new InvalidDataException("SSAR sequence table start is out of range.");

            int maxEntriesByFile = Math.Max(0, (fileBytes.Length - tableStart) / SequenceRecordSize);
            int maxEntries = maxEntriesByFile;

            if (dataOffset > (uint)tableStart && dataOffset <= effectiveFileSize)
            {
                int maxEntriesByDataOffset = (int)((dataOffset - (uint)tableStart) / SequenceRecordSize);
                maxEntries = Math.Min(maxEntries, maxEntriesByDataOffset);
            }

            int parseCount = (int)Math.Min(sequenceCount, (uint)Math.Max(0, maxEntries));

            var sequences = new List<SequenceInfo>(parseCount);
            for (int i = 0; i < parseCount; i++)
            {
                int recordOffset = tableStart + (i * SequenceRecordSize);
                if (recordOffset + SequenceRecordSize > fileBytes.Length)
                    break;

                ms.Position = recordOffset;

                uint sequenceOffsetRaw = br.ReadUInt32();
                ushort sbnkId = br.ReadUInt16();
                byte volume = br.ReadByte();
                byte channelPriority = br.ReadByte();
                byte playerPriority = br.ReadByte();
                byte playerId = br.ReadByte();
                ushort reserved = br.ReadUInt16();

                sequences.Add(new SequenceInfo
                {
                    Index = i,
                    SequenceOffsetRaw = sequenceOffsetRaw,
                    SequenceOffset = ResolveSequenceOffset(sequenceOffsetRaw, dataOffset, fileBytes),
                    SbnkId = sbnkId,
                    Volume = volume,
                    ChannelPriority = channelPriority,
                    PlayerPriority = playerPriority,
                    PlayerId = playerId,
                    Reserved = reserved
                });
            }

            var header = new HeaderInfo
            {
                Magic = magic,
                ByteOrder = byteOrder,
                Version = version,
                FileSize = fileSize,
                HeaderSize = headerSize,
                BlockCount = blockCount,
                DataMagic = dataMagic,
                DataSize = dataSize,
                DataOffset = dataOffset,
                SequenceCount = sequenceCount
            };

            return new SSAR(header, sequences);
        }

        private static int ResolveSequenceOffset(uint rawOffset, uint dataOffset, ReadOnlySpan<byte> fileBytes)
        {
            if (rawOffset == 0xFFFFFFFF)
                return -1;

            int fileLength = fileBytes.Length;
            bool rawInRange = rawOffset < (uint)fileLength;
            long withBase = (long)dataOffset + rawOffset;
            bool withBaseInRange = withBase >= 0 && withBase < fileLength;

            if (rawInRange && !withBaseInRange)
                return (int)rawOffset;
            if (!rawInRange && withBaseInRange)
                return (int)withBase;
            if (!rawInRange && !withBaseInRange)
                return -1;

            if (rawOffset < dataOffset)
                return (int)withBase;

            int absolute = (int)rawOffset;
            int relative = (int)withBase;

            bool absoluteDecodable = SseqEventDecoder.TryDecode(fileBytes, absolute, out _, out _);
            bool relativeDecodable = SseqEventDecoder.TryDecode(fileBytes, relative, out _, out _);

            if (relativeDecodable && !absoluteDecodable)
                return relative;
            if (absoluteDecodable && !relativeDecodable)
                return absolute;

            // NDS SSAR sequence offsets are DATA-relative in practice.
            return relative;
        }

        private static string ReadFourCc(BinaryReader br)
        {
            byte[] bytes = br.ReadBytes(4);
            if (bytes.Length != 4)
                throw new EndOfStreamException("Unexpected end of stream while reading fourcc.");
            return Encoding.ASCII.GetString(bytes);
        }
    }
}
