using NitroSynth.App;

namespace NitroSynth.Tests;

public sealed class SseqEventDecoderTests
{
    [Fact]
    public void Decode_CmpIfJumpSequence_HasExpectedBoundaries()
    {
        byte[] data =
        [
            0xB8, 0x00, 0x01, 0x00, // cmp_eq 0, 1
            0xA2,                   // if
            0x94, 0x0E, 0x00, 0x00, // jump loc_00000E
            0x80, 0x06,             // wait 6
            0xFF,                   // fin
            0x80, 0x03,             // wait 3
            0xFF                    // fin
        ];

        AssertDecoded(data, 0, SseqDecodedKind.Variable, 4, op: 0xB8);
        AssertDecoded(data, 4, SseqDecodedKind.If, 1, op: 0xA2);
        AssertDecoded(data, 5, SseqDecodedKind.Jump, 4, op: 0x94);
        AssertDecoded(data, 9, SseqDecodedKind.Wait, 2, op: 0x80);
        AssertDecoded(data, 11, SseqDecodedKind.EndTrack, 1, op: 0xFF);
        AssertDecoded(data, 12, SseqDecodedKind.Wait, 2, op: 0x80);
        AssertDecoded(data, 14, SseqDecodedKind.EndTrack, 1, op: 0xFF);
    }

    [Fact]
    public void Decode_RandomAndFromVariable_ParsesArguments()
    {
        byte[] data =
        [
            0xA0, 0x80, 0x01, 0x00, 0x04, 0x00, // wait_r 1,4
            0xA0, 0xB0, 0x00, 0xFE, 0xFF, 0x03, 0x00, // setvar_r 0,-2,3
            0xA1, 0x80, 0x05, // wait_v 5
            0xA1, 0xB8, 0x00, 0x01, // cmp_eq_v 0,1
            0xA1, 0x3C, 0x40, 0x02 // cn4_v 64,2
        ];

        Assert.True(SseqEventDecoder.TryDecode(data, 0, out var rWait, out var err0), err0);
        Assert.Equal(SseqDecodedKind.Random, rWait.Kind);
        Assert.Equal(6, rWait.Length);
        Assert.Equal((short)1, rWait.Signed0);
        Assert.Equal((short)4, rWait.Signed1);
        Assert.Equal(0, rWait.WrapperArgCount);

        Assert.True(SseqEventDecoder.TryDecode(data, 6, out var rSetVar, out var err1), err1);
        Assert.Equal(SseqDecodedKind.Random, rSetVar.Kind);
        Assert.Equal(7, rSetVar.Length);
        Assert.Equal(0xB0, rSetVar.SubType);
        Assert.Equal(1, rSetVar.WrapperArgCount);
        Assert.Equal((byte)0x00, rSetVar.WrapperArg0);
        Assert.Equal((short)-2, rSetVar.Signed0);
        Assert.Equal((short)3, rSetVar.Signed1);

        Assert.True(SseqEventDecoder.TryDecode(data, 13, out var fromVarWait, out var err2), err2);
        Assert.Equal(SseqDecodedKind.FromVariable, fromVarWait.Kind);
        Assert.Equal(3, fromVarWait.Length);
        Assert.Equal(0x80, fromVarWait.SubType);
        Assert.Equal((short)5, fromVarWait.Signed0);

        Assert.True(SseqEventDecoder.TryDecode(data, 16, out var fromVarCmp, out var err3), err3);
        Assert.Equal(SseqDecodedKind.FromVariable, fromVarCmp.Kind);
        Assert.Equal(4, fromVarCmp.Length);
        Assert.Equal(0xB8, fromVarCmp.SubType);
        Assert.Equal((short)0, fromVarCmp.Signed0);
        Assert.Equal((short)1, fromVarCmp.Signed1);

        Assert.True(SseqEventDecoder.TryDecode(data, 20, out var fromVarNote, out var err4), err4);
        Assert.Equal(SseqDecodedKind.FromVariable, fromVarNote.Kind);
        Assert.Equal(4, fromVarNote.Length);
        Assert.Equal((byte)0x3C, fromVarNote.SubType);
        Assert.Equal(1, fromVarNote.WrapperArgCount);
        Assert.Equal((byte)0x40, fromVarNote.WrapperArg0);
        Assert.Equal((short)2, fromVarNote.Signed0);
    }

    [Fact]
    public void Decode_InvalidCommands_Fail()
    {
        byte[] truncatedRandom = [0xA0, 0x80, 0x01, 0x00];
        Assert.False(SseqEventDecoder.TryDecode(truncatedRandom, 0, out _, out _));

        byte[] unknown = [0x92];
        Assert.False(SseqEventDecoder.TryDecode(unknown, 0, out _, out _));
    }

    [Fact]
    public void Decode_ModDelayAndSweepPitch_AreCompatibleWithSdkVariants()
    {
        byte[] data =
        [
            0xE0, 0x34, 0x12, // mod_delay 0x1234
            0xE2, 0x80, 0xFF, // sweep_pitch -128 (variant A)
            0xE3, 0x7F, 0x00  // sweep_pitch 127 (variant B)
        ];

        Assert.True(SseqEventDecoder.TryDecode(data, 0, out var modDelay, out var err0), err0);
        Assert.Equal(SseqDecodedKind.ModDelay, modDelay.Kind);
        Assert.Equal(3, modDelay.Length);
        Assert.Equal(0x1234, modDelay.Value0);

        Assert.True(SseqEventDecoder.TryDecode(data, 3, out var sweepA, out var err1), err1);
        Assert.Equal(SseqDecodedKind.SweepPitch, sweepA.Kind);
        Assert.Equal(3, sweepA.Length);
        Assert.Equal((short)-128, sweepA.Signed0);

        Assert.True(SseqEventDecoder.TryDecode(data, 6, out var sweepB, out var err2), err2);
        Assert.Equal(SseqDecodedKind.SweepPitch, sweepB.Kind);
        Assert.Equal(3, sweepB.Length);
        Assert.Equal((short)127, sweepB.Signed0);
    }

    private static void AssertDecoded(byte[] data, int offset, SseqDecodedKind kind, int length, byte op)
    {
        Assert.True(SseqEventDecoder.TryDecode(data, offset, out var ev, out var error), error);
        Assert.Equal(kind, ev.Kind);
        Assert.Equal(length, ev.Length);
        Assert.Equal(op, ev.Op);
    }
}
