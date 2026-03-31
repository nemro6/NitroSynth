using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using NitroSynth.App;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    public sealed class SsarOption
    {
        public int Id { get; }
        public string Name { get; }

        public SsarOption(int id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Display => $"{Id:D3}: {Name}";
        public override string ToString() => Display;
    }

    public sealed class SsarSequenceRow
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Offset { get; init; } = string.Empty;
        public ushort SbnkId { get; init; }
        public byte Volume { get; init; }
        public byte ChannelPriority { get; init; }
        public byte PlayerPriority { get; init; }
        public byte PlayerId { get; init; }
    }

    public ObservableCollection<SsarOption> SsarOptions { get; } = new();
    public ObservableCollection<SsarSequenceRow> SsarSequences { get; } = new();

    private SsarOption? _selectedSsar;
    public SsarOption? SelectedSsar
    {
        get => _selectedSsar;
        set
        {
            if (!SetField(ref _selectedSsar, value))
                return;

            LoadSelectedSsarSequences();
        }
    }

    private void RebuildSsarOptions()
    {
        SsarOptions.Clear();
        SsarSequences.Clear();

        var ssarIds = new SortedSet<int>();
        if (_lastSymb is not null)
        {
            foreach (int id in _lastSymb.Ssar.Keys)
                ssarIds.Add(id);
        }
        if (_lastInfo is not null)
        {
            foreach (int id in _lastInfo.Ssar.Keys)
                ssarIds.Add(id);
        }

        foreach (int id in ssarIds)
        {
            string name = $"SSAR {id:D3}";
            if (_lastSymb is not null && _lastSymb.Ssar.TryGetValue(id, out string? symbName) && !string.IsNullOrWhiteSpace(symbName))
                name = symbName;

            SsarOptions.Add(new SsarOption(id, name));
        }

        SelectedSsar = SsarOptions.FirstOrDefault();
    }

    private void LoadSelectedSsarSequences()
    {
        SsarSequences.Clear();

        if (SelectedSsar is null || _lastInfo is null)
            return;

        if (!_lastInfo.Ssar.TryGetValue(SelectedSsar.Id, out SsarInfo ssarInfo))
            return;

        if (!TryReadFileFromFat(ssarInfo.FileId, out byte[] ssarBytes))
            return;

        SSAR parsed;
        try
        {
            parsed = SSAR.Read(ssarBytes);
        }
        catch
        {
            return;
        }

        IReadOnlyDictionary<int, string>? sequenceNames = null;
        if (_lastSymb is not null && _lastSymb.SsarSequenceNames.TryGetValue(SelectedSsar.Id, out var nestedNames))
            sequenceNames = nestedNames;

        foreach (var sequence in parsed.Sequences)
        {
            string offsetText = sequence.SequenceOffset >= 0
                ? $"0x{sequence.SequenceOffset:X6}"
                : "N/A";
            string sequenceName = $"SEQ {sequence.Index:D3}";
            if (sequenceNames is not null
                && sequenceNames.TryGetValue(sequence.Index, out string? resolvedName)
                && !string.IsNullOrWhiteSpace(resolvedName))
            {
                sequenceName = resolvedName;
            }

            SsarSequences.Add(new SsarSequenceRow
            {
                Id = sequence.Index,
                Name = sequenceName,
                Offset = offsetText,
                SbnkId = sequence.SbnkId,
                Volume = sequence.Volume,
                ChannelPriority = sequence.ChannelPriority,
                PlayerPriority = sequence.PlayerPriority,
                PlayerId = sequence.PlayerId
            });
        }
    }

    public void NotifySsarSequenceActionNotImplemented(string action, SsarSequenceRow? row)
    {
        if (SelectedSsar is null || row is null)
        {
            StatusMessage = $"{action} is not implemented yet.";
            return;
        }

        StatusMessage = $"{action} is not implemented yet: SSAR {SelectedSsar.Id:D3} / SEQ {row.Id:D3}";
    }
}
