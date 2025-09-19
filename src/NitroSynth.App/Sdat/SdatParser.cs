using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NitroSynth.App.Sdat;

public static class SdatParser
{
    private const int InfoTableCount = 8;
    private const int BankInfoTableIndex = 2;

    public static async Task<SdatArchive> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var seekableStream = stream.CanSeek ? stream : await CopyToMemoryStreamAsync(stream, cancellationToken).ConfigureAwait(false);
        try
        {
            using var reader = new BinaryReader(seekableStream, Encoding.ASCII, leaveOpen: true);

            var magic = ReadFourCc(reader);
            if (!string.Equals(magic, "SDAT", StringComparison.Ordinal))
            {
                throw new InvalidDataException("SDATヘッダーが見つかりませんでした。");
            }

            _ = reader.ReadUInt32(); // header size
            _ = reader.ReadUInt16(); // version
            _ = reader.ReadUInt16(); // reserved
            var fileSize = reader.ReadUInt32();
            var blockCount = reader.ReadUInt16();
            _ = reader.ReadUInt16(); // reserved

            var blocks = new List<BlockInfo>(blockCount);
            for (int i = 0; i < blockCount; i++)
            {
                var offset = reader.ReadUInt32();
                var size = reader.ReadUInt32();
                if (offset == 0 || size == 0)
                {
                    continue;
                }

                blocks.Add(new BlockInfo(offset, size));
            }

            var bankNames = new Dictionary<int, string>();
            foreach (var block in blocks)
            {
                var header = ReadBlockHeader(reader, seekableStream, block.Offset);
                if (header.Type == "SYMB")
                {
                    bankNames = ReadBankNames(reader, seekableStream, block.Offset, header.Size);
                    break;
                }
            }

            IReadOnlyList<SoundBankSummary> banks = Array.Empty<SoundBankSummary>();
            foreach (var block in blocks)
            {
                var header = ReadBlockHeader(reader, seekableStream, block.Offset);
                if (header.Type == "INFO")
                {
                    banks = ReadSoundBanks(reader, seekableStream, block.Offset, header.Size, bankNames);
                    break;
                }
            }

            return new SdatArchive(fileSize, banks);
        }
        finally
        {
            if (!ReferenceEquals(seekableStream, stream))
            {
                seekableStream.Dispose();
            }
        }
    }

    private static async Task<Stream> CopyToMemoryStreamAsync(Stream source, CancellationToken cancellationToken)
    {
        var memoryStream = new MemoryStream();
        await source.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        memoryStream.Position = 0;
        return memoryStream;
    }

    private static (string Type, uint Size) ReadBlockHeader(BinaryReader reader, Stream stream, uint offset)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var type = ReadFourCc(reader);
        var size = reader.ReadUInt32();
        return (type, size);
    }

    private static string ReadFourCc(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        return Encoding.ASCII.GetString(bytes);
    }

    private static Dictionary<int, string> ReadBankNames(BinaryReader reader, Stream stream, uint blockOffset, uint blockSize)
    {
        var names = new Dictionary<int, string>();
        var blockStart = (long)blockOffset;
        var blockEnd = blockStart + blockSize;

        stream.Seek(blockStart + 8, SeekOrigin.Begin);
        _ = reader.ReadUInt32(); // sequence table offset
        var bankTableOffset = reader.ReadUInt32();

        if (bankTableOffset == 0)
        {
            return names;
        }

        var bankTableBase = blockStart + bankTableOffset;
        if (bankTableBase + 4 > blockEnd)
        {
            return names;
        }

        stream.Seek(bankTableBase, SeekOrigin.Begin);
        var bankCount = reader.ReadUInt32();
        var entryOffsets = new uint[bankCount];
        for (int i = 0; i < bankCount; i++)
        {
            entryOffsets[i] = reader.ReadUInt32();
        }

        for (int i = 0; i < bankCount; i++)
        {
            var relativeOffset = entryOffsets[i];
            if (relativeOffset == 0 || relativeOffset == 0xFFFFFFFF)
            {
                continue;
            }

            var namePosition = bankTableBase + relativeOffset;
            if (namePosition < blockStart || namePosition >= blockEnd)
            {
                continue;
            }

            stream.Seek(namePosition, SeekOrigin.Begin);
            var name = ReadNullTerminatedString(reader, blockEnd);
            if (!string.IsNullOrWhiteSpace(name))
            {
                names[i] = name;
            }
        }

        return names;
    }

    private static IReadOnlyList<SoundBankSummary> ReadSoundBanks(
        BinaryReader reader,
        Stream stream,
        uint blockOffset,
        uint blockSize,
        IReadOnlyDictionary<int, string> bankNames)
    {
        var results = new List<SoundBankSummary>();
        var blockStart = (long)blockOffset;
        var blockEnd = blockStart + blockSize;

        stream.Seek(blockStart + 8, SeekOrigin.Begin);
        var tableOffsets = new List<uint>(InfoTableCount);
        for (int i = 0; i < InfoTableCount; i++)
        {
            if (stream.Position + 4 > blockEnd)
            {
                break;
            }

            tableOffsets.Add(reader.ReadUInt32());
        }

        if (tableOffsets.Count <= BankInfoTableIndex)
        {
            return results;
        }

        var bankTableOffset = tableOffsets[BankInfoTableIndex];
        if (bankTableOffset == 0)
        {
            return results;
        }

        var bankTableBase = blockStart + bankTableOffset;
        if (bankTableBase + 4 > blockEnd)
        {
            return results;
        }

        stream.Seek(bankTableBase, SeekOrigin.Begin);
        var bankCount = reader.ReadUInt32();
        if (bankCount == 0)
        {
            return results;
        }

        var entryOffsets = new uint[bankCount];
        for (int i = 0; i < bankCount; i++)
        {
            entryOffsets[i] = reader.ReadUInt32();
        }

        for (int i = 0; i < bankCount; i++)
        {
            var relativeOffset = entryOffsets[i];
            if (relativeOffset == 0 || relativeOffset == 0xFFFFFFFF)
            {
                continue;
            }

            var entryPosition = blockStart + relativeOffset;
            if (entryPosition < blockStart || entryPosition + 4 > blockEnd)
            {
                continue;
            }

            stream.Seek(entryPosition, SeekOrigin.Begin);
            var fileId = reader.ReadUInt32();
            var name = bankNames.TryGetValue(i, out var existing)
                ? existing
                : null;
            results.Add(new SoundBankSummary(i, fileId, name));
        }

        return results;
    }

    private static string ReadNullTerminatedString(BinaryReader reader, long limit)
    {
        var buffer = new List<byte>();
        while (reader.BaseStream.Position < limit)
        {
            var value = reader.ReadByte();
            if (value == 0)
            {
                break;
            }

            buffer.Add(value);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private readonly struct BlockInfo
    {
        public BlockInfo(uint offset, uint size)
        {
            Offset = offset;
            Size = size;
        }

        public uint Offset { get; }

        public uint Size { get; }
    }
}
