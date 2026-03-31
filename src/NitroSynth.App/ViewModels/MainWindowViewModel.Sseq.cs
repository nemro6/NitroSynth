using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    public sealed class SseqOption
    {
        public int Id { get; }
        public string Name { get; }

        public SseqOption(int id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Display => $"{Id:D3}: {Name}";
        public override string ToString() => Display;
    }

    public sealed class PlayerOption
    {
        public int Id { get; }
        public string Name { get; }

        public PlayerOption(int id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Display => $"{Id:D3}: {Name}";
        public override string ToString() => Display;
    }

    public ObservableCollection<SseqOption> SseqOptions { get; } = new();
    public ObservableCollection<PlayerOption> SseqPlayerOptions { get; } = new();

    private byte[] _loadedSseqEventData = Array.Empty<byte>();
    private Dictionary<int, int> _loadedSseqInstructionLengths = new();

    private SseqOption? _selectedSseq;
    public SseqOption? SelectedSseq
    {
        get => _selectedSseq;
        set
        {
            if (!SetField(ref _selectedSseq, value))
                return;

            ResetMidi();
            LoadSelectedSseqDetails();
            OnPropertyChanged(nameof(CanPlaySseq));
        }
    }

    private SBNK.BankOption? _selectedSseqBank;
    public SBNK.BankOption? SelectedSseqBank
    {
        get => _selectedSseqBank;
        set => SetField(ref _selectedSseqBank, value);
    }

    private int _selectedSseqVolume;
    public int SelectedSseqVolume
    {
        get => _selectedSseqVolume;
        set
        {
            int clamped = Math.Clamp(value, 0, 127);
            if (!SetField(ref _selectedSseqVolume, clamped))
                return;

            OnPropertyChanged(nameof(SelectedSseqVolumeKnobAngle));
            OnPropertyChanged(nameof(SelectedSseqVolumeArcPathData));
            OnPropertyChanged(nameof(SelectedSseqVolumeHasArc));
            OnPropertyChanged(nameof(SelectedSseqVolumeNeedlePathData));

            if (IsSseqPlaying || IsSseqPaused)
                ApplySseqMainVolumeToAllChannels(clamped, executionContext: null);
        }
    }

    public double SelectedSseqVolumeKnobAngle
        => -135.0 + (_selectedSseqVolume / 127.0) * 270.0;

    public bool SelectedSseqVolumeHasArc => _selectedSseqVolume > 0;

    public string SelectedSseqVolumeArcPathData
    {
        get
        {
            const double centerX = 11.0;
            const double centerY = 11.0;
            const double radius = 9.0;
            const double startAngle = -135.0;
            double endAngle = SelectedSseqVolumeKnobAngle;
            double delta = endAngle - startAngle;
            int largeArc = delta > 180.0 ? 1 : 0;

            double startRadians = startAngle * Math.PI / 180.0;
            double endRadians = endAngle * Math.PI / 180.0;

            double startX = centerX + radius * Math.Sin(startRadians);
            double startY = centerY - radius * Math.Cos(startRadians);
            double endX = centerX + radius * Math.Sin(endRadians);
            double endY = centerY - radius * Math.Cos(endRadians);

            return string.Create(
                CultureInfo.InvariantCulture,
                $"M {startX:F3},{startY:F3} A {radius:F3},{radius:F3} 0 {largeArc} 1 {endX:F3},{endY:F3}");
        }
    }

    public string SelectedSseqVolumeNeedlePathData
    {
        get
        {
            const double centerX = 11.0;
            const double centerY = 11.0;
            const double length = 7.0;

            double angle = SelectedSseqVolumeKnobAngle;
            double radians = angle * Math.PI / 180.0;
            double tipX = centerX + length * Math.Sin(radians);
            double tipY = centerY - length * Math.Cos(radians);

            return string.Create(
                CultureInfo.InvariantCulture,
                $"M {centerX:F3},{centerY:F3} L {tipX:F3},{tipY:F3}");
        }
    }

    private int _selectedSseqChannelPriority;
    public int SelectedSseqChannelPriority
    {
        get => _selectedSseqChannelPriority;
        set => SetField(ref _selectedSseqChannelPriority, Math.Clamp(value, 0, 127));
    }

    private int _selectedSseqPlayerPriority;
    public int SelectedSseqPlayerPriority
    {
        get => _selectedSseqPlayerPriority;
        set => SetField(ref _selectedSseqPlayerPriority, Math.Clamp(value, 0, 127));
    }

    private PlayerOption? _selectedSseqPlayer;
    public PlayerOption? SelectedSseqPlayer
    {
        get => _selectedSseqPlayer;
        set
        {
            if (!SetField(ref _selectedSseqPlayer, value))
                return;

            if (value is not null)
                SelectMixerPlayerById(value.Id);
        }
    }

    private string _sseqDecompilerText = string.Empty;
    public string SseqDecompilerText
    {
        get => _sseqDecompilerText;
        set => SetField(ref _sseqDecompilerText, value);
    }

    private void RebuildSseqOptions()
    {
        SseqOptions.Clear();
        SseqPlayerOptions.Clear();

        var sseqIds = new SortedSet<int>();
        if (_lastSymb is not null)
        {
            foreach (var id in _lastSymb.Sseq.Keys)
                sseqIds.Add(id);
        }
        if (_lastInfo is not null)
        {
            foreach (var id in _lastInfo.Sseq.Keys)
                sseqIds.Add(id);
        }

        foreach (var id in sseqIds)
        {
            string name = $"SSEQ {id:D3}";
            if (_lastSymb is not null && _lastSymb.Sseq.TryGetValue(id, out var symbName) && !string.IsNullOrWhiteSpace(symbName))
                name = symbName;
            SseqOptions.Add(new SseqOption(id, name));
        }

        var playerIds = new SortedSet<int>();
        if (_lastSymb is not null)
        {
            foreach (var id in _lastSymb.Player.Keys)
                playerIds.Add(id);
        }
        if (_lastInfo is not null)
        {
            foreach (var id in _lastInfo.Player.Keys)
                playerIds.Add(id);
        }

        foreach (var id in playerIds)
        {
            string name = $"PLAYER {id:D3}";
            if (_lastSymb is not null && _lastSymb.Player.TryGetValue(id, out var symbName) && !string.IsNullOrWhiteSpace(symbName))
                name = symbName;
            SseqPlayerOptions.Add(new PlayerOption(id, name));
        }

        SelectedSseq = SseqOptions.FirstOrDefault();
        OnPropertyChanged(nameof(CanPlaySseq));
    }

    private void ResetSseqDetails()
    {
        SelectedSseqBank = null;
        SelectedSseqVolume = 0;
        SelectedSseqChannelPriority = 0;
        SelectedSseqPlayerPriority = 0;
        SelectedSseqPlayer = null;
        SseqDecompilerText = string.Empty;
        _loadedSseqEventData = Array.Empty<byte>();
        _loadedSseqInstructionLengths = new Dictionary<int, int>();
    }

    private void LoadSelectedSseqDetails()
    {
        var sseq = SelectedSseq;
        if (sseq is null || _lastInfo is null || _lastFat is null || string.IsNullOrEmpty(LoadedFilePath))
        {
            ResetSseqDetails();
            return;
        }

        if (!_lastInfo.Sseq.TryGetValue(sseq.Id, out var sseqInfo))
        {
            ResetSseqDetails();
            SseqDecompilerText = $"; INFO entry not found for SSEQ {sseq.Id:D3}";
            return;
        }

        SelectedSseqBank = BankOptions.FirstOrDefault(b => b.Id == sseqInfo.SbnkId);
        SelectedSseqVolume = sseqInfo.Volume;
        SelectedSseqChannelPriority = sseqInfo.ChannelPriority;
        SelectedSseqPlayerPriority = sseqInfo.PlayerPriority;
        SelectedSseqPlayer = SseqPlayerOptions.FirstOrDefault(p => p.Id == sseqInfo.PlayerId);
        SelectMixerPlayerById(sseqInfo.PlayerId);

        if (!TryReadFileFromFat(sseqInfo.FileId, out var sseqBytes))
        {
            SseqDecompilerText = $"; Could not read SSEQ file data (FileId={sseqInfo.FileId})";
            _loadedSseqEventData = Array.Empty<byte>();
            _loadedSseqInstructionLengths = new Dictionary<int, int>();
            return;
        }

        try
        {
            var parsed = SSEQ.Read(sseqBytes);
            string baseName = GetSelectedSseqExportBaseName();
            SseqDecompilerText = parsed.Decompile(baseName, $"{baseName}.smft");
            _loadedSseqEventData = parsed.EventData.ToArray();
            _loadedSseqInstructionLengths = BuildSseqInstructionLengthMap(_loadedSseqEventData);
        }
        catch (Exception ex)
        {
            SseqDecompilerText = $"; Failed to parse SSEQ {sseq.Id:D3}: {ex.Message}";
            _loadedSseqEventData = Array.Empty<byte>();
            _loadedSseqInstructionLengths = new Dictionary<int, int>();
        }
    }

    private bool TryGetSelectedSseqData(out SSEQ sseq, out SseqInfo sseqInfo)
    {
        sseq = null!;
        sseqInfo = default;

        var selected = SelectedSseq;
        if (selected is null || _lastInfo is null || _lastFat is null || string.IsNullOrEmpty(LoadedFilePath))
            return false;

        if (!_lastInfo.Sseq.TryGetValue(selected.Id, out sseqInfo))
            return false;

        if (!TryReadFileFromFat(sseqInfo.FileId, out var sseqBytes))
            return false;

        try
        {
            sseq = SSEQ.Read(sseqBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryReadFileFromFat(uint fileId, out byte[] fileBytes)
    {
        fileBytes = Array.Empty<byte>();

        if (_fatFileOverrides.TryGetValue(fileId, out var overridden))
        {
            fileBytes = (byte[])overridden.Clone();
            return true;
        }

        if (_lastFat is null || string.IsNullOrEmpty(LoadedFilePath))
            return false;

        if (fileId >= _lastFat.Entries.Count)
            return false;

        var fatEntry = _lastFat.Entries[(int)fileId];
        if (fatEntry.Size == 0)
            return false;

        if (fatEntry.Size > int.MaxValue)
            return false;

        using var fs = File.OpenRead(LoadedFilePath);
        fs.Seek(fatEntry.Offset, SeekOrigin.Begin);

        var bytes = new byte[(int)fatEntry.Size];
        int read = fs.Read(bytes, 0, bytes.Length);
        if (read != bytes.Length)
            return false;

        fileBytes = bytes;
        return true;
    }
}
