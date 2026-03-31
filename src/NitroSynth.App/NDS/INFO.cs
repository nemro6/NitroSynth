using NitroSynth.App;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace NitroSynth.App
{
    public sealed class INFO
    {
        public uint Size { get; }

        public IReadOnlyDictionary<int, SseqInfo> Sseq { get; }
        public IReadOnlyDictionary<int, SsarInfo> Ssar { get; }
        public IReadOnlyDictionary<int, SbnkInfo> Sbnk { get; }
        public IReadOnlyDictionary<int, SwarInfo> Swar { get; }
        public IReadOnlyDictionary<int, PlayerInfo> Player { get; }
        public IReadOnlyDictionary<int, GroupInfo> Group { get; }
        public IReadOnlyDictionary<int, StrmPlayerInfo> StrmPlayer { get; }
        public IReadOnlyDictionary<int, StrmInfo> Strm { get; }

        private INFO(
            uint size,
            Dictionary<int, SseqInfo> sseq,
            Dictionary<int, SsarInfo> ssar,
            Dictionary<int, SbnkInfo> sbnk,
            Dictionary<int, SwarInfo> swar,
            Dictionary<int, PlayerInfo> player,
            Dictionary<int, GroupInfo> group,
            Dictionary<int, StrmPlayerInfo> strmPlayer,
            Dictionary<int, StrmInfo> strm)
        {
            Size = size;
            Sseq = sseq;
            Ssar = ssar;
            Sbnk = sbnk;
            Swar = swar;
            Player = player;
            Group = group;
            StrmPlayer = strmPlayer;
            Strm = strm;
        }

        public static INFO Load(Stream stream, uint infoOffset, uint infoSize)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            var blockStart = (long)infoOffset;
            var blockEnd = blockStart + infoSize;

            stream.Seek(blockStart, SeekOrigin.Begin);

            var fourcc = ReadFourCc(reader);
            if (fourcc != "INFO")
                throw new InvalidDataException($"INFO fourcc mismatch: '{fourcc}'");

            var sizeFromHeader = reader.ReadUInt32();
            if (sizeFromHeader != infoSize)
                throw new InvalidDataException($"INFO size mismatch: header={sizeFromHeader} actual={infoSize}");

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
                throw new InvalidDataException("INFO padding overrun.");
            stream.Seek(PaddingSize, SeekOrigin.Current);

            var sseq = ReadTable(reader, blockStart, blockEnd, sseqTableOfs, ReadSseqEntry);
            var ssar = ReadTable(reader, blockStart, blockEnd, ssarTableOfs, ReadSsarEntry);
            var sbnk = ReadTable(reader, blockStart, blockEnd, sbnkTableOfs, ReadSbnkEntry);
            var swar = ReadTable(reader, blockStart, blockEnd, swarTableOfs, ReadSwarEntry);
            var player = ReadTable(reader, blockStart, blockEnd, playerTableOfs, ReadPlayerEntry);
            var group = ReadTable(reader, blockStart, blockEnd, groupTableOfs, ReadGroupEntry);
            var splr = ReadTable(reader, blockStart, blockEnd, strmPlayerTableOfs, ReadStrmPlayerEntry);
            var strm = ReadTable(reader, blockStart, blockEnd, strmTableOfs, ReadStrmEntry);

            return new INFO(infoSize, sseq, ssar, sbnk, swar, player, group, splr, strm);
        }

        private static Dictionary<int, T> ReadTable<T>(BinaryReader reader, long blockStart, long blockEnd, uint tableRelOfs, Func<BinaryReader, long, long, T> parse)
        {
            var result = new Dictionary<int, T>();
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
                if (count > 0x100000) throw new InvalidDataException($"INFO table too large: {count}");

                if (stream.Position + count * 4L > blockEnd)
                    throw new InvalidDataException("INFO table offsets out of range.");

                var rels = new uint[count];
                for (int i = 0; i < count; i++) rels[i] = reader.ReadUInt32();

                for (int i = 0; i < count; i++)
                {
                    uint rel = rels[i];
                    if (rel == 0 || rel == 0xFFFFFFFF) continue;
                    long pos = blockStart + rel;
                    if (pos < blockStart || pos >= blockEnd) continue;

                    stream.Seek(pos, SeekOrigin.Begin);
                    var entry = parse(reader, blockStart, blockEnd);
                    result[i] = entry;
                }
            }
            finally
            {
                stream.Seek(save, SeekOrigin.Begin);
            }

            return result;
        }

        private static SseqInfo ReadSseqEntry(BinaryReader r, long start, long end)
        {
            if (r.BaseStream.Position + 12 > end) throw new InvalidDataException("SSEQ entry truncated.");
            var fileId = r.ReadUInt32();
            var sbnkId = r.ReadUInt16();
            var volume = r.ReadByte();
            var channelPriority = r.ReadByte();
            var playerPriority = r.ReadByte();
            var playerId = r.ReadByte();
            _ = r.ReadUInt16(); 
            return new SseqInfo(fileId, sbnkId, volume, channelPriority, playerPriority, playerId);
        }

        private static SsarInfo ReadSsarEntry(BinaryReader r, long start, long end)
        {
            if (r.BaseStream.Position + 4 > end) throw new InvalidDataException("SSAR entry truncated.");
            var fileId = r.ReadUInt32();
            return new SsarInfo(fileId);
        }

        private static SbnkInfo ReadSbnkEntry(BinaryReader r, long start, long end)
        {
            if (r.BaseStream.Position + 12 > end) throw new InvalidDataException("SBNK entry truncated.");
            var fileId = r.ReadUInt32();
            var sw0 = r.ReadInt16();
            var sw1 = r.ReadInt16();
            var sw2 = r.ReadInt16();
            var sw3 = r.ReadInt16();
            return new SbnkInfo(fileId, sw0, sw1, sw2, sw3);
        }

        private static SwarInfo ReadSwarEntry(BinaryReader r, long start, long end)
        {
            if (r.BaseStream.Position + 4 > end) throw new InvalidDataException("SWAR entry truncated.");
            var raw = r.ReadUInt32();
            return new SwarInfo(raw);
        }

        private static PlayerInfo ReadPlayerEntry(BinaryReader r, long start, long end)
        {
            if (r.BaseStream.Position + 8 > end) throw new InvalidDataException("PLAYER entry truncated.");
            var maxSeq = r.ReadUInt16();
            var chFlags = r.ReadUInt16();
            var heapSize = r.ReadUInt32();
            return new PlayerInfo(maxSeq, chFlags, heapSize);
        }

        private static GroupInfo ReadGroupEntry(BinaryReader r, long start, long end)
        {
            if (r.BaseStream.Position + 8 > end) throw new InvalidDataException("GROUP entry truncated.");
            var type = r.ReadByte();
            var flags = r.ReadByte();
            _ = r.ReadUInt16(); 
            var entryId = r.ReadUInt32();
            var entry = new GroupEntry(type, flags, entryId);
            return new GroupInfo(new List<GroupEntry> { entry });
        }

        private static StrmPlayerInfo ReadStrmPlayerEntry(BinaryReader r, long start, long end)
        {
            if (r.BaseStream.Position + 24 > end) throw new InvalidDataException("STRMPLAYER entry truncated.");
            var channels = r.ReadByte();
            var leftMono = r.ReadByte();
            var right = r.ReadByte();
            for (int i = 0; i < 21; i++) r.ReadByte(); 
            return new StrmPlayerInfo(channels, leftMono, right);
        }

        private static StrmInfo ReadStrmEntry(BinaryReader r, long start, long end)
        {
            if (r.BaseStream.Position + 12 > end) throw new InvalidDataException("STRM entry truncated.");
            var rawId = r.ReadUInt32();
            var volume = r.ReadByte();
            var prio = r.ReadByte();
            var player = r.ReadByte();
            for (int i = 0; i < 5; i++) r.ReadByte(); 
            return new StrmInfo(rawId, volume, prio, player);
        }

        public void DumpToDebug(string tag = "INFO", SYMB? symb = null, FAT? fat = null)
        {
            void Log(string s) { Debug.WriteLine(s); try { Console.WriteLine(s); } catch { } }
            Log($"[{tag}] --- INFO dump --- Size=0x{Size:X8} ({Size:N0} bytes)");


            static string? NameOrNull(IReadOnlyDictionary<int, string>? dict, int idx)
            => (dict != null && dict.TryGetValue(idx, out var n)) ? n : null;


            static string Label(string table, int idx, string? name)
            => name is null ? $"{table}[{idx}]" : $"{table}[{idx}] \"{name}\"";


            string FatSuffix(uint fileId)
            {
                if (fat?.Entries is { } list && fileId < list.Count)
                {
                    var e = list[(int)fileId];
                    return $" Off=0x{e.Offset:X8}({e.Offset:N0}) Size=0x{e.Size:X8}({e.Size:N0})";
                }
                return " Off=? Size=?";
            }


            void Dump<T>(string table, IReadOnlyDictionary<int, T> dict, Func<int, string?> nameGetter, Func<T, string> fmt)
            {
                if (dict.Count == 0) { Log($"[{tag}] {table}: (empty)"); return; }
                foreach (var kv in dict.OrderBy(k => k.Key))
                    Log($"[{tag}] {Label(table, kv.Key, nameGetter(kv.Key))} = {fmt(kv.Value)}");
            }


            Dump("SSEQ", Sseq, i => NameOrNull(symb?.Sseq, i), v => $"FileId={v.FileId} SBNK={v.SbnkId} Vol={v.Volume} ChPrio={v.ChannelPriority} PlPrio={v.PlayerPriority} PlayerId={v.PlayerId}{FatSuffix(v.FileId)}");
            Dump("SSAR", Ssar, i => NameOrNull(symb?.Ssar, i), v => $"FileId={v.FileId}{FatSuffix(v.FileId)}");
            Dump("SBNK", Sbnk, i => NameOrNull(symb?.Sbnk, i), v => $"FileId={v.FileId} SWARs=[{v.Swar0},{v.Swar1},{v.Swar2},{v.Swar3}]{FatSuffix(v.FileId)}");
            Dump("SWAR", Swar, i => NameOrNull(symb?.Swar, i), v => $"Raw=0x{v.RawId:X8} FileId={v.FileId} LoadIndividual={(v.LoadIndividually ? 1 : 0)}{FatSuffix(v.FileId)}");
            Dump("PLAYER", Player, i => NameOrNull(symb?.Player, i), v => $"MaxSeq={v.MaxSequences} ChFlags=0x{v.ChannelBitflags:X4} Heap={v.HeapSize}");
            Dump("GROUP", Group, i => NameOrNull(symb?.Group, i), v => string.Join(" ", v.Entries.Select(e => $"(T={e.EntryType} F=0x{e.Flags:X2} Id={e.EntryId})")));
            Dump("STRMPLR", StrmPlayer, i => NameOrNull(symb?.StrmPlayer, i), v => $"Ch={v.Channels} L/M={v.LeftOrMono} R={v.Right}");
            Dump("STRM", Strm, i => NameOrNull(symb?.Strm, i), v => $"Raw=0x{v.RawId:X8} FileId={v.FileId} AutoStereo={(v.AutoStereo ? 1 : 0)} Vol={v.Volume} Prio={v.Priority} Player={v.PlayerId}{FatSuffix(v.FileId)}");
        }


        private static string ReadFourCc(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);
            return Encoding.ASCII.GetString(bytes);
        }
    }

    public readonly record struct SseqInfo(uint FileId, ushort SbnkId, byte Volume, byte ChannelPriority, byte PlayerPriority, byte PlayerId);
    public readonly record struct SsarInfo(uint FileId);
    public readonly record struct SbnkInfo(uint FileId, short Swar0, short Swar1, short Swar2, short Swar3);

    public readonly record struct SwarInfo(uint RawId)
    {
        public uint FileId => RawId & 0x00FF_FFFF;                 
        public bool LoadIndividually => ((RawId >> 24) & 0x01) != 0; 
    }

    public readonly record struct PlayerInfo(ushort MaxSequences, ushort ChannelBitflags, uint HeapSize);

    public readonly record struct GroupEntry(byte EntryType, byte Flags, uint EntryId);
    public sealed class GroupInfo
    {
        public IReadOnlyList<GroupEntry> Entries { get; }
        public GroupInfo(IReadOnlyList<GroupEntry> entries) { Entries = entries; }
    }

    public readonly record struct StrmPlayerInfo(byte Channels, byte LeftOrMono, byte Right);

    public readonly record struct StrmInfo(uint RawId, byte Volume, byte Priority, byte PlayerId)
    {
        public uint FileId => RawId & 0x00FF_FFFF;                 
        public bool AutoStereo => ((RawId >> 24) & 0x01) != 0;     
    }
}

