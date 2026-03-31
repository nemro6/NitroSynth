using NitroSynth.App;

namespace NitroSynth.Tests;

public sealed class SwarParserTests
{
    [Fact]
    public void Parse_SwavContainer_AsSingleEntrySwar()
    {
        byte[] swavFile = BuildMinimalSwavContainer();

        var swar = SWAR.Parse(swavFile);

        Assert.Single(swar.Entries);
        Assert.True(swar.TryGetSwav(0, out var swav));
        Assert.NotNull(swav);
        Assert.Equal(SwavEncoding.Pcm8, swav.Encoding);
        Assert.False(swav.Loop);
        Assert.NotEmpty(swav.PCM16);
    }

    private static byte[] BuildMinimalSwavContainer()
    {
        // SWAV header(0x10) + DATA chunk header(0x08) + payload(0x0D)
        const int payloadSize = 13;
        const int totalSize = 0x18 + payloadSize;

        byte[] data = new byte[totalSize];

        // NDS std header
        data[0] = (byte)'S'; data[1] = (byte)'W'; data[2] = (byte)'A'; data[3] = (byte)'V';
        data[4] = 0xFF; data[5] = 0xFE; // byte order
        data[6] = 0x00; data[7] = 0x01; // version 1.0
        BitConverter.TryWriteBytes(data.AsSpan(8, 4), totalSize);
        BitConverter.TryWriteBytes(data.AsSpan(12, 2), (ushort)0x10); // header size
        BitConverter.TryWriteBytes(data.AsSpan(14, 2), (ushort)1);    // block count

        // DATA chunk
        data[16] = (byte)'D'; data[17] = (byte)'A'; data[18] = (byte)'T'; data[19] = (byte)'A';
        BitConverter.TryWriteBytes(data.AsSpan(20, 4), (uint)(8 + payloadSize));

        // SWAV payload (same shape as SWAR entry body)
        int p = 0x18;
        data[p + 0] = 0; // Pcm8
        data[p + 1] = 0; // loop off
        BitConverter.TryWriteBytes(data.AsSpan(p + 2, 2), (ushort)8000);
        BitConverter.TryWriteBytes(data.AsSpan(p + 4, 2), (ushort)0);
        BitConverter.TryWriteBytes(data.AsSpan(p + 6, 2), (ushort)0);
        BitConverter.TryWriteBytes(data.AsSpan(p + 8, 4), (uint)0);
        data[p + 12] = 0x80; // one sample

        return data;
    }
}
