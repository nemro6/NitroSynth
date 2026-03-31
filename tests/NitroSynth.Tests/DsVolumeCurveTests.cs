using NitroSynth.App.Audio;

namespace NitroSynth.Tests;

public sealed class DsVolumeCurveTests
{
    [Fact]
    public void GetChannelVolume_ZeroAttenuation_IsMax()
    {
        Assert.Equal(127, DsVolumeCurve.GetChannelVolume(0));
    }

    [Fact]
    public void GetChannelVolume_MinSustainAttenuation_IsSilent()
    {
        Assert.Equal(0, DsVolumeCurve.GetChannelVolume(-92544));
    }

    [Fact]
    public void GetChannelVolume_SustainExtremes_MatchExpected()
    {
        Assert.Equal(127, DsVolumeCurve.GetChannelVolume(DsVolumeCurve.SustainTable[127]));
        Assert.Equal(0, DsVolumeCurve.GetChannelVolume(DsVolumeCurve.SustainTable[0]));
    }

    [Fact]
    public void GetChannelVolume_RepresentativeSustainPoints_AreMonotonic()
    {
        int[] points = [0, 1, 2, 10, 64, 100, 120, 127];
        byte prev = 0;
        bool first = true;
        foreach (int x in points)
        {
            byte current = DsVolumeCurve.GetChannelVolume(DsVolumeCurve.SustainTable[x]);
            if (!first)
            {
                Assert.True(current >= prev, $"x={x}: {current} < {prev}");
            }
            prev = current;
            first = false;
        }
    }

    [Fact]
    public void GetComposedGain_WithFullLevels_IsUnity()
    {
        float gain = DsVolumeCurve.GetComposedGain(127, 127, 127, 127, 127, 0);
        Assert.Equal(1f, gain);
    }

    [Fact]
    public void GetComposedGain_WithMinEnvelope_IsSilent()
    {
        float gain = DsVolumeCurve.GetComposedGain(127, 127, 127, 127, 127, -92544);
        Assert.Equal(0f, gain);
    }

    [Fact]
    public void DsEnvelope_ZeroAttack_StartsAtPeak()
    {
        var env = new DsEnvelope();
        env.Init(127, 0, 127, 0, 48000);

        double gain = env.Next();

        Assert.True(gain >= 0.999999);
        Assert.False(env.IsDone);
    }

    [Fact]
    public void DsEnvelope_Release_ReachesDoneState()
    {
        var env = new DsEnvelope();
        env.Init(127, 0, 127, 127, 2000);
        env.NoteOff();

        for (int i = 0; i < 64 && !env.IsDone; i++)
        {
            _ = env.Next();
        }

        Assert.True(env.IsDone);
        Assert.Equal(0.0, env.Next());
    }
}
