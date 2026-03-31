using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    private static readonly Dictionary<string, byte> SseqVariableOps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["setvar"] = 0xB0,
        ["addvar"] = 0xB1,
        ["subvar"] = 0xB2,
        ["mulvar"] = 0xB3,
        ["divvar"] = 0xB4,
        ["shiftvar"] = 0xB5,
        ["randvar"] = 0xB6,
        ["unkvar_b7"] = 0xB7,
        ["cmp_eq"] = 0xB8,
        ["cmp_ge"] = 0xB9,
        ["cmp_gt"] = 0xBA,
        ["cmp_le"] = 0xBB,
        ["cmp_lt"] = 0xBC,
        ["cmp_ne"] = 0xBD
    };

    private bool TryGetSelectedSseqPlaybackEventData(out byte[] eventData, out string? error)
    {
        eventData = Array.Empty<byte>();
        error = null;

        if (!TryGetSelectedSseqData(out var sseq, out _))
        {
            error = "SSEQ data is not available.";
            return false;
        }

        eventData = sseq.EventData.ToArray();

        if (_loadedSseqEventData.Length == 0 || _loadedSseqInstructionLengths.Count == 0)
        {
            error = "SMFT compile state is not initialized. Re-select the SSEQ.";
            return false;
        }

        if (!TryBuildEditedSseqEventData(eventData, SseqDecompilerText, out var edited, out error))
            return false;

        eventData = edited;
        return true;
    }

    private bool TryBuildEditedSseqEventData(
        byte[] baseEventData,
        string editedText,
        out byte[] editedEventData,
        out string? error)
    {
        editedEventData = baseEventData;
        error = null;

        var orderedOffsets = _loadedSseqInstructionLengths.Keys
            .OrderBy(x => x)
            .ToArray();

        var editedCommands = BuildSseqCommandMap(editedText, orderedOffsets, out var labels, out var mapError);
        if (mapError is not null)
        {
            error = mapError;
            return false;
        }

        if (editedCommands.Count == 0)
        {
            error = "SMFT compile error: no instruction lines were found.";
            return false;
        }

        foreach (var requiredOffset in _loadedSseqInstructionLengths.Keys)
        {
            if (!editedCommands.ContainsKey(requiredOffset))
            {
                error = $"SMFT compile error: missing instruction at 0x{requiredOffset:X6}.";
                return false;
            }
        }

        var patched = (byte[])baseEventData.Clone();

        foreach (var (offset, editedCommandText) in editedCommands)
        {
            if (!_loadedSseqInstructionLengths.TryGetValue(offset, out int expectedLength))
            {
                error = $"SMFT compile error: 0x{offset:X6} is not an existing instruction offset.";
                return false;
            }

            if (!TryAssembleSseqCommand(editedCommandText, labels, out var assembled, out var assembleError))
            {
                error = $"SMFT compile error at 0x{offset:X6}: {assembleError}";
                return false;
            }

            if (assembled.Length != expectedLength)
            {
                error = $"SMFT compile error at 0x{offset:X6}: instruction byte length changed ({expectedLength} -> {assembled.Length}).";
                return false;
            }

            if (offset < 0 || offset + expectedLength > patched.Length)
            {
                error = $"SMFT compile error at 0x{offset:X6}: write range is out of bounds.";
                return false;
            }

            Buffer.BlockCopy(assembled, 0, patched, offset, expectedLength);
        }

        editedEventData = patched;
        return true;
    }

    private static Dictionary<int, int> BuildSseqInstructionLengthMap(ReadOnlySpan<byte> eventData)
    {
        var map = new Dictionary<int, int>();
        int offset = 0;

        while (offset < eventData.Length)
        {
            if (SseqEventDecoder.TryDecode(eventData, offset, out var decoded, out _))
            {
                map[offset] = decoded.Length;
                offset += decoded.Length;
            }
            else
            {
                map[offset] = 1;
                offset++;
            }
        }

        return map;
    }

    private static Dictionary<int, string> BuildSseqCommandMap(
        string text,
        IReadOnlyList<int> orderedOffsets,
        out Dictionary<string, int> labels,
        out string? error)
    {
        labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var commands = new Dictionary<int, string>();
        var pendingLabels = new List<(string label, int lineNo)>();
        int sequentialCommandIndex = 0;
        bool sawExplicitOffset = false;
        bool sawImplicitOffsetlessCommand = false;
        error = null;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNo = i + 1;
            string line = StripSseqTextComment(lines[i]);
            if (line.Length == 0)
                continue;

            if (TryParseOffsetCommandLine(line, out int offset, out string commandText))
            {
                if (sawImplicitOffsetlessCommand)
                {
                    error = $"SSEQ text parse error line {lineNo}: cannot mix offseted and offsetless instruction lines.";
                    return new Dictionary<int, string>();
                }

                sawExplicitOffset = true;

                foreach (var (pendingLabel, labelLineNo) in pendingLabels)
                {
                    if (!labels.TryAdd(pendingLabel, offset))
                    {
                        error = $"SSEQ text parse error line {labelLineNo}: duplicate label '{pendingLabel}'.";
                        return new Dictionary<int, string>();
                    }
                }
                pendingLabels.Clear();

                if (!commands.TryAdd(offset, commandText))
                {
                    error = $"SSEQ text parse error line {lineNo}: duplicate instruction offset 0x{offset:X6}.";
                    return new Dictionary<int, string>();
                }

                continue;
            }

            if (TryParseLabelLine(line, out string label))
            {
                pendingLabels.Add((label, lineNo));
                continue;
            }

            if (sawExplicitOffset)
            {
                error = $"SSEQ text parse error line {lineNo}: unsupported line format.";
                return new Dictionary<int, string>();
            }

            sawImplicitOffsetlessCommand = true;

            if ((uint)sequentialCommandIndex >= (uint)orderedOffsets.Count)
            {
                error = $"SSEQ text parse error line {lineNo}: too many instructions for this SSEQ.";
                return new Dictionary<int, string>();
            }

            int sequentialOffset = orderedOffsets[sequentialCommandIndex++];

            foreach (var (pendingLabel, labelLineNo) in pendingLabels)
            {
                if (!labels.TryAdd(pendingLabel, sequentialOffset))
                {
                    error = $"SSEQ text parse error line {labelLineNo}: duplicate label '{pendingLabel}'.";
                    return new Dictionary<int, string>();
                }
            }
            pendingLabels.Clear();

            if (!commands.TryAdd(sequentialOffset, line))
            {
                error = $"SSEQ text parse error line {lineNo}: duplicate instruction offset 0x{sequentialOffset:X6}.";
                return new Dictionary<int, string>();
            }
        }

        if (pendingLabels.Count > 0)
        {
            var dangling = pendingLabels[0];
            error = $"SSEQ text parse error line {dangling.lineNo}: label '{dangling.label}' is not followed by an instruction.";
            return new Dictionary<int, string>();
        }

        return commands;
    }

    private static bool TryAssembleSseqCommand(
        string commandText,
        IReadOnlyDictionary<string, int> labels,
        out byte[] bytes,
        out string? error)
    {
        bytes = Array.Empty<byte>();
        error = null;

        if (!TrySplitHead(commandText, out string opToken, out string rest))
        {
            error = "empty instruction.";
            return false;
        }

        string op = opToken.ToLowerInvariant();

        if (op.EndsWith("_if", StringComparison.OrdinalIgnoreCase))
        {
            string baseOp = op[..^3];
            string innerCommand = string.IsNullOrWhiteSpace(rest)
                ? baseOp
                : $"{baseOp} {rest}";

            if (!TryAssembleSseqCommand(innerCommand, labels, out var innerBytes, out error))
                return false;

            bytes = new byte[innerBytes.Length + 1];
            bytes[0] = 0xA2;
            Buffer.BlockCopy(innerBytes, 0, bytes, 1, innerBytes.Length);
            return true;
        }

        if (op.EndsWith("_r", StringComparison.OrdinalIgnoreCase))
        {
            string baseOp = op[..^2];
            if (TryResolveWrappedSubtypeToken(baseOp, out byte subtype))
            {
                return TryAssembleRandom($"0x{subtype:X2} {rest}", out bytes, out error);
            }
        }

        if (op.EndsWith("_v", StringComparison.OrdinalIgnoreCase))
        {
            string baseOp = op[..^2];
            if (TryResolveWrappedSubtypeToken(baseOp, out byte subtype))
            {
                return TryAssembleFromVar($"0x{subtype:X2} {rest}", out bytes, out error);
            }
        }

        if (TryParseSseqNoteToken(op, out int note))
        {
            var args = SplitCsvArgs(rest);
            if (args.Count != 2 ||
                !TryParseIntToken(args[0], 0, 127, out int velocity, out error) ||
                !TryParseIntToken(args[1], 0, 0x0FFFFFFF, out int duration, out error))
            {
                error ??= "note syntax is '<note> velocity, duration'.";
                return false;
            }

            var list = new List<byte>(6) { (byte)note, (byte)velocity };
            WriteVarLen(list, duration);
            bytes = list.ToArray();
            return true;
        }

        switch (op)
        {
            case "wait":
            {
                if (!TryParseSingleArg(rest, 0, 0x0FFFFFFF, out int wait, out error))
                    return false;

                var list = new List<byte>(6) { 0x80 };
                WriteVarLen(list, wait);
                bytes = list.ToArray();
                return true;
            }
            case "prg":
            {
                var args = SplitCsvArgs(rest);
                int raw;
                if (args.Count == 1)
                {
                    if (!TryParseIntToken(args[0], 0, 32767, out raw, out error))
                        return false;
                }
                else if (args.Count == 2)
                {
                    if (!TryParseIntToken(args[0], 0, 255, out int bank, out error) ||
                        !TryParseIntToken(args[1], 0, 0x7F, out int program, out error))
                        return false;

                    raw = (bank << 7) | program;
                    if (raw > 32767)
                    {
                        error = "prg value is out of range [0..32767].";
                        return false;
                    }
                }
                else
                {
                    error = "prg syntax is 'prg program' or 'prg bank, program'.";
                    return false;
                }

                var list = new List<byte>(6) { 0x81 };
                WriteVarLen(list, raw);
                bytes = list.ToArray();
                return true;
            }
            case "opentrack":
            {
                var args = SplitCsvArgs(rest);
                if (args.Count != 2 ||
                    !TryParseIntToken(args[0], 0, 0xFF, out int trackNo, out error) ||
                    !TryResolveAddressToken(args[1], labels, out int dest, out error))
                {
                    error ??= "opentrack syntax is 'opentrack trackNo, destination'.";
                    return false;
                }

                bytes = new[] { (byte)0x93, (byte)trackNo, (byte)(dest & 0xFF), (byte)((dest >> 8) & 0xFF), (byte)((dest >> 16) & 0xFF) };
                return true;
            }
            case "jump":
            case "call":
            {
                var args = SplitCsvArgs(rest);
                if (args.Count != 1 || !TryResolveAddressToken(args[0], labels, out int dest, out error))
                {
                    error ??= $"{op} syntax is '{op} destination'.";
                    return false;
                }

                bytes = new[] { op == "jump" ? (byte)0x94 : (byte)0x95, (byte)(dest & 0xFF), (byte)((dest >> 8) & 0xFF), (byte)((dest >> 16) & 0xFF) };
                return true;
            }
            case "if":
                bytes = new[] { (byte)0xA2 };
                return true;
            case "random":
                return TryAssembleRandom(rest, out bytes, out error);
            case "fromvar":
                return TryAssembleFromVar(rest, out bytes, out error);
            case "loop_end":
                bytes = new[] { (byte)0xFC };
                return true;
            case "ret":
                bytes = new[] { (byte)0xFD };
                return true;
            case "fin":
                bytes = new[] { (byte)0xFF };
                return true;
            case "mod_delay":
            {
                if (!TryParseSingleArg(rest, 0, 0xFFFF, out int value, out error))
                    return false;

                bytes = new[] { (byte)0xE0, (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };
                return true;
            }
            case "tempo":
            {
                if (!TryParseSingleArg(rest, 0, 0xFFFF, out int value, out error))
                    return false;

                bytes = new[] { (byte)0xE1, (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };
                return true;
            }
            case "sweep_pitch":
            {
                if (!TryParseSingleArg(rest, short.MinValue, short.MaxValue, out int value, out error))
                    return false;

                short s = unchecked((short)value);
                bytes = new[] { (byte)0xE2, (byte)(s & 0xFF), (byte)((s >> 8) & 0xFF) };
                return true;
            }
            case "alloctrack":
            {
                if (!TryParseSingleArg(rest, 0, 0xFFFF, out int value, out error))
                    return false;

                bytes = new[] { (byte)0xFE, (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };
                return true;
            }
            case "db":
            {
                var args = SplitCsvArgs(rest);
                if (args.Count == 0)
                {
                    error = "db requires at least one byte.";
                    return false;
                }

                var list = new List<byte>(args.Count);
                foreach (var arg in args)
                {
                    if (!TryParseIntToken(arg, 0, 255, out int value, out error))
                        return false;
                    list.Add((byte)value);
                }

                bytes = list.ToArray();
                return true;
            }
            case "envelope":
            {
                var args = SplitCsvArgs(rest);
                if (args.Count != 4)
                {
                    error = "envelope syntax is 'envelope attack, decay, sustain, release'.";
                    return false;
                }

                if (!TryParseIntToken(args[0], -1, 127, out int attack, out error) ||
                    !TryParseIntToken(args[1], -1, 127, out int decay, out error) ||
                    !TryParseIntToken(args[2], -1, 127, out int sustain, out error) ||
                    !TryParseIntToken(args[3], -1, 127, out int release, out error))
                {
                    return false;
                }

                bytes =
                [
                    0xD0, EncodeEnvelopeByte(attack),
                    0xD1, EncodeEnvelopeByte(decay),
                    0xD2, EncodeEnvelopeByte(sustain),
                    0xD3, EncodeEnvelopeByte(release)
                ];
                return true;
            }
        }

        if (SseqVariableOps.TryGetValue(op, out byte varOp))
        {
            var args = SplitCsvArgs(rest);
            if (args.Count != 2 ||
                !TryParseIntToken(args[0], -128, 255, out int varNo, out error) ||
                !TryParseIntToken(args[1], short.MinValue, short.MaxValue, out int value, out error))
            {
                error ??= $"{op} syntax is '{op} varNo, value'.";
                return false;
            }

            short s = unchecked((short)value);
            bytes = new[] { varOp, unchecked((byte)varNo), (byte)(s & 0xFF), (byte)((s >> 8) & 0xFF) };
            return true;
        }

        if (TryAssembleSimpleByte(op, rest, out bytes, out error))
            return true;

        error = $"unsupported instruction '{opToken}'.";
        return false;
    }

    private static bool TryAssembleSimpleByte(string op, string rest, out byte[] bytes, out string? error)
    {
        bytes = Array.Empty<byte>();
        error = null;

        switch (op)
        {
            case "pan":
                return TryAssembleSimpleByteArg(0xC0, rest, 0, 255, null, out bytes, out error);
            case "volume":
                return TryAssembleSimpleByteArg(0xC1, rest, 0, 255, null, out bytes, out error);
            case "main_volume":
                return TryAssembleSimpleByteArg(0xC2, rest, 0, 255, null, out bytes, out error);
            case "transpose":
                return TryAssembleSimpleByteArg(0xC3, rest, sbyte.MinValue, sbyte.MaxValue, v => unchecked((byte)(sbyte)v), out bytes, out error);
            case "pitchbend":
                return TryAssembleSimpleByteArg(0xC4, rest, sbyte.MinValue, sbyte.MaxValue, v => unchecked((byte)(sbyte)v), out bytes, out error);
            case "bendrange":
                return TryAssembleSimpleByteArg(0xC5, rest, 0, 255, null, out bytes, out error);
            case "prio":
                return TryAssembleSimpleByteArg(0xC6, rest, 0, 255, null, out bytes, out error);
            case "notewait_off": bytes = new[] { (byte)0xC7, (byte)0x00 }; return true;
            case "notewait_on": bytes = new[] { (byte)0xC7, (byte)0x01 }; return true;
            case "tieoff": bytes = new[] { (byte)0xC8, (byte)0x00 }; return true;
            case "tieon": bytes = new[] { (byte)0xC8, (byte)0x01 }; return true;
            case "porta":
                return TryAssembleSimpleByteArg(0xC9, rest, 0, 255, null, out bytes, out error);
            case "mod_depth":
                return TryAssembleSimpleByteArg(0xCA, rest, 0, 255, null, out bytes, out error);
            case "mod_speed":
                return TryAssembleSimpleByteArg(0xCB, rest, 0, 255, null, out bytes, out error);
            case "mod_type":
                return TryAssembleSimpleByteArg(0xCC, rest, 0, 255, null, out bytes, out error);
            case "mod_range":
                return TryAssembleSimpleByteArg(0xCD, rest, 0, 255, null, out bytes, out error);
            case "porta_off": bytes = new[] { (byte)0xCE, (byte)0x00 }; return true;
            case "porta_on": bytes = new[] { (byte)0xCE, (byte)0x01 }; return true;
            case "porta_time":
                return TryAssembleSimpleByteArg(0xCF, rest, 0, 255, null, out bytes, out error);
            case "attack":
                return TryAssembleSimpleByteArg(0xD0, rest, -1, 127, EncodeEnvelopeByte, out bytes, out error);
            case "decay":
                return TryAssembleSimpleByteArg(0xD1, rest, -1, 127, EncodeEnvelopeByte, out bytes, out error);
            case "sustain":
                return TryAssembleSimpleByteArg(0xD2, rest, -1, 127, EncodeEnvelopeByte, out bytes, out error);
            case "release":
                return TryAssembleSimpleByteArg(0xD3, rest, -1, 127, EncodeEnvelopeByte, out bytes, out error);
            case "loop_start":
                return TryAssembleSimpleByteArg(0xD4, rest, 0, 255, null, out bytes, out error);
            case "volume2":
                return TryAssembleSimpleByteArg(0xD5, rest, 0, 255, null, out bytes, out error);
            case "printvar":
                return TryAssembleSimpleByteArg(0xD6, rest, 0, 255, null, out bytes, out error);
            case "mute":
                return TryAssembleSimpleByteArg(0xD7, rest, 0, 3, null, out bytes, out error);
            default:
                return false;
        }
    }

    private static bool TryAssembleSimpleByteArg(
        byte opcode,
        string argText,
        int min,
        int max,
        Func<int, byte>? encode,
        out byte[] bytes,
        out string? error)
    {
        bytes = Array.Empty<byte>();
        error = null;

        if (!TryParseSingleArg(argText, min, max, out int value, out error))
            return false;

        byte arg = encode is null ? (byte)value : encode(value);
        bytes = new[] { opcode, arg };
        return true;
    }

    private static byte EncodeEnvelopeByte(int value)
    {
        return value < 0 ? (byte)0xFF : (byte)value;
    }

    private static bool TryAssembleRandom(string rest, out byte[] bytes, out string? error)
    {
        bytes = Array.Empty<byte>();
        error = null;

        if (!TrySplitHead(rest, out string subtypeToken, out string argsText) ||
            !TryParseIntToken(subtypeToken, 0, 255, out int subtypeInt, out error))
        {
            error ??= "random syntax is 'random subtype, ...'.";
            return false;
        }

        byte subtype = (byte)subtypeInt;
        if (!TryGetRandomWrapperArgCount(subtype, out int wrapperArgs))
        {
            error = $"unsupported random subtype 0x{subtype:X2}.";
            return false;
        }

        var args = SplitCsvArgs(argsText);
        if (args.Count != wrapperArgs + 2)
        {
            error = $"random 0x{subtype:X2} requires {wrapperArgs + 2} args.";
            return false;
        }

        var list = new List<byte>(8) { 0xA0, subtype };
        for (int i = 0; i < wrapperArgs; i++)
        {
            if (!TryParseIntToken(args[i], 0, 255, out int argValue, out error))
                return false;
            list.Add((byte)argValue);
        }

        if (!TryParseIntToken(args[wrapperArgs], short.MinValue, short.MaxValue, out int minValue, out error) ||
            !TryParseIntToken(args[wrapperArgs + 1], short.MinValue, short.MaxValue, out int maxValue, out error))
        {
            return false;
        }

        short min = unchecked((short)minValue);
        short max = unchecked((short)maxValue);
        list.Add((byte)(min & 0xFF));
        list.Add((byte)((min >> 8) & 0xFF));
        list.Add((byte)(max & 0xFF));
        list.Add((byte)((max >> 8) & 0xFF));

        bytes = list.ToArray();
        return true;
    }

    private static bool TryAssembleFromVar(string rest, out byte[] bytes, out string? error)
    {
        bytes = Array.Empty<byte>();
        error = null;

        if (!TrySplitHead(rest, out string subtypeToken, out string argsText) ||
            !TryParseIntToken(subtypeToken, 0, 255, out int subtypeInt, out error))
        {
            error ??= "fromvar syntax is 'fromvar subtype, ...'.";
            return false;
        }

        byte subtype = (byte)subtypeInt;
        var args = SplitCsvArgs(argsText);

        if (subtype <= 0x7F)
        {
            if (args.Count != 2 ||
                !TryParseIntToken(args[0], 0, 255, out int velocity, out error) ||
                !TryParseIntToken(args[1], 0, 255, out int varNo, out error))
            {
                error ??= "fromvar(note) syntax is 'fromvar subtype, velocity, varNo'.";
                return false;
            }

            bytes = new[] { (byte)0xA1, subtype, (byte)velocity, (byte)varNo };
            return true;
        }

        if (subtype >= 0xB0 && subtype <= 0xBD)
        {
            if (args.Count != 2 ||
                !TryParseIntToken(args[0], sbyte.MinValue, sbyte.MaxValue, out int targetVar, out error) ||
                !TryParseIntToken(args[1], sbyte.MinValue, sbyte.MaxValue, out int sourceVar, out error))
            {
                error ??= "fromvar(varop) syntax is 'fromvar subtype, targetVar, sourceVar'.";
                return false;
            }

            bytes = new[] { (byte)0xA1, subtype, unchecked((byte)(sbyte)targetVar), unchecked((byte)(sbyte)sourceVar) };
            return true;
        }

        if (args.Count != 1 || !TryParseIntToken(args[0], 0, 255, out int variableNo, out error))
        {
            error ??= "fromvar syntax is 'fromvar subtype, varNo'.";
            return false;
        }

        bytes = new[] { (byte)0xA1, subtype, (byte)variableNo };
        return true;
    }

    private static bool TryGetRandomWrapperArgCount(byte subType, out int argCount)
    {
        argCount = subType switch
        {
            <= 0x7F => 1,
            0x80 => 0,
            0x81 => 0,
            >= 0xB0 and <= 0xBD => 1,
            >= 0xC0 and <= 0xD7 => 0,
            0xE0 => 0,
            0xE1 => 0,
            0xE2 => 0,
            0xE3 => 0,
            _ => -1
        };

        return argCount >= 0;
    }

    private static bool TryResolveAddressToken(
        string token,
        IReadOnlyDictionary<string, int> labels,
        out int address,
        out string? error)
    {
        address = 0;
        error = null;

        string trimmed = token.Trim();
        if (labels.TryGetValue(trimmed, out int labelOffset))
        {
            address = labelOffset;
            return true;
        }

        return TryParseIntToken(trimmed, 0, 0xFFFFFF, out address, out error);
    }

    private static bool TryParseSingleArg(string rest, int min, int max, out int value, out string? error)
    {
        value = 0;
        error = null;

        var args = SplitCsvArgs(rest);
        if (args.Count != 1)
        {
            error = "exactly one argument is required.";
            return false;
        }

        return TryParseIntToken(args[0], min, max, out value, out error);
    }

    private static bool TrySplitHead(string text, out string head, out string rest)
    {
        head = string.Empty;
        rest = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.Trim();
        int pos = 0;
        while (pos < trimmed.Length && !char.IsWhiteSpace(trimmed[pos]))
            pos++;

        head = trimmed[..pos];
        rest = pos < trimmed.Length ? trimmed[pos..].TrimStart() : string.Empty;
        return head.Length > 0;
    }

    private static List<string> SplitCsvArgs(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text.Split(',')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();
    }

    private static bool TryParseIntToken(string token, int min, int max, out int value, out string? error)
    {
        value = 0;
        error = null;

        string t = token.Trim();
        if (t.Length == 0)
        {
            error = "missing numeric value.";
            return false;
        }

        bool negative = false;
        if (t[0] == '+' || t[0] == '-')
        {
            negative = t[0] == '-';
            t = t[1..];
        }

        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            t = t[2..];
            if (!int.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int parsedHex))
            {
                error = $"invalid hex value '{token}'.";
                return false;
            }
            value = negative ? -parsedHex : parsedHex;
        }
        else
        {
            string signed = negative ? "-" + t : t;
            if (!int.TryParse(signed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                error = $"invalid integer value '{token}'.";
                return false;
            }
        }

        if (value < min || value > max)
        {
            error = $"value out of range [{min}..{max}]: {value}.";
            return false;
        }

        return true;
    }

    private static bool TryParseSseqNoteToken(string token, out int note)
    {
        note = -1;
        if (token.Length < 3)
            return false;

        int semitone = token[..2] switch
        {
            "cn" => 0,
            "cs" => 1,
            "dn" => 2,
            "ds" => 3,
            "en" => 4,
            "fn" => 5,
            "fs" => 6,
            "gn" => 7,
            "gs" => 8,
            "an" => 9,
            "as" => 10,
            "bn" => 11,
            _ => -1
        };

        if (semitone < 0)
            return false;

        if (!int.TryParse(token[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int octave))
            return false;

        int midiNote = (octave + 1) * 12 + semitone;
        if ((uint)midiNote > 127)
            return false;

        note = midiNote;
        return true;
    }

    private static bool TryResolveWrappedSubtypeToken(string op, out byte subtype)
    {
        subtype = 0;

        if (TryParseSseqNoteToken(op, out int note))
        {
            subtype = (byte)note;
            return true;
        }

        if (SseqVariableOps.TryGetValue(op, out byte varOp))
        {
            subtype = varOp;
            return true;
        }

        subtype = op switch
        {
            "wait" => 0x80,
            "prg" => 0x81,
            "pan" => 0xC0,
            "volume" => 0xC1,
            "main_volume" => 0xC2,
            "transpose" => 0xC3,
            "pitchbend" => 0xC4,
            "bendrange" => 0xC5,
            "prio" => 0xC6,
            "porta" => 0xC9,
            "mod_depth" => 0xCA,
            "mod_speed" => 0xCB,
            "mod_type" => 0xCC,
            "mod_range" => 0xCD,
            "porta_time" => 0xCF,
            "attack" => 0xD0,
            "decay" => 0xD1,
            "sustain" => 0xD2,
            "release" => 0xD3,
            "loop_start" => 0xD4,
            "volume2" => 0xD5,
            "printvar" => 0xD6,
            "mute" => 0xD7,
            "mod_delay" => 0xE0,
            "tempo" => 0xE1,
            "sweep_pitch" => 0xE2,
            _ => 0xFF
        };

        return subtype != 0xFF;
    }

    private static void WriteVarLen(List<byte> bytes, int value)
    {
        if (value < 0 || value > 0x0FFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(value));

        Span<byte> temp = stackalloc byte[4];
        int count = 0;

        temp[count++] = (byte)(value & 0x7F);
        value >>= 7;

        while (value > 0)
        {
            temp[count++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        for (int i = count - 1; i >= 0; i--)
            bytes.Add(temp[i]);
    }

    private static string StripSseqTextComment(string line)
    {
        int semicolon = line.IndexOf(';');
        string noComment = semicolon >= 0 ? line[..semicolon] : line;
        return noComment.Trim();
    }

    private static bool TryParseOffsetCommandLine(string line, out int offset, out string commandText)
    {
        offset = 0;
        commandText = string.Empty;

        int colon = line.IndexOf(':');
        if (colon <= 0 || colon > 8)
            return false;

        string left = line[..colon].Trim();
        if (left.Length == 0 || !left.All(IsHexDigit))
            return false;

        if (!int.TryParse(left, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out offset))
            return false;

        commandText = line[(colon + 1)..].TrimStart();
        return commandText.Length > 0;
    }

    private static bool TryParseLabelLine(string line, out string label)
    {
        label = string.Empty;
        if (!line.EndsWith(':'))
            return false;

        string token = line[..^1].Trim();
        if (token.Length == 0 || !IsIdentifier(token))
            return false;

        label = token;
        return true;
    }

    private static bool IsIdentifier(string token)
    {
        if (token.Length == 0)
            return false;

        char first = token[0];
        if (!(char.IsLetter(first) || first == '_'))
            return false;

        for (int i = 1; i < token.Length; i++)
        {
            char c = token[i];
            if (!(char.IsLetterOrDigit(c) || c == '_'))
                return false;
        }

        return true;
    }

    private static bool IsHexDigit(char c)
    {
        return c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }
}
