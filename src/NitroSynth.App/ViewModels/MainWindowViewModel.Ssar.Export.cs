using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    public readonly record struct MusSaveResult(bool Success, string Message);

    public readonly record struct MusEditorSession(
        string Title,
        string HeaderText,
        string InitialText,
        Func<string, Task<MusSaveResult>> SaveAsync);

    private readonly Dictionary<int, string> _ssarMusTextOverrides = new();

    private readonly record struct SsarMusEntry(
        int SequenceId,
        string Label,
        string SbnkLabel,
        string PlayerLabel,
        byte Volume,
        byte ChannelPriority,
        byte PlayerPriority,
        string DataText);

    public string GetSelectedSsarExportBaseName()
    {
        string? raw = SelectedSsar?.Name;
        if (string.IsNullOrWhiteSpace(raw))
            raw = SelectedSsar is null ? "sequence_archive" : $"ssar_{SelectedSsar.Id:D3}";

        return SanitizeExportBaseName(raw);
    }

    public void NotifySsarImportNotImplemented()
    {
        StatusMessage = "SSAR REPLACE is not implemented yet.";
    }

    public void NotifySsarSaveNotImplemented()
    {
        StatusMessage = "SSAR SAVE is not implemented yet.";
    }

    public void NotifySsarExportFailed(string reason)
    {
        StatusMessage = $"SSAR EXPORT failed: {reason}";
    }

    public bool ExportSelectedSsar(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            NotifySsarExportFailed("output path is empty.");
            return false;
        }

        try
        {
            if (SelectedSsar is null)
            {
                NotifySsarExportFailed("no SSAR selected.");
                return false;
            }

            if (_lastInfo is null || !_lastInfo.Ssar.TryGetValue(SelectedSsar.Id, out var ssarInfo))
            {
                NotifySsarExportFailed("selected SSAR entry is missing in INFO.");
                return false;
            }

            if (!TryReadFileFromFat(ssarInfo.FileId, out var ssarBytes))
            {
                NotifySsarExportFailed($"could not read file data (FileId={ssarInfo.FileId}).");
                return false;
            }

            File.WriteAllBytes(outputPath, ssarBytes);
            StatusMessage = $"Exported SSAR: {outputPath}";
            return true;
        }
        catch (Exception ex)
        {
            NotifySsarExportFailed(ex.Message);
            return false;
        }
    }

    public bool ExportSelectedSsarMus(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            NotifySsarExportFailed("output path is empty.");
            return false;
        }

        try
        {
            if (SelectedSsar is null)
            {
                NotifySsarExportFailed("no SSAR selected.");
                return false;
            }

            if (_lastInfo is null || !_lastInfo.Ssar.TryGetValue(SelectedSsar.Id, out var ssarInfo))
            {
                NotifySsarExportFailed("selected SSAR entry is missing in INFO.");
                return false;
            }

            if (!_ssarMusTextOverrides.TryGetValue(SelectedSsar.Id, out var musText))
            {
                if (!TryReadFileFromFat(ssarInfo.FileId, out var ssarBytes))
                {
                    NotifySsarExportFailed($"could not read file data (FileId={ssarInfo.FileId}).");
                    return false;
                }

                if (!TryBuildSelectedSsarMusText(SelectedSsar.Id, SelectedSsar.Name, ssarBytes, out musText, out var musError))
                {
                    NotifySsarExportFailed(musError ?? "failed to build MUS text.");
                    return false;
                }
            }

            File.WriteAllText(outputPath, NormalizeMusText(musText), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusMessage = $"Exported SSAR MUS: {outputPath}";
            return true;
        }
        catch (Exception ex)
        {
            NotifySsarExportFailed(ex.Message);
            return false;
        }
    }

    public bool TryCreateSelectedSsarMusEditorSession(out MusEditorSession session)
    {
        session = default;

        if (SelectedSsar is null)
        {
            StatusMessage = "MUS editor: no SSAR selected.";
            return false;
        }

        if (_lastInfo is null || !_lastInfo.Ssar.TryGetValue(SelectedSsar.Id, out var ssarInfo))
        {
            StatusMessage = "MUS editor: selected SSAR info is unavailable.";
            return false;
        }

        if (!TryReadFileFromFat(ssarInfo.FileId, out var ssarBytes))
        {
            StatusMessage = $"MUS editor: could not read SSAR data (FileId={ssarInfo.FileId}).";
            return false;
        }

        if (!_ssarMusTextOverrides.TryGetValue(SelectedSsar.Id, out var musText))
        {
            if (!TryBuildSelectedSsarMusText(SelectedSsar.Id, SelectedSsar.Name, ssarBytes, out musText, out var error))
            {
                StatusMessage = error ?? "MUS editor: failed to build text.";
                return false;
            }
        }

        string? expectedFilePath = LoadedFilePath;
        int ssarId = SelectedSsar.Id;

        session = new MusEditorSession(
            $"MUS Editor - SSAR {ssarId:D3}",
            $"SSAR {ssarId:D3}: {SelectedSsar.Name} (FileId={ssarInfo.FileId})",
            musText,
            editedText => SaveSsarMusTextAsync(expectedFilePath, ssarId, editedText));

        return true;
    }

    private Task<MusSaveResult> SaveSsarMusTextAsync(string? expectedFilePath, int ssarId, string editedText)
    {
        if (!IsSameLoadedSdat(expectedFilePath))
            return Task.FromResult(new MusSaveResult(false, "Loaded SDAT changed. Reopen MUS editor."));

        string normalized = NormalizeMusText(editedText);
        _ssarMusTextOverrides[ssarId] = normalized;
        StatusMessage = $"SSAR {ssarId:D3} MUS text saved in memory.";
        return Task.FromResult(new MusSaveResult(true, "Saved MUS text in memory."));
    }

    private bool TryBuildSelectedSsarMusText(int ssarId, string ssarName, byte[] ssarBytes, out string musText, out string? error)
    {
        musText = string.Empty;
        error = null;

        SSAR ssar;
        try
        {
            ssar = SSAR.Read(ssarBytes);
        }
        catch (Exception ex)
        {
            error = $"MUS editor: failed to parse SSAR data: {ex.Message}";
            return false;
        }

        IReadOnlyDictionary<int, string>? sequenceNames = null;
        if (_lastSymb is not null && _lastSymb.SsarSequenceNames.TryGetValue(ssarId, out var nestedNames))
            sequenceNames = nestedNames;

        var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<SsarMusEntry>(ssar.Sequences.Count);

        foreach (var sequence in ssar.Sequences)
        {
            string rawSeqName = ResolveSsarSequenceName(sequenceNames, sequence.Index);
            string label = BuildUniqueMusIdentifier(rawSeqName, $"SEQ_{sequence.Index:D3}", usedLabels);
            string sbnkLabel = ToMusIdentifier(ResolveSbnkName(sequence.SbnkId), $"SBNK_{sequence.SbnkId:D3}");
            string playerLabel = ToMusIdentifier(ResolvePlayerName(sequence.PlayerId), $"PLAYER_{sequence.PlayerId:D3}");

            string body;
            string? extractError;
            string? decompileError = null;
            if (TryBuildSsarPlaybackData(ssar, ssarBytes, sequence, out var eventData, out _, out extractError)
                && TryDecompileStandaloneSseq(eventData, label, out var decompiledText, out decompileError))
            {
                body = ExtractSmftBodyForMus(decompiledText, label);
                if (body.Length == 0)
                    body = $"{label}:{Environment.NewLine}\tfin";
            }
            else
            {
                string failReason = (extractError ?? decompileError ?? "unknown error").Replace("\r", " ").Replace("\n", " ");
                body = $"{label}:{Environment.NewLine}\tfin{Environment.NewLine}; Failed to decode original data: {failReason}";
            }

            entries.Add(new SsarMusEntry(
                SequenceId: sequence.Index,
                Label: label,
                SbnkLabel: sbnkLabel,
                PlayerLabel: playerLabel,
                Volume: sequence.Volume,
                ChannelPriority: sequence.ChannelPriority,
                PlayerPriority: sequence.PlayerPriority,
                DataText: body));
        }

        string baseName = SanitizeExportBaseName(string.IsNullOrWhiteSpace(ssarName) ? $"SSAR_{ssarId:D3}" : ssarName);
        var sb = new StringBuilder(entries.Count * 256 + 256);

        sb.AppendLine(";;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;");
        sb.AppendLine(";");
        sb.AppendLine($"; {baseName}.mus");
        sb.AppendLine(";     Generated By Nitro Synth");
        sb.AppendLine(";");
        sb.AppendLine(";;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;");
        sb.AppendLine();

        sb.AppendLine("@SEQ_TABLE");
        foreach (var entry in entries)
        {
            sb.Append(entry.Label)
              .Append(" = ")
              .Append(entry.SequenceId)
              .Append(":\t")
              .Append(entry.Label)
              .Append(",\t")
              .Append(entry.SbnkLabel)
              .Append(",\t")
              .Append(entry.Volume)
              .Append(",\t")
              .Append(entry.ChannelPriority)
              .Append(",\t")
              .Append(entry.PlayerPriority)
              .Append(",\t")
              .Append(entry.PlayerLabel)
              .AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("@SEQ_DATA");

        for (int i = 0; i < entries.Count; i++)
        {
            sb.AppendLine(entries[i].DataText.TrimEnd());
            if (i + 1 < entries.Count)
                sb.AppendLine();
        }

        musText = NormalizeMusText(sb.ToString());
        return true;
    }

    private static string ResolveSsarSequenceName(IReadOnlyDictionary<int, string>? sequenceNames, int sequenceId)
    {
        if (sequenceNames is not null
            && sequenceNames.TryGetValue(sequenceId, out var resolvedName)
            && !string.IsNullOrWhiteSpace(resolvedName))
        {
            return resolvedName;
        }

        return $"SEQ_{sequenceId:D3}";
    }

    private static string BuildUniqueMusIdentifier(string raw, string fallback, ISet<string> used)
    {
        string baseName = ToMusIdentifier(raw, fallback);
        string candidate = baseName;
        int suffix = 1;

        while (!used.Add(candidate))
        {
            candidate = $"{baseName}_{suffix:D2}";
            suffix++;
        }

        return candidate;
    }

    private static string ToMusIdentifier(string raw, string fallback)
    {
        string source = string.IsNullOrWhiteSpace(raw) ? fallback : raw;
        var sb = new StringBuilder(source.Length);

        foreach (char c in source)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

        string normalized = sb.ToString();
        while (normalized.Contains("__", StringComparison.Ordinal))
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);

        normalized = normalized.Trim('_');
        if (normalized.Length == 0)
            normalized = fallback;

        if (!(char.IsLetter(normalized[0]) || normalized[0] == '_'))
            normalized = "_" + normalized;

        return normalized;
    }

    private static string ExtractSmftBodyForMus(string smftText, string sequenceLabel)
    {
        string normalized = smftText.Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lines = normalized.Split('\n');

        var bodyLines = new List<string>(lines.Length);

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            string trimmedStart = line.TrimStart();

            if (trimmedStart.StartsWith(";", StringComparison.Ordinal))
                continue;

            if (trimmedStart.Length == 0)
            {
                if (bodyLines.Count > 0 && bodyLines[^1].Length > 0)
                    bodyLines.Add(string.Empty);
                continue;
            }

            if (line.StartsWith("    ", StringComparison.Ordinal))
                line = "\t" + line[4..];

            bodyLines.Add(line);
        }

        while (bodyLines.Count > 0 && bodyLines[^1].Length == 0)
            bodyLines.RemoveAt(bodyLines.Count - 1);

        if (bodyLines.Count == 0)
            return $"{sequenceLabel}:{Environment.NewLine}\tfin";

        string body = string.Join(Environment.NewLine, bodyLines);
        body = body.Replace($"{sequenceLabel}_Start", sequenceLabel, StringComparison.Ordinal);
        return body;
    }

    private static string NormalizeMusText(string? text)
    {
        string normalized = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .TrimEnd('\n');

        return normalized + Environment.NewLine;
    }
}
