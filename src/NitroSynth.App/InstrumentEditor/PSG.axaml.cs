using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace NitroSynth.App.InstrumentEditor
{
    public partial class PSG : Window
    {
        public PSG()
        {
            InitializeComponent();
        }

        public PSG(byte[] articulationBytes, string title = "PSG Instrument Editor") : this()
        {
            DataContext = new Vm(articulationBytes) { Title = title };
        }

        private void OnCloseClicked(object? s, Avalonia.Interactivity.RoutedEventArgs e) => Close();

        public byte[] GetEditedBytes() => ((Vm)DataContext!).ToBytes();

        private sealed class Vm : INotifyPropertyChanged
        {
            public string Title { get; set; } = "PSG Instrument Editor";

            public string[] DutyOptions { get; } =
            {
                "0x0 - 12.5%",
                "0x1 - 25.0%",
                "0x2 - 37.5%",
                "0x3 - 50.0%",
                "0x4 - 62.5%",
                "0x5 - 75.0%",
                "0x6 - 87.5%",
            };

            int _dutyIndex;
            public int DutyIndex { get => _dutyIndex; set { _dutyIndex = Clamp(value, 0, 6); OnPropertyChanged(); } }

            byte _baseKey = 60; public byte BaseKey { get => _baseKey; set { _baseKey = value; OnPropertyChanged(); } }
            byte _attack; public byte AttackRate { get => _attack; set { _attack = value; OnPropertyChanged(); } }
            byte _decay; public byte DecayRate { get => _decay; set { _decay = value; OnPropertyChanged(); } }
            byte _sustain; public byte SustainLevel { get => _sustain; set { _sustain = value; OnPropertyChanged(); } }
            byte _release; public byte ReleaseRate { get => _release; set { _release = value; OnPropertyChanged(); } }
            byte _pan = 64; public byte Pan { get => _pan; set { _pan = value; OnPropertyChanged(); } }

            public Vm(byte[] bytes)
            {
                if (bytes == null || bytes.Length < 0x0A)
                    throw new ArgumentException("Articulation must be 10 bytes for PSG.");
                ushort swav = BitConverter.ToUInt16(bytes, 0x00); 

                DutyIndex = Clamp(swav, 0, 6);  
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
                BitConverter.TryWriteBytes(buf.AsSpan(0x00, 2), (ushort)DutyIndex);
                buf[0x02] = 0; buf[0x03] = 0;

                buf[0x04] = BaseKey;
                buf[0x05] = AttackRate;
                buf[0x06] = DecayRate;
                buf[0x07] = SustainLevel;
                buf[0x08] = ReleaseRate;
                buf[0x09] = Pan;
                return buf;
            }

            static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

            public event PropertyChangedEventHandler? PropertyChanged;
            void OnPropertyChanged([CallerMemberName] string? n = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }
}


