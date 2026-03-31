using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    public readonly record struct HexSaveResult(bool Success, string Message);

    public readonly record struct HexEditorSession(
        string Title,
        string HeaderText,
        byte[] InitialBytes,
        Func<byte[], Task<HexSaveResult>> SaveAsync);

    public bool TryCreateSelectedSseqHexEditorSession(out HexEditorSession session)
    {
        session = default;

        if (SelectedSseq is null)
        {
            StatusMessage = "HEX editor: no SSEQ selected.";
            return false;
        }

        if (_lastInfo is null || !_lastInfo.Sseq.TryGetValue(SelectedSseq.Id, out var sseqInfo))
        {
            StatusMessage = "HEX editor: selected SSEQ info is unavailable.";
            return false;
        }

        if (!TryReadFileFromFat(sseqInfo.FileId, out var sseqBytes))
        {
            StatusMessage = $"HEX editor: could not read SSEQ data (FileId={sseqInfo.FileId}).";
            return false;
        }

        string? expectedFilePath = LoadedFilePath;
        int sseqId = SelectedSseq.Id;
        string sseqName = SelectedSseq.Name;
        uint fileId = sseqInfo.FileId;

        session = new HexEditorSession(
            $"HEX Editor - SSEQ {sseqId:D3}",
            $"SSEQ {sseqId:D3}: {sseqName} (FileId={fileId})",
            sseqBytes,
            updatedBytes => SaveSseqHexAsync(expectedFilePath, sseqId, fileId, updatedBytes));

        return true;
    }

    public bool TryCreateSelectedSbnkHexEditorSession(out HexEditorSession session)
    {
        session = default;

        if (SelectedBank is null)
        {
            StatusMessage = "HEX editor: no SBNK selected.";
            return false;
        }

        if (_lastInfo is null || !_lastInfo.Sbnk.TryGetValue(SelectedBank.Id, out var sbnkInfo))
        {
            StatusMessage = "HEX editor: selected SBNK info is unavailable.";
            return false;
        }

        if (!TryReadFileFromFat(sbnkInfo.FileId, out var sbnkBytes))
        {
            StatusMessage = $"HEX editor: could not read SBNK data (FileId={sbnkInfo.FileId}).";
            return false;
        }

        string? expectedFilePath = LoadedFilePath;
        int bankId = SelectedBank.Id;
        string bankName = SelectedBank.Name;
        uint fileId = sbnkInfo.FileId;

        session = new HexEditorSession(
            $"HEX Editor - SBNK {bankId:D3}",
            $"SBNK {bankId:D3}: {bankName} (FileId={fileId})",
            sbnkBytes,
            updatedBytes => SaveSbnkHexAsync(expectedFilePath, bankId, fileId, updatedBytes));

        return true;
    }

    public bool TryCreateSelectedSwarHexEditorSession(out HexEditorSession session)
    {
        session = default;

        if (SelectedSwar is null || SelectedSwar.Id < 0)
        {
            StatusMessage = "HEX editor: no SWAR selected.";
            return false;
        }

        if (_lastInfo is null || !_lastInfo.Swar.TryGetValue(SelectedSwar.Id, out var swarInfo))
        {
            StatusMessage = "HEX editor: selected SWAR info is unavailable.";
            return false;
        }

        if (!TryReadFileFromFat(swarInfo.FileId, out var swarBytes))
        {
            StatusMessage = $"HEX editor: could not read SWAR data (FileId={swarInfo.FileId}).";
            return false;
        }

        string? expectedFilePath = LoadedFilePath;
        int swarId = SelectedSwar.Id;
        string swarName = SelectedSwar.Name;
        uint fileId = swarInfo.FileId;

        session = new HexEditorSession(
            $"HEX Editor - SWAR {swarId:D3}",
            $"SWAR {swarId:D3}: {swarName} (FileId={fileId})",
            swarBytes,
            updatedBytes => SaveSwarHexAsync(expectedFilePath, swarId, fileId, updatedBytes));

        return true;
    }

    public bool TryCreateSelectedSsarHexEditorSession(out HexEditorSession session)
    {
        session = default;

        if (SelectedSsar is null)
        {
            StatusMessage = "HEX editor: no SSAR selected.";
            return false;
        }

        if (_lastInfo is null || !_lastInfo.Ssar.TryGetValue(SelectedSsar.Id, out var ssarInfo))
        {
            StatusMessage = "HEX editor: selected SSAR info is unavailable.";
            return false;
        }

        if (!TryReadFileFromFat(ssarInfo.FileId, out var ssarBytes))
        {
            StatusMessage = $"HEX editor: could not read SSAR data (FileId={ssarInfo.FileId}).";
            return false;
        }

        string? expectedFilePath = LoadedFilePath;
        int ssarId = SelectedSsar.Id;
        string ssarName = SelectedSsar.Name;
        uint fileId = ssarInfo.FileId;

        session = new HexEditorSession(
            $"HEX Editor - SSAR {ssarId:D3}",
            $"SSAR {ssarId:D3}: {ssarName} (FileId={fileId})",
            ssarBytes,
            updatedBytes => SaveSsarHexAsync(expectedFilePath, ssarId, fileId, updatedBytes));

        return true;
    }

    private static HexSaveResult SaveFailed(string message) => new(false, message);
    private static HexSaveResult SaveSucceeded(string message) => new(true, message);

    private bool IsSameLoadedSdat(string? expectedFilePath)
    {
        if (string.IsNullOrWhiteSpace(expectedFilePath) || string.IsNullOrWhiteSpace(LoadedFilePath))
            return false;

        return string.Equals(LoadedFilePath, expectedFilePath, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HexSaveResult> SaveSseqHexAsync(string? expectedFilePath, int sseqId, uint fileId, byte[] updatedBytes)
    {
        if (!IsSameLoadedSdat(expectedFilePath))
            return SaveFailed("Loaded SDAT changed. Reopen HEX editor.");

        if (updatedBytes.Length == 0)
            return SaveFailed("SSEQ data is empty.");

        try
        {
            _ = SSEQ.Read(updatedBytes);
        }
        catch (Exception ex)
        {
            return SaveFailed($"Invalid SSEQ data: {ex.Message}");
        }

        _fatFileOverrides[fileId] = (byte[])updatedBytes.Clone();
        await StopSelectedSseqPlaybackAsync();

        if (SelectedSseq?.Id == sseqId)
            LoadSelectedSseqDetails();

        StatusMessage = $"SSEQ {sseqId:D3} updated in memory.";
        return SaveSucceeded("Saved and reflected to main view.");
    }

    private async Task<HexSaveResult> SaveSbnkHexAsync(string? expectedFilePath, int bankId, uint fileId, byte[] updatedBytes)
    {
        if (!IsSameLoadedSdat(expectedFilePath))
            return SaveFailed("Loaded SDAT changed. Reopen HEX editor.");

        if (updatedBytes.Length == 0)
            return SaveFailed("SBNK data is empty.");

        try
        {
            using var ms = new MemoryStream(updatedBytes, writable: false);
            _ = SBNK.Read(ms, 0, (uint)updatedBytes.Length);
        }
        catch (Exception ex)
        {
            return SaveFailed($"Invalid SBNK data: {ex.Message}");
        }

        _fatFileOverrides[fileId] = (byte[])updatedBytes.Clone();

        _swarCache.Clear();
        _swavCache.Clear();

        if (SelectedBank?.Id == bankId)
            await LoadSelectedSbnkInstsAsync();

        if (SelectedSwar?.Id >= 0)
            LoadSelectedSwarSwavs();

        StatusMessage = $"SBNK {bankId:D3} updated in memory.";
        return SaveSucceeded("Saved and reflected to main view.");
    }

    private async Task<HexSaveResult> SaveSwarHexAsync(string? expectedFilePath, int swarId, uint fileId, byte[] updatedBytes)
    {
        if (!IsSameLoadedSdat(expectedFilePath))
            return SaveFailed("Loaded SDAT changed. Reopen HEX editor.");

        if (updatedBytes.Length == 0)
            return SaveFailed("SWAR data is empty.");

        if (!SWAR.TryParse(updatedBytes, out _))
            return SaveFailed("Invalid SWAR data.");

        _fatFileOverrides[fileId] = (byte[])updatedBytes.Clone();
        InvalidateSwarCache(swarId);

        if (SelectedSwar?.Id == swarId)
            LoadSelectedSwarSwavs();

        if (SelectedBank is not null)
            await LoadSelectedSbnkInstsAsync();

        StatusMessage = $"SWAR {swarId:D3} updated in memory.";
        return SaveSucceeded("Saved and reflected to main view.");
    }

    private Task<HexSaveResult> SaveSsarHexAsync(string? expectedFilePath, int ssarId, uint fileId, byte[] updatedBytes)
    {
        if (!IsSameLoadedSdat(expectedFilePath))
            return Task.FromResult(SaveFailed("Loaded SDAT changed. Reopen HEX editor."));

        if (updatedBytes.Length == 0)
            return Task.FromResult(SaveFailed("SSAR data is empty."));

        try
        {
            _ = SSAR.Read(updatedBytes);
        }
        catch (Exception ex)
        {
            return Task.FromResult(SaveFailed($"Invalid SSAR data: {ex.Message}"));
        }

        _fatFileOverrides[fileId] = (byte[])updatedBytes.Clone();

        if (SelectedSsar?.Id == ssarId)
            LoadSelectedSsarSequences();

        StatusMessage = $"SSAR {ssarId:D3} updated in memory.";
        return Task.FromResult(SaveSucceeded("Saved and reflected to main view."));
    }

    private void InvalidateSwarCache(int swarInfoId)
    {
        _swarCache.Remove(swarInfoId);

        if (_swavCache.Count == 0)
            return;

        var removeKeys = new List<(int swarInfoId, int swavId)>();
        foreach (var key in _swavCache.Keys)
        {
            if (key.swarInfoId == swarInfoId)
                removeKeys.Add(key);
        }

        foreach (var key in removeKeys)
            _swavCache.Remove(key);
    }
}
