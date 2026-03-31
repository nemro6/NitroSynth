using System.IO;
using NitroSynth.App;

namespace NitroSynth.Tests;

public sealed class SwarIndexingTests
{
    [Fact]
    public void SparseWaveIndices_CanResolveByOriginalIndex()
    {
        byte[] bytes = BuildSparseIndexSwar();

        var swar = SWAR.Parse(bytes);

        Assert.Equal(2, swar.Entries.Count);
        Assert.Contains(swar.Entries, e => e.Index == 0);
        Assert.Contains(swar.Entries, e => e.Index == 2);

        Assert.True(swar.TryGetSwav(0, out var swav0));
        Assert.False(swar.TryGetSwav(1, out _));
        Assert.True(swar.TryGetSwav(2, out var swav2));

        Assert.NotEmpty(swav0.PCM16);
        Assert.NotEmpty(swav2.PCM16);
    }

    private static byte[] BuildSparseIndexSwar()
    {
        const int fileSize = 112;
        var bytes = new byte[fileSize];

        using var ms = new MemoryStream(bytes, writable: true);
        using var bw = new BinaryWriter(ms);

        bw.Write(new[] { 'S', 'W', 'A', 'R' });
        bw.Write((ushort)0xFEFF);
        bw.Write((ushort)0x0100);
        bw.Write((uint)fileSize);
        bw.Write((ushort)0x0010);
        bw.Write((ushort)1);
        bw.Write(new[] { 'D', 'A', 'T', 'A' });
        bw.Write((uint)(fileSize - 0x10));

        ms.Position = 56;
        bw.Write((uint)3);
        bw.Write(80);
        bw.Write(96);
        bw.Write(96);

        WriteSimplePcm8Swav(ms, bw, 80);
        WriteSimplePcm8Swav(ms, bw, 96);

        return bytes;
    }

    private static void WriteSimplePcm8Swav(MemoryStream ms, BinaryWriter bw, int offset)
    {
        ms.Position = offset;
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((ushort)8000);
        bw.Write((ushort)0);
        bw.Write((ushort)0);
        bw.Write((uint)1);
        bw.Write((byte)0x00);
        bw.Write((byte)0x10);
        bw.Write((byte)0x20);
        bw.Write((byte)0x30);
    }
}
