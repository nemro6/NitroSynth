using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NAudio.Wave;
using NitroSynth.App.Audio;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    public sealed class AudioOutputOption
    {
        public string Id { get; }
        public string Name { get; }

        public AudioOutputOption(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public override string ToString() => Name;
    }

    public sealed class AudioBufferSizeOption
    {
        public int LatencyMs { get; }
        public string Name { get; }

        public AudioBufferSizeOption(int latencyMs)
        {
            LatencyMs = latencyMs;
            Name = $"{latencyMs} ms";
        }

        public override string ToString() => Name;
    }

    public sealed class AudioSampleRateOption
    {
        public int Rate { get; }
        public string Name { get; }

        public AudioSampleRateOption(int rate, string? name = null)
        {
            Rate = rate;
            Name = name ?? $"{rate} Hz";
        }

        public override string ToString() => Name;
    }

    public ObservableCollection<AudioOutputOption> AudioOutOptions { get; } = new();
    public ObservableCollection<AudioBufferSizeOption> AudioBufferSizeOptions { get; } = new()
    {
        new AudioBufferSizeOption(48),
        new AudioBufferSizeOption(64),
        new AudioBufferSizeOption(96),
        new AudioBufferSizeOption(128),
        new AudioBufferSizeOption(192),
        new AudioBufferSizeOption(256),
        new AudioBufferSizeOption(384),
        new AudioBufferSizeOption(512)
    };
    public ObservableCollection<AudioSampleRateOption> AudioSampleRateOptions { get; } = new()
    {
        new AudioSampleRateOption(16000),
        new AudioSampleRateOption(22050),
        new AudioSampleRateOption(32000),
        new AudioSampleRateOption(32768, "32768 Hz (Nintendo DS)"),
        new AudioSampleRateOption(44100),
        new AudioSampleRateOption(48000)
    };

    private AudioOutputOption? _selectedAudioOut;
    private bool _suppressAudioOutApply;
    private bool _suppressMonoOutputApply;
    private bool _isApplyingAudioOut;
    private AudioBufferSizeOption? _selectedAudioBufferSize;
    private bool _suppressAudioBufferApply;
    private bool _isApplyingAudioBuffer;
    private AudioSampleRateOption? _selectedAudioSampleRate;
    private bool _suppressAudioSampleRateApply;
    private bool _isApplyingAudioSampleRate;
    private string _audioOutConnectedName = "(none)";
    private bool _isMonoOutput;

    public AudioOutputOption? SelectedAudioOut
    {
        get => _selectedAudioOut;
        set
        {
            if (!SetField(ref _selectedAudioOut, value)) return;
            if (value is not null)
            {
                _preferredAudioOutputId = value.Id;
                _preferredAudioOutputName = value.Name;
                SaveAppSettings();
            }

            if (!_suppressAudioOutApply)
                _ = ApplySelectedAudioOutputAsync();
        }
    }

    public string AudioOutStatus => $"AUDIO OUT: {_audioOutConnectedName}";

    public AudioBufferSizeOption? SelectedAudioBufferSize
    {
        get => _selectedAudioBufferSize;
        set
        {
            if (!SetField(ref _selectedAudioBufferSize, value))
                return;

            if (value is not null)
            {
                _preferredAudioBufferLatencyMs = value.LatencyMs;
                SaveAppSettings();
            }

            if (!_suppressAudioBufferApply)
                _ = ApplySelectedAudioBufferSizeAsync();
        }
    }

    public AudioSampleRateOption? SelectedAudioSampleRate
    {
        get => _selectedAudioSampleRate;
        set
        {
            if (!SetField(ref _selectedAudioSampleRate, value))
                return;

            if (value is not null)
            {
                _preferredAudioSampleRate = value.Rate;
                SaveAppSettings();
            }

            if (!_suppressAudioSampleRateApply)
                _ = ApplySelectedAudioSampleRateAsync();
        }
    }

    public bool IsMonoOutput
    {
        get => _isMonoOutput;
        set
        {
            if (!SetField(ref _isMonoOutput, value))
                return;

            AudioEngine.Instance.SetMonoOutput(value);
            if (_suppressMonoOutputApply)
                return;

            _preferredMonoOutput = value;
            SaveAppSettings();
        }
    }

    public void InitializeAudioOutputPreference()
    {
        EnsureAppSettingsLoaded();
        _suppressMonoOutputApply = true;
        _suppressAudioBufferApply = true;
        _suppressAudioSampleRateApply = true;
        try
        {
            IsMonoOutput = _preferredMonoOutput;
            SelectedAudioBufferSize = FindPreferredAudioBufferSize();
            SelectedAudioSampleRate = FindPreferredAudioSampleRate();
        }
        finally
        {
            _suppressMonoOutputApply = false;
            _suppressAudioBufferApply = false;
            _suppressAudioSampleRateApply = false;
        }

        if (SelectedAudioSampleRate is not null)
            _ = ApplySelectedAudioSampleRateAsync();
        if (SelectedAudioBufferSize is not null)
            _ = ApplySelectedAudioBufferSizeAsync();
    }

    private AudioOutputOption? FindPreferredAudioOutput()
    {
        if (AudioOutOptions.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(_preferredAudioOutputName))
        {
            var byName = AudioOutOptions.FirstOrDefault(o => o.Name == _preferredAudioOutputName);
            if (byName is not null)
                return byName;
        }

        if (!string.IsNullOrWhiteSpace(_preferredAudioOutputId))
        {
            var byId = AudioOutOptions.FirstOrDefault(o => o.Id == _preferredAudioOutputId);
            if (byId is not null)
                return byId;
        }

        return null;
    }

    private AudioBufferSizeOption? FindPreferredAudioBufferSize()
    {
        if (AudioBufferSizeOptions.Count == 0)
            return null;

        var exact = AudioBufferSizeOptions.FirstOrDefault(o => o.LatencyMs == _preferredAudioBufferLatencyMs);
        if (exact is not null)
            return exact;

        return AudioBufferSizeOptions
            .OrderBy(o => Math.Abs(o.LatencyMs - _preferredAudioBufferLatencyMs))
            .FirstOrDefault();
    }

    private AudioSampleRateOption? FindPreferredAudioSampleRate()
    {
        if (AudioSampleRateOptions.Count == 0)
            return null;

        var exact = AudioSampleRateOptions.FirstOrDefault(o => o.Rate == _preferredAudioSampleRate);
        if (exact is not null)
            return exact;

        return AudioSampleRateOptions
            .OrderBy(o => Math.Abs(o.Rate - _preferredAudioSampleRate))
            .FirstOrDefault();
    }

    public Task RefreshAudioOutputsAsync()
    {
        EnsureAppSettingsLoaded();
        AudioOutOptions.Clear();
        AudioOutOptions.Add(new AudioOutputOption("-1", "(System Default)"));

        int count = WaveInterop.waveOutGetNumDevs();
        int capsSize = Marshal.SizeOf<WaveOutCapabilities>();
        for (int device = 0; device < count; device++)
        {
            if (WaveInterop.waveOutGetDevCaps((IntPtr)device, out var caps, capsSize) == NAudio.MmResult.NoError)
                AudioOutOptions.Add(new AudioOutputOption(device.ToString(), caps.ProductName));
            else
                AudioOutOptions.Add(new AudioOutputOption(device.ToString(), $"Device {device}"));
        }

        if (AudioOutOptions.Count == 0)
        {
            SelectedAudioOut = null;
            _audioOutConnectedName = "(none)";
            OnPropertyChanged(nameof(AudioOutStatus));
            return Task.CompletedTask;
        }

        _suppressAudioOutApply = true;
        try
        {
            var selected = FindPreferredAudioOutput();
            if (selected is null && SelectedAudioOut is { } current)
            {
                selected = AudioOutOptions.FirstOrDefault(o => o.Name == current.Name) ??
                           AudioOutOptions.FirstOrDefault(o => o.Id == current.Id);
            }

            SelectedAudioOut = selected ?? AudioOutOptions[0];
        }
        finally
        {
            _suppressAudioOutApply = false;
        }

        return ApplySelectedAudioOutputAsync();
    }

    private Task ApplySelectedAudioOutputAsync()
    {
        if (_isApplyingAudioOut)
            return Task.CompletedTask;
        if (SelectedAudioOut is null)
            return Task.CompletedTask;
        if (!int.TryParse(SelectedAudioOut.Id, out var deviceId))
            return Task.CompletedTask;

        _isApplyingAudioOut = true;
        try
        {
            if (AudioEngine.Instance.TrySetOutputDevice(deviceId, out var error))
            {
                _audioOutConnectedName = SelectedAudioOut.Name;
                OnPropertyChanged(nameof(AudioOutStatus));
                StatusMessage = $"Audio output connected: {_audioOutConnectedName}";
            }
            else
            {
                StatusMessage = $"Failed to connect audio output: {error}";
            }
        }
        finally
        {
            _isApplyingAudioOut = false;
        }

        return Task.CompletedTask;
    }

    private Task ApplySelectedAudioBufferSizeAsync()
    {
        if (_isApplyingAudioBuffer)
            return Task.CompletedTask;
        if (SelectedAudioBufferSize is null)
            return Task.CompletedTask;

        _isApplyingAudioBuffer = true;
        try
        {
            if (AudioEngine.Instance.TrySetOutputBufferLatency(SelectedAudioBufferSize.LatencyMs, out var error))
            {
                StatusMessage = $"Audio buffer size set: {SelectedAudioBufferSize.Name}";
            }
            else
            {
                StatusMessage = $"Failed to set audio buffer size: {error}";
            }
        }
        finally
        {
            _isApplyingAudioBuffer = false;
        }

        return Task.CompletedTask;
    }

    private Task ApplySelectedAudioSampleRateAsync()
    {
        if (_isApplyingAudioSampleRate)
            return Task.CompletedTask;
        if (SelectedAudioSampleRate is null)
            return Task.CompletedTask;

        _isApplyingAudioSampleRate = true;
        try
        {
            if (AudioEngine.Instance.TrySetOutputSampleRate(SelectedAudioSampleRate.Rate, out var error))
            {
                StatusMessage = $"Playback sample rate set: {SelectedAudioSampleRate.Name}";
            }
            else
            {
                StatusMessage = $"Failed to set playback sample rate: {error}";
            }
        }
        finally
        {
            _isApplyingAudioSampleRate = false;
        }

        return Task.CompletedTask;
    }
}
