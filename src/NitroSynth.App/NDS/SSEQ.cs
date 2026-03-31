using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NitroSynth.App
{
    public sealed class SSEQ
    {
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
        }

        public HeaderInfo Header { get; }
        public ReadOnlyMemory<byte> EventData { get; }

        private SSEQ(HeaderInfo header, byte[] eventData)
        {
            Header = header;
            EventData = eventData;
        }

        public static SSEQ Read(byte[] fileBytes)
        {
            if (fileBytes is null) throw new ArgumentNullException(nameof(fileBytes));
            if (fileBytes.Length < 0x1C) throw new InvalidDataException("SSEQ file is too short.");

            using var ms = new MemoryStream(fileBytes, writable: false);
            using var br = new BinaryReader(ms, Encoding.ASCII, leaveOpen: true);

            string magic = ReadFourCc(br);
            if (!string.Equals(magic, "SSEQ", StringComparison.Ordinal))
                throw new InvalidDataException($"Invalid SSEQ magic: {magic}");

            ushort byteOrder = br.ReadUInt16();
            ushort version = br.ReadUInt16();
            uint fileSize = br.ReadUInt32();
            ushort headerSize = br.ReadUInt16();
            ushort blockCount = br.ReadUInt16();

            string dataMagic = ReadFourCc(br);
            uint dataSize = br.ReadUInt32();
            uint dataOffset = br.ReadUInt32();

            if (!string.Equals(dataMagic, "DATA", StringComparison.Ordinal))
                throw new InvalidDataException($"Invalid SSEQ DATA magic: {dataMagic}");

            if (dataOffset > fileBytes.Length)
                throw new InvalidDataException($"SSEQ data offset is out of range: 0x{dataOffset:X8}");

            uint effectiveFileSize = fileSize;
            if (effectiveFileSize == 0 || effectiveFileSize > fileBytes.Length)
                effectiveFileSize = (uint)fileBytes.Length;

            if (dataOffset > effectiveFileSize)
                throw new InvalidDataException("SSEQ data offset exceeds file size.");

            int eventLength = checked((int)(effectiveFileSize - dataOffset));
            var events = new byte[eventLength];
            Buffer.BlockCopy(fileBytes, (int)dataOffset, events, 0, eventLength);

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
                DataOffset = dataOffset
            };

            return new SSEQ(header, events);
        }

        public static SSEQ Read(Stream stream, long offset, uint size)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));

            stream.Seek(offset, SeekOrigin.Begin);
            var bytes = new byte[checked((int)size)];
            int read = stream.Read(bytes, 0, bytes.Length);
            if (read != bytes.Length)
                throw new EndOfStreamException("Could not read full SSEQ file data.");

            return Read(bytes);
        }

        public string Decompile()
        {
            return Decompile(sequenceName: null, smftFileName: null);
        }

        public string Decompile(string? sequenceName, string? smftFileName)
        {
            string labelPrefix = NormalizeIdentifier(sequenceName);
            string fileName = string.IsNullOrWhiteSpace(smftFileName)
                ? $"{labelPrefix}.smft"
                : smftFileName.Trim();
            return Decompile(EventData.Span, labelPrefix, fileName);
        }

        private static string Decompile(ReadOnlySpan<byte> data, string labelPrefix, string smftFileName)
        {
            var labels = CollectLabels(data, labelPrefix, out var trackOffsets);
            var emittedTrackHeaders = new HashSet<int>();
            var sb = new StringBuilder();

            AppendSmftHeader(sb, smftFileName);
            sb.AppendLine();

            int offset = 0;
            while (offset < data.Length)
            {
                if (trackOffsets.TryGetValue(offset, out int trackNo) && emittedTrackHeaders.Add(offset))
                {
                    sb.AppendLine(";;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;");
                    sb.AppendLine($"; Track {trackNo}");
                    sb.AppendLine(";;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;");
                    sb.AppendLine();
                }

                if (labels.TryGetValue(offset, out var labelList))
                {
                    foreach (var name in labelList)
                        sb.AppendLine($"{name}:");
                    if (trackOffsets.ContainsKey(offset))
                        sb.AppendLine("; Measure 1 ----------------------------------");
                }

                if (!TryDecodeEvent(data, offset, labels, out var consumed, out var text))
                {
                    sb.AppendLine($"    db 0x{data[offset]:X2}");
                    offset++;
                    continue;
                }

                sb.AppendLine($"    {text}");
                offset += consumed;
            }

            return sb.ToString();
        }

        private static Dictionary<int, List<string>> CollectLabels(
            ReadOnlySpan<byte> data,
            string labelPrefix,
            out Dictionary<int, int> trackOffsets)
        {
            int track0BodyOffset = FindTrack0BodyOffset(data);
            var labels = new Dictionary<int, List<string>>();
            trackOffsets = new Dictionary<int, int>
            {
                [track0BodyOffset] = 0
            };
            var trackPrimaryOffsets = new Dictionary<int, int>
            {
                [0] = track0BodyOffset
            };

            AddLabel(labels, 0, $"{labelPrefix}_Start");
            AddLabel(labels, track0BodyOffset, $"{labelPrefix}_Track_0");

            int offset = 0;
            while (offset < data.Length)
            {
                if (!SseqEventDecoder.TryDecode(data, offset, out var decoded, out _))
                {
                    offset++;
                    continue;
                }

                switch (decoded.Kind)
                {
                    case SseqDecodedKind.OpenTrack:
                    {
                        int trackNo = decoded.Value0 & 0x0F;
                        int destination = decoded.Value1;
                        if ((uint)destination < (uint)data.Length)
                        {
                            string trackLabel = BuildTrackLabel(labelPrefix, trackNo, destination, trackPrimaryOffsets);
                            AddLabel(labels, destination, trackLabel);
                            if (!trackOffsets.ContainsKey(destination))
                                trackOffsets[destination] = trackNo;
                        }
                        break;
                    }
                    case SseqDecodedKind.Jump:
                    case SseqDecodedKind.Call:
                    {
                        int destination = decoded.Value0;
                        if ((uint)destination < (uint)data.Length)
                            AddLabel(labels, destination, $"{labelPrefix}_Label_{destination:X6}");
                        break;
                    }
                }

                offset += decoded.Length;
            }

            return labels;
        }

        private static string BuildTrackLabel(
            string labelPrefix,
            int trackNo,
            int destination,
            IDictionary<int, int> trackPrimaryOffsets)
        {
            if (!trackPrimaryOffsets.TryGetValue(trackNo, out int primaryOffset))
            {
                trackPrimaryOffsets[trackNo] = destination;
                return $"{labelPrefix}_Track_{trackNo}";
            }

            if (primaryOffset == destination)
                return $"{labelPrefix}_Track_{trackNo}";

            return $"{labelPrefix}_Track_{trackNo}_{destination:X6}";
        }

        private static int FindTrack0BodyOffset(ReadOnlySpan<byte> data)
        {
            int offset = 0;
            int afterLastOpenTrack = 0;
            bool sawOpenTrack = false;

            while (offset < data.Length)
            {
                if (!SseqEventDecoder.TryDecode(data, offset, out var decoded, out _))
                    break;

                if (decoded.Kind == SseqDecodedKind.OpenTrack)
                {
                    sawOpenTrack = true;
                    afterLastOpenTrack = offset + decoded.Length;
                    offset += decoded.Length;
                    continue;
                }

                if (decoded.Kind == SseqDecodedKind.AllocateTrack)
                {
                    offset += decoded.Length;
                    continue;
                }

                break;
            }

            if (!sawOpenTrack)
                return 0;

            return Math.Clamp(afterLastOpenTrack, 0, data.Length);
        }

        private static void AddLabel(Dictionary<int, List<string>> labels, int offset, string name)
        {
            if (!labels.TryGetValue(offset, out var list))
            {
                list = new List<string>();
                labels[offset] = list;
            }

            if (!list.Contains(name, StringComparer.Ordinal))
                list.Add(name);
        }

        private static bool TryDecodeEvent(
            ReadOnlySpan<byte> data,
            int offset,
            IReadOnlyDictionary<int, List<string>> labels,
            out int consumed,
            out string text)
        {
            consumed = 0;
            text = string.Empty;

            if (offset >= data.Length)
                return false;

            if (!SseqEventDecoder.TryDecode(data, offset, out var decoded, out _))
                return false;

            consumed = decoded.Length;
            text = FormatDecodedEvent(decoded, labels);
            return true;
        }

        private static string FormatDecodedEvent(
            in SseqDecodedEvent decoded,
            IReadOnlyDictionary<int, List<string>> labels)
        {
            switch (decoded.Kind)
            {
                case SseqDecodedKind.Note:
                    return $"{NoteName(decoded.Value0)} {decoded.Value1}, {decoded.Value2}";

                case SseqDecodedKind.Wait:
                    return $"wait {decoded.Value0}";

                case SseqDecodedKind.Program:
                {
                    int bankId = decoded.Value0 >> 7;
                    int program = decoded.Value0 & 0x7F;
                    return bankId > 0 ? $"prg {bankId}, {program}" : $"prg {program}";
                }

                case SseqDecodedKind.OpenTrack:
                    return $"opentrack {decoded.Value0}, {ResolveLabel(labels, decoded.Value1)}";

                case SseqDecodedKind.Jump:
                    return $"jump {ResolveLabel(labels, decoded.Value0)}";

                case SseqDecodedKind.Call:
                    return $"call {ResolveLabel(labels, decoded.Value0)}";

                case SseqDecodedKind.If:
                    return "if";

                case SseqDecodedKind.Variable:
                    return $"{VariableOpName(decoded.Op)} {decoded.Value0}, {decoded.Signed0}";

                case SseqDecodedKind.Random:
                {
                    var args = new List<string>();
                    if (decoded.WrapperArgCount > 0) args.Add(decoded.WrapperArg0.ToString());
                    if (decoded.WrapperArgCount > 1) args.Add(decoded.WrapperArg1.ToString());
                    if (decoded.WrapperArgCount > 2) args.Add(decoded.WrapperArg2.ToString());
                    if (decoded.WrapperArgCount > 3) args.Add(decoded.WrapperArg3.ToString());
                    string argText = args.Count > 0 ? $"{string.Join(", ", args)}, " : string.Empty;
                    return $"random 0x{decoded.SubType:X2} {argText}{decoded.Signed0}, {decoded.Signed1}";
                }

                case SseqDecodedKind.FromVariable:
                {
                    if (decoded.SubType <= 0x7F)
                        return $"fromvar 0x{decoded.SubType:X2}, {decoded.WrapperArg0}, {decoded.Signed0}";

                    if (decoded.SubType >= 0xB0 && decoded.SubType <= 0xBD)
                        return $"fromvar 0x{decoded.SubType:X2}, {decoded.Signed0}, {decoded.Signed1}";

                    return $"fromvar 0x{decoded.SubType:X2}, {decoded.Signed0}";
                }

                case SseqDecodedKind.SimpleByte:
                    return DecodeSimpleByteCommand(decoded.Op, (byte)decoded.Value0);

                case SseqDecodedKind.ModDelay:
                    return $"mod_delay {decoded.Value0}";

                case SseqDecodedKind.Tempo:
                    return $"tempo {decoded.Value0}";

                case SseqDecodedKind.SweepPitch:
                    return $"sweep_pitch {decoded.Signed0}";

                case SseqDecodedKind.LoopEnd:
                    return "loop_end";

                case SseqDecodedKind.Return:
                    return "ret";

                case SseqDecodedKind.AllocateTrack:
                    return $"alloctrack 0x{decoded.Value0:X4}";

                case SseqDecodedKind.EndTrack:
                    return "fin";
            }

            return $"db 0x{decoded.Op:X2}";
        }

        private static string DecodeSimpleByteCommand(byte op, byte arg)
        {
            return op switch
            {
                0xC0 => $"pan {arg}",
                0xC1 => $"volume {arg}",
                0xC2 => $"main_volume {arg}",
                0xC3 => $"transpose {unchecked((sbyte)arg)}",
                0xC4 => $"pitchbend {unchecked((sbyte)arg)}",
                0xC5 => $"bendrange {arg}",
                0xC6 => $"prio {arg}",
                0xC7 => arg == 0 ? "notewait_off" : "notewait_on",
                0xC8 => arg == 0 ? "tieoff" : "tieon",
                0xC9 => $"porta {arg}",
                0xCA => $"mod_depth {arg}",
                0xCB => $"mod_speed {arg}",
                0xCC => $"mod_type {arg}",
                0xCD => $"mod_range {arg}",
                0xCE => arg == 0 ? "porta_off" : "porta_on",
                0xCF => $"porta_time {arg}",
                0xD0 => $"attack {DecodeEnvelopeArg(arg)}",
                0xD1 => $"decay {DecodeEnvelopeArg(arg)}",
                0xD2 => $"sustain {DecodeEnvelopeArg(arg)}",
                0xD3 => $"release {DecodeEnvelopeArg(arg)}",
                0xD4 => $"loop_start {arg}",
                0xD5 => $"volume2 {arg}",
                0xD6 => $"printvar {arg}",
                0xD7 => $"mute {arg}",
                _ => $"db 0x{op:X2}, 0x{arg:X2}"
            };
        }

        private static int DecodeEnvelopeArg(byte value)
        {
            return value == 0xFF ? -1 : value;
        }

        private static string VariableOpName(byte op)
        {
            return op switch
            {
                0xB0 => "setvar",
                0xB1 => "addvar",
                0xB2 => "subvar",
                0xB3 => "mulvar",
                0xB4 => "divvar",
                0xB5 => "shiftvar",
                0xB6 => "randvar",
                0xB7 => "unkvar_b7",
                0xB8 => "cmp_eq",
                0xB9 => "cmp_ge",
                0xBA => "cmp_gt",
                0xBB => "cmp_le",
                0xBC => "cmp_lt",
                0xBD => "cmp_ne",
                _ => $"op_0x{op:X2}"
            };
        }

        private static string ResolveLabel(IReadOnlyDictionary<int, List<string>> labels, int offset)
        {
            if (labels.TryGetValue(offset, out var list) && list.Count > 0)
            {
                string? start = list.FirstOrDefault(x => x.EndsWith("_Start", StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(start))
                    return start;

                string? track = list.FirstOrDefault(x => x.Contains("_Track_", StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(track))
                    return track;

                return list[0];
            }

            return $"Label_{offset:X6}";
        }

        private static void AppendSmftHeader(StringBuilder sb, string fileName)
        {
            sb.AppendLine(";;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;");
            sb.AppendLine(";");
            sb.AppendLine($"; {fileName}");
            sb.AppendLine(";     Generated By Nitro Synth");
            sb.AppendLine(";");
            sb.AppendLine(";;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;");
        }

        private static string NormalizeIdentifier(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "SSEQ";

            var sb = new StringBuilder(value.Length);
            bool hasHead = false;

            foreach (char c in value)
            {
                bool isAllowed = char.IsLetterOrDigit(c) || c == '_';
                char mapped = isAllowed ? c : '_';

                if (!hasHead)
                {
                    if (!(char.IsLetter(mapped) || mapped == '_'))
                    {
                        sb.Append('_');
                        hasHead = true;
                    }
                }

                sb.Append(mapped);
                hasHead = true;
            }

            string normalized = sb.ToString().Trim('_');
            if (normalized.Length == 0)
                return "SSEQ";

            if (!(char.IsLetter(normalized[0]) || normalized[0] == '_'))
                normalized = "_" + normalized;

            return normalized;
        }

        private static string ReadFourCc(BinaryReader br)
        {
            var bytes = br.ReadBytes(4);
            if (bytes.Length != 4)
                throw new EndOfStreamException("Unexpected end of stream while reading fourcc.");
            return Encoding.ASCII.GetString(bytes);
        }

        private static string NoteName(int midiNote)
        {
            string[] names =
            {
                "cn", "cs", "dn", "ds", "en", "fn",
                "fs", "gn", "gs", "an", "as", "bn"
            };

            int note = midiNote % 12;
            if (note < 0) note += 12;
            int octave = midiNote / 12 - 1;
            return $"{names[note]}{octave}";
        }
    }
}
