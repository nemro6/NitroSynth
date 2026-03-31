using System;

namespace NitroSynth.App;

internal enum SseqDecodedKind
{
    Note,
    Wait,
    Program,
    OpenTrack,
    Jump,
    Call,
    If,
    Variable,
    Random,
    FromVariable,
    SimpleByte,
    ModDelay,
    Tempo,
    SweepPitch,
    LoopEnd,
    Return,
    AllocateTrack,
    EndTrack
}

internal readonly record struct SseqDecodedEvent(
    SseqDecodedKind Kind,
    byte Op,
    int Offset,
    int Length,
    int Value0 = 0,
    int Value1 = 0,
    int Value2 = 0,
    int Value3 = 0,
    short Signed0 = 0,
    short Signed1 = 0,
    byte SubType = 0,
    int WrapperArgCount = 0,
    byte WrapperArg0 = 0,
    byte WrapperArg1 = 0,
    byte WrapperArg2 = 0,
    byte WrapperArg3 = 0);

internal static class SseqEventDecoder
{
    public static bool TryDecode(
        ReadOnlySpan<byte> data,
        int offset,
        out SseqDecodedEvent ev,
        out string? error)
    {
        ev = default;
        error = null;

        if ((uint)offset >= (uint)data.Length)
        {
            error = "Offset is out of range.";
            return false;
        }

        byte op = data[offset];

        if (op <= 0x7F)
        {
            if (!Ensure(data, offset, 2, out error))
                return false;

            if (!TryReadVarLen(data, offset + 2, out int duration, out int varLen))
            {
                error = "Invalid note duration varlen.";
                return false;
            }

            byte velocityRaw = data[offset + 1];
            ev = new SseqDecodedEvent(
                Kind: SseqDecodedKind.Note,
                Op: op,
                Offset: offset,
                Length: 2 + varLen,
                Value0: op,
                Value1: velocityRaw & 0x7F,
                Value2: duration,
                Value3: velocityRaw);
            return true;
        }

        switch (op)
        {
            case 0x80:
            {
                if (!TryReadVarLen(data, offset + 1, out int wait, out int varLen))
                {
                    error = "Invalid wait varlen.";
                    return false;
                }

                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.Wait,
                    Op: op,
                    Offset: offset,
                    Length: 1 + varLen,
                    Value0: wait);
                return true;
            }
            case 0x81:
            {
                if (!TryReadVarLen(data, offset + 1, out int programRaw, out int varLen))
                {
                    error = "Invalid program varlen.";
                    return false;
                }

                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.Program,
                    Op: op,
                    Offset: offset,
                    Length: 1 + varLen,
                    Value0: programRaw);
                return true;
            }
            case 0x93:
            {
                if (!Ensure(data, offset, 5, out error))
                    return false;

                int trackNo = data[offset + 1];
                int destination = ReadU24(data, offset + 2);
                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.OpenTrack,
                    Op: op,
                    Offset: offset,
                    Length: 5,
                    Value0: trackNo,
                    Value1: destination);
                return true;
            }
            case 0x94:
            {
                if (!Ensure(data, offset, 4, out error))
                    return false;

                int destination = ReadU24(data, offset + 1);
                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.Jump,
                    Op: op,
                    Offset: offset,
                    Length: 4,
                    Value0: destination);
                return true;
            }
            case 0x95:
            {
                if (!Ensure(data, offset, 4, out error))
                    return false;

                int destination = ReadU24(data, offset + 1);
                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.Call,
                    Op: op,
                    Offset: offset,
                    Length: 4,
                    Value0: destination);
                return true;
            }
            case 0xA0:
                return TryDecodeRandom(data, offset, out ev, out error);
            case 0xA1:
                return TryDecodeFromVariable(data, offset, out ev, out error);
            case 0xA2:
                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.If,
                    Op: op,
                    Offset: offset,
                    Length: 1);
                return true;
            case >= 0xB0 and <= 0xBD:
            {
                if (!Ensure(data, offset, 4, out error))
                    return false;

                int variableId = data[offset + 1];
                short value = ReadI16(data, offset + 2);
                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.Variable,
                    Op: op,
                    Offset: offset,
                    Length: 4,
                    Value0: variableId,
                    Signed0: value);
                return true;
            }
            case >= 0xC0 and <= 0xD6:
            {
                if (!Ensure(data, offset, 2, out error))
                    return false;

                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.SimpleByte,
                    Op: op,
                    Offset: offset,
                    Length: 2,
                    Value0: data[offset + 1]);
                return true;
            }
            case 0xD7:
            {
                if (!Ensure(data, offset, 2, out error))
                    return false;

                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.SimpleByte,
                    Op: op,
                    Offset: offset,
                    Length: 2,
                    Value0: data[offset + 1]);
                return true;
            }
            case 0xE0:
            {
                if (!Ensure(data, offset, 3, out error))
                    return false;

                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.ModDelay,
                    Op: op,
                    Offset: offset,
                    Length: 3,
                    Value0: ReadU16(data, offset + 1));
                return true;
            }
            case 0xE1:
            {
                if (!Ensure(data, offset, 3, out error))
                    return false;

                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.Tempo,
                    Op: op,
                    Offset: offset,
                    Length: 3,
                    Value0: ReadU16(data, offset + 1));
                return true;
            }
            case 0xE2:
            case 0xE3:
            {
                if (!Ensure(data, offset, 3, out error))
                    return false;

                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.SweepPitch,
                    Op: op,
                    Offset: offset,
                    Length: 3,
                    Signed0: ReadI16(data, offset + 1));
                return true;
            }
            case 0xFC:
                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.LoopEnd,
                    Op: op,
                    Offset: offset,
                    Length: 1);
                return true;
            case 0xFD:
                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.Return,
                    Op: op,
                    Offset: offset,
                    Length: 1);
                return true;
            case 0xFE:
            {
                if (!Ensure(data, offset, 3, out error))
                    return false;

                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.AllocateTrack,
                    Op: op,
                    Offset: offset,
                    Length: 3,
                    Value0: ReadU16(data, offset + 1));
                return true;
            }
            case 0xFF:
                ev = new SseqDecodedEvent(
                    Kind: SseqDecodedKind.EndTrack,
                    Op: op,
                    Offset: offset,
                    Length: 1);
                return true;
            default:
                error = $"Unsupported opcode 0x{op:X2}.";
                return false;
        }
    }

    public static bool TryReadVarLen(ReadOnlySpan<byte> data, int offset, out int value, out int length)
    {
        value = 0;
        length = 0;

        if ((uint)offset >= (uint)data.Length)
            return false;

        int result = 0;
        for (int i = 0; i < 4; i++)
        {
            int pos = offset + i;
            if ((uint)pos >= (uint)data.Length)
                return false;

            byte b = data[pos];
            result = (result << 7) | (b & 0x7F);
            length++;

            if ((b & 0x80) == 0)
            {
                value = result;
                return true;
            }
        }

        return false;
    }

    private static bool TryDecodeRandom(
        ReadOnlySpan<byte> data,
        int offset,
        out SseqDecodedEvent ev,
        out string? error)
    {
        ev = default;
        error = null;

        if (!Ensure(data, offset, 2, out error))
            return false;

        byte subType = data[offset + 1];
        if (!TryGetRandomWrapperArgCount(subType, out int argCount))
        {
            error = $"Unsupported random subtype 0x{subType:X2}.";
            return false;
        }

        int totalLength = 6 + argCount;
        if (!Ensure(data, offset, totalLength, out error))
            return false;

        byte arg0 = 0;
        byte arg1 = 0;
        byte arg2 = 0;
        byte arg3 = 0;

        if (argCount > 0) arg0 = data[offset + 2];
        if (argCount > 1) arg1 = data[offset + 3];
        if (argCount > 2) arg2 = data[offset + 4];
        if (argCount > 3) arg3 = data[offset + 5];

        int rangeOffset = offset + 2 + argCount;
        short randMin = ReadI16(data, rangeOffset);
        short randMax = ReadI16(data, rangeOffset + 2);

        ev = new SseqDecodedEvent(
            Kind: SseqDecodedKind.Random,
            Op: 0xA0,
            Offset: offset,
            Length: totalLength,
            SubType: subType,
            WrapperArgCount: argCount,
            WrapperArg0: arg0,
            WrapperArg1: arg1,
            WrapperArg2: arg2,
            WrapperArg3: arg3,
            Signed0: randMin,
            Signed1: randMax);

        return true;
    }

    private static bool TryDecodeFromVariable(
        ReadOnlySpan<byte> data,
        int offset,
        out SseqDecodedEvent ev,
        out string? error)
    {
        ev = default;
        error = null;

        if (!Ensure(data, offset, 2, out error))
            return false;

        byte subType = data[offset + 1];

        if (subType <= 0x7F)
        {
            if (!Ensure(data, offset, 4, out error))
                return false;

            byte velocity = data[offset + 2];
            byte variableId = data[offset + 3];
            ev = new SseqDecodedEvent(
                Kind: SseqDecodedKind.FromVariable,
                Op: 0xA1,
                Offset: offset,
                Length: 4,
                SubType: subType,
                WrapperArgCount: 1,
                WrapperArg0: velocity,
                Signed0: variableId);
            return true;
        }

        if (subType >= 0xB0 && subType <= 0xBD)
        {
            if (!Ensure(data, offset, 4, out error))
                return false;

            sbyte targetVariable = unchecked((sbyte)data[offset + 2]);
            sbyte sourceVariable = unchecked((sbyte)data[offset + 3]);
            ev = new SseqDecodedEvent(
                Kind: SseqDecodedKind.FromVariable,
                Op: 0xA1,
                Offset: offset,
                Length: 4,
                SubType: subType,
                Signed0: targetVariable,
                Signed1: sourceVariable);
            return true;
        }

        if (!Ensure(data, offset, 3, out error))
            return false;

        byte varNo = data[offset + 2];
        ev = new SseqDecodedEvent(
            Kind: SseqDecodedKind.FromVariable,
            Op: 0xA1,
            Offset: offset,
            Length: 3,
            SubType: subType,
            Signed0: varNo);
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

    private static bool Ensure(ReadOnlySpan<byte> data, int offset, int length, out string? error)
    {
        error = null;
        if (length <= 0)
        {
            error = "Requested non-positive read length.";
            return false;
        }

        if (offset < 0)
        {
            error = "Negative offset.";
            return false;
        }

        if (offset + length > data.Length)
        {
            error = "Command exceeds buffer.";
            return false;
        }

        return true;
    }

    private static int ReadU24(ReadOnlySpan<byte> data, int offset)
    {
        return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset)
    {
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    private static short ReadI16(ReadOnlySpan<byte> data, int offset)
    {
        return unchecked((short)ReadU16(data, offset));
    }
}
