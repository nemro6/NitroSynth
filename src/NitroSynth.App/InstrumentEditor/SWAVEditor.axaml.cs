using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NAudio.Wave;

namespace NitroSynth.App.InstrumentEditor;

public partial class SWAVEditor : Window
{
    private Vm _vm;
    private global::NitroSynth.App.SWAV _swav;
    private SwavPlaybackProvider? _provider;
    private WaveOutEvent? _waveOut;
    private readonly DispatcherTimer _uiTimer;

    public SWAVEditor()
    {
        InitializeComponent();
        _swav = new global::NitroSynth.App.SWAV();
        _vm = new Vm(_swav, -1, -1, "SWAV Editor");
        DataContext = _vm;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 60.0) };
        _uiTimer.Tick += OnUiTick;
        _vm.PropertyChanged += OnVmPropertyChanged;

        ConfigureWaveform(_swav);
    }

    public SWAVEditor(global::NitroSynth.App.SWAV swav, int swarId, int swavId, string? title = null) : this()
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _swav = swav ?? new global::NitroSynth.App.SWAV();
        _vm = new Vm(_swav, swarId, swavId, title ?? $"SWAV - SWAR {swarId:D3} / SWAV {swavId:D3}");
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;

        ConfigureWaveform(_swav);
    }

    private void ConfigureWaveform(global::NitroSynth.App.SWAV swav)
    {
        WaveformView.Samples = swav.PCM16 ?? Array.Empty<short>();
        WaveformView.LoopStartSample = swav.LoopStartSample;
        WaveformView.LoopEndSample = GetLoopEndSample(swav);
        WaveformView.ShowLoopMarkers = swav.Loop && WaveformView.LoopEndSample > WaveformView.LoopStartSample;
        WaveformView.PlayheadSample = 0;
    }

    private void OnPlayClicked(object? sender, RoutedEventArgs e)
    {
        EnsurePlayer();
        if (_provider is null || _waveOut is null)
            return;

        _provider.Seek(0);
        _waveOut.Play();
        if (!_uiTimer.IsEnabled)
            _uiTimer.Start();
    }

    private void OnStopClicked(object? sender, RoutedEventArgs e)
    {
        _waveOut?.Stop();
        _uiTimer.Stop();
        _provider?.Seek(0);
        WaveformView.PlayheadSample = 0;
        _vm.UpdatePlayheadText(0);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void EnsurePlayer()
    {
        if (_provider is not null && _waveOut is not null)
            return;
        if (_swav.PCM16.Length == 0)
            return;

        int sampleRate = _swav.SampleRate > 0 ? _swav.SampleRate : 32768;
        int loopStart = _swav.Loop ? Math.Clamp(_swav.LoopStartSample, 0, _swav.PCM16.Length) : 0;
        int loopEnd = _swav.Loop ? Math.Clamp(GetLoopEndSample(_swav), 0, _swav.PCM16.Length) : _swav.PCM16.Length;
        if (loopEnd <= loopStart)
        {
            loopStart = 0;
            loopEnd = _swav.PCM16.Length;
        }

        _provider = new SwavPlaybackProvider(_swav.PCM16, sampleRate, loopStart, loopEnd)
        {
            PlaybackLoopEnabled = _vm.LoopPlayback
        };

        _waveOut = new WaveOutEvent();
        _waveOut.Init(_provider);
        _waveOut.PlaybackStopped += OnPlaybackStopped;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Vm.LoopPlayback) && _provider is not null)
            _provider.PlaybackLoopEnabled = _vm.LoopPlayback;
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        if (_provider is null)
            return;

        int playhead = _provider.PositionSample;
        WaveformView.PlayheadSample = playhead;
        _vm.UpdatePlayheadText(playhead);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_provider is null)
                return;

            int playhead = _provider.PositionSample;
            WaveformView.PlayheadSample = playhead;
            _vm.UpdatePlayheadText(playhead);
            _uiTimer.Stop();
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        _vm.PropertyChanged -= OnVmPropertyChanged;

        if (_waveOut is not null)
        {
            _waveOut.PlaybackStopped -= OnPlaybackStopped;
            _waveOut.Stop();
            _waveOut.Dispose();
            _waveOut = null;
        }

        _provider = null;
        base.OnClosed(e);
    }

    private static int GetLoopEndSample(global::NitroSynth.App.SWAV swav)
    {
        if (swav.LoopEndSample > 0)
            return swav.LoopEndSample;
        return swav.PCM16.Length;
    }

    private sealed class Vm : INotifyPropertyChanged
    {
        private readonly int _sampleRate;

        public Vm(global::NitroSynth.App.SWAV swav, int swarId, int swavId, string title)
        {
            Title = title;
            SwarIdText = swarId >= 0 ? swarId.ToString("D3") : "-";
            SwavIdText = swavId >= 0 ? swavId.ToString("D3") : "-";
            EncodingText = swav.Encoding switch
            {
                global::NitroSynth.App.SwavEncoding.Pcm8 => "PCM8",
                global::NitroSynth.App.SwavEncoding.Pcm16 => "PCM16",
                global::NitroSynth.App.SwavEncoding.ImaAdpcm => "IMA-ADPCM",
                _ => ((byte)swav.Encoding).ToString()
            };

            _sampleRate = swav.SampleRate > 0 ? swav.SampleRate : 32768;
            SampleRateText = $"{_sampleRate} Hz";
            SampleCountText = swav.PCM16.Length.ToString();
            DurationText = FormatTime(swav.PCM16.Length, _sampleRate);

            int loopStart = Math.Clamp(swav.LoopStartSample, 0, swav.PCM16.Length);
            int loopEnd = Math.Clamp(GetLoopEndSample(swav), 0, swav.PCM16.Length);
            LoopRangeText = swav.Loop && loopEnd > loopStart
                ? $"{loopStart}..{loopEnd}"
                : "-";

            LoopPlayback = swav.Loop && loopEnd > loopStart;
            UpdatePlayheadText(0);
        }

        public string Title { get; }
        public string SwarIdText { get; }
        public string SwavIdText { get; }
        public string EncodingText { get; }
        public string SampleRateText { get; }
        public string SampleCountText { get; }
        public string DurationText { get; }
        public string LoopRangeText { get; }

        private bool _loopPlayback;
        public bool LoopPlayback
        {
            get => _loopPlayback;
            set
            {
                if (_loopPlayback == value)
                    return;
                _loopPlayback = value;
                OnPropertyChanged();
            }
        }

        private string _playheadText = "0.000s";
        public string PlayheadText
        {
            get => _playheadText;
            private set
            {
                if (_playheadText == value)
                    return;
                _playheadText = value;
                OnPropertyChanged();
            }
        }

        public void UpdatePlayheadText(int sample)
        {
            string left = FormatTime(sample, _sampleRate);
            PlayheadText = $"{left} / {DurationText}";
        }

        private static string FormatTime(int samples, int sampleRate)
        {
            if (sampleRate <= 0)
                return "0.000s";
            double sec = Math.Max(0, samples) / (double)sampleRate;
            return $"{sec:0.000}s";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class SwavPlaybackProvider : IWaveProvider
    {
        private readonly object _sync = new();
        private readonly short[] _samples;
        private readonly int _loopStart;
        private readonly int _loopEnd;
        private int _positionSample;
        private bool _playbackLoopEnabled;

        public SwavPlaybackProvider(short[] samples, int sampleRate, int loopStart, int loopEnd)
        {
            _samples = samples ?? Array.Empty<short>();
            int safeRate = sampleRate > 0 ? sampleRate : 32768;
            WaveFormat = new WaveFormat(safeRate, 16, 1);

            _loopStart = Math.Clamp(loopStart, 0, _samples.Length);
            _loopEnd = Math.Clamp(loopEnd, 0, _samples.Length);
            if (_loopEnd <= _loopStart)
            {
                _loopStart = 0;
                _loopEnd = _samples.Length;
            }
        }

        public WaveFormat WaveFormat { get; }

        public bool PlaybackLoopEnabled
        {
            get
            {
                lock (_sync)
                {
                    return _playbackLoopEnabled;
                }
            }
            set
            {
                lock (_sync)
                {
                    _playbackLoopEnabled = value;
                }
            }
        }

        public int PositionSample
        {
            get
            {
                lock (_sync)
                {
                    return _positionSample;
                }
            }
        }

        public void Seek(int sample)
        {
            lock (_sync)
            {
                _positionSample = Math.Clamp(sample, 0, _samples.Length);
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                if (_samples.Length == 0 || count <= 0)
                    return 0;

                int samplesRequested = count / 2;
                int samplesWritten = 0;

                while (samplesWritten < samplesRequested)
                {
                    if (_positionSample >= _samples.Length)
                    {
                        if (_playbackLoopEnabled)
                        {
                            _positionSample = _loopStart;
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (_playbackLoopEnabled && _positionSample >= _loopEnd)
                    {
                        _positionSample = _loopStart;
                        continue;
                    }

                    if (_positionSample >= _samples.Length)
                        break;

                    short sample = _samples[_positionSample++];
                    int outIndex = offset + samplesWritten * 2;
                    buffer[outIndex] = (byte)(sample & 0xFF);
                    buffer[outIndex + 1] = (byte)((sample >> 8) & 0xFF);
                    samplesWritten++;
                }

                return samplesWritten * 2;
            }
        }
    }
}
