using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace NitroSynth.App.ViewModels;

public enum SdatTreeNodeKind
{
    None,
    SseqEntry,
    SsarEntry,
    SbnkEntry,
    SwarEntry,
    StrmEntry
}

public sealed class SdatTreeNode
{
    public SdatTreeNode(
        string text,
        SdatTreeNodeKind kind = SdatTreeNodeKind.None,
        int? itemId = null,
        bool isExpanded = false)
    {
        Text = text;
        Kind = kind;
        ItemId = itemId;
        IsExpanded = isExpanded;
    }

    public string Text { get; }
    public SdatTreeNodeKind Kind { get; }
    public int? ItemId { get; }
    public bool IsExpanded { get; set; }
    public bool IsRealFileEntry => ItemId.HasValue && Kind is not SdatTreeNodeKind.None;
    public ObservableCollection<SdatTreeNode> Children { get; } = new();
}

public partial class MainWindowViewModel
{
    public ObservableCollection<SdatTreeNode> SdatTreeRoots { get; } = new();

    private void RebuildSdatTree(SYMB? symb, INFO? info, FAT? fat)
    {
        SdatTreeRoots.Clear();

        var root = new SdatTreeNode("SDAT", isExpanded: true);
        root.Children.Add(BuildSequenceNode(symb, info, fat));
        root.Children.Add(BuildSequenceArchiveNode(symb, info, fat));
        root.Children.Add(BuildBankNode(symb, info, fat));
        root.Children.Add(BuildWaveArchiveNode(symb, info, fat));
        root.Children.Add(BuildPlayerNode(symb, info));
        root.Children.Add(BuildGroupNode(symb, info));
        root.Children.Add(BuildStreamPlayerNode(symb, info));
        root.Children.Add(BuildStreamNode(symb, info, fat));

        SdatTreeRoots.Add(root);
    }

    private static SdatTreeNode BuildSequenceNode(SYMB? symb, INFO? info, FAT? fat)
    {
        var branch = new SdatTreeNode("Sequence(SSEQ)");
        var ids = UnionKeys(symb?.Sseq.Keys, info?.Sseq.Keys);
        foreach (int id in ids)
        {
            string? name = null;
            symb?.Sseq.TryGetValue(id, out name);
            branch.Children.Add(new SdatTreeNode(
                $"{id:D3}: {NameOrFallback(name, $"SSEQ {id:D3}")}",
                SdatTreeNodeKind.SseqEntry,
                id));
        }

        EnsureNotEmpty(branch);
        return branch;
    }

    private static SdatTreeNode BuildSequenceArchiveNode(SYMB? symb, INFO? info, FAT? fat)
    {
        var branch = new SdatTreeNode("Sequence Archive(SSAR)");
        var ids = UnionKeys(symb?.Ssar.Keys, info?.Ssar.Keys);
        foreach (int id in ids)
        {
            string? name = null;
            symb?.Ssar.TryGetValue(id, out name);
            branch.Children.Add(new SdatTreeNode(
                $"{id:D3}: {NameOrFallback(name, $"SSAR {id:D3}")}",
                SdatTreeNodeKind.SsarEntry,
                id));
        }

        EnsureNotEmpty(branch);
        return branch;
    }

    private static SdatTreeNode BuildBankNode(SYMB? symb, INFO? info, FAT? fat)
    {
        var branch = new SdatTreeNode("Bank(SBNK)");
        var ids = UnionKeys(symb?.Sbnk.Keys, info?.Sbnk.Keys);
        foreach (int id in ids)
        {
            string? name = null;
            symb?.Sbnk.TryGetValue(id, out name);
            branch.Children.Add(new SdatTreeNode(
                $"{id:D3}: {NameOrFallback(name, $"SBNK {id:D3}")}",
                SdatTreeNodeKind.SbnkEntry,
                id));
        }

        EnsureNotEmpty(branch);
        return branch;
    }

    private static SdatTreeNode BuildWaveArchiveNode(SYMB? symb, INFO? info, FAT? fat)
    {
        var branch = new SdatTreeNode("Wave Archive(SWAR)");
        var ids = UnionKeys(symb?.Swar.Keys, info?.Swar.Keys);
        foreach (int id in ids)
        {
            string? name = null;
            symb?.Swar.TryGetValue(id, out name);
            branch.Children.Add(new SdatTreeNode(
                $"{id:D3}: {NameOrFallback(name, $"SWAR {id:D3}")}",
                SdatTreeNodeKind.SwarEntry,
                id));
        }

        EnsureNotEmpty(branch);
        return branch;
    }

    private static SdatTreeNode BuildPlayerNode(SYMB? symb, INFO? info)
    {
        var branch = new SdatTreeNode("Player");
        var ids = UnionKeys(symb?.Player.Keys, info?.Player.Keys);
        foreach (int id in ids)
        {
            string? name = null;
            symb?.Player.TryGetValue(id, out name);
            branch.Children.Add(new SdatTreeNode($"{id:D3}: {NameOrFallback(name, $"PLAYER {id:D3}")}"));
        }

        EnsureNotEmpty(branch);
        return branch;
    }

    private static SdatTreeNode BuildGroupNode(SYMB? symb, INFO? info)
    {
        var branch = new SdatTreeNode("Group");
        var ids = UnionKeys(symb?.Group.Keys, info?.Group.Keys);
        foreach (int id in ids)
        {
            string? name = null;
            symb?.Group.TryGetValue(id, out name);
            branch.Children.Add(new SdatTreeNode($"{id:D3}: {NameOrFallback(name, $"GROUP {id:D3}")}"));
        }

        EnsureNotEmpty(branch);
        return branch;
    }

    private static SdatTreeNode BuildStreamPlayerNode(SYMB? symb, INFO? info)
    {
        var branch = new SdatTreeNode("Stream Player");
        var ids = UnionKeys(symb?.StrmPlayer.Keys, info?.StrmPlayer.Keys);
        foreach (int id in ids)
        {
            string? name = null;
            symb?.StrmPlayer.TryGetValue(id, out name);
            branch.Children.Add(new SdatTreeNode($"{id:D3}: {NameOrFallback(name, $"STRMPLAYER {id:D3}")}"));
        }

        EnsureNotEmpty(branch);
        return branch;
    }

    private static SdatTreeNode BuildStreamNode(SYMB? symb, INFO? info, FAT? fat)
    {
        var branch = new SdatTreeNode("Stream(STRM)");
        var ids = UnionKeys(symb?.Strm.Keys, info?.Strm.Keys);
        foreach (int id in ids)
        {
            string? name = null;
            symb?.Strm.TryGetValue(id, out name);
            branch.Children.Add(new SdatTreeNode(
                $"{id:D3}: {NameOrFallback(name, $"STRM {id:D3}")}",
                SdatTreeNodeKind.StrmEntry,
                id));
        }

        EnsureNotEmpty(branch);
        return branch;
    }

    private static List<int> UnionKeys(IEnumerable<int>? first, IEnumerable<int>? second)
    {
        var set = new SortedSet<int>();
        if (first is not null)
        {
            foreach (int key in first)
                set.Add(key);
        }
        if (second is not null)
        {
            foreach (int key in second)
                set.Add(key);
        }
        return set.ToList();
    }

    private static string NameOrFallback(string? name, string fallback)
    {
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private static void EnsureNotEmpty(SdatTreeNode node)
    {
        if (node.Children.Count == 0)
            node.Children.Add(new SdatTreeNode("(empty)"));
    }

    public void NotifySdatTreeActionNotImplemented(string action, SdatTreeNode node)
    {
        if (node.ItemId is int id)
        {
            StatusMessage = $"{action} is not implemented yet: {node.Kind} {id:D3}";
            return;
        }

        StatusMessage = $"{action} is not implemented yet.";
    }

    public bool SelectBankById(int bankId)
    {
        var bank = BankOptions.FirstOrDefault(b => b.Id == bankId);
        if (bank is null)
            return false;

        SelectedBank = bank;
        return true;
    }

    public bool SelectSseqById(int sseqId)
    {
        var sseq = SseqOptions.FirstOrDefault(s => s.Id == sseqId);
        if (sseq is null)
            return false;

        SelectedSseq = sseq;
        return true;
    }

    public bool SelectSwarById(int swarId)
    {
        var swar = SwarOptions.FirstOrDefault(s => s.Id == swarId);
        if (swar is null)
            return false;

        SelectedSwar = swar;
        return true;
    }

    public bool SelectSsarById(int ssarId)
    {
        var ssar = SsarOptions.FirstOrDefault(s => s.Id == ssarId);
        if (ssar is null)
            return false;

        SelectedSsar = ssar;
        return true;
    }
}
