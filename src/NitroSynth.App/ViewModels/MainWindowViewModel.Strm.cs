using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    public sealed class StrmRow
    {
        public int Id { get; init; }
        public int No => Id;
        public string Label { get; init; } = string.Empty;
        public uint FileId { get; init; }
        public int Volume { get; init; }
        public int Priority { get; init; }
        public int PlayerId { get; init; }
        public string PlayerLabel { get; init; } = string.Empty;
        public bool AutoStereo { get; init; }
    }

    public ObservableCollection<StrmRow> Strms { get; } = new();

    private StrmRow? _selectedStrm;
    public StrmRow? SelectedStrm
    {
        get => _selectedStrm;
        set => SetField(ref _selectedStrm, value);
    }

    private void RebuildStrmRows()
    {
        int? previousSelectedId = SelectedStrm?.Id;
        Strms.Clear();

        var ids = new SortedSet<int>();
        if (_lastSymb is not null)
        {
            foreach (int id in _lastSymb.Strm.Keys)
                ids.Add(id);
        }

        if (_lastInfo is not null)
        {
            foreach (int id in _lastInfo.Strm.Keys)
                ids.Add(id);
        }

        foreach (int id in ids)
        {
            string label = $"STRM {id:D3}";
            if (_lastSymb is not null &&
                _lastSymb.Strm.TryGetValue(id, out string? symbName) &&
                !string.IsNullOrWhiteSpace(symbName))
            {
                label = symbName;
            }

            uint fileId = 0;
            int volume = 0;
            int priority = 0;
            int playerId = 0;
            bool autoStereo = false;
            if (_lastInfo is not null && _lastInfo.Strm.TryGetValue(id, out var info))
            {
                fileId = info.FileId;
                volume = info.Volume;
                priority = info.Priority;
                playerId = info.PlayerId;
                autoStereo = info.AutoStereo;
            }

            string playerLabel = ResolvePlayerLabel(playerId);
            Strms.Add(new StrmRow
            {
                Id = id,
                Label = label,
                FileId = fileId,
                Volume = volume,
                Priority = priority,
                PlayerId = playerId,
                PlayerLabel = playerLabel,
                AutoStereo = autoStereo
            });
        }

        if (previousSelectedId.HasValue)
            SelectedStrm = Strms.FirstOrDefault(s => s.Id == previousSelectedId.Value);

        SelectedStrm ??= Strms.FirstOrDefault();
    }

    public bool SelectStrmById(int strmId)
    {
        var row = Strms.FirstOrDefault(s => s.Id == strmId);
        if (row is null)
            return false;

        SelectedStrm = row;
        return true;
    }

    private string ResolvePlayerLabel(int playerId)
    {
        if (_lastSymb is not null &&
            _lastSymb.Player.TryGetValue(playerId, out string? name) &&
            !string.IsNullOrWhiteSpace(name))
        {
            return $"{playerId:D3}: {name}";
        }

        if (SeqPlayers.FirstOrDefault(p => p.Id == playerId) is { } player)
            return $"{playerId:D3}: {player.Name}";

        return $"{playerId:D3}: PLAYER {playerId:D3}";
    }
}
