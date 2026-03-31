using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    private readonly Dictionary<int, string> _sbnkBnkTextOverrides = new();

    public bool TryCreateSelectedSbnkBnkEditorSession(out MusEditorSession session)
    {
        session = default;

        if (SelectedBank is null)
        {
            StatusMessage = "BNK editor: no SBNK selected.";
            return false;
        }

        if (_lastInfo is null || !_lastInfo.Sbnk.TryGetValue(SelectedBank.Id, out var sbnkInfo))
        {
            StatusMessage = "BNK editor: selected SBNK info is unavailable.";
            return false;
        }

        int? overrideCount = (_sbnkInstrumentCount > 0) ? _sbnkInstrumentCount : null;
        if (!TryOpenSelectedBankSbnk(out var fs, out _, out var sbnk, overrideCount))
        {
            StatusMessage = $"BNK editor: could not read SBNK data (FileId={sbnkInfo.FileId}).";
            return false;
        }

        using (fs)
        {
            if (!_sbnkBnkTextOverrides.TryGetValue(SelectedBank.Id, out var bnkText))
            {
                if (!TryBuildSelectedSbnkBnkText(SelectedBank.Id, SelectedBank.Name, sbnk, out bnkText, out var error))
                {
                    StatusMessage = error ?? "BNK editor: failed to build text.";
                    return false;
                }
            }

            string? expectedFilePath = LoadedFilePath;
            int bankId = SelectedBank.Id;

            session = new MusEditorSession(
                $"BNK Editor - SBNK {bankId:D3}",
                $"SBNK {bankId:D3}: {SelectedBank.Name} (FileId={sbnkInfo.FileId})",
                bnkText,
                editedText => SaveSbnkBnkTextAsync(expectedFilePath, bankId, editedText));

            return true;
        }
    }

    private Task<MusSaveResult> SaveSbnkBnkTextAsync(string? expectedFilePath, int bankId, string editedText)
    {
        if (!IsSameLoadedSdat(expectedFilePath))
            return Task.FromResult(new MusSaveResult(false, "Loaded SDAT changed. Reopen BNK editor."));

        string normalized = NormalizeMusText(editedText);
        _sbnkBnkTextOverrides[bankId] = normalized;
        StatusMessage = $"SBNK {bankId:D3} BNK text saved in memory.";
        return Task.FromResult(new MusSaveResult(true, "Saved BNK text in memory."));
    }

    private bool TryBuildSelectedSbnkBnkText(int bankId, string bankName, SBNK sbnk, out string bnkText, out string? error)
    {
        bnkText = string.Empty;
        error = null;

        try
        {
            var instLines = new List<string>(sbnk.Records.Count);
            var drumSections = new List<(string Label, SBNK.DrumSet DrumSet)>();
            var keySplitSections = new List<(string Label, SBNK.KeySplit KeySplit)>();
            var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int currentInstWaveGroup = 0;

            for (int instId = 0; instId < sbnk.Records.Count; instId++)
            {
                var rec = sbnk.Records[instId];

                if (rec.Type is SBNK.InstrumentType.Null or SBNK.InstrumentType.NullInstrument)
                {
                    instLines.Add($"{instId:D3} : NULL");
                    continue;
                }

                if (rec.Type == SBNK.InstrumentType.DrumSet)
                {
                    if (rec.Articulation is SBNK.DrumSet drumSet)
                    {
                        string label = BuildUniqueMusIdentifier($"DRUM_SET_{instId:D3}", $"DRUM_SET_{instId:D3}", usedLabels);
                        instLines.Add($"{instId:D3} : DRUM_SET, {label}");
                        drumSections.Add((label, drumSet));
                    }
                    else
                    {
                        instLines.Add($"{instId:D3} : NULL ; invalid DRUM_SET articulation");
                    }

                    continue;
                }

                if (rec.Type == SBNK.InstrumentType.KeySplit)
                {
                    if (rec.Articulation is SBNK.KeySplit keySplit)
                    {
                        string label = BuildUniqueMusIdentifier($"KEY_SPLIT_{instId:D3}", $"KEY_SPLIT_{instId:D3}", usedLabels);
                        instLines.Add($"{instId:D3} : KEY_SPLIT, {label}");
                        keySplitSections.Add((label, keySplit));
                    }
                    else
                    {
                        instLines.Add($"{instId:D3} : NULL ; invalid KEY_SPLIT articulation");
                    }

                    continue;
                }

                if (rec.Articulation is not SBNK.SingleInst singleInst)
                {
                    instLines.Add($"{instId:D3} : NULL ; missing articulation");
                    continue;
                }

                if (TryResolveWaveGroup(rec.Type, singleInst.SwarId, out var waveGroup)
                    && waveGroup != currentInstWaveGroup)
                {
                    instLines.Add($"@WGROUP {waveGroup}");
                    currentInstWaveGroup = waveGroup;
                }

                if (!TryBuildInstrumentElementText(
                        rec.Type,
                        singleInst.SwarId,
                        singleInst.SwavId,
                        singleInst.BaseKey,
                        singleInst.Attack,
                        singleInst.Decay,
                        singleInst.Sustain,
                        singleInst.Release,
                        singleInst.Pan,
                        out var elementText))
                {
                    instLines.Add($"{instId:D3} : NULL ; unsupported type 0x{(byte)rec.Type:X2}");
                    continue;
                }

                instLines.Add($"{instId:D3} : {elementText}");
            }

            string baseName = SanitizeExportBaseName(string.IsNullOrWhiteSpace(bankName) ? $"SBNK_{bankId:D3}" : bankName);
            var sb = new StringBuilder(Math.Max(1024, instLines.Count * 64));

            sb.AppendLine(";;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;");
            sb.AppendLine(";");
            sb.AppendLine($"; {baseName}.bnk");
            sb.AppendLine(";     Generated By Nitro Synth");
            sb.AppendLine(";");
            sb.AppendLine(";;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;");
            sb.AppendLine();

            sb.AppendLine("@PATH \".\"");
            sb.AppendLine();

            sb.AppendLine("@INSTLIST");
            foreach (var line in instLines)
                sb.AppendLine(line);

            if (drumSections.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("@DRUM_SET");
                sb.AppendLine();

                foreach (var section in drumSections)
                {
                    sb.Append(section.Label).AppendLine(" =");
                    int currentDrumWaveGroup = 0;

                    for (int i = 0; i < section.DrumSet.Entries.Count; i++)
                    {
                        var entry = section.DrumSet.Entries[i];
                        int triggerKey = Math.Clamp(section.DrumSet.LowKey + i, 0, 127);
                        string trigger = MidiNoteToBnkKey(triggerKey);

                        if (TryResolveWaveGroup(entry.Type, entry.SwarId, out var waveGroup)
                            && waveGroup != currentDrumWaveGroup)
                        {
                            sb.Append("@WGROUP ").AppendLine(waveGroup.ToString());
                            currentDrumWaveGroup = waveGroup;
                        }

                        string body;
                        if (!TryBuildInstrumentElementText(
                                entry.Type,
                                entry.SwarId,
                                entry.SwavId,
                                entry.Key,
                                entry.Attack,
                                entry.Decay,
                                entry.Sustain,
                                entry.Release,
                                entry.Pan,
                                out body))
                        {
                            body = $"NULL ; unsupported type 0x{(byte)entry.Type:X2}";
                        }

                        sb.Append(trigger).Append(" : ").AppendLine(body);
                    }

                    sb.AppendLine();
                }
            }

            if (keySplitSections.Count > 0)
            {
                sb.AppendLine("@KEY_SPLIT");
                sb.AppendLine();

                foreach (var section in keySplitSections)
                {
                    sb.Append(section.Label).AppendLine(" =");
                    int currentKeySplitWaveGroup = 0;

                    int entryIndex = 0;
                    for (int i = 0; i < section.KeySplit.SplitKeys.Length; i++)
                    {
                        byte splitKey = section.KeySplit.SplitKeys[i];
                        if (splitKey == 0)
                            continue;

                        if (entryIndex >= section.KeySplit.Entries.Count)
                            break;

                        var entry = section.KeySplit.Entries[entryIndex++];
                        string splitToken = splitKey == 127 ? "127" : MidiNoteToBnkKey(splitKey);

                        if (TryResolveWaveGroup(entry.Type, entry.SwarId, out var waveGroup)
                            && waveGroup != currentKeySplitWaveGroup)
                        {
                            sb.Append("@WGROUP ").AppendLine(waveGroup.ToString());
                            currentKeySplitWaveGroup = waveGroup;
                        }

                        string body;
                        if (!TryBuildInstrumentElementText(
                                entry.Type,
                                entry.SwarId,
                                entry.SwavId,
                                entry.BaseKey,
                                entry.Attack,
                                entry.Decay,
                                entry.Sustain,
                                entry.Release,
                                entry.Pan,
                                out body))
                        {
                            body = $"NULL ; unsupported type 0x{(byte)entry.Type:X2}";
                        }

                        sb.Append(splitToken).Append(" : ").AppendLine(body);
                    }

                    while (entryIndex < section.KeySplit.Entries.Count)
                    {
                        var entry = section.KeySplit.Entries[entryIndex++];

                        if (TryResolveWaveGroup(entry.Type, entry.SwarId, out var waveGroup)
                            && waveGroup != currentKeySplitWaveGroup)
                        {
                            sb.Append("@WGROUP ").AppendLine(waveGroup.ToString());
                            currentKeySplitWaveGroup = waveGroup;
                        }

                        string body;
                        if (!TryBuildInstrumentElementText(
                                entry.Type,
                                entry.SwarId,
                                entry.SwavId,
                                entry.BaseKey,
                                entry.Attack,
                                entry.Decay,
                                entry.Sustain,
                                entry.Release,
                                entry.Pan,
                                out body))
                        {
                            body = $"NULL ; unsupported type 0x{(byte)entry.Type:X2}";
                        }

                        sb.Append("127 : ").AppendLine(body);
                    }

                    sb.AppendLine();
                }
            }

            bnkText = NormalizeMusText(sb.ToString());
            return true;
        }
        catch (Exception ex)
        {
            error = $"BNK editor: {ex.Message}";
            return false;
        }
    }

    private bool TryBuildInstrumentElementText(
        SBNK.InstrumentType type,
        int swarId,
        int swavId,
        int baseKey,
        int attack,
        int decay,
        int sustain,
        int release,
        int pan,
        out string text)
    {
        text = string.Empty;

        attack = Math.Clamp(attack, 0, 127);
        decay = Math.Clamp(decay, 0, 127);
        sustain = Math.Clamp(sustain, 0, 127);
        pan = Math.Clamp(pan, 0, 127);
        baseKey = Math.Clamp(baseKey, 0, 127);
        string releaseToken = release >= 128
            ? "DISABLE"
            : Math.Clamp(release, 0, 127).ToString();

        switch (type)
        {
            case SBNK.InstrumentType.Null:
            case SBNK.InstrumentType.NullInstrument:
                text = "NULL";
                return true;

            case SBNK.InstrumentType.Pcm:
            case SBNK.InstrumentType.DirectPcm:
            {
                int swarInfoId = ResolveSwarInfoId(swarId);
                string format = ResolvePcmFormatToken();
                string swarName = ResolveSwarNameForBnk(swarInfoId);
                string waveFile = BuildGeneratedWaveFileName(swarName, swavId);

                text = $"{format}, \"{waveFile}\", {MidiNoteToBnkKey(baseKey)}, {attack},{decay},{sustain},{releaseToken},{pan}";
                return true;
            }

            case SBNK.InstrumentType.Psg:
            {
                string duty = ResolvePsgDutyToken(swavId);
                text = $"PSG, {duty}, {MidiNoteToBnkKey(baseKey)}, {attack},{decay},{sustain},{releaseToken},{pan}";
                return true;
            }

            case SBNK.InstrumentType.Noise:
                text = $"NOISE, {MidiNoteToBnkKey(baseKey)}, {attack},{decay},{sustain},{releaseToken},{pan}";
                return true;
        }

        return false;
    }

    private static string ResolvePcmFormatToken() => "SWAV";

    private string ResolveSwarNameForBnk(int swarInfoId)
    {
        if (_lastSymb is not null
            && swarInfoId >= 0
            && _lastSymb.Swar.TryGetValue(swarInfoId, out var symbName)
            && !string.IsNullOrWhiteSpace(symbName))
        {
            return symbName;
        }

        var swarOption = SwarOptions.FirstOrDefault(s => s.Id == swarInfoId);
        if (swarOption is not null && !string.IsNullOrWhiteSpace(swarOption.Name))
            return swarOption.Name;

        return swarInfoId >= 0 ? $"SWAR_{swarInfoId:D3}" : "SWAR_UNKNOWN";
    }

    private static string BuildGeneratedWaveFileName(string swarName, int swavId)
    {
        string src = string.IsNullOrWhiteSpace(swarName) ? "SWAR" : swarName;
        var sb = new StringBuilder(src.Length);

        foreach (char c in src)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');

        string normalized = sb.ToString().Trim('_');
        while (normalized.Contains("__", StringComparison.Ordinal))
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);

        if (normalized.Length == 0)
            normalized = "SWAR";

        int safeSwavId = Math.Clamp(swavId, 0, 65535);
        return $"{normalized}_SWAV{safeSwavId:D3}.swav";
    }

    private static string ResolvePsgDutyToken(int rawDuty)
    {
        int duty = Math.Clamp(rawDuty + 1, 1, 7);
        return $"DUTY_{duty}_8";
    }

    private static bool TryResolveWaveGroup(SBNK.InstrumentType type, int swarId, out int waveGroup)
    {
        waveGroup = 0;
        if (type is not SBNK.InstrumentType.Pcm and not SBNK.InstrumentType.DirectPcm)
            return false;

        // Bank articulation selects SWAR slot 0..3 as wave group.
        if (swarId >= 0 && swarId <= 3)
            waveGroup = swarId;
        else
            waveGroup = 0;

        return true;
    }

    private static string MidiNoteToBnkKey(int midiNote)
    {
        string[] names =
        {
            "cn", "cs", "dn", "ds", "en", "fn",
            "fs", "gn", "gs", "an", "as", "bn"
        };

        int note = Math.Clamp(midiNote, 0, 127);
        int scale = note % 12;
        int octave = note / 12 - 1;
        return $"{names[scale]}{octave}";
    }
}
