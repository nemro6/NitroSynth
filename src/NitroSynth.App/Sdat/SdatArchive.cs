using System.Collections.Generic;

namespace NitroSynth.App.Sdat;

public sealed class SdatArchive
{
    public SdatArchive(uint fileSize, IReadOnlyList<SoundBankSummary> soundBanks)
    {
        FileSize = fileSize;
        SoundBanks = soundBanks;
    }

    public uint FileSize { get; }

    public IReadOnlyList<SoundBankSummary> SoundBanks { get; }
}
