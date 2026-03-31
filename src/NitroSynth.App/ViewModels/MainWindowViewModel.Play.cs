using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using NAudio.Midi;
using NAudio.Wave;
using NitroSynth.App.Audio;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    private static bool IsLightThemeVariant()
    {
        var app = Application.Current;
        if (app is null)
            return false;

        if (app.RequestedThemeVariant == ThemeVariant.Light)
            return true;
        if (app.RequestedThemeVariant == ThemeVariant.Dark)
            return false;

        return app.ActualThemeVariant == ThemeVariant.Light;
    }

    public sealed class MixerChannelGate : INotifyPropertyChanged
    {
        private static readonly IBrush DarkInactiveBorderBrush = new SolidColorBrush(Color.Parse("#353535"));
        private static readonly IBrush DarkDisabledBorderBrush = new SolidColorBrush(Color.Parse("#303030"));
        private static readonly IBrush LightInactiveBorderBrush = new SolidColorBrush(Color.Parse("#DCDCDC"));
        private static readonly IBrush LightDisabledBorderBrush = new SolidColorBrush(Color.Parse("#DFDFDF"));

        private readonly Action<int, bool> _onChanged;
        private bool _isChannelEnabled = true;
        private bool _isPlayerAvailable = true;
        private bool _isAudibleActive;
        private IBrush _audibleTrackBrush;

        public MixerChannelGate(int channelIndex, IBrush channelBrush, Action<int, bool> onChanged)
        {
            ChannelIndex = Math.Clamp(channelIndex, 0, 15);
            ChannelBrush = channelBrush;
            _audibleTrackBrush = channelBrush;
            _onChanged = onChanged;
        }

        public int ChannelIndex { get; }
        public IBrush ChannelBrush { get; }
        public string Label => ChannelIndex.ToString("X1");
        public double UiOpacity => _isPlayerAvailable ? 1.0 : 0.45;
        public IBrush ToggleBorderBrush => !_isPlayerAvailable
            ? (IsLightThemeVariant() ? LightDisabledBorderBrush : DarkDisabledBorderBrush)
            : (_isAudibleActive ? _audibleTrackBrush : (IsLightThemeVariant() ? LightInactiveBorderBrush : DarkInactiveBorderBrush));
        public bool IsAudibleActive
        {
            get => _isAudibleActive;
            private set
            {
                if (_isAudibleActive == value)
                    return;

                _isAudibleActive = value;
                OnP();
                OnP(nameof(ToggleBorderBrush));
            }
        }

        public bool IsPlayerAvailable
        {
            get => _isPlayerAvailable;
            private set
            {
                if (_isPlayerAvailable == value)
                    return;

                _isPlayerAvailable = value;
                OnP();
                OnP(nameof(UiOpacity));
                OnP(nameof(ToggleBorderBrush));
            }
        }

        public bool IsChannelEnabled
        {
            get => _isChannelEnabled;
            set
            {
                if (_isChannelEnabled == value)
                    return;

                _isChannelEnabled = value;
                OnP();
                _onChanged(ChannelIndex, value);
            }
        }

        public void SetPlayerAvailable(bool isAvailable)
        {
            IsPlayerAvailable = isAvailable;
        }

        public void SetAudibleActive(bool isActive, IBrush? trackBrush = null)
        {
            if (trackBrush is not null && !ReferenceEquals(_audibleTrackBrush, trackBrush))
            {
                _audibleTrackBrush = trackBrush;
                if (_isAudibleActive)
                    OnP(nameof(ToggleBorderBrush));
            }

            IsAudibleActive = isActive;
        }

        public void NotifyThemeVariantChanged()
        {
            OnP(nameof(ToggleBorderBrush));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnP([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
    }

    public ObservableCollection<MixerChannelGate> MixerChannelGates { get; } = new();

    private void OnMixerChannelGateChanged(int channel, bool isEnabled)
    {
        AudioEngine.Instance.SetHardwareChannelEnabled(channel, isEnabled);
    }

    private ushort ResolveMixerChannelGateMask()
    {
        ushort mask = 0;
        int count = Math.Min(16, MixerChannelGates.Count);
        for (int ch = 0; ch < count; ch++)
        {
            if (MixerChannelGates[ch].IsChannelEnabled)
                mask |= (ushort)(1 << ch);
        }

        return mask;
    }

    private ushort ResolveEffectiveMixerAudibleMask()
    {
        ushort playerMask = ResolveSelectedMixerPlayerChannelMask();
        ushort gateMask = ResolveMixerChannelGateMask();
        return (ushort)(playerMask & gateMask);
    }

    private void UpdateMixerChannelGateAvailability()
    {
        ushort playerMask = ResolveSelectedMixerPlayerChannelMask();
        int count = Math.Min(16, MixerChannelGates.Count);
        for (int ch = 0; ch < count; ch++)
        {
            bool isAvailable = (playerMask & (1 << ch)) != 0;
            MixerChannelGates[ch].SetPlayerAvailable(isAvailable);
        }
    }

    private void UpdateMixerChannelGateActivity(ushort audibleMask, ushort slotActiveMask, ulong slotTrackNibbles)
    {
        int count = Math.Min(16, MixerChannelGates.Count);
        for (int ch = 0; ch < count; ch++)
        {
            bool audibleEnabled = (audibleMask & (1 << ch)) != 0;
            bool slotActive = (slotActiveMask & (1 << ch)) != 0;
            int track = (int)((slotTrackNibbles >> (ch * 4)) & 0xFUL);
            IBrush trackBrush = (uint)track < (uint)PianoChannelBrushes.Count
                ? PianoChannelBrushes[track]
                : MixerChannelGates[ch].ChannelBrush;
            MixerChannelGates[ch].SetAudibleActive(audibleEnabled && slotActive, trackBrush);
        }
    }

    public void HandleMixerChannelGateRightClick(MixerChannelGate? clickedGate)
    {
        if (clickedGate is null || !clickedGate.IsPlayerAvailable)
            return;

        var availableGates = MixerChannelGates
            .Where(g => g.IsPlayerAvailable)
            .ToList();
        if (availableGates.Count == 0)
            return;

        bool allOtherAvailableGatesOff = availableGates
            .Where(g => !ReferenceEquals(g, clickedGate))
            .All(g => !g.IsChannelEnabled);

        if (allOtherAvailableGatesOff)
        {
            foreach (var gate in availableGates)
                gate.IsChannelEnabled = true;
            return;
        }

        clickedGate.IsChannelEnabled = true;
        foreach (var gate in availableGates)
        {
            if (!ReferenceEquals(gate, clickedGate))
                gate.IsChannelEnabled = false;
        }
    }

    public void HandleMixerStripOutputToggleRightClick(MixerStrip? clickedStrip)
    {
        if (clickedStrip is null || MixerStrips.Count == 0)
            return;

        bool allOthersMuted = MixerStrips
            .Where(s => !ReferenceEquals(s, clickedStrip))
            .All(s => s.Mute);

        if (allOthersMuted)
        {
            foreach (var strip in MixerStrips)
                strip.OutputEnabled = true;
            return;
        }

        clickedStrip.OutputEnabled = true;
        foreach (var strip in MixerStrips)
        {
            if (!ReferenceEquals(strip, clickedStrip))
                strip.OutputEnabled = false;
        }
    }

    private void OnMixerStripOutputEnabledChanged(int track, bool isEnabled)
    {
        AudioEngine.Instance.SetTrackOutputEnabled(track, isEnabled);
    }

    private ushort ResolveMixerStripOutputEnabledMask()
    {
        ushort mask = 0;
        int count = Math.Min(16, MixerStrips.Count);
        for (int ch = 0; ch < count; ch++)
        {
            if (MixerStrips[ch].OutputEnabled)
                mask |= (ushort)(1 << ch);
        }

        return mask;
    }

    private readonly struct PlaySpec
    {
        public PlaySpec(SBNK.InstrumentType type, int swarInfoId, int swavId, int baseKey, int attack, int decay, int sustain, int release, int pan)
        { Type = type; SwarInfoId = swarInfoId; SwavId = swavId; BaseKey = baseKey; Attack = attack; Decay = decay; Sustain = sustain; Release = release; Pan = pan; }
        public SBNK.InstrumentType Type { get; }
        public int SwarInfoId { get; }
        public int SwavId { get; }
        public int BaseKey { get; }
        public int Attack { get; }
        public int Decay { get; }
        public int Sustain { get; }
        public int Release { get; }
        public int Pan { get; }
    }

    
    
    
    
    private bool TryResolvePlayableSingle(InstRow row, int midiNote, out PlaySpec spec)
    {
        spec = default;

        
        if (row.Type == SBNK.InstrumentType.Psg)
        {
            spec = new PlaySpec(
                type: SBNK.InstrumentType.Psg,
                swarInfoId: -1,
                swavId: Math.Clamp(row.SwavId, 0, 6), 
                baseKey: Math.Clamp(row.BaseKey, 0, 127),
                attack: row.Attack, decay: row.Decay, sustain: row.Sustain, release: row.Release,
                pan: row.Pan
            );
            return true;
        }

        if (row.Type == SBNK.InstrumentType.Noise)
        {
            spec = new PlaySpec(
                type: SBNK.InstrumentType.Noise,
                swarInfoId: -1,
                swavId: row.SwavId,
                baseKey: Math.Clamp(row.BaseKey, 0, 127),
                attack: row.Attack, decay: row.Decay, sustain: row.Sustain, release: row.Release,
                pan: row.Pan
            );
            return true;
        }

        
        if (row.Type is SBNK.InstrumentType.Pcm or SBNK.InstrumentType.DirectPcm)
        {
            int swarInfoId = ResolveSwarInfoId(row.SwarId);
            if (swarInfoId < 0) return false;

            spec = new PlaySpec(
                type: row.Type,
                swarInfoId: swarInfoId,
                swavId: row.SwavId,
                baseKey: Math.Clamp(row.BaseKey, 0, 127),
                attack: row.Attack, decay: row.Decay, sustain: row.Sustain, release: row.Release,
                pan: row.Pan
            );
            return true;
        }

        
        if (row.Type == SBNK.InstrumentType.DrumSet)
        {
            if (!TryGetDrumSetModel(row.Id, out var ds))
                return false;

            byte note = (byte)midiNote;

            
            if (note < ds.LowKey || note > ds.HighKey)
                return false;

            int index = note - ds.LowKey;
            if (index < 0 || index >= ds.Entries.Count)
                return false;

            var e = ds.Entries[index];

            
            if (e.Type is SBNK.InstrumentType.Null
                    or SBNK.InstrumentType.NullInstrument
                    or SBNK.InstrumentType.Unknown)
                return false;

            int swarInfoId = -1;
            if (e.Type is SBNK.InstrumentType.Pcm or SBNK.InstrumentType.DirectPcm)
            {
                swarInfoId = ResolveSwarInfoId(e.SwarId);
                if (swarInfoId < 0)
                    return false;
            }

            spec = new PlaySpec(
                type: e.Type,
                swarInfoId: swarInfoId,
                swavId: e.SwavId,
                baseKey: Math.Clamp((int)e.Key, 0, 127),
                attack: e.Attack,
                decay: e.Decay,
                sustain: e.Sustain,
                release: e.Release,
                pan: e.Pan
            );
            return true;
        }

        
        if (row.Type == SBNK.InstrumentType.KeySplit)
        {
            if (!TryGetKeySplitModel(row.Id, out var ks)) return false;

            
            int prev = -1;
            SBNK.KeySplit.KeySplitEntry? zone = null;
            int entryIndex = -1;

            for (int i = 0, used = 0; i < ks.SplitKeys.Length; i++)
            {
                byte split = ks.SplitKeys[i];
                if (split == 0) continue;                   
                if (midiNote <= split)
                {
                    
                    if (used < ks.Entries.Count)
                    {
                        zone = ks.Entries[used];
                        entryIndex = used;
                    }
                    break;
                }
                prev = split;
                used++;
                if (i == ks.SplitKeys.Length - 1 && used > 0 && zone is null)
                {
                    
                    zone = ks.Entries[Math.Min(used - 1, ks.Entries.Count - 1)];
                    entryIndex = Math.Min(used - 1, ks.Entries.Count - 1);
                }
            }

            if (zone is null)
            {
                if (ks.Entries.Count == 0) return false;
                zone = ks.Entries[0];
                entryIndex = 0;
            }

            int swarInfoId = -1;
            if (zone.Type is SBNK.InstrumentType.Pcm or SBNK.InstrumentType.DirectPcm)
            {
                swarInfoId = ResolveSwarInfoId(zone.SwarId);
                if (swarInfoId < 0) return false;
            }

            spec = new PlaySpec(
                type: zone.Type,
                swarInfoId: swarInfoId,
                swavId: zone.SwavId,
                baseKey: Math.Clamp((int)zone.BaseKey, 0, 127),
                attack: (int)zone.Attack, decay: (int)zone.Decay, sustain: (int)zone.Sustain, release: (int)zone.Release,
                pan: (int)zone.Pan
            );
            return true;
        }

        return false;
    }

    
    private bool TryResolvePlayableSingleFromProgram(int programId, int midiNote, out PlaySpec spec)
    {
        spec = default;

        if (!_progToRow.TryGetValue(programId, out var row))
            return false;

        if (row.Type is SBNK.InstrumentType.Null or SBNK.InstrumentType.NullInstrument or SBNK.InstrumentType.Unknown)
            return false;

        
        return TryResolvePlayableSingle(row, midiNote, out spec);
    }

    private void PlayMidiNoteOn(
        int ch,
        int midiNote,
        int velocity,
        ushort allowedHardwareChannelMask = 0,
        int noteDurationTicks = 0,
        double tempo = 120.0)
    {
        ushort effectiveChannelMask = allowedHardwareChannelMask == 0
            ? ResolveSelectedMixerPlayerChannelMask()
            : allowedHardwareChannelMask;

        
        if (_lastInfo is null || _lastFat is null || SelectedBank is null || string.IsNullOrEmpty(LoadedFilePath))
            return;
        if (ch < 0 || ch >= 16) return;

        UpdateStripInstrumentType(ch);
        int noteDurationSamples = ToEngineSamples(noteDurationTicks, tempo);

        int programId = MixerStrips[ch].ProgramId;

        if (!TryResolvePlayableSingleFromProgram(programId, midiNote, out var spec))
        {
            SetMixerStripIssueLog(ch, "NoSound: invalid program");
            return;
        }

        try
        {
            var dict = _activeVoices[ch];
            if (dict.TryGetValue(midiNote, out var oldVoiceId))
            {
                AudioEngine.Instance.NoteOff(oldVoiceId);
                dict.Remove(midiNote);
            }

            int voiceId;
            string toneInfo;
            switch (spec.Type)
            {
                case SBNK.InstrumentType.Psg:
                {
                    int duty = Math.Clamp(spec.SwavId, 0, 6);
                    voiceId = AudioEngine.Instance.PlayPsg(
                        midiChannel: ch,
                        velocity: (byte)velocity,
                        midiNote: midiNote,
                        baseKey: spec.BaseKey,
                        dutyIndex: duty,
                        attack: spec.Attack,
                        decay: spec.Decay,
                        sustain: spec.Sustain,
                        release: spec.Release,
                        defaultPan: spec.Pan,
                        baseVolume: 1.0f,
                        allowedHardwareChannelMask: effectiveChannelMask,
                        noteDurationSamples: noteDurationSamples);
                    toneInfo = $"PSG duty {duty}";
                    break;
                }
                case SBNK.InstrumentType.Noise:
                {
                    bool use7bit = false;
                    voiceId = AudioEngine.Instance.PlayNoise(
                        midiChannel: ch,
                        velocity: (byte)velocity,
                        midiNote: midiNote,
                        baseKey: spec.BaseKey,
                        use7bit: use7bit,
                        attack: spec.Attack,
                        decay: spec.Decay,
                        sustain: spec.Sustain,
                        release: spec.Release,
                        defaultPan: spec.Pan,
                        baseVolume: 1.0f,
                        allowedHardwareChannelMask: effectiveChannelMask,
                        noteDurationSamples: noteDurationSamples);
                    toneInfo = "Noise";
                    break;
                }
                case SBNK.InstrumentType.Pcm:
                case SBNK.InstrumentType.DirectPcm:
                {
                    if (!TryGetSwavCached(spec.SwarInfoId, spec.SwavId, out var swav))
                        return;

                    voiceId = AudioEngine.Instance.PlaySwav(
                        midiChannel: ch,
                        velocity: (byte)velocity,
                        pcm16: swav.PCM16,
                        sampleRate: swav.SampleRate,
                        midiNote: midiNote,
                        baseKey: spec.BaseKey,
                        loop: swav.Loop,
                        loopStart: swav.LoopStartSample,
                        loopEnd: swav.LoopEndSample,
                        attack: spec.Attack,
                        decay: spec.Decay,
                        sustain: spec.Sustain,
                        release: spec.Release,
                        defaultPan: spec.Pan,
                        allowedHardwareChannelMask: effectiveChannelMask,
                        noteDurationSamples: noteDurationSamples
                    );
                    toneInfo = $"SWAR {spec.SwarInfoId} SWAV {spec.SwavId}";
                    break;
                }
                default:
                    return;
            }

            if (voiceId < 0)
            {
                if (dict.Count == 0)
                    MixerStrips[ch].ActiveNote = -1;

                SetMixerStripIssueLog(ch, "NoSound: alloc fail");
                StatusMessage =
                    $"Ch{ch:00} NoteOn skipped (voice limit) Pgm{programId:00000} " +
                    $"{toneInfo} Note {midiNote}";
                return;
            }

            dict[midiNote] = voiceId;

            MixerStrips[ch].ActiveNote = midiNote;
            MixerStrips[ch].Velocity = velocity;
            MixerStrips[ch].Level = Math.Max(MixerStrips[ch].Level, velocity / 127f);

            StatusMessage =
                $"Ch{ch:00} NoteOn Pgm{programId:00000} " +
                $"{toneInfo} Note {midiNote}";
        }
        catch (Exception ex)
        {
            SetMixerStripIssueLog(ch, "NoSound: playback error");
            StatusMessage = $"Playback error: {ex.Message}";
        }
    }

    private static int ToEngineSamples(int noteDurationTicks, double tempo)
    {
        if (noteDurationTicks <= 0)
            return 0;

        double clampedTempo = Math.Clamp(tempo, 1.0, 1023.0);
        double secondsPerTick = 60.0 / (clampedTempo * 48.0);
        double durationSec = noteDurationTicks * secondsPerTick;
        if (durationSec <= 0.0)
            return 0;

        int sampleRate = AudioEngine.Instance.SampleRate;
        int samples = (int)Math.Round(durationSec * sampleRate);
        return Math.Max(1, samples);
    }



    private void StopMidiNote(int ch, int midiNote)
    {
        if (ch < 0 || ch >= 16) return;

        var dict = _activeVoices[ch];
        if (dict.TryGetValue(midiNote, out var voiceId))
        {
            AudioEngine.Instance.NoteOff(voiceId);
            dict.Remove(midiNote);
        }

        if (dict.Count == 0)
            MixerStrips[ch].ActiveNote = -1;
    }

    private static int MidiCh0(int naudioCh)
    {
        return Math.Clamp(naudioCh - 1, 0, 15);
    }

    private static bool IsMixerOutputBlocked(int ch)
    {
        return false;
    }

    private void SetMixerStripIssueLog(int track, string message)
    {
        if ((uint)track >= (uint)MixerStrips.Count)
            return;

        MixerStrips[track].IssueLog = string.IsNullOrWhiteSpace(message) ? "-" : message;
    }

    public sealed class MixerStrip : INotifyPropertyChanged
    {
        private static readonly IBrush DarkInactiveToggleBorderBrush = new SolidColorBrush(Color.Parse("#353535"));
        private static readonly IBrush LightInactiveToggleBorderBrush = new SolidColorBrush(Color.Parse("#DCDCDC"));

        private readonly Action<int, bool>? _onOutputEnabledChanged;

        public int ChannelIndex { get; }
        public string ChannelLabel => $"Tr{ChannelIndex:X1}h";
        public bool IsLastTrack => ChannelIndex == 0xF;
        public IBrush ChannelBrush { get; }
        public IBrush OutputToggleBorderBrush => _outputAudibleActive
            ? ChannelBrush
            : (IsLightThemeVariant() ? LightInactiveToggleBorderBrush : DarkInactiveToggleBorderBrush);

        bool _solo; public bool Solo { get => _solo; set { if (_solo != value) { _solo = value; OnP(); } } }
        bool _mute;
        public bool Mute
        {
            get => _mute;
            set
            {
                if (_mute == value)
                    return;

                _mute = value;
                OnP();
                OnP(nameof(OutputEnabled));
                _onOutputEnabledChanged?.Invoke(ChannelIndex, !_mute);
            }
        }

        public bool OutputEnabled
        {
            get => !Mute;
            set => Mute = !value;
        }

        string _outputChannelDisplay = "--";
        public string OutputChannelDisplay
        {
            get => _outputChannelDisplay;
            set
            {
                if (_outputChannelDisplay != value)
                {
                    _outputChannelDisplay = value;
                    OnP();
                }
            }
        }

        bool _outputAudibleActive;
        public bool OutputAudibleActive
        {
            get => _outputAudibleActive;
            set
            {
                if (_outputAudibleActive != value)
                {
                    _outputAudibleActive = value;
                    OnP();
                    OnP(nameof(OutputToggleBorderBrush));
                }
            }
        }

        string _issueLog = "-";
        public string IssueLog
        {
            get => _issueLog;
            set
            {
                if (_issueLog != value)
                {
                    _issueLog = value;
                    OnP();
                }
            }
        }

        int _activeNote = -1;
        public int ActiveNote { get => _activeNote; set { var v = Math.Clamp(value, -1, 127); if (_activeNote != v) { _activeNote = v; OnP(); OnP(nameof(ActiveNoteDisplay)); } } }
        public string ActiveNoteDisplay => _activeNote >= 0 ? _activeNote.ToString() : "-";

        int _programId = 0;
        public int ProgramId { get => _programId; set { var v = Math.Clamp(value, 0, 32767); if (_programId != v) { _programId = v; OnP(); } } }

        int _velocity = 0;
        public int Velocity
        {
            get => _velocity;
            set { var v = Math.Clamp(value, 0, 127); if (_velocity != v) { _velocity = v; OnP(); OnP(nameof(VelocityRatio)); } }
        }
        public double VelocityRatio => _velocity / 127.0;

        double _level;
        public double Level { get => _level; set { var v = Math.Clamp(value, 0.0, 1.0); if (Math.Abs(_level - v) > 1e-6) { _level = v; OnP(); } } }

        int _volume = 127;
        public int Volume
        {
            get => _volume;
            set { var v = Math.Clamp(value, 0, 127); if (_volume != v) { _volume = v; OnP(); OnP(nameof(VolumeRatio)); } }
        }
        public double VolumeRatio => _volume / 127.0;

        int _volume2 = 127;
        public int Volume2
        {
            get => _volume2;
            set { var v = Math.Clamp(value, 0, 127); if (_volume2 != v) { _volume2 = v; OnP(); OnP(nameof(Volume2Ratio)); } }
        }
        public double Volume2Ratio => _volume2 / 127.0;

        int _pan = 0; 
        public int Pan
        {
            get => _pan;
            set
            {
                var v = Math.Clamp(value, -64, 63);
                if (_pan != v)
                {
                    _pan = v;
                    OnP();
                    OnP(nameof(PanNeg));
                    OnP(nameof(PanPos));
                    OnP(nameof(PanNegRatio));
                    OnP(nameof(PanPosRatio));
                    OnP(nameof(PanNegWidth));
                    OnP(nameof(PanPosWidth));
                }
            }
        }
        public int PanNeg => _pan < 0 ? Math.Clamp(-_pan, 0, 64) : 0; 
        public int PanPos => _pan > 0 ? Math.Clamp(_pan, 0, 63) : 0;  
        public double PanNegRatio => PanNeg / 64.0;
        public double PanPosRatio => PanPos / 63.0;

        int _pitchBend = 0;
        public int PitchBend
        {
            get => _pitchBend;
            set
            {
                var v = Math.Clamp(value, -127, 127);
                if (_pitchBend != v)
                {
                    _pitchBend = v;
                    OnP();
                    OnP(nameof(PitchBendNeg));
                    OnP(nameof(PitchBendPos));
                    OnP(nameof(PitchBendNegRatio));
                    OnP(nameof(PitchBendPosRatio));
                    OnP(nameof(PitchBendNegWidth));
                    OnP(nameof(PitchBendPosWidth));
                }
            }
        }
        public int PitchBendNeg => _pitchBend < 0 ? -_pitchBend : 0; 
        public int PitchBendPos => _pitchBend > 0 ? _pitchBend : 0;  
        public double PitchBendNegRatio => PitchBendNeg / 127.0;
        public double PitchBendPosRatio => PitchBendPos / 127.0;

        int _portamento = 0;
        public int Portamento
        {
            get => _portamento;
            set { var v = Math.Clamp(value, 0, 127); if (_portamento != v) { _portamento = v; OnP(); OnP(nameof(PortamentoRatio)); } }
        }
        public double PortamentoRatio => _portamento / 127.0;

        bool _portaEnabled;
        public bool PortaEnabled
        {
            get => _portaEnabled;
            set
            {
                if (_portaEnabled == value)
                    return;

                _portaEnabled = value;
                OnP();
                OnP(nameof(PortaMeterBrush));
                OnP(nameof(PortaPulseBrush));
            }
        }

        int _modSpeed = 16;
        public int ModSpeed
        {
            get => _modSpeed;
            set
            {
                var v = Math.Clamp(value, 0, 127);
                if (_modSpeed != v)
                {
                    _modSpeed = v;
                    OnP();
                    OnP(nameof(ModSpeedRatio));
                }
            }
        }
        public double ModSpeedRatio => _modSpeed / 127.0;

        int _modRange = 1;
        public int ModRange
        {
            get => _modRange;
            set
            {
                var v = Math.Clamp(value, 0, 127);
                if (_modRange != v)
                {
                    _modRange = v;
                    OnP();
                    OnP(nameof(ModRangeRatio));
                }
            }
        }
        public double ModRangeRatio => _modRange / 127.0;

        int _modDelay = 0;
        public int ModDelay
        {
            get => _modDelay;
            set
            {
                var v = Math.Clamp(value, 0, 32767);
                if (_modDelay != v)
                {
                    _modDelay = v;
                    OnP();
                    OnP(nameof(ModDelayRatio));
                }
            }
        }
        public double ModDelayRatio
        {
            get
            {
                if (_modDelay <= 0)
                    return 0.0;

                const double maxDelay = 32767.0;
                return Math.Clamp(Math.Log10(_modDelay + 1.0) / Math.Log10(maxDelay + 1.0), 0.0, 1.0);
            }
        }

        int _bendRange = 2;
        public int BendRange
        {
            get => _bendRange;
            set
            {
                var v = Math.Clamp(value, 0, 127);
                if (_bendRange != v)
                {
                    _bendRange = v;
                    OnP();
                    OnP(nameof(BendRangeRatio));
                }
            }
        }
        public double BendRangeRatio => _bendRange / 127.0;

        
        int _modulation = 0;
        public int Modulation
        {
            get => _modulation;
            set
            {
                var v = Math.Clamp(value, 0, 127);
                if (_modulation == v) return;

                _modulation = v;
                OnP();
                OnP(nameof(ModulationRatio));

                if (_modulation > 0 && Math.Abs(_modSwing) < 1e-6)
                    _modPhase = 0.0;
            }
        }
        public double ModulationRatio => _modulation / 127.0;

        int _modType = 0;
        public int ModType
        {
            get => _modType;
            set
            {
                int normalized = value switch
                {
                    1 => 1,
                    2 => 2,
                    _ => 0
                };

                if (_modType == normalized)
                    return;

                _modType = normalized;
                OnP();
                OnP(nameof(ModulationMeterBrush));
                OnP(nameof(ModulationPulseBrush));
            }
        }

        private static readonly IBrush VolumeFillBrush = new SolidColorBrush(Color.Parse("#00A8CC"));
        private static readonly IBrush Volume2FillBrush = new SolidColorBrush(Color.Parse("#00B8C8"));
        private static readonly IBrush VelocityFillBrush = new SolidColorBrush(Color.Parse("#22A39F"));
        private static readonly IBrush PanFillBrush = new SolidColorBrush(Color.Parse("#C79A4A"));
        private static readonly IBrush ModSpeedFillBrush = new SolidColorBrush(Color.Parse("#4E6EC7"));
        private static readonly IBrush ModRangeFillBrush = new SolidColorBrush(Color.Parse("#6B55C9"));
        private static readonly IBrush ModDelayFillBrush = new SolidColorBrush(Color.Parse("#8660C7"));
        private static readonly IBrush BendRangeFillBrush = new SolidColorBrush(Color.Parse("#35AF74"));
        private static readonly IBrush PitchBendFillBrush = new SolidColorBrush(Color.Parse("#3ABF86"));

        private static readonly IBrush ModDefaultFillBrush = new SolidColorBrush(Color.Parse("#5A74CC"));
        private static readonly IBrush ModDefaultPulseBrush = new SolidColorBrush(Color.Parse("#8FA4E0"));
        private static readonly IBrush VolumeModFillBrush = new SolidColorBrush(Color.Parse("#2B8FB0"));
        private static readonly IBrush VolumeModPulseBrush = new SolidColorBrush(Color.Parse("#72BFD4"));
        private static readonly IBrush PanModFillBrush = new SolidColorBrush(Color.Parse("#B88733"));
        private static readonly IBrush PanModPulseBrush = new SolidColorBrush(Color.Parse("#D8B56A"));

        private static readonly IBrush PortaOnFillBrush = new SolidColorBrush(Color.Parse("#2D9B92"));
        private static readonly IBrush PortaOnPulseBrush = new SolidColorBrush(Color.Parse("#7BC8C0"));
        private static readonly IBrush PortaOffFillBrush = new SolidColorBrush(Color.Parse("#A63F3F"));
        private static readonly IBrush PortaOffPulseBrush = new SolidColorBrush(Color.Parse("#D37C7C"));

        public IBrush VolumeMeterBrush => VolumeFillBrush;
        public IBrush Volume2MeterBrush => Volume2FillBrush;
        public IBrush VelocityMeterBrush => VelocityFillBrush;
        public IBrush PanMeterBrush => PanFillBrush;
        public IBrush ModSpeedMeterBrush => ModSpeedFillBrush;
        public IBrush ModRangeMeterBrush => ModRangeFillBrush;
        public IBrush ModDelayMeterBrush => ModDelayFillBrush;
        public IBrush BendRangeMeterBrush => BendRangeFillBrush;
        public IBrush PitchBendMeterBrush => PitchBendFillBrush;

        public IBrush ModulationMeterBrush => ModType switch
        {
            1 => VolumeModFillBrush,
            2 => PanModFillBrush,
            _ => ModDefaultFillBrush
        };

        public IBrush ModulationPulseBrush => ModType switch
        {
            1 => VolumeModPulseBrush,
            2 => PanModPulseBrush,
            _ => ModDefaultPulseBrush
        };

        public IBrush PortaMeterBrush => PortaEnabled ? PortaOnFillBrush : PortaOffFillBrush;
        public IBrush PortaPulseBrush => PortaEnabled ? PortaOnPulseBrush : PortaOffPulseBrush;

        
        double _modPhase = 0.0;
        double _modSwing = 0.0;

        public double ModSwingNeg => _modSwing < 0 ? -_modSwing : 0.0; 
        public double ModSwingPos => _modSwing > 0 ? _modSwing : 0.0;  

        double _portaSwing = 0.0;
        public double PortaSwingNeg => _portaSwing < 0 ? -_portaSwing : 0.0;
        public double PortaSwingPos => _portaSwing > 0 ? _portaSwing : 0.0;


        private const double BipolarHalfWidth = 24.0; 

        
        public double PanNegWidth => BipolarHalfWidth * PanNegRatio;
        public double PanPosWidth => BipolarHalfWidth * PanPosRatio;

        
        public double PitchBendNegWidth => BipolarHalfWidth * PitchBendNegRatio;
        public double PitchBendPosWidth => BipolarHalfWidth * PitchBendPosRatio;

        
        public double ModSwingNegWidth => BipolarHalfWidth * ModSwingNeg;
        public double ModSwingPosWidth => BipolarHalfWidth * ModSwingPos;
        public double PortaSwingNegWidth => BipolarHalfWidth * PortaSwingNeg;
        public double PortaSwingPosWidth => BipolarHalfWidth * PortaSwingPos;

        public void SetModSwingFromWave(double wave)
        {
            _modSwing = Math.Clamp(wave, -1.0, 1.0);
            OnP(nameof(ModSwingNeg));
            OnP(nameof(ModSwingPos));
            OnP(nameof(ModSwingNegWidth));
            OnP(nameof(ModSwingPosWidth));
        }

        public void SetPortaSwingFromWave(double wave)
        {
            _portaSwing = Math.Clamp(wave, -1.0, 1.0);
            OnP(nameof(PortaSwingNeg));
            OnP(nameof(PortaSwingPos));
            OnP(nameof(PortaSwingNegWidth));
            OnP(nameof(PortaSwingPosWidth));
        }

        
        
        
        public void TickUi(double dt, double modLfoHz)
        {
            if (dt <= 0) return;

            if (_modulation > 0)
            {
                double hz = Math.Clamp(modLfoHz, 0.1, 30.0); 
                _modPhase += dt * hz * (Math.PI * 2.0);
                if (_modPhase > Math.PI * 2.0) _modPhase -= Math.PI * 2.0;

                _modSwing = Math.Sin(_modPhase); 
                OnP(nameof(ModSwingNeg));
                OnP(nameof(ModSwingPos));
                OnP(nameof(ModSwingNegWidth));
                OnP(nameof(ModSwingPosWidth));
            }
            else
            {
                if (Math.Abs(_modSwing) > 1e-4)
                {
                    double k = Math.Exp(-dt * 14.0);
                    _modSwing *= k;
                    if (Math.Abs(_modSwing) < 0.01) _modSwing = 0.0;

                    OnP(nameof(ModSwingNeg));
                    OnP(nameof(ModSwingPos));
                    OnP(nameof(ModSwingNegWidth));
                    OnP(nameof(ModSwingPosWidth));
                }
            }
        }

        string _instrumentType = "-";
        public string InstrumentType { get => _instrumentType; set { if (_instrumentType != value) { _instrumentType = value; OnP(); } } }

        public MixerStrip(int ch, IBrush channelBrush, Action<int, bool>? onOutputEnabledChanged = null)
        {
            ChannelIndex = ch;
            ChannelBrush = channelBrush;
            _onOutputEnabledChanged = onOutputEnabledChanged;
        }

        public void NotifyThemeVariantChanged()
        {
            OnP(nameof(OutputToggleBorderBrush));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnP([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));

        
        private double _meterLevel;
        public double MeterLevel { get => _meterLevel; private set { if (Math.Abs(_meterLevel - value) > 1e-9) { _meterLevel = value; OnP(); } } }

        private double _meterPeak;
        public double MeterPeak { get => _meterPeak; private set { if (Math.Abs(_meterPeak - value) > 1e-9) { _meterPeak = value; OnP(); } } }

        private bool _meterClip;
        public bool MeterClip { get => _meterClip; private set { if (_meterClip != value) { _meterClip = value; OnP(); } } }

        private double _levelDisplay;
        private double _peakHold;
        private double _peakHoldRemainSec;

        private double _levelAttackMs = 1;
        private double _levelReleaseMs = 100;
        private double _peakHoldSec = 0.10;
        private double _peakDecayPerSec = 0.50;

        public void SetMeterReleaseMs(double releaseMs)
        {
            _levelReleaseMs = Math.Round(Math.Clamp(releaseMs, 0.0, 200.0), 1);
        }

        public void UpdateMeter(double rawLevel, double rawPeak, bool rawClip, double dt)
        {
            rawLevel = Math.Clamp(rawLevel, 0.0, 1.0);
            rawPeak = Math.Clamp(rawPeak, 0.0, 1.0);

            double attackAlpha = _levelAttackMs <= 0.0
                ? 1.0
                : 1.0 - Math.Exp(-dt / (_levelAttackMs / 1000.0));
            double releaseAlpha = _levelReleaseMs <= 0.0
                ? 1.0
                : 1.0 - Math.Exp(-dt / (_levelReleaseMs / 1000.0));
            double a = (rawLevel > _levelDisplay) ? attackAlpha : releaseAlpha;

            _levelDisplay += (rawLevel - _levelDisplay) * a;
            MeterLevel = _levelDisplay;

            if (rawPeak >= _peakHold)
            {
                _peakHold = rawPeak;
                _peakHoldRemainSec = _peakHoldSec;
            }
            else
            {
                if (_peakHoldRemainSec > 0) _peakHoldRemainSec -= dt;
                else _peakHold = Math.Max(0.0, _peakHold - _peakDecayPerSec * dt);
            }

            MeterPeak = _peakHold;
            MeterClip = rawClip;
        }
    }



    private void UpdateStripInstrumentType(int ch)
    {
        if ((uint)ch >= (uint)MixerStrips.Count) return;

        int programId = MixerStrips[ch].ProgramId;

        if (_progToRow.TryGetValue(programId, out var row))
        {
            MixerStrips[ch].InstrumentType = row.TypeLabel;
        }
        else
        {
            MixerStrips[ch].InstrumentType = "-";
        }
    }


    
    public ObservableCollection<MixerStrip> MixerStrips { get; } = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Stopwatch _uiSw = Stopwatch.StartNew();
    private TimeSpan _uiLastTs;

    public MainWindowViewModel()
    {
        _statusTypewriterTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(18)
        };
        _statusTypewriterTimer.Tick += OnStatusTypewriterTick;
        _statusRightPlaybackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _statusRightPlaybackTimer.Tick += OnRightStatusPlaybackTick;
        _statusBlinkTimer = new DispatcherTimer
        {
            Interval = ResolveStatusBlinkInterval(DefaultBlinkTempoBpm)
        };
        ApplyStatusMessage(DefaultMessage);
        UpdateRightStatusBlinking();

        RebuildSdatTree(null, null, null);

        for (int ch = 0; ch < 16; ch++)
            MixerChannelGates.Add(new MixerChannelGate(ch, PianoChannelBrushes[ch], OnMixerChannelGateChanged));
        UpdateMixerChannelGateAvailability();
        AudioEngine.Instance.SetHardwareChannelEnabledMask(ResolveMixerChannelGateMask());

        for (int ch = 0; ch < 16; ch++)
            MixerStrips.Add(new MixerStrip(ch, PianoChannelBrushes[ch], OnMixerStripOutputEnabledChanged));
        AudioEngine.Instance.SetTrackOutputEnabledMask(ResolveMixerStripOutputEnabledMask());
        InitializeMeterPreference();
        RefreshMixerToggleThemeBrushes();

        InitializeMidiInputPreference();
        InitializeAudioOutputPreference();
        _ = RefreshAudioOutputsAsync();
        _ = RefreshMidiInputsAsync();

        StartMasterMeterPolling();

        AudioEngine.Instance.SetMasterVolume((float)MasterVolume);

        _uiLastTs = _uiSw.Elapsed;

        _uiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        _uiTimer.Tick += (_, __) =>
        {
            for (int ch = 0; ch < MixerStrips.Count; ch++)
            {
                string outputSlot = MixerStrips[ch].OutputChannelDisplay;
                bool hasTrackVoice = outputSlot != "-" && outputSlot != "--";

                double wave = hasTrackVoice ? AudioEngine.Instance.GetModLfoVizWave(ch) : 0.0;
                MixerStrips[ch].SetModSwingFromWave(wave);

                double portaWave = hasTrackVoice ? AudioEngine.Instance.GetPortaVizWave(ch) : 0.0;
                MixerStrips[ch].SetPortaSwingFromWave(portaWave);
            }
        };

        _uiTimer.Start();
    }

    private int _previewVoiceId = -1;
    
    
    
    public void PlaySelectedInstNote(int midiNote)
    {
        if (_lastInfo is null || _lastFat is null || SelectedBank is null || string.IsNullOrEmpty(LoadedFilePath))
            return;
        if (SelectedInst is null) return;

        
        if (_previewVoiceId != -1)
        {
            AudioEngine.Instance.StopVoice(_previewVoiceId);
            _previewVoiceId = -1;
        }

        if (!TryResolvePlayableSingle(SelectedInst, midiNote, out var spec))
            return;

        try
        {
            const int previewChannel = 0;
            const byte previewVel = 100;
            int voiceId;
            string toneInfo;
            switch (spec.Type)
            {
                case SBNK.InstrumentType.Psg:
                {
                    int duty = Math.Clamp(spec.SwavId, 0, 6);
                    voiceId = AudioEngine.Instance.PlayPsg(
                        midiChannel: previewChannel,
                        velocity: previewVel,
                        midiNote: midiNote,
                        baseKey: spec.BaseKey,
                        dutyIndex: duty,
                        attack: spec.Attack,
                        decay: spec.Decay,
                        sustain: spec.Sustain,
                        release: spec.Release,
                        defaultPan: spec.Pan,
                        baseVolume: 1.0f);
                    toneInfo = $"PSG duty {duty}";
                    break;
                }
                case SBNK.InstrumentType.Noise:
                {
                    bool use7bit = false;
                    voiceId = AudioEngine.Instance.PlayNoise(
                        midiChannel: previewChannel,
                        velocity: previewVel,
                        midiNote: midiNote,
                        baseKey: spec.BaseKey,
                        use7bit: use7bit,
                        attack: spec.Attack,
                        decay: spec.Decay,
                        sustain: spec.Sustain,
                        release: spec.Release,
                        defaultPan: spec.Pan,
                        baseVolume: 1.0f);
                    toneInfo = "Noise";
                    break;
                }
                case SBNK.InstrumentType.Pcm:
                case SBNK.InstrumentType.DirectPcm:
                {
                    if (!TryGetSwavCached(spec.SwarInfoId, spec.SwavId, out var swav))
                        return;

                    voiceId = AudioEngine.Instance.PlaySwav(
                        midiChannel: previewChannel,
                        velocity: previewVel,
                        pcm16: swav.PCM16,
                        sampleRate: swav.SampleRate,
                        midiNote: midiNote,
                        baseKey: spec.BaseKey,
                        loop: swav.Loop,
                        loopStart: swav.LoopStartSample,
                        loopEnd: swav.LoopEndSample,
                        attack: spec.Attack,
                        decay: spec.Decay,
                        sustain: spec.Sustain,
                        release: spec.Release,
                        defaultPan: spec.Pan
                    );
                    toneInfo = $"SWAR {spec.SwarInfoId}, SWAV {spec.SwavId}";
                    break;
                }
                default:
                    return;
            }

            if (voiceId < 0)
            {
                StatusMessage = $"Play skipped (voice limit): {toneInfo}, Note {midiNote}";
                return;
            }

            _previewVoiceId = voiceId;
            StatusMessage = $"Play: {toneInfo}, Note {midiNote}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Playback error: {ex.Message}";
        }
    }

    public void StopAudio()
    {
        
        if (_previewVoiceId != -1)
        {
            AudioEngine.Instance.StopVoice(_previewVoiceId);
            _previewVoiceId = -1;
        }

        
        StopPreviewOut();
    }


    
    private readonly NitroSynth.App.Audio.Psg _psg = new();
    private readonly NitroSynth.App.Audio.Noise _noise = new();
    private WaveOutEvent? _previewOut;
    private const int PreviewSR = 48000;

    private void StopPreviewOut()
    {
        if (_previewOut != null)
        {
            _previewOut.Stop();
            _previewOut.Dispose();
            _previewOut = null;
        }
    }

    private sealed class PsgWaveProvider : IWaveProvider
    {
        private readonly NitroSynth.App.Audio.Psg _psg;
        private readonly float _gl;
        private readonly float _gr;

        public WaveFormat WaveFormat { get; }

        public PsgWaveProvider(NitroSynth.App.Audio.Psg psg, int sampleRate, int pan)
        {
            _psg = psg;

            WaveFormat = WaveFormat.CreateCustomFormat(
                WaveFormatEncoding.Pcm,
                sampleRate,
                2,               
                sampleRate * 4,  
                4,               
                16);             

            (_gl, _gr) = PanGains(pan);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / 4; 
            int o = offset;

            for (int i = 0; i < frames; i++)
            {
                float m = _psg.ProcessSample(); 

                short ls = FloatToInt16(m * _gl);
                short rs = FloatToInt16(m * _gr);

                buffer[o++] = (byte)(ls & 0xFF);
                buffer[o++] = (byte)((ls >> 8) & 0xFF);
                buffer[o++] = (byte)(rs & 0xFF);
                buffer[o++] = (byte)((rs >> 8) & 0xFF);
            }

            return frames * 4;
        }
    }

    private sealed class NoiseWaveProvider : IWaveProvider
    {
        private readonly NitroSynth.App.Audio.Noise _noise;
        private readonly float _gl;
        private readonly float _gr;

        public WaveFormat WaveFormat { get; }

        public NoiseWaveProvider(NitroSynth.App.Audio.Noise noise, int sampleRate, int pan)
        {
            _noise = noise;

            WaveFormat = WaveFormat.CreateCustomFormat(
                WaveFormatEncoding.Pcm,
                sampleRate,
                2,
                sampleRate * 4,
                4,
                16);

            (_gl, _gr) = PanGains(pan);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / 4;
            int o = offset;

            for (int i = 0; i < frames; i++)
            {
                float m = _noise.ProcessSample(); 

                short ls = FloatToInt16(m * _gl);
                short rs = FloatToInt16(m * _gr);

                buffer[o++] = (byte)(ls & 0xFF);
                buffer[o++] = (byte)((ls >> 8) & 0xFF);
                buffer[o++] = (byte)(rs & 0xFF);
                buffer[o++] = (byte)((rs >> 8) & 0xFF);
            }

            return frames * 4;
        }
    }



    private static short FloatToInt16(float x)
    {
        if (float.IsNaN(x)) x = 0f;
        x = MathF.Max(-1f, MathF.Min(1f, x));
        int v = (int)MathF.Round(x * short.MaxValue);
        if (v > short.MaxValue) v = short.MaxValue;
        if (v < short.MinValue) v = short.MinValue;
        return (short)v;
    }

    
    private static (float gl, float gr) PanGains(int pan)
    {
        
        float x = MathF.Max(-64, MathF.Min(63, pan)) / 63f;
        
        float a = (x + 1f) * 0.5f; 
        float gl = MathF.Cos(a * MathF.PI * 0.5f);
        float gr = MathF.Sin(a * MathF.PI * 0.5f);
        return (gl, gr);
    }

    
    
    
    
    private int ResolveSwarInfoId(int swarIdFromInst)
    {
        
        switch (swarIdFromInst)
        {
            case 0: return _selectedSwar0?.Id ?? -1;
            case 1: return _selectedSwar1?.Id ?? -1;
            case 2: return _selectedSwar2?.Id ?? -1;
            case 3: return _selectedSwar3?.Id ?? -1;
            default:
                
                return swarIdFromInst;
        }
    }

    
    public ObservableCollection<string> ThemeOptions { get; } =
        new() { "System", "Light", "Dark" };

    private int _selectedThemeIndex;
    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set
        {
            if (!SetField(ref _selectedThemeIndex, value)) return;
            ApplyThemeVariant(value);
            RefreshMixerToggleThemeBrushes();
            Dispatcher.UIThread.Post(RefreshMixerToggleThemeBrushes, DispatcherPriority.Render);
        }
    }

    private void RefreshMixerToggleThemeBrushes()
    {
        for (int i = 0; i < MixerChannelGates.Count; i++)
            MixerChannelGates[i].NotifyThemeVariantChanged();

        for (int i = 0; i < MixerStrips.Count; i++)
            MixerStrips[i].NotifyThemeVariantChanged();
    }

    private static void ApplyThemeVariant(int index)
    {
        var app = Application.Current;
        if (app is null) return;

        app.RequestedThemeVariant = index switch
        {
            1 => ThemeVariant.Light,
            2 => ThemeVariant.Dark,
            _ => ThemeVariant.Default,     
        };
    }
}
