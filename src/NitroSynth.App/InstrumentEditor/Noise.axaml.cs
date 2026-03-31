using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace NitroSynth.App.InstrumentEditor
{
    public partial class Noise : Window
    {
        public Noise() { InitializeComponent(); }

        public Noise(byte[] articulationBytes, string title = "Noise Instrument Editor") : this()
        {
            DataContext = new Vm(articulationBytes) { Title = title };
        }

        private void OnCloseClicked(object? s, Avalonia.Interactivity.RoutedEventArgs e) => Close();

        public byte[] GetEditedBytes() => ((Vm)DataContext!).ToBytes();

        private sealed class Vm : INotifyPropertyChanged
        {
            public string Title { get; set; } = "Noise Instrument Editor";

            ushort _swav, _swar;
            public string SwavHex => $"0x{_swav:X4}";
            public string SwarHex => $"0x{_swar:X4}";

            byte _baseKey = 60; public byte BaseKey { get => _baseKey; set { _baseKey = value; OnPropertyChanged(); } }
            byte _attack; public byte AttackRate { get => _attack; set { _attack = value; OnPropertyChanged(); } }
            byte _decay; public byte DecayRate { get => _decay; set { _decay = value; OnPropertyChanged(); } }
            byte _sustain; public byte SustainLevel { get => _sustain; set { _sustain = value; OnPropertyChanged(); } }
            byte _release; public byte ReleaseRate { get => _release; set { _release = value; OnPropertyChanged(); } }
            byte _pan = 64; public byte Pan { get => _pan; set { _pan = value; OnPropertyChanged(); } }

            public Vm(byte[] bytes)
            {
                if (bytes == null || bytes.Length < 0x0A)
                    throw new ArgumentException("Articulation must be 10 bytes for Noise.");
                _swav = BitConverter.ToUInt16(bytes, 0x00); 
                _swar = BitConverter.ToUInt16(bytes, 0x02); 
                BaseKey = bytes[0x04];
                AttackRate = bytes[0x05];
                DecayRate = bytes[0x06];
                SustainLevel = bytes[0x07];
                ReleaseRate = bytes[0x08];
                Pan = bytes[0x09];
            }

            public byte[] ToBytes()
            {
                var buf = new byte[0x0A];
                BitConverter.TryWriteBytes(buf.AsSpan(0x00, 2), (ushort)0);
                BitConverter.TryWriteBytes(buf.AsSpan(0x02, 2), (ushort)0);
                buf[0x04] = BaseKey;
                buf[0x05] = AttackRate;
                buf[0x06] = DecayRate;
                buf[0x07] = SustainLevel;
                buf[0x08] = ReleaseRate;
                buf[0x09] = Pan;
                return buf;
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            void OnPropertyChanged([CallerMemberName] string? n = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }
}

