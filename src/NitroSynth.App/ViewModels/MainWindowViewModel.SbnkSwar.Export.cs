using System;
using System.IO;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    public string GetSelectedSbnkExportBaseName()
    {
        string? raw = SelectedBank?.Name;
        if (string.IsNullOrWhiteSpace(raw))
            raw = SelectedBank is null ? "bank" : $"sbnk_{SelectedBank.Id:D3}";

        return SanitizeExportBaseName(raw);
    }

    public string GetSelectedSwarExportBaseName()
    {
        string? raw = SelectedSwar?.Name;
        if (string.IsNullOrWhiteSpace(raw))
            raw = SelectedSwar is null ? "wave_archive" : $"swar_{SelectedSwar.Id:D3}";

        return SanitizeExportBaseName(raw);
    }

    public void NotifySbnkImportNotImplemented()
    {
        StatusMessage = "SBNK REPLACE is not implemented yet.";
    }

    public void NotifySbnkSaveNotImplemented()
    {
        StatusMessage = "SBNK SAVE is not implemented yet.";
    }

    public void NotifySbnkExportFailed(string reason)
    {
        StatusMessage = $"SBNK EXPORT failed: {reason}";
    }

    public void NotifySwarImportNotImplemented()
    {
        StatusMessage = "SWAR REPLACE is not implemented yet.";
    }

    public void NotifySwarSaveNotImplemented()
    {
        StatusMessage = "SWAR SAVE is not implemented yet.";
    }

    public void NotifySwarExportFailed(string reason)
    {
        StatusMessage = $"SWAR EXPORT failed: {reason}";
    }

    public bool ExportSelectedSbnk(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            NotifySbnkExportFailed("output path is empty.");
            return false;
        }

        try
        {
            if (SelectedBank is null)
            {
                NotifySbnkExportFailed("no SBNK selected.");
                return false;
            }

            if (_lastInfo is null || !_lastInfo.Sbnk.TryGetValue(SelectedBank.Id, out var sbnkInfo))
            {
                NotifySbnkExportFailed("selected SBNK entry is missing in INFO.");
                return false;
            }

            if (!TryReadFileFromFat(sbnkInfo.FileId, out var sbnkBytes))
            {
                NotifySbnkExportFailed($"could not read file data (FileId={sbnkInfo.FileId}).");
                return false;
            }

            File.WriteAllBytes(outputPath, sbnkBytes);
            StatusMessage = $"Exported SBNK: {outputPath}";
            return true;
        }
        catch (Exception ex)
        {
            NotifySbnkExportFailed(ex.Message);
            return false;
        }
    }

    public bool ExportSelectedSwar(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            NotifySwarExportFailed("output path is empty.");
            return false;
        }

        try
        {
            if (SelectedSwar is null || SelectedSwar.Id < 0)
            {
                NotifySwarExportFailed("no SWAR selected.");
                return false;
            }

            if (_lastInfo is null || !_lastInfo.Swar.TryGetValue(SelectedSwar.Id, out var swarInfo))
            {
                NotifySwarExportFailed("selected SWAR entry is missing in INFO.");
                return false;
            }

            if (!TryReadFileFromFat(swarInfo.FileId, out var swarBytes))
            {
                NotifySwarExportFailed($"could not read file data (FileId={swarInfo.FileId}).");
                return false;
            }

            File.WriteAllBytes(outputPath, swarBytes);
            StatusMessage = $"Exported SWAR: {outputPath}";
            return true;
        }
        catch (Exception ex)
        {
            NotifySwarExportFailed(ex.Message);
            return false;
        }
    }

    private static string SanitizeExportBaseName(string raw)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            raw = raw.Replace(c, '_');

        raw = raw.Trim();
        return raw.Length == 0 ? "export" : raw;
    }
}
