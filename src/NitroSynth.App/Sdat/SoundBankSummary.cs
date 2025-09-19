using System;

namespace NitroSynth.App.Sdat;

public sealed record class SoundBankSummary(int Index, uint FileId, string? Name)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? $"SBNK {Index:D3} (FILE {FileId})"
        : $"{Index:D3}: {Name}";
}
