using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using NitroSynth.App.Audio;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    public sealed class RenderFpsOption
    {
        public int Fps { get; }
        public string Name { get; }

        public RenderFpsOption(int fps, string? name = null)
        {
            Fps = fps;
            Name = name ?? fps.ToString();
        }

        public override string ToString() => Name;
    }

    private double _masterVolume = 0.5;
    private bool _suppressMeterRenderFpsApply;
    private DispatcherTimer? _meterTimer;

    
    private double _levelAttackMs = 1;   
    private double _levelReleaseMs = 100;  

    private double _peakHoldSec = 0.1;   
    private double _peakDecayPerSec = 0.5; 
    private bool _suppressMeterDecayApply;
    private double _masterMeterDecayMs = 100;
    private double _mixerMeterDecayMs = 100;
    private RenderFpsOption? _selectedRenderFps;

    public ObservableCollection<RenderFpsOption> RenderFpsOptions { get; } = new()
    {
        new RenderFpsOption(24),
        new RenderFpsOption(30),
        new RenderFpsOption(60),
        new RenderFpsOption(120),
        new RenderFpsOption(144),
        new RenderFpsOption(240),
        new RenderFpsOption(280),
        new RenderFpsOption(320),
        new RenderFpsOption(360),
        new RenderFpsOption(400),
        new RenderFpsOption(480),
        new RenderFpsOption(510),
        new RenderFpsOption(540),
        new RenderFpsOption(610),
        new RenderFpsOption(0, "Unlimited")
    };

    public RenderFpsOption? SelectedRenderFps
    {
        get => _selectedRenderFps;
        set
        {
            if (!SetField(ref _selectedRenderFps, value))
                return;
            if (value is null)
                return;

            _preferredMeterUpdateFps = value.Fps;
            ApplyMeterPollingRate(value.Fps);

            if (_suppressMeterRenderFpsApply)
                return;

            SaveAppSettings();
        }
    }

    public double MasterMeterDecayMs
    {
        get => _masterMeterDecayMs;
        set
        {
            var normalized = Math.Round(Math.Clamp(value, 0.0, 200.0), 1);
            if (!SetField(ref _masterMeterDecayMs, normalized))
                return;

            _levelReleaseMs = normalized;
            OnPropertyChanged(nameof(MasterMeterDecayMsText));

            if (_suppressMeterDecayApply)
                return;

            _preferredMasterMeterDecayMs = normalized;
            SaveAppSettings();
        }
    }

    public string MasterMeterDecayMsText => $"{MasterMeterDecayMs:0.0} ms";

    public double MixerMeterDecayMs
    {
        get => _mixerMeterDecayMs;
        set
        {
            var normalized = Math.Round(Math.Clamp(value, 0.0, 200.0), 1);
            if (!SetField(ref _mixerMeterDecayMs, normalized))
                return;

            ApplyMixerMeterDecayToAllStrips(normalized);
            OnPropertyChanged(nameof(MixerMeterDecayMsText));

            if (_suppressMeterDecayApply)
                return;

            _preferredMixerMeterDecayMs = normalized;
            SaveAppSettings();
        }
    }

    public string MixerMeterDecayMsText => $"{MixerMeterDecayMs:0.0} ms";

    public void InitializeMeterPreference()
    {
        EnsureAppSettingsLoaded();
        _suppressMeterDecayApply = true;
        _suppressMeterRenderFpsApply = true;
        try
        {
            MasterMeterDecayMs = _preferredMasterMeterDecayMs;
            MixerMeterDecayMs = _preferredMixerMeterDecayMs;
            SelectedRenderFps = FindPreferredRenderFpsOption();
        }
        finally
        {
            _suppressMeterDecayApply = false;
            _suppressMeterRenderFpsApply = false;
        }
    }

    private void ApplyMixerMeterDecayToAllStrips(double decayMs)
    {
        for (int ch = 0; ch < MixerStrips.Count; ch++)
            MixerStrips[ch].SetMeterReleaseMs(decayMs);
    }

    public double MasterVolume
    {
        get => _masterVolume;
        set
        {
            var v = Math.Clamp(value, 0.0, 1.0);
            if (!SetField(ref _masterVolume, v)) return;

            
            AudioEngine.Instance.SetMasterVolume((float)v);

            OnPropertyChanged(nameof(MasterVolumeText));
        }
    }

    public string MasterVolumeText => $"{(int)Math.Round(MasterVolume * 100)}%";

    private string _mixerNotePcmText = "PCM 00/16";
    public string MixerNotePcmText
    {
        get => _mixerNotePcmText;
        private set => SetField(ref _mixerNotePcmText, value);
    }

    private string _mixerNotePsgText = "PSG 00/06";
    public string MixerNotePsgText
    {
        get => _mixerNotePsgText;
        private set => SetField(ref _mixerNotePsgText, value);
    }

    private string _mixerNoteNoiseText = "Noise 00/02";
    public string MixerNoteNoiseText
    {
        get => _mixerNoteNoiseText;
        private set => SetField(ref _mixerNoteNoiseText, value);
    }

    private string _mixerNoteTotalText = "Total 00/16";
    public string MixerNoteTotalText
    {
        get => _mixerNoteTotalText;
        private set => SetField(ref _mixerNoteTotalText, value);
    }

    private string _mixerVoicePcmText = "PCM 00/16";
    public string MixerVoicePcmText
    {
        get => _mixerVoicePcmText;
        private set => SetField(ref _mixerVoicePcmText, value);
    }

    private string _mixerVoicePsgText = "PSG 00/06";
    public string MixerVoicePsgText
    {
        get => _mixerVoicePsgText;
        private set => SetField(ref _mixerVoicePsgText, value);
    }

    private string _mixerVoiceNoiseText = "Noise 00/02";
    public string MixerVoiceNoiseText
    {
        get => _mixerVoiceNoiseText;
        private set => SetField(ref _mixerVoiceNoiseText, value);
    }

    private string _mixerVoiceTotalText = "Total 00/16";
    public string MixerVoiceTotalText
    {
        get => _mixerVoiceTotalText;
        private set => SetField(ref _mixerVoiceTotalText, value);
    }

    public ObservableCollection<string> MixerChannelSlotTexts { get; } = CreateInitialMixerChannelSlotTexts();

    private double _masterMeterLevel;

    private double _masterMeterLevelL;
    public double MasterMeterLevelL { get => _masterMeterLevelL; private set => SetField(ref _masterMeterLevelL, value); }

    private double _masterMeterLevelR;
    public double MasterMeterLevelR { get => _masterMeterLevelR; private set => SetField(ref _masterMeterLevelR, value); }

    private double _masterMeterPeakL;
    public double MasterMeterPeakL { get => _masterMeterPeakL; private set => SetField(ref _masterMeterPeakL, value); }

    private double _masterMeterPeakR;
    public double MasterMeterPeakR { get => _masterMeterPeakR; private set => SetField(ref _masterMeterPeakR, value); }

    private bool _masterMeterClipL;
    public bool MasterMeterClipL { get => _masterMeterClipL; private set => SetField(ref _masterMeterClipL, value); }

    private bool _masterMeterClipR;
    public bool MasterMeterClipR { get => _masterMeterClipR; private set => SetField(ref _masterMeterClipR, value); }

    private double _levelDisplayL, _levelDisplayR;

    private double _peakHoldL, _peakHoldR;
    private double _peakHoldRemainSecL, _peakHoldRemainSecR;

    public double MasterMeterLevel
    {
        get => _masterMeterLevel;
        private set => SetField(ref _masterMeterLevel, value);
    }

    private double _masterMeterPeak;
    public double MasterMeterPeak
    {
        get => _masterMeterPeak;
        private set => SetField(ref _masterMeterPeak, value);
    }

    private bool _masterMeterClip;
    public bool MasterMeterClip
    {
        get => _masterMeterClip;
        private set => SetField(ref _masterMeterClip, value);
    }

    private readonly Stopwatch _meterSw = Stopwatch.StartNew();
    private TimeSpan _lastMeterTs;
    
    private RenderFpsOption? FindPreferredRenderFpsOption()
    {
        if (RenderFpsOptions.Count == 0)
            return null;

        for (int i = 0; i < RenderFpsOptions.Count; i++)
        {
            if (RenderFpsOptions[i].Fps == _preferredMeterUpdateFps)
                return RenderFpsOptions[i];
        }

        if (_preferredMeterUpdateFps <= 0)
        {
            for (int i = 0; i < RenderFpsOptions.Count; i++)
            {
                if (RenderFpsOptions[i].Fps == 0)
                    return RenderFpsOptions[i];
            }
        }

        RenderFpsOption? nearest = null;
        int bestDelta = int.MaxValue;
        for (int i = 0; i < RenderFpsOptions.Count; i++)
        {
            int fps = RenderFpsOptions[i].Fps;
            if (fps <= 0)
                continue;

            int delta = Math.Abs(fps - _preferredMeterUpdateFps);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                nearest = RenderFpsOptions[i];
            }
        }

        return nearest ?? RenderFpsOptions[0];
    }

    private static TimeSpan ResolveMeterPollingInterval(int fps)
    {
        if (fps <= 0)
            return TimeSpan.Zero;

        return TimeSpan.FromSeconds(1.0 / fps);
    }

    private void ApplyMeterPollingRate(int fps)
    {
        if (_meterTimer is null)
            return;

        _meterTimer.Interval = ResolveMeterPollingInterval(fps);
    }

    private void StartMasterMeterPolling()
    {
        if (_meterTimer is not null)
            return;

        _meterTimer = new DispatcherTimer
        {
            Interval = ResolveMeterPollingInterval(SelectedRenderFps?.Fps ?? _preferredMeterUpdateFps)
        };

        _lastMeterTs = _meterSw.Elapsed;

        _meterTimer.Tick += (_, __) =>
        {
            var now = _meterSw.Elapsed;
            var dt = (now - _lastMeterTs).TotalSeconds;
            _lastMeterTs = now;

            var m = AudioEngine.Instance.GetMasterMeterLR();

            
            double rawL = Math.Clamp(m.LevelL, 0.0, 1.0);
            double attackAlpha = _levelAttackMs <= 0.0
                ? 1.0
                : 1.0 - Math.Exp(-dt / (_levelAttackMs / 1000.0));
            double releaseAlpha = _levelReleaseMs <= 0.0
                ? 1.0
                : 1.0 - Math.Exp(-dt / (_levelReleaseMs / 1000.0));
            double aL = (rawL > _levelDisplayL) ? attackAlpha : releaseAlpha;
            _levelDisplayL += (rawL - _levelDisplayL) * aL;
            MasterMeterLevelL = _levelDisplayL;

            
            double rawR = Math.Clamp(m.LevelR, 0.0, 1.0);
            double aR = (rawR > _levelDisplayR) ? attackAlpha : releaseAlpha;
            _levelDisplayR += (rawR - _levelDisplayR) * aR;
            MasterMeterLevelR = _levelDisplayR;

            
            double pL = Math.Clamp(m.PeakL, 0.0, 1.0);
            if (pL >= _peakHoldL) { _peakHoldL = pL; _peakHoldRemainSecL = _peakHoldSec; }
            else
            {
                if (_peakHoldRemainSecL > 0) _peakHoldRemainSecL -= dt;
                else _peakHoldL = Math.Max(0.0, _peakHoldL - _peakDecayPerSec * dt);
            }
            MasterMeterPeakL = _peakHoldL;

            
            double pR = Math.Clamp(m.PeakR, 0.0, 1.0);
            if (pR >= _peakHoldR) { _peakHoldR = pR; _peakHoldRemainSecR = _peakHoldSec; }
            else
            {
                if (_peakHoldRemainSecR > 0) _peakHoldRemainSecR -= dt;
                else _peakHoldR = Math.Max(0.0, _peakHoldR - _peakDecayPerSec * dt);
            }
            MasterMeterPeakR = _peakHoldR;

            
            MasterMeterClipL = m.ClipL;
            MasterMeterClipR = m.ClipR;

            
            for (int ch = 0; ch < MixerStrips.Count; ch++)
            {
                var cm = AudioEngine.Instance.GetChannelMeter(ch);
                MixerStrips[ch].UpdateMeter(
                    rawLevel: cm.Level,
                    rawPeak: cm.Peak,
                    rawClip: cm.Clip,
                    dt: dt
                );
            }

            var usage = AudioEngine.Instance.GetVoiceUsage();
            MixerNotePcmText = FormatUsageText("PCM", usage.NotePcm, 16);
            MixerNotePsgText = FormatUsageText("PSG", usage.NotePsg, 6);
            MixerNoteNoiseText = FormatUsageText("Noise", usage.NoteNoise, 2);
            MixerNoteTotalText = FormatUsageText("Total", usage.NoteTotal, 16);

            MixerVoicePcmText = FormatUsageText("PCM", usage.Pcm, 16);
            MixerVoicePsgText = FormatUsageText("PSG", usage.Psg, 6);
            MixerVoiceNoiseText = FormatUsageText("Noise", usage.Noise, 2);
            MixerVoiceTotalText = FormatUsageText("Total", usage.Total, 16);

            ushort audibleMask = ResolveEffectiveMixerAudibleMask();
            UpdateMixerChannelSlotTexts(audibleMask, usage);
            UpdateMixerChannelGateActivity(audibleMask, usage.SlotActiveMask, usage.SlotTrackNibbles);
            UpdateMixerStripOutputChannelDisplays(usage, audibleMask);
            UpdateMixerStripIssueLogs(usage);

        };

        _meterTimer.Start();
    }
    public void ResetMidi()
    {
        AudioEngine.Instance.ResetMidiAllChannels();
        ClearPianoMidiNotes();

        foreach (var s in MixerStrips)
        {
            s.Volume = 127;      
            s.Volume2 = 127;
            s.Velocity = 0;
            s.ProgramId = 0;
            s.Pan = 0;           
            s.Modulation = 0;    
            s.ModType = 0;
            s.ModSpeed = 16;
            s.ModRange = 1;
            s.ModDelay = 0;
            s.PitchBend = 0;
            s.BendRange = 2;
            s.Portamento = 0; 
            s.PortaEnabled = false;
            s.ActiveNote = -1;
            s.Level = 0;
            s.InstrumentType = "-";
            s.IssueLog = "-";
        }
    }

    private static string FormatUsageText(string label, int value, int max)
    {
        int current = Math.Clamp(value, 0, max);
        return $"{label} {current:D2}/{max:D2}";
    }

    private static ObservableCollection<string> CreateInitialMixerChannelSlotTexts()
    {
        var cells = new ObservableCollection<string>();
        for (int slot = 0; slot < 16; slot++)
            cells.Add($"{slot:X1}:----");

        return cells;
    }

    private void UpdateMixerChannelSlotTexts(ushort audibleMask, AudioEngine.VoiceUsageSnapshot usage)
    {
        for (int slot = 0; slot < 16; slot++)
        {
            bool audibleEnabled = (audibleMask & (1 << slot)) != 0;
            bool slotActive = (usage.SlotActiveMask & (1 << slot)) != 0;
            string cellText = FormatChannelSlotText(
                slot,
                audibleEnabled,
                slotActive,
                usage.SlotTrackNibbles,
                usage.SlotMidiNotesLo,
                usage.SlotMidiNotesHi);
            if (MixerChannelSlotTexts[slot] != cellText)
                MixerChannelSlotTexts[slot] = cellText;
        }
    }

    private static string FormatChannelSlotText(
        int slot,
        bool audibleEnabled,
        bool slotActive,
        ulong slotTrackNibbles,
        ulong slotMidiNotesLo,
        ulong slotMidiNotesHi)
    {
        string slotHex = slot.ToString("X1");
        if (!audibleEnabled || !slotActive)
            return $"{slotHex}:----";

        int track = (int)((slotTrackNibbles >> (slot * 4)) & 0xFUL);
        int note = slot < 8
            ? (int)((slotMidiNotesLo >> (slot * 8)) & 0xFFUL)
            : (int)((slotMidiNotesHi >> ((slot - 8) * 8)) & 0xFFUL);

        return $"{slotHex}:{track:X1}-{note:X2}";
    }

    private void UpdateMixerStripOutputChannelDisplays(AudioEngine.VoiceUsageSnapshot usage, ushort audibleMask)
    {
        int stripCount = MixerStrips.Count;
        for (int track = 0; track < stripCount; track++)
        {
            MixerStrips[track].OutputChannelDisplay = "-";
            MixerStrips[track].OutputAudibleActive = false;
        }

        for (int slot = 0; slot < 16; slot++)
        {
            if ((usage.SlotActiveMask & (1 << slot)) == 0)
                continue;

            int track = (int)((usage.SlotTrackNibbles >> (slot * 4)) & 0xFUL);
            if ((uint)track >= (uint)stripCount)
                continue;

            bool slotAudible = (audibleMask & (1 << slot)) != 0;
            if (slotAudible && MixerStrips[track].OutputEnabled)
                MixerStrips[track].OutputAudibleActive = true;
            if (MixerStrips[track].OutputChannelDisplay == "-")
                MixerStrips[track].OutputChannelDisplay = slot.ToString("X2");
        }
    }

    private void UpdateMixerStripIssueLogs(AudioEngine.VoiceUsageSnapshot usage)
    {
        ushort stealMask = usage.TrackStealMask;
        if (stealMask == 0)
            return;

        int stripCount = Math.Min(16, MixerStrips.Count);
        for (int track = 0; track < stripCount; track++)
        {
            if ((stealMask & (1 << track)) != 0)
                MixerStrips[track].IssueLog = "Cut: voice steal";
        }
    }

}
