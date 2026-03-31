using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NitroSynth.App;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace NitroSynth.app.NDS
{
    public sealed class SDAT
    {
        public uint   SdatSize { get; }
        public ushort SdatVersion { get; }
        public ushort HeaderSize { get; }
        public ushort BlockCount { get; }

        public uint   SymbOffset { get; }
        public uint   SymbSize { get; }
        public uint   InfoOffset { get; }
        public uint   InfoSize { get; }
        public uint   FatOffset { get; }
        public uint   FatSize { get; }
        public uint   FileBlockOffset { get; }
        public uint   FileBlockSize { get; }

        private SDAT(
            uint sdatSize, ushort sdatVersion, ushort headerSize, ushort blockCount,
            uint symbOffset, uint symbSize, uint infoOffset, uint infoSize,
            uint fatOffset, uint fatSize, uint fileOffset, uint fileSize)
        {
            SdatSize        = sdatSize;
            SdatVersion     = sdatVersion;
            HeaderSize      = headerSize;
            BlockCount      = blockCount;
            SymbOffset      = symbOffset;
            SymbSize        = symbSize;
            InfoOffset      = infoOffset;
            InfoSize        = infoSize;
            FatOffset       = fatOffset;
            FatSize         = fatSize;
            FileBlockOffset = fileOffset;
            FileBlockSize   = fileSize;
        }

        public static async Task<SDAT> LoadAsync(Stream stream, CancellationToken ct = default)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));
            var seekable = stream.CanSeek ? stream : await CopyToMemoryAsync(stream, ct).ConfigureAwait(false);

            try
            {
                using var reader = new BinaryReader(seekable, Encoding.ASCII, leaveOpen: true);

                var magic = ReadFourCc(reader);
                if (!string.Equals(magic, "SDAT", StringComparison.Ordinal))
                    throw new InvalidDataException("SDAT header was not found.");

                ushort byteOrder    = reader.ReadUInt16();
                ushort version      = reader.ReadUInt16();
                uint fileSize       = reader.ReadUInt32();
                ushort headerSize   = reader.ReadUInt16();
                ushort blockCount   = reader.ReadUInt16();

                uint symbOffset     = reader.ReadUInt32();
                uint symbSize       = reader.ReadUInt32();
                uint infoOffset     = reader.ReadUInt32();
                uint infoSize       = reader.ReadUInt32();
                uint fatOffset      = reader.ReadUInt32();
                uint fatSize        = reader.ReadUInt32();
                uint fileOffset     = reader.ReadUInt32();
                uint fileSizeBlk    = reader.ReadUInt32();

                if (byteOrder != 0xFEFF)
                    throw new InvalidDataException($"Unsupported byte order: 0x{byteOrder:X4}");
                if (version == 0)
                    throw new InvalidDataException("Invalid SDAT version (0)");
                if (infoOffset == 0 || fatOffset == 0 || fileOffset == 0)
                    throw new InvalidDataException("Required block offsets (INFO/FAT/FILE) are zero.");

                return new SDAT(fileSize, version, headerSize, blockCount,
                                 symbOffset, symbSize, infoOffset, infoSize,
                                 fatOffset, fatSize, fileOffset, fileSizeBlk);
            }
            finally
            {
                if (!ReferenceEquals(seekable, stream))
                    seekable.Dispose();
            }
        }

        public SYMB? ReadSYMB(Stream stream)
        {
            if (SymbOffset == 0 || SymbSize < 8) return null;

            long save = stream.Position;
            try
            {
                stream.Seek(SymbOffset, SeekOrigin.Begin);
                using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

                var four = ReadFourCc(reader);
                var size = reader.ReadUInt32();

                return SYMB.Load(stream, SymbOffset, SymbSize);
            }
            finally
            {
                stream.Seek(save, SeekOrigin.Begin);
            }
        }

        public INFO? ReadINFO(Stream stream)
        {
            if (InfoOffset == 0 || InfoSize < 8) return null;
            return INFO.Load(stream, InfoOffset, InfoSize);
        }

        public FAT? ReadFAT(Stream stream)
        {
            if (FatOffset == 0 || FatSize < 12) return null;
            return FAT.Load(stream, FatOffset, FatSize);
        }

        private static async Task<Stream> CopyToMemoryAsync(Stream src, CancellationToken ct)
        {
            var ms = new MemoryStream();
            await src.CopyToAsync(ms, ct).ConfigureAwait(false);
            ms.Position = 0;
            return ms;
        }

        internal static string ReadFourCc(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);
            return Encoding.ASCII.GetString(bytes);
        }
    }
}


