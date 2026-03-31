using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    public sealed class SeqPlayerRow
    {
        public int Id { get; init; }
        public int No => Id;
        public string Name { get; init; } = string.Empty;
        public string Label => Name;
        public string Display => $"{Id:D3}: {Name}";
        public ushort MaxSequences { get; init; }
        public ushort ChannelBitflags { get; init; }
        public string ChannelBitflagsHex => $"0x{ChannelBitflags:x}";
        public uint HeapSize { get; init; }
    }

    public readonly record struct SeqPlayerWindowSession(
        string Title,
        int PlayerNo,
        string PlayerLabel,
        ushort MaxSequences,
        uint HeapSize,
        ushort ChannelBitflags);

    public ObservableCollection<SeqPlayerRow> SeqPlayers { get; } = new();

    private SeqPlayerRow? _selectedMixerPlayer;
    public SeqPlayerRow? SelectedMixerPlayer
    {
        get => _selectedMixerPlayer;
        set
        {
            if (!SetField(ref _selectedMixerPlayer, value))
                return;

            UpdateMixerChannelGateAvailability();
        }
    }

    private bool _isMixerPlayerChannelLimitEnabled = true;
    public bool IsMixerPlayerChannelLimitEnabled
    {
        get => _isMixerPlayerChannelLimitEnabled;
        set
        {
            if (!SetField(ref _isMixerPlayerChannelLimitEnabled, value))
                return;

            UpdateMixerChannelGateAvailability();
        }
    }

    private static ushort NormalizeChannelMask(ushort mask)
    {
        return mask == 0 ? (ushort)0xFFFF : mask;
    }

    private ushort ResolveSelectedMixerPlayerChannelMask()
    {
        if (!IsMixerPlayerChannelLimitEnabled)
            return 0xFFFF;

        return NormalizeChannelMask(SelectedMixerPlayer?.ChannelBitflags ?? 0);
    }

    public bool SelectMixerPlayerById(int playerId)
    {
        var option = SeqPlayers.FirstOrDefault(p => p.Id == playerId);
        if (option is null)
            return false;

        SelectedMixerPlayer = option;
        return true;
    }

    private void RebuildSeqPlayerRows()
    {
        int? previousSelectedId = SelectedMixerPlayer?.Id;
        SeqPlayers.Clear();

        var playerIds = new SortedSet<int>();
        if (_lastSymb is not null)
        {
            foreach (int id in _lastSymb.Player.Keys)
                playerIds.Add(id);
        }
        if (_lastInfo is not null)
        {
            foreach (int id in _lastInfo.Player.Keys)
                playerIds.Add(id);
        }

        foreach (int id in playerIds)
        {
            string name = $"PLAYER {id:D3}";
            if (_lastSymb is not null
                && _lastSymb.Player.TryGetValue(id, out string? symbName)
                && !string.IsNullOrWhiteSpace(symbName))
            {
                name = symbName;
            }

            ushort maxSequences = 0;
            ushort channelBitflags = 0;
            uint heapSize = 0;
            if (_lastInfo is not null && _lastInfo.Player.TryGetValue(id, out var info))
            {
                maxSequences = info.MaxSequences;
                channelBitflags = info.ChannelBitflags;
                heapSize = info.HeapSize;
            }

            SeqPlayers.Add(new SeqPlayerRow
            {
                Id = id,
                Name = name,
                MaxSequences = maxSequences,
                ChannelBitflags = channelBitflags,
                HeapSize = heapSize
            });
        }

        if (previousSelectedId.HasValue)
            SelectedMixerPlayer = SeqPlayers.FirstOrDefault(p => p.Id == previousSelectedId.Value);

        SelectedMixerPlayer ??= SeqPlayers.FirstOrDefault();
        UpdateMixerChannelGateAvailability();
    }

    public bool TryCreateSeqPlayerWindowSession(SeqPlayerRow? row, out SeqPlayerWindowSession session)
    {
        session = default;
        if (row is null)
            return false;

        session = new SeqPlayerWindowSession(
            Title: $"SeqPlayer Editor - PLAYER {row.Id:D3}",
            PlayerNo: row.Id,
            PlayerLabel: row.Name,
            MaxSequences: row.MaxSequences,
            HeapSize: row.HeapSize,
            ChannelBitflags: row.ChannelBitflags);
        return true;
    }
}
