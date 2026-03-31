using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NitroSynth.App.ViewModels;

namespace NitroSynth.App
{
    public partial class SeqPlayerWindow : Window
    {
        public SeqPlayerWindow()
        {
            InitializeComponent();
        }

        public SeqPlayerWindow(MainWindowViewModel.SeqPlayerWindowSession session)
            : this()
        {
            DataContext = new Vm(session);
        }

        private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

        private sealed class Vm
        {
            public sealed class ChannelFlagVm
            {
                public ChannelFlagVm(string label, bool isChecked)
                {
                    Label = label;
                    IsChecked = isChecked;
                }

                public string Label { get; }
                public bool IsChecked { get; }
            }

            public Vm(MainWindowViewModel.SeqPlayerWindowSession session)
            {
                Title = session.Title;
                PlayerDisplay = $"{session.PlayerNo:D3}: {session.PlayerLabel}";
                MaxSequenceDisplay = session.MaxSequences.ToString();
                HeapSizeDisplay = session.HeapSize.ToString();
                ChannelBitflagsHex = $"0x{session.ChannelBitflags:x}";

                var flags = new ObservableCollection<ChannelFlagVm>();
                for (int channel = 0; channel < 16; channel++)
                {
                    bool isChecked = (session.ChannelBitflags & (1 << channel)) != 0;
                    flags.Add(new ChannelFlagVm(channel.ToString("D2"), isChecked));
                }

                ChannelFlags = flags;
            }

            public string Title { get; }
            public string PlayerDisplay { get; }
            public string MaxSequenceDisplay { get; }
            public string HeapSizeDisplay { get; }
            public string ChannelBitflagsHex { get; }
            public ObservableCollection<ChannelFlagVm> ChannelFlags { get; }
        }
    }
}
