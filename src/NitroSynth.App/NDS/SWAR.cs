using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NitroSynth.App
{
    public sealed class SWAR
    {
        public readonly struct Entry
        {
            public readonly int Index;
            public readonly int AbsOffsetFromFileStart;
            public readonly int Size;

            public Entry(int idx, int absOff, int size)
            {
                Index = idx;
                AbsOffsetFromFileStart = absOff;
                Size = size;
            }

            public override string ToString() => $"SWAV[{Index}] @0x{AbsOffsetFromFileStart:X} ({Size}B)";
        }

        public IReadOnlyList<Entry> Entries => _entries;
        private readonly List<Entry> _entries = new();
        private readonly Dictionary<int, Entry> _entryByIndex = new();

        private byte[] _raw = Array.Empty<byte>();

        public static SWAR Parse(byte[] swarBytes)
        {
            if (TryParse(swarBytes, out var swar))
                return swar;

            string head = (swarBytes is not null && swarBytes.Length >= 1)
                ? Encoding.ASCII.GetString(swarBytes, 0, Math.Min(4, swarBytes.Length))
                : string.Empty;
            throw new InvalidDataException($"SWAR magic not found. Header='{head}'.");
        }

        public static bool TryParse(byte[] swarBytes, out SWAR swar)
        {
            swar = null!;

            if (swarBytes is null || swarBytes.Length < 4)
                return false;

            if (TryParseAsSwar(swarBytes, out swar))
                return true;

            // Some SDATs may reference standalone SWAV files from SWAR slots.
            // Treat those as a single-entry pseudo-SWAR.
            if (TryParseAsSwavContainer(swarBytes, out swar))
                return true;

            return false;
        }

        private static bool TryParseAsSwar(byte[] swarBytes, out SWAR swar)
        {
            swar = null!;

            using var ms = new MemoryStream(swarBytes, false);
            using var br = new BinaryReader(ms);

            var parsed = new SWAR();

            if (new string(br.ReadChars(4)) != "SWAR")
                return false;

            _ = br.ReadUInt16(); // byteOrder
            _ = br.ReadUInt16(); // version
            uint fileSize = br.ReadUInt32();
            _ = br.ReadUInt16(); // headerSize
            _ = br.ReadUInt16(); // blockCount

            if (new string(br.ReadChars(4)) != "DATA")
                return false;

            _ = br.ReadUInt32(); // dataSize

            br.BaseStream.Position += 0x20;

            uint waveCount = br.ReadUInt32();
            var offs = new int[waveCount];
            for (int i = 0; i < waveCount; i++)
                offs[i] = br.ReadInt32();

            int fileEnd = (int)Math.Min(fileSize, (uint)swarBytes.Length);
            for (int i = 0; i < waveCount; i++)
            {
                int startAbs = offs[i];
                int endAbs = (i + 1 < waveCount) ? offs[i + 1] : fileEnd;

                if (startAbs < 0 || endAbs < 0 || endAbs < startAbs) continue;
                if (startAbs >= swarBytes.Length) continue;
                if (endAbs > swarBytes.Length) endAbs = swarBytes.Length;

                int size = endAbs - startAbs;
                if (size <= 0) continue;

                var entry = new Entry(i, startAbs, size);
                parsed._entries.Add(entry);
                parsed._entryByIndex[i] = entry;
            }

            parsed._raw = swarBytes;
            swar = parsed;
            return true;
        }

        private static bool TryParseAsSwavContainer(byte[] swavContainerBytes, out SWAR swar)
        {
            swar = null!;

            if (swavContainerBytes.Length < 0x1C)
                return false;

            if (Encoding.ASCII.GetString(swavContainerBytes, 0, 4) != "SWAV")
                return false;

            if (Encoding.ASCII.GetString(swavContainerBytes, 0x10, 4) != "DATA")
                return false;

            uint fileSize = BitConverter.ToUInt32(swavContainerBytes, 0x08);
            uint dataSize = BitConverter.ToUInt32(swavContainerBytes, 0x14);

            int payloadStart = 0x18;
            int safeFileEnd = (int)Math.Min(fileSize, (uint)swavContainerBytes.Length);
            int payloadEnd = safeFileEnd;

            if (dataSize >= 8)
            {
                int dataChunkEnd = 0x10 + (int)Math.Min(dataSize, int.MaxValue);
                payloadEnd = Math.Min(payloadEnd, dataChunkEnd);
            }

            if (payloadStart >= payloadEnd || payloadEnd > swavContainerBytes.Length)
                return false;

            int payloadSize = payloadEnd - payloadStart;
            if (payloadSize < 12) // SWAV info(12B)+PCM
                return false;

            var raw = new byte[payloadSize];
            Buffer.BlockCopy(swavContainerBytes, payloadStart, raw, 0, payloadSize);

            var parsed = new SWAR();
            var single = new Entry(0, 0, raw.Length);
            parsed._entries.Add(single);
            parsed._entryByIndex[0] = single;
            parsed._raw = raw;

            swar = parsed;
            return true;
        }

        public SWAV GetSwav(int index)
        {
            if (!_entryByIndex.TryGetValue(index, out var e))
                throw new ArgumentOutOfRangeException(nameof(index), $"SWAV index {index} is not present in this SWAR.");

            if (e.Size <= 0) throw new InvalidDataException("SWAR entry has non-positive size.");
            if (e.AbsOffsetFromFileStart < 0 || e.AbsOffsetFromFileStart > _raw.Length)
                throw new InvalidDataException("SWAR entry start out of range.");
            if (e.AbsOffsetFromFileStart + e.Size > _raw.Length)
                throw new InvalidDataException("SWAR entry exceeds buffer.");

            var slice = new byte[e.Size];
            Buffer.BlockCopy(_raw, e.AbsOffsetFromFileStart, slice, 0, e.Size);
            return SWAV.Parse(slice);
        }

        public bool TryGetSwav(int index, out SWAV swav)
        {
            swav = null!;
            if (!_entryByIndex.TryGetValue(index, out var e))
                return false;

            try
            {
                if (e.Size <= 0) return false;
                if (e.AbsOffsetFromFileStart < 0 || e.AbsOffsetFromFileStart > _raw.Length) return false;
                if (e.AbsOffsetFromFileStart + e.Size > _raw.Length) return false;

                var slice = new byte[e.Size];
                Buffer.BlockCopy(_raw, e.AbsOffsetFromFileStart, slice, 0, e.Size);
                swav = SWAV.Parse(slice);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
