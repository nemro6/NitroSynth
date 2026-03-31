using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace NitroSynth.App.InstrumentEditor
{
    public partial class KeySplit : Window
    {
        public KeySplit() { InitializeComponent(); }
        public KeySplit(SBNK.KeySplit model, string title = "Key Split Editor") : this()
        {
            DataContext = new Vm(model) { Title = title };
        }

        private void OnCloseClicked(object? s, Avalonia.Interactivity.RoutedEventArgs e) => Close();

        public SBNK.KeySplit ToModel() => ((Vm)DataContext!).ToModel();

        private sealed class Vm : INotifyPropertyChanged
        {
            public string Title { get; set; } = "Key Split Editor";

            int _k1, _k2, _k3, _k4, _k5, _k6, _k7, _k8;
            public int SplitKey1 { get => _k1; set { _k1 = C(value); OnPropertyChanged(); } }
            public int SplitKey2 { get => _k2; set { _k2 = C(value); OnPropertyChanged(); } }
            public int SplitKey3 { get => _k3; set { _k3 = C(value); OnPropertyChanged(); } }
            public int SplitKey4 { get => _k4; set { _k4 = C(value); OnPropertyChanged(); } }
            public int SplitKey5 { get => _k5; set { _k5 = C(value); OnPropertyChanged(); } }
            public int SplitKey6 { get => _k6; set { _k6 = C(value); OnPropertyChanged(); } }
            public int SplitKey7 { get => _k7; set { _k7 = C(value); OnPropertyChanged(); } }
            public int SplitKey8 { get => _k8; set { _k8 = C(value); OnPropertyChanged(); } }

            public ObservableCollection<EntryVm> Entries { get; } = new();

            public Vm(SBNK.KeySplit src)
            {
                if (src.SplitKeys is { Length: 8 })
                {
                    _k1 = src.SplitKeys[0]; _k2 = src.SplitKeys[1];
                    _k3 = src.SplitKeys[2]; _k4 = src.SplitKeys[3];
                    _k5 = src.SplitKeys[4]; _k6 = src.SplitKeys[5];
                    _k7 = src.SplitKeys[6]; _k8 = src.SplitKeys[7];
                }
                foreach (var e in src.Entries)
                    Entries.Add(new EntryVm(e));
            }

            public SBNK.KeySplit ToModel()
            {
                var splitKeys = new byte[8];
                int[] raw = { SplitKey1, SplitKey2, SplitKey3, SplitKey4, SplitKey5, SplitKey6, SplitKey7, SplitKey8 };

                int prev = 0;
                for (int i = 0; i < splitKeys.Length; i++)
                {
                    int key = C(raw[i]);
                    if (key == 0)
                    {
                        splitKeys[i] = 0;
                        continue;
                    }

                    if (key < prev)
                        key = prev;

                    splitKeys[i] = (byte)key;
                    prev = key;
                }

                int lastUsed = -1;
                for (int i = splitKeys.Length - 1; i >= 0; i--)
                {
                    if (splitKeys[i] != 0)
                    {
                        lastUsed = i;
                        break;
                    }
                }

                if (lastUsed >= 0)
                    splitKeys[lastUsed] = 127;

                var ks = new SBNK.KeySplit { SplitKeys = splitKeys };

                int sourceIndex = 0;
                for (int i = 0; i < splitKeys.Length; i++)
                {
                    if (splitKeys[i] == 0)
                        continue;

                    EntryVm? source = null;
                    if (sourceIndex < Entries.Count)
                        source = Entries[sourceIndex];
                    else if (Entries.Count > 0)
                        source = Entries[^1];

                    ks.Entries.Add(source is null
                        ? CreateNullEntry()
                        : ToKeySplitEntry(source));
                    sourceIndex++;
                }

                return ks;
            }

            private static SBNK.KeySplit.KeySplitEntry ToKeySplitEntry(EntryVm vm)
            {
                return new SBNK.KeySplit.KeySplitEntry
                {
                    Type = vm.GetTypeEnum(),
                    Reserved = 0,
                    SwavId = (ushort)Math.Clamp(vm.SwavId, 0, 65535),
                    SwarId = (ushort)Math.Clamp(vm.SwarId, 0, 65535),
                    BaseKey = (byte)Math.Clamp(vm.BaseKey, 0, 127),
                    Attack = (byte)Math.Clamp(vm.Attack, 0, 127),
                    Decay = (byte)Math.Clamp(vm.Decay, 0, 127),
                    Sustain = (byte)Math.Clamp(vm.Sustain, 0, 127),
                    Release = (byte)Math.Clamp(vm.Release, 0, 255),
                    Pan = (byte)Math.Clamp(vm.Pan, 0, 127),
                };
            }

            private static SBNK.KeySplit.KeySplitEntry CreateNullEntry()
            {
                return new SBNK.KeySplit.KeySplitEntry
                {
                    Type = SBNK.InstrumentType.NullInstrument,
                    Reserved = 0,
                    SwavId = 0,
                    SwarId = 0,
                    BaseKey = 60,
                    Attack = 127,
                    Decay = 127,
                    Sustain = 127,
                    Release = 127,
                    Pan = 64,
                };
            }

            static int C(int v) => Math.Clamp(v, 0, 127);

            public event PropertyChangedEventHandler? PropertyChanged;
            void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
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

            int _baseKey; public int BaseKey { get => _baseKey; set { _baseKey = Clamp(value, 0, 127); OnPropertyChanged(); } }
            int _swavId; public int SwavId { get => _swavId; set { _swavId = Clamp(value, 0, 65535); OnPropertyChanged(); } }
            int _swarId; public int SwarId { get => _swarId; set { _swarId = Clamp(value, 0, 65535); OnPropertyChanged(); } }
            int _attack; public int Attack { get => _attack; set { _attack = Clamp(value, 0, 127); OnPropertyChanged(); } }
            int _decay; public int Decay { get => _decay; set { _decay = Clamp(value, 0, 127); OnPropertyChanged(); } }
            int _sustain; public int Sustain { get => _sustain; set { _sustain = Clamp(value, 0, 127); OnPropertyChanged(); } }
            int _release; public int Release { get => _release; set { _release = Clamp(value, 0, 255); OnPropertyChanged(); } }
            int _pan; public int Pan { get => _pan; set { _pan = Clamp(value, 0, 127); OnPropertyChanged(); } }

            public EntryVm(SBNK.KeySplit.KeySplitEntry e)
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
                BaseKey = e.BaseKey;
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
