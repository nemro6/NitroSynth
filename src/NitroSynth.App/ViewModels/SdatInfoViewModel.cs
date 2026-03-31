using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace NitroSynth.App.ViewModels
{
    public class SdatInfoViewModel
    {
        public string? FileDisplayPath { get; }
        public uint SdatSize { get; }
        public ushort SdatVersion { get; }
        public string VersionText => $"{(SdatVersion >> 8) & 0xFF}.{SdatVersion & 0xFF}";

        public ObservableCollection<BlockRow> Blocks { get; } = new();

        public SdatInfoViewModel(string? fileDisplayPath, uint sdatSize, ushort sdatVersion, IEnumerable<BlockRow> blocks)
        {
            FileDisplayPath = fileDisplayPath;
            SdatSize = sdatSize;
            SdatVersion = sdatVersion;
            foreach (var b in blocks)
                Blocks.Add(new BlockRow { Name = b.Name, Offset = b.Offset, Size = b.Size });
        }
    }
}
