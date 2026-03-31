using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace NitroSynth.App.InstrumentEditor
{
    public partial class DrumSet : Window
    {
        public DrumSet() { InitializeComponent(); }

        public DrumSet(SBNK.DrumSet model, string title = "Drum Set Editor") : this()
        {
            DataContext = new Vm(model) { Title = title };
        }

        private void OnCloseClicked(object? s, Avalonia.Interactivity.RoutedEventArgs e) => Close();

        public SBNK.DrumSet ToModel() => ((Vm)DataContext!).ToModel();

        private sealed class Vm : INotifyPropertyChanged
        {
            public string Title { get; set; } = "Drum Set Editor";

            int _lowNote; public int LowNote
            {
                get => _lowNote;
                set { _lowNote = Clamp(value, 0, 127); OnPropertyChanged(); OnPropertyChanged(nameof(LowNoteValue)); }
            }

            int _highNote; public int HighNote
            {
                get => _highNote;
                set { _highNote = Clamp(value, 0, 127); OnPropertyChanged(); OnPropertyChanged(nameof(HighNoteValue)); }
            }

            public double LowNoteValue
            {
                get => _lowNote;
                set => LowNote = (int)Math.Round(value); 
            }

            public double HighNoteValue
            {
                get => _highNote;
                set => HighNote = (int)Math.Round(value);
            }

            public ObservableCollection<EntryVm> Entries { get; } = new();

            public Vm(SBNK.DrumSet src)
            {
                LowNote = src.LowKey;
                HighNote = src.HighKey;

                foreach (var e in src.Entries)
                    Entries.Add(new EntryVm(e));
            }

            public SBNK.DrumSet ToModel()
            {
                int low = Clamp(LowNote, 0, 127);
                int high = Clamp(HighNote, 0, 127);
                if (high < low)
                    (low, high) = (high, low);

                var ds = new SBNK.DrumSet
                {
                    LowKey = (byte)low,
                    HighKey = (byte)high
                };

                int slotCount = high - low + 1;
                EntryVm? carry = null;

                for (int i = 0; i < slotCount; i++)
                {
                    EntryVm? source = null;
                    if (i < Entries.Count)
                    {
                        source = Entries[i];
                        carry = source;
                    }
                    else if (carry is not null)
                    {
                        source = carry;
                    }

                    ds.Entries.Add(source is null
                        ? CreateNullEntry()
                        : ToDrumEntry(source));
                }

                return ds;
            }

            private static SBNK.DrumSet.DrumEntry ToDrumEntry(EntryVm vm)
            {
                return new SBNK.DrumSet.DrumEntry
                {
                    Type = vm.GetTypeEnum(),
                    Reserved = 0,
                    SwavId = (ushort)Clamp(vm.SwavId, 0, 65535),
                    SwarId = (ushort)Clamp(vm.SwarId, 0, 65535),
                    Key = (byte)Clamp(vm.Key, 0, 127),
                    Attack = (byte)Clamp(vm.Attack, 0, 127),
                    Decay = (byte)Clamp(vm.Decay, 0, 127),
                    Sustain = (byte)Clamp(vm.Sustain, 0, 127),
                    Release = (byte)Clamp(vm.Release, 0, 255),
                    Pan = (byte)Clamp(vm.Pan, 0, 127),
                };
            }

            private static SBNK.DrumSet.DrumEntry CreateNullEntry()
            {
                return new SBNK.DrumSet.DrumEntry
                {
                    Type = SBNK.InstrumentType.NullInstrument,
                    Reserved = 0,
                    SwavId = 0,
                    SwarId = 0,
                    Key = 60,
                    Attack = 127,
                    Decay = 127,
                    Sustain = 127,
                    Release = 127,
                    Pan = 64,
                };
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

            static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        }

        private sealed class EntryVm : INotifyPropertyChanged
        {
            public string[] TypeOptions { get; } =
                { "PCM (01h)", "PSG (02h)", "Noise (03h)", "Direct PCM (04h)", "Null Inst (05h)" };

            int _selectedType;
            public int SelectedType
            {
                get => _selectedType;
                set { _selectedType = Clamp(value, 0, 4); OnPropertyChanged(); OnPropertyChanged(nameof(SelectedTypeLabel)); }
            }

            public string SelectedTypeLabel => TypeOptions[SelectedType];

            int _key; public int Key { get => _key; set { _key = Clamp(value, 0, 127); OnPropertyChanged(); } }
            int _swavId; public int SwavId { get => _swavId; set { _swavId = Clamp(value, 0, 65535); OnPropertyChanged(); } }
            int _swarId; public int SwarId { get => _swarId; set { _swarId = Clamp(value, 0, 65535); OnPropertyChanged(); } }
            int _attack; public int Attack { get => _attack; set { _attack = Clamp(value, 0, 127); OnPropertyChanged(); } }
            int _decay; public int Decay { get => _decay; set { _decay = Clamp(value, 0, 127); OnPropertyChanged(); } }
            int _sustain; public int Sustain { get => _sustain; set { _sustain = Clamp(value, 0, 127); OnPropertyChanged(); } }
            int _release; public int Release { get => _release; set { _release = Clamp(value, 0, 255); OnPropertyChanged(); } }
            int _pan; public int Pan { get => _pan; set { _pan = Clamp(value, 0, 127); OnPropertyChanged(); } }

            public EntryVm(SBNK.DrumSet.DrumEntry e)
            {
                SelectedType = e.Type switch
                {
                    SBNK.InstrumentType.Pcm => 0,
                    SBNK.InstrumentType.Psg => 1,
                    SBNK.InstrumentType.Noise => 2,
                    SBNK.InstrumentType.DirectPcm => 3,
                    SBNK.InstrumentType.NullInstrument => 4,
                    _ => 4
                };
                Key = e.Key;
                SwavId = e.SwavId;
                SwarId = e.SwarId;
                Attack = e.Attack;
                Decay = e.Decay;
                Sustain = e.Sustain;
                Release = e.Release;
                Pan = e.Pan;
            }

            public SBNK.InstrumentType GetTypeEnum() => SelectedType switch
            {
                0 => SBNK.InstrumentType.Pcm,
                1 => SBNK.InstrumentType.Psg,
                2 => SBNK.InstrumentType.Noise,
                3 => SBNK.InstrumentType.DirectPcm,
                _ => SBNK.InstrumentType.NullInstrument
            };

            public event PropertyChangedEventHandler? PropertyChanged;
            void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

            static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        }
    }
}

