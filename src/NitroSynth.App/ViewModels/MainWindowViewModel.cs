using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using NitroSynth.app.NDS;
using NitroSynth.App.Audio;
using NAudio.Midi;
using NAudio.Wave;
using System.Runtime.Intrinsics.X86;
using System.Diagnostics.Metrics;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Diagnostics;

namespace NitroSynth.App.ViewModels;


public sealed class BlockRow
{
    public string Name { get; init; } = string.Empty;
    public uint Offset { get; init; }
    public uint Size { get; init; }

    public string OffsetHex => $"0x{Offset:X8}";
    public string SizeHex => $"0x{Size:X8}";
    public string OffsetDec => Offset.ToString("N0");
    public string SizeDec => Size.ToString("N0");
}



public partial class MainWindowViewModel : INotifyPropertyChanged
{
    private enum RightStatusPlaybackPhase
    {
        Idle,
        CursorHoldBeforeChar,
        CharHold,
        ShiftWithCursor,
        BlinkWait,
        ScrollOut
    }

    private readonly Dictionary<int, InstRow> _progToRow = new();
    private readonly Dictionary<int, int>[] _activeVoices =
    Enumerable.Range(0, 16).Select(_ => new Dictionary<int, int>()).ToArray();


    private const string DefaultMessage = "Open an .sdat file from the menu.";
    private const double DefaultBlinkTempoBpm = 120.0;
    private const double RightStatusCursorHoldMs = 32.0;
    private const double RightStatusCharHoldMs = 32.0;
    private const double RightStatusCharShiftMs = 64.0;
    private const double RightStatusCharShiftPx = 6.0;
    private const double RightStatusScrollSpeedPxPerSec = 64.0;
    private const double RightStatusCharWidthPx = 7.0;
    private const int RightStatusBlinkWaitCycles = 32;

    private string _statusMessage = DefaultMessage;
    private string _leftStatusMessage = string.Empty;
    private string _rightStatusMessageDisplay = DefaultMessage;
    private double _rightStatusOpacity = 1.0;
    private double _rightStatusOffset;
    private readonly DispatcherTimer _statusTypewriterTimer;
    private readonly DispatcherTimer _statusRightPlaybackTimer;
    private readonly DispatcherTimer _statusBlinkTimer;
    private string _statusTypewriterTarget = string.Empty;
    private int _statusTypewriterIndex;
    private bool _statusBlinkVisible = true;
    private int _rightStatusRevealLength;
    private double _rightStatusPhaseElapsedMs;
    private double _rightStatusBlinkElapsedMs;
    private int _rightStatusBlinkToggleCount;
    private RightStatusPlaybackPhase _rightStatusPlaybackPhase = RightStatusPlaybackPhase.Idle;
    private double _statusBlinkTempoBpm = DefaultBlinkTempoBpm;
    private string? _loadedFilePath;

    public ObservableCollection<BlockRow> Blocks { get; } = new();

    private SYMB? _lastSymb;
    private INFO? _lastInfo;

    public ObservableCollection<SwarOption> SwarOptions { get; } = new();
    public ObservableCollection<SwavRow> Swavs { get; } = new();

    private SwarOption? _selectedSwar0;
    private SwarOption? _selectedSwar1;
    private SwarOption? _selectedSwar2;
    private SwarOption? _selectedSwar3;
    private SwarOption? _selectedSwar;
    private bool _selectedSwarLoadIndividually;
    private int _selectedSwarSwavCount;

    public SwarOption? SelectedSwar0 { get => _selectedSwar0; set => SetField(ref _selectedSwar0, value); }
    public SwarOption? SelectedSwar1 { get => _selectedSwar1; set => SetField(ref _selectedSwar1, value); }
    public SwarOption? SelectedSwar2 { get => _selectedSwar2; set => SetField(ref _selectedSwar2, value); }
    public SwarOption? SelectedSwar3 { get => _selectedSwar3; set => SetField(ref _selectedSwar3, value); }
    public SwarOption? SelectedSwar
    {
        get => _selectedSwar;
        set
        {
            if (!SetField(ref _selectedSwar, value))
                return;

            LoadSelectedSwarSwavs();
        }
    }

    public bool SelectedSwarLoadIndividually
    {
        get => _selectedSwarLoadIndividually;
        private set => SetField(ref _selectedSwarLoadIndividually, value);
    }

    public int SelectedSwarSwavCount
    {
        get => _selectedSwarSwavCount;
        private set => SetField(ref _selectedSwarSwavCount, value);
    }

    public sealed class SwarOption
    {
        public int Id { get; }            
        public string Name { get; }
        public SwarOption(int id, string name) { Id = id; Name = name; }
        public string Display => Id < 0 ? "(None)" : $"{Id:D3}: {Name}";
        public override string ToString() => Display;
    }

    public sealed class SwavRow
    {
        public SwavRow(int swarId, int id, string encoding, string loop, int sampleRate, int loopStart, int loopEnd, int sampleCount, int sizeBytes)
        {
            SwarId = swarId;
            Id = id;
            Encoding = encoding;
            Loop = loop;
            SampleRate = sampleRate;
            LoopStart = loopStart;
            LoopEnd = loopEnd;
            SampleCount = sampleCount;
            SizeBytes = sizeBytes;
        }

        public int SwarId { get; }
        public int Id { get; }
        public string Encoding { get; }
        public string Loop { get; }
        public int SampleRate { get; }
        public int LoopStart { get; }
        public int LoopEnd { get; }
        public int SampleCount { get; }
        public int SizeBytes { get; }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetStatusMessage(value);
    }

    public string LeftStatusMessage
    {
        get => _leftStatusMessage;
        private set => SetField(ref _leftStatusMessage, value);
    }

    public string RightStatusMessageDisplay
    {
        get => _rightStatusMessageDisplay;
        private set => SetField(ref _rightStatusMessageDisplay, value);
    }

    public double RightStatusOpacity
    {
        get => _rightStatusOpacity;
        private set => SetField(ref _rightStatusOpacity, Math.Clamp(value, 0.0, 1.0));
    }

    public double RightStatusOffset
    {
        get => _rightStatusOffset;
        private set => SetField(ref _rightStatusOffset, value);
    }

    public string? LoadedFilePath
    {
        get => _loadedFilePath;
        private set => SetField(ref _loadedFilePath, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private FAT? _lastFat;
    private bool _isShuttingDown;

    private void SetStatusMessage(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? DefaultMessage : value;

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyStatusMessage(normalized);
            return;
        }

        Dispatcher.UIThread.Post(() => ApplyStatusMessage(normalized), DispatcherPriority.Background);
    }

    private void ApplyStatusMessage(string message)
    {
        SetField(ref _statusMessage, message, nameof(StatusMessage));

        if (ShouldUseLeftStatusLane(message))
        {
            LeftStatusMessage = message;
            return;
        }

        StartRightStatusTypewriter(message);
    }

    private static bool ShouldUseLeftStatusLane(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        bool isTrackNoteLog =
            message.StartsWith("Ch", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("Note", StringComparison.OrdinalIgnoreCase);

        bool isPreviewNoteLog =
            (message.StartsWith("Play:", StringComparison.OrdinalIgnoreCase) ||
             message.StartsWith("Play skipped", StringComparison.OrdinalIgnoreCase)) &&
            message.Contains("Note", StringComparison.OrdinalIgnoreCase);

        return isTrackNoteLog || isPreviewNoteLog;
    }

    private void StartRightStatusTypewriter(string message)
    {
        _statusTypewriterTarget = message;
        _statusTypewriterTimer.Stop();
        StopRightStatusPlaybackAnimation(resetVisual: false);

        _statusTypewriterIndex = 0;
        RightStatusOpacity = 1.0;
        RightStatusOffset = 0.0;
        RightStatusMessageDisplay = string.Empty;

        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        if (IsSseqPlaying)
        {
            StartRightStatusPlaybackAnimation();
            return;
        }

        _statusTypewriterTimer.Start();
    }

    private void OnStatusTypewriterTick(object? sender, EventArgs e)
    {
        if (_statusTypewriterIndex >= _statusTypewriterTarget.Length)
        {
            _statusTypewriterTimer.Stop();
            return;
        }

        _statusTypewriterIndex = Math.Min(_statusTypewriterIndex + 1, _statusTypewriterTarget.Length);
        RightStatusOpacity = 1.0;
        RightStatusOffset = 0.0;
        RightStatusMessageDisplay = _statusTypewriterTarget[.._statusTypewriterIndex];
    }

    private void StartRightStatusPlaybackAnimation()
    {
        if (string.IsNullOrEmpty(_statusTypewriterTarget))
        {
            StopRightStatusPlaybackAnimation();
            RightStatusMessageDisplay = string.Empty;
            return;
        }

        _statusTypewriterTimer.Stop();
        _statusRightPlaybackTimer.Stop();

        _rightStatusRevealLength = 0;
        RightStatusOpacity = 1.0;
        RightStatusOffset = 0.0;
        EnterRightStatusPlaybackPhase(RightStatusPlaybackPhase.CursorHoldBeforeChar);
        RightStatusMessageDisplay = BuildRightStatusPlaybackText(
            _statusTypewriterTarget,
            _rightStatusRevealLength,
            showCursor: true);

        _statusRightPlaybackTimer.Start();
    }

    private void OnRightStatusPlaybackTick(object? sender, EventArgs e)
    {
        if (!IsSseqPlaying || string.IsNullOrEmpty(_statusTypewriterTarget))
        {
            StopRightStatusPlaybackAnimation();
            return;
        }

        double elapsedMs = _statusRightPlaybackTimer.Interval.TotalMilliseconds;
        _rightStatusPhaseElapsedMs += elapsedMs;
        int textLength = _statusTypewriterTarget.Length;
        int nextRevealLength = Math.Min(_rightStatusRevealLength + 1, textLength);

        switch (_rightStatusPlaybackPhase)
        {
            case RightStatusPlaybackPhase.CursorHoldBeforeChar:
                RightStatusOffset = 0.0;
                RightStatusOpacity = 1.0;
                RightStatusMessageDisplay = BuildRightStatusPlaybackText(
                    _statusTypewriterTarget,
                    _rightStatusRevealLength,
                    showCursor: true);
                if (_rightStatusPhaseElapsedMs >= RightStatusCursorHoldMs)
                {
                    if (_rightStatusRevealLength >= textLength)
                        _rightStatusRevealLength = 0;
                    EnterRightStatusPlaybackPhase(RightStatusPlaybackPhase.CharHold);
                    int immediateNextRevealLength = Math.Min(_rightStatusRevealLength + 1, textLength);
                    RightStatusMessageDisplay = BuildRightStatusPlaybackText(
                        _statusTypewriterTarget,
                        immediateNextRevealLength,
                        showCursor: false);
                }
                break;

            case RightStatusPlaybackPhase.CharHold:
                RightStatusOffset = 0.0;
                RightStatusOpacity = 1.0;
                RightStatusMessageDisplay = BuildRightStatusPlaybackText(
                    _statusTypewriterTarget,
                    nextRevealLength,
                    showCursor: false);
                if (_rightStatusPhaseElapsedMs >= RightStatusCharHoldMs)
                {
                    if (nextRevealLength >= textLength)
                    {
                        _rightStatusRevealLength = textLength;
                        EnterRightStatusPlaybackPhase(RightStatusPlaybackPhase.BlinkWait);
                        RightStatusMessageDisplay = BuildRightStatusPlaybackText(
                            _statusTypewriterTarget,
                            textLength,
                            showCursor: false);
                        break;
                    }

                    bool showCursorInShift = nextRevealLength < textLength;
                    EnterRightStatusPlaybackPhase(RightStatusPlaybackPhase.ShiftWithCursor);
                    RightStatusOffset = RightStatusCharShiftPx;
                    RightStatusMessageDisplay = BuildRightStatusPlaybackText(
                        _statusTypewriterTarget,
                        nextRevealLength,
                        showCursor: showCursorInShift);
                }
                break;

            case RightStatusPlaybackPhase.ShiftWithCursor:
            {
                double progress = Math.Clamp(_rightStatusPhaseElapsedMs / RightStatusCharShiftMs, 0.0, 1.0);
                bool showCursorInShift = nextRevealLength < textLength;
                RightStatusOpacity = 1.0;
                RightStatusOffset = RightStatusCharShiftPx * (1.0 - progress);
                RightStatusMessageDisplay = BuildRightStatusPlaybackText(
                    _statusTypewriterTarget,
                    nextRevealLength,
                    showCursor: showCursorInShift);
                if (progress < 1.0)
                    break;

                _rightStatusRevealLength = nextRevealLength;
                RightStatusOffset = 0.0;
                if (_rightStatusRevealLength >= textLength)
                {
                    EnterRightStatusPlaybackPhase(RightStatusPlaybackPhase.BlinkWait);
                    RightStatusMessageDisplay = BuildRightStatusPlaybackText(
                        _statusTypewriterTarget,
                        textLength,
                        showCursor: false);
                    break;
                }

                EnterRightStatusPlaybackPhase(RightStatusPlaybackPhase.CursorHoldBeforeChar);
                RightStatusMessageDisplay = BuildRightStatusPlaybackText(
                    _statusTypewriterTarget,
                    _rightStatusRevealLength,
                    showCursor: true);
                break;
            }

            case RightStatusPlaybackPhase.BlinkWait:
            {
                RightStatusOffset = 0.0;
                RightStatusMessageDisplay = BuildRightStatusPlaybackText(
                    _statusTypewriterTarget,
                    textLength,
                    showCursor: false);

                AdvanceRightStatusBlink(elapsedMs);
                RightStatusOpacity = _statusBlinkVisible ? 1.0 : 0.0;
                if (_rightStatusBlinkToggleCount < RightStatusBlinkWaitCycles)
                    break;

                EnterRightStatusPlaybackPhase(RightStatusPlaybackPhase.ScrollOut);
                RightStatusOpacity = 1.0;
                RightStatusMessageDisplay = BuildRightStatusPlaybackText(
                    _statusTypewriterTarget,
                    textLength,
                    showCursor: false);
                break;
            }

            case RightStatusPlaybackPhase.ScrollOut:
            {
                RightStatusOpacity = 1.0;
                RightStatusMessageDisplay = BuildRightStatusPlaybackText(
                    _statusTypewriterTarget,
                    textLength,
                    showCursor: false);

                double scrollDistancePx = ResolveRightStatusScrollDistancePx(textLength);
                double scrollDurationMs = ResolveRightStatusScrollDurationMs(scrollDistancePx);
                double progress = Math.Clamp(_rightStatusPhaseElapsedMs / scrollDurationMs, 0.0, 1.0);
                RightStatusOffset = scrollDistancePx * progress;
                if (progress < 1.0)
                    break;

                _rightStatusRevealLength = 0;
                EnterRightStatusPlaybackPhase(RightStatusPlaybackPhase.CursorHoldBeforeChar);
                RightStatusMessageDisplay = BuildRightStatusPlaybackText(
                    _statusTypewriterTarget,
                    _rightStatusRevealLength,
                    showCursor: true);
                break;
            }
        }
    }

    private void EnterRightStatusPlaybackPhase(RightStatusPlaybackPhase phase)
    {
        _rightStatusPlaybackPhase = phase;
        _rightStatusPhaseElapsedMs = 0.0;
        RightStatusOpacity = 1.0;
        RightStatusOffset = 0.0;
        if (phase == RightStatusPlaybackPhase.BlinkWait)
        {
            _statusBlinkVisible = true;
            _rightStatusBlinkElapsedMs = 0.0;
            _rightStatusBlinkToggleCount = 0;
        }
    }

    private void StopRightStatusPlaybackAnimation(bool resetVisual = true)
    {
        if (_statusRightPlaybackTimer.IsEnabled)
            _statusRightPlaybackTimer.Stop();

        _rightStatusPlaybackPhase = RightStatusPlaybackPhase.Idle;
        _rightStatusRevealLength = 0;
        _rightStatusPhaseElapsedMs = 0.0;
        _statusBlinkVisible = true;
        _rightStatusBlinkElapsedMs = 0.0;
        _rightStatusBlinkToggleCount = 0;

        if (!resetVisual)
            return;

        RightStatusOpacity = 1.0;
        RightStatusOffset = 0.0;
    }

    private static string BuildRightStatusPlaybackText(string source, int visibleLength, bool showCursor)
    {
        if (string.IsNullOrEmpty(source))
            return showCursor ? "_" : string.Empty;

        int clampedLength = Math.Clamp(visibleLength, 0, source.Length);
        string baseText = source[..clampedLength];
        return showCursor ? baseText + "_" : baseText;
    }

    private void AdvanceRightStatusBlink(double elapsedMs)
    {
        double intervalMs = Math.Max(_statusBlinkTimer.Interval.TotalMilliseconds, 1.0);
        _rightStatusBlinkElapsedMs += elapsedMs;

        while (_rightStatusBlinkElapsedMs >= intervalMs)
        {
            _rightStatusBlinkElapsedMs -= intervalMs;
            _statusBlinkVisible = !_statusBlinkVisible;
            _rightStatusBlinkToggleCount++;
        }
    }

    private static double ResolveRightStatusScrollDistancePx(int textLength)
    {
        int clampedLength = Math.Max(textLength, 0);
        return (clampedLength + 1) * RightStatusCharWidthPx;
    }

    private static double ResolveRightStatusScrollDurationMs(double scrollDistancePx)
    {
        double clampedDistance = Math.Max(scrollDistancePx, RightStatusCharShiftPx);
        double pixelsPerMs = Math.Max(RightStatusScrollSpeedPxPerSec / 1000.0, 0.01);
        return clampedDistance / pixelsPerMs;
    }

    private void SetRightStatusBlinkTempo(double bpm)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyRightStatusBlinkTempo(bpm);
            return;
        }

        Dispatcher.UIThread.Post(
            () => ApplyRightStatusBlinkTempo(bpm),
            DispatcherPriority.Background);
    }

    private void ApplyRightStatusBlinkTempo(double bpm)
    {
        double clamped = Math.Clamp(bpm, 1.0, 1023.0);
        if (Math.Abs(_statusBlinkTempoBpm - clamped) < 0.001)
            return;

        _statusBlinkTempoBpm = clamped;
        _statusBlinkTimer.Interval = ResolveStatusBlinkInterval(clamped);
    }

    private static TimeSpan ResolveStatusBlinkInterval(double bpm)
    {
        double clamped = Math.Clamp(bpm, 1.0, 1023.0);
        double halfBeatMs = 60000.0 / clamped / 1.0;
        double intervalMs = Math.Clamp(halfBeatMs, 40.0, 1000.0);
        return TimeSpan.FromMilliseconds(intervalMs);
    }

    private void UpdateRightStatusBlinking()
    {
        if (IsSseqPlaying)
        {
            StartRightStatusPlaybackAnimation();
            return;
        }

        StopRightStatusPlaybackAnimation();
    }

    public void Shutdown()
    {
        if (_isShuttingDown)
            return;

        _isShuttingDown = true;

        try
        {
            _uiTimer.Stop();
        }
        catch
        {
        }

        try
        {
            _statusTypewriterTimer.Stop();
        }
        catch
        {
        }

        try
        {
            _statusBlinkTimer.Stop();
        }
        catch
        {
        }

        try
        {
            _statusRightPlaybackTimer.Stop();
        }
        catch
        {
        }

        try
        {
            StopSelectedSseqPlayback();
        }
        catch
        {
        }

        try
        {
            StopAllActiveVoicesImmediate();
        }
        catch
        {
        }

        try
        {
            StopAudio();
        }
        catch
        {
        }

        try
        {
            CloseMidiInput();
        }
        catch
        {
        }

        try
        {
            AudioEngine.Instance.Dispose();
        }
        catch
        {
        }
    }

    
    public sealed class InstRow : INotifyPropertyChanged
    {
        public string SwavIdText
        {
            get => IsSingleEditable ? (IsPsg ? SwavDisplay : SwavId.ToString()) : string.Empty;
            set
            {
                if (!IsSingleEditable || IsPsg) return;                 
                SwavId = ParseClamp(value, 0, 65535, SwavId);
                OnP(); OnP(nameof(SwavDisplay));
            }
        }

        public string SwarIdText
        {
            get => (IsSingleEditable && IsNonPsg) ? SwarId.ToString() : string.Empty;
            set
            {
                if (!IsSingleEditable || !IsNonPsg) return;
                SwarId = ParseClamp(value, 0, 65535, SwarId);
                OnP();
            }
        }

        public string BaseKeyText
        {
            get => IsSingleEditable ? BaseKey.ToString() : string.Empty;
            set
            {
                if (!IsSingleEditable) return;
                BaseKey = ParseClamp(value, 0, 127, BaseKey);
                OnP();
            }
        }

        public string AttackText
        {
            get => IsSingleEditable ? Attack.ToString() : string.Empty;
            set { if (IsSingleEditable) { Attack = ParseClamp(value, 0, 127, Attack); OnP(); } }
        }
        public string DecayText
        {
            get => IsSingleEditable ? Decay.ToString() : string.Empty;
            set { if (IsSingleEditable) { Decay = ParseClamp(value, 0, 127, Decay); OnP(); } }
        }
        public string SustainText
        {
            get => IsSingleEditable ? Sustain.ToString() : string.Empty;
            set { if (IsSingleEditable) { Sustain = ParseClamp(value, 0, 127, Sustain); OnP(); } }
        }
        public string ReleaseText
        {
            get => IsSingleEditable ? (Release >= 128 ? "DISABLE" : Release.ToString()) : string.Empty;
            set
            {
                if (!IsSingleEditable) return;
                if (string.Equals(value?.Trim(), "DISABLE", StringComparison.OrdinalIgnoreCase))
                {
                    Release = 255;
                    OnP();
                    return;
                }
                Release = ParseClamp(value, 0, 255, Release);
                OnP();
            }
        }
        public string PanText
        {
            get => IsSingleEditable ? Pan.ToString() : string.Empty;
            set { if (IsSingleEditable) { Pan = ParseClamp(value, 0, 127, Pan); OnP(); } }
        }
        public int Id { get; }                             
        public string Name { get; }                        

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnP([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));

        private static int ParseClamp(string? s, int min, int max, int fallback)
        {
            if (int.TryParse(s, out var v)) return Math.Clamp(v, min, max);
            return fallback;
        }

        
        public SBNK.InstrumentType Type { get; private set; }
        public string[] TypeOptions { get; } = { "Null (00h)", "PCM (01h)", "PSG (02h)", "Noise (03h)", "Direct PCM (04h)", "Null Inst (05h)", "Drum Set (10h)", "Key Split (11h)" };
        public int SelectedType
        {
            get => Type switch
            {
                SBNK.InstrumentType.Null => 0,
                SBNK.InstrumentType.Pcm => 1,
                SBNK.InstrumentType.Psg => 2,
                SBNK.InstrumentType.Noise => 3,
                SBNK.InstrumentType.DirectPcm => 4,
                SBNK.InstrumentType.NullInstrument => 5,
                SBNK.InstrumentType.DrumSet => 6,
                SBNK.InstrumentType.KeySplit => 7,
                _ => 5
            };
            set
            {
                Type = value switch
                {
                    0 => SBNK.InstrumentType.Null,
                    1 => SBNK.InstrumentType.Pcm,
                    2 => SBNK.InstrumentType.Psg,
                    3 => SBNK.InstrumentType.Noise,
                    4 => SBNK.InstrumentType.DirectPcm,
                    5 => SBNK.InstrumentType.NullInstrument,
                    6 => SBNK.InstrumentType.DrumSet,
                    7 => SBNK.InstrumentType.KeySplit,
                    _ => SBNK.InstrumentType.NullInstrument
                };

                OnP(nameof(TypeLabel));
                OnP(nameof(IsSingleEditable));
                OnP(nameof(IsPsg));
                OnP(nameof(IsNonPsg));

                OnP(nameof(SwavIdText));
                OnP(nameof(SwarIdText));
                OnP(nameof(BaseKeyText));
                OnP(nameof(AttackText));
                OnP(nameof(DecayText));
                OnP(nameof(SustainText));
                OnP(nameof(ReleaseText));
                OnP(nameof(PanText));

                if (Type == SBNK.InstrumentType.Psg)
                {
                    DutyIndex = Math.Clamp(SwavId, 0, 6); 
                    OnP(nameof(SwavDisplay));
                }
            }

        }

        public string TypeLabel => TypeOptions[SelectedType];
        public bool IsSingleEditable => Type is SBNK.InstrumentType.Pcm or SBNK.InstrumentType.DirectPcm or SBNK.InstrumentType.Psg or SBNK.InstrumentType.Noise;
        public bool IsPsg => Type == SBNK.InstrumentType.Psg;
        public bool IsNonPsg => !IsPsg;

        
        int _programNo;
        public int ProgramNo { get => _programNo; set { _programNo = Math.Clamp(value, 0, 32767); OnP(); } }

        
        public void SetFromSingle(SBNK.SingleInst sv)
        {
            SwavId = sv.SwavId;         
            SwarId = sv.SwarId;
            BaseKey = sv.BaseKey;        
            Attack = sv.Attack;
            Decay = sv.Decay;
            Sustain = sv.Sustain;
            Release = sv.Release;
            Pan = sv.Pan;

            if (IsPsg)
            {
                 
                DutyIndex = Math.Clamp((int)sv.SwavId, 0, 6);
                OnP(nameof(SwavDisplay));
            }

            OnP(nameof(SwavId)); OnP(nameof(SwarId));
            OnP(nameof(BaseKey)); OnP(nameof(Attack));
            OnP(nameof(Decay)); OnP(nameof(Sustain));
            OnP(nameof(Release)); OnP(nameof(Pan));
            OnP(nameof(SwavIdValue)); OnP(nameof(SwarIdValue));
            OnP(nameof(BaseKeyValue)); OnP(nameof(AttackValue));
            OnP(nameof(DecayValue)); OnP(nameof(SustainValue));
            OnP(nameof(ReleaseValue)); OnP(nameof(PanValue));

            OnP(nameof(SwavIdText));
            OnP(nameof(SwarIdText));
            OnP(nameof(BaseKeyText));
            OnP(nameof(AttackText));
            OnP(nameof(DecayText));
            OnP(nameof(SustainText));
            OnP(nameof(ReleaseText));
            OnP(nameof(PanText));
        }

        
        public int SwavId { get; private set; }
        public int SwarId { get; private set; }
        public int BaseKey { get; private set; }
        public int Attack { get; private set; }
        public int Decay { get; private set; }
        public int Sustain { get; private set; }
        public int Release { get; private set; }
        public int Pan { get; private set; }

        
        public double SwavIdValue { get => SwavId; set { SwavId = (int)Math.Round(value); OnP(nameof(SwavDisplay)); OnP(nameof(SwavIdText)); } }
        public double SwarIdValue { get => SwarId; set { SwarId = (int)Math.Round(value); OnP(nameof(SwarIdText)); } }
        public double BaseKeyValue { get => BaseKey; set { BaseKey = (int)Math.Round(value); OnP(nameof(BaseKeyText)); } }
        public double AttackValue { get => Attack; set { Attack = (int)Math.Round(value); OnP(nameof(AttackText)); } }
        public double DecayValue { get => Decay; set { Decay = (int)Math.Round(value); OnP(nameof(DecayText)); } }
        public double SustainValue { get => Sustain; set { Sustain = (int)Math.Round(value); OnP(nameof(SustainText)); } }
        public double ReleaseValue { get => Release; set { Release = (int)Math.Round(value); OnP(nameof(ReleaseText)); } }
        public double PanValue { get => Pan; set { Pan = (int)Math.Round(value); OnP(nameof(PanText)); } }


        
        public string[] DutyOptions { get; } = { "12.5%", "25.0%", "37.5%", "50.0%", "62.5%", "75.0%", "87.5%" };
        int _dutyIndex;
        public int DutyIndex { get => _dutyIndex; set { _dutyIndex = Math.Clamp(value, 0, 6); SwavId = _dutyIndex; OnP(nameof(SwavDisplay)); } }
        public string SwavDisplay => IsPsg ? DutyOptions[Math.Clamp(SwavId, 0, 6)] : SwavId.ToString();

        public InstRow(int id, SBNK.InstrumentType type, string name)
        {
            Id = id; Type = type; Name = name; _programNo = id;
        }
    }


    
    public ObservableCollection<InstRow> Insts { get; } = new();

    private InstRow? _selectedInst;
    public InstRow? SelectedInst
    {
        get => _selectedInst;
        set => SetField(ref _selectedInst, value);
    }

    
    public ObservableCollection<SBNK.BankOption> BankOptions { get; } = new();

    private SBNK.BankOption? _selectedBank;
    public SBNK.BankOption? SelectedBank
    {
        get => _selectedBank;
        set
        {
            if (!SetField(ref _selectedBank, value)) return;
            ResetMidi();

            if (value is not null && _lastInfo is { } info &&
                info.Sbnk.TryGetValue(value.Id, out var sb))
            {
                SelectedSwar0 = FindSwar(sb.Swar0);
                SelectedSwar1 = FindSwar(sb.Swar1);
                SelectedSwar2 = FindSwar(sb.Swar2);
                SelectedSwar3 = FindSwar(sb.Swar3);
            }
            else
            {
                var none = SwarOptions.FirstOrDefault(o => o.Id < 0);
                SelectedSwar0 = SelectedSwar1 = SelectedSwar2 = SelectedSwar3 = none;
            }

            SetSbnkInstrumentCountInternal(0);

            _ = LoadSelectedSbnkInstsAsync();
        }
    }
    
    
    
    private bool TryGetSwarCached(int swarInfoId, out SWAR swar)
    {
        swar = null!;

        if (swarInfoId < 0) return false;
        bool loadIndividually = IsSwarLoadIndividually(swarInfoId);
        if (!loadIndividually
            && _swarCache.TryGetValue(swarInfoId, out var cachedSwar)
            && cachedSwar is not null)
        {
            swar = cachedSwar;
            return true;
        }

        if (!TryReadSwarBytes(swarInfoId, out var swarBytes))
            return false;

        if (!SWAR.TryParse(swarBytes, out swar))
            return false;

        if (!loadIndividually)
            _swarCache[swarInfoId] = swar;

        return true;
    }

    
    
    
    private bool TryGetSwavCached(int swarInfoId, int swavId, out SWAV swav)
    {
        swav = null!;
        if (swarInfoId < 0 || swavId < 0) return false;

        if (_swavCache.TryGetValue((swarInfoId, swavId), out var cachedSwav) && cachedSwav is not null)
        {
            swav = cachedSwav;
            return true;
        }

        bool loadIndividually = IsSwarLoadIndividually(swarInfoId);
        if (loadIndividually)
        {
            if (!TryReadSwarBytes(swarInfoId, out var swarBytes))
                return false;
            if (!SWAR.TryParse(swarBytes, out var swar))
                return false;
            if (!swar.TryGetSwav(swavId, out swav))
                return false;
        }
        else
        {
            if (!TryGetSwarCached(swarInfoId, out var swar))
                return false;
            if (!swar.TryGetSwav(swavId, out swav))
                return false;
        }

        _swavCache[(swarInfoId, swavId)] = swav;
        return true;
    }

    public bool TryGetSwav(int swarInfoId, int swavId, out SWAV swav)
    {
        return TryGetSwavCached(swarInfoId, swavId, out swav);
    }

    
    
    
    

    private readonly Dictionary<int, SWAR> _swarCache = new();
    private readonly Dictionary<(int swarInfoId, int swavId), SWAV> _swavCache = new();
    private readonly Dictionary<uint, byte[]> _fatFileOverrides = new();

    private bool IsSwarLoadIndividually(int swarInfoId)
    {
        if (_lastInfo is null || swarInfoId < 0)
            return false;
        return _lastInfo.Swar.TryGetValue(swarInfoId, out var swarInfo) && swarInfo.LoadIndividually;
    }

    private bool TryReadSwarBytes(int swarInfoId, out byte[] swarBytes)
    {
        swarBytes = Array.Empty<byte>();
        if (_lastInfo is null || swarInfoId < 0 || string.IsNullOrEmpty(LoadedFilePath))
            return false;
        if (!_lastInfo.Swar.TryGetValue(swarInfoId, out var swarInfo))
            return false;
        return TryReadFileFromFat(swarInfo.FileId, out swarBytes);
    }

    private void PrewarmSwavCacheForSbnk(SBNK sbnk)
    {
        if (_lastInfo is null) return;

        var visited = new HashSet<(int swarInfoId, int swavId)>();
        int skippedIndividual = 0;

        for (int i = 0; i < sbnk.Records.Count; i++)
        {
            var r = sbnk.Records[i];

            
            if (r.Type is SBNK.InstrumentType.Pcm
                or SBNK.InstrumentType.DirectPcm)
            {
                if (r.Articulation is SBNK.SingleInst sv)
                {
                    int swarInfoId = ResolveSwarInfoId(sv.SwarId);
                    int swavId = sv.SwavId;
                    if (IsSwarLoadIndividually(swarInfoId))
                    {
                        skippedIndividual++;
                        continue;
                    }
                    if (swarInfoId >= 0 && swavId >= 0 && visited.Add((swarInfoId, swavId)))
                    {
                        TryGetSwavCached(swarInfoId, swavId, out _);
                    }
                }
            }

            
            if (r.Type == SBNK.InstrumentType.DrumSet &&
                TryGetDrumSetModel(i, out var ds))
            {
                foreach (var e in ds.Entries)
                {
                    int swarInfoId = ResolveSwarInfoId(e.SwarId);
                    int swavId = e.SwavId;
                    if (IsSwarLoadIndividually(swarInfoId))
                    {
                        skippedIndividual++;
                        continue;
                    }
                    if (swarInfoId >= 0 && swavId >= 0 && visited.Add((swarInfoId, swavId)))
                    {
                        TryGetSwavCached(swarInfoId, swavId, out _);
                    }
                }
            }

            
            if (r.Type == SBNK.InstrumentType.KeySplit &&
                TryGetKeySplitModel(i, out var ks))
            {
                foreach (var e in ks.Entries)
                {
                    int swarInfoId = ResolveSwarInfoId(e.SwarId);
                    int swavId = e.SwavId;
                    if (IsSwarLoadIndividually(swarInfoId))
                    {
                        skippedIndividual++;
                        continue;
                    }
                    if (swarInfoId >= 0 && swavId >= 0 && visited.Add((swarInfoId, swavId)))
                    {
                        TryGetSwavCached(swarInfoId, swavId, out _);
                    }
                }
            }
        }

        StatusMessage = $"Preloaded SWAVs in bank: {visited.Count} (LoadIndividually skipped: {skippedIndividual})";
    }


    private SwarOption? FindSwar(int swarId)
    {
        if (swarId < 0) return SwarOptions.FirstOrDefault(o => o.Id < 0); 
        return SwarOptions.FirstOrDefault(o => o.Id == swarId);
    }

    private void LoadSelectedSwarSwavs()
    {
        Swavs.Clear();
        SelectedSwarSwavCount = 0;
        SelectedSwarLoadIndividually = false;

        var selected = SelectedSwar;
        if (selected is null || selected.Id < 0)
            return;

        if (_lastInfo is not null && _lastInfo.Swar.TryGetValue(selected.Id, out var swarInfo))
            SelectedSwarLoadIndividually = swarInfo.LoadIndividually;

        if (!TryGetSwarCached(selected.Id, out var swar))
            return;

        for (int i = 0; i < swar.Entries.Count; i++)
        {
            var entry = swar.Entries[i];

            if (swar.TryGetSwav(entry.Index, out var swav))
            {
                var encodingText = swav.Encoding switch
                {
                    SwavEncoding.Pcm8 => "PCM8",
                    SwavEncoding.Pcm16 => "PCM16",
                    SwavEncoding.ImaAdpcm => "IMA-ADPCM",
                    _ => ((byte)swav.Encoding).ToString()
                };

                Swavs.Add(new SwavRow(
                    selected.Id,
                    entry.Index,
                    encodingText,
                    swav.Loop ? "On" : "Off",
                    swav.SampleRate,
                    swav.LoopStartSample,
                    swav.LoopEndSample,
                    swav.PCM16.Length,
                    entry.Size));
            }
            else
            {
                Swavs.Add(new SwavRow(selected.Id, entry.Index, "Invalid", "-", 0, 0, 0, 0, entry.Size));
            }
        }

        SelectedSwarSwavCount = Swavs.Count;
    }

    public async Task LoadSdatAsync(IStorageFile file)
    {
        string nextLoadedFilePath = file.Path?.LocalPath ?? file.Name;

        try
        {
            // Prevent old playback thread from reading cache/state while SDAT context is switching.
            await StopSelectedSseqPlaybackAsync();
            LoadedFilePath = nextLoadedFilePath;

            await using var stream = await file.OpenReadAsync();
            var sdat = await SDAT.LoadAsync(stream);

            _swarCache.Clear();
            _swavCache.Clear();
            _fatFileOverrides.Clear();

            _lastSdatSize = sdat.SdatSize;
            _lastSdatVersion = sdat.SdatVersion;

            var symb = sdat.ReadSYMB(stream);
            var info = sdat.ReadINFO(stream);
            var fat = sdat.ReadFAT(stream);

            _lastSymb = symb;
            _lastInfo = info;
            _lastFat = fat;
            RebuildSdatTree(symb, info, fat);

            BankOptions.Clear();
            SwarOptions.Clear();
            SwarOptions.Add(new SwarOption(-1, "(None)"));

            if (symb?.Sbnk is { Count: > 0 })
            {
                foreach (var kv in symb.Sbnk.OrderBy(k => k.Key))
                {
                    var id = kv.Key;
                    var name = string.IsNullOrWhiteSpace(kv.Value) ? $"SBNK {id:D3}" : kv.Value;
                    BankOptions.Add(new SBNK.BankOption(id, name));
                }
            }
            else if (info?.Sbnk is { Count: > 0 })
            {
                foreach (var id in info.Sbnk.Keys.OrderBy(i => i))
                    BankOptions.Add(new SBNK.BankOption(id, $"SBNK {id:D3}"));
            }
            
            if (symb?.Swar is { Count: > 0 })
            {
                foreach (var kv in symb.Swar.OrderBy(k => k.Key))
                    SwarOptions.Add(new SwarOption(kv.Key, kv.Value));
            }
            else if (info?.Swar is { Count: > 0 })
            {
                foreach (var id in info.Swar.Keys.OrderBy(i => i))
                    SwarOptions.Add(new SwarOption(id, $"SWAR {id:D3}"));
            }

            SelectedSwar = SwarOptions.FirstOrDefault(o => o.Id >= 0) ?? SwarOptions.FirstOrDefault();
            RebuildSseqOptions();
            RebuildSeqPlayerRows();
            RebuildStrmRows();
            RebuildSsarOptions();

            if (info?.Sbnk != null && info.Sbnk.Count > 0 && fat is not null)
            {
                foreach (var kv in info.Sbnk)
                {
                    uint fileId = kv.Value.FileId;
                    if (fileId >= fat.Entries.Count) continue;

                    var e = fat.Entries[(int)fileId];
                    _ = SBNK.Read(stream, e.Offset, e.Size);
                }
            }

            
            SelectedBank = BankOptions.FirstOrDefault();

            LoadedFilePath = nextLoadedFilePath;
            var major = sdat.SdatVersion >> 8 & 0xFF;
            var minor = sdat.SdatVersion & 0xFF;
            StatusMessage = $"Loaded: {file.Name} ({sdat.SdatSize:N0} bytes, v{major}.{minor})";

            UpdateBlocksFrom(sdat);
        }
        catch (Exception ex)
        {
            LoadedFilePath = nextLoadedFilePath;
            StatusMessage = $"Failed to load: {ex.Message}";
            Blocks.Clear();
            RebuildSdatTree(null, null, null);
            SseqOptions.Clear();
            SseqPlayerOptions.Clear();
            SeqPlayers.Clear();
            SelectedMixerPlayer = null;
            Strms.Clear();
            SelectedStrm = null;
            ResetSseqDetails();
            SsarOptions.Clear();
            SsarSequences.Clear();
            SelectedSsar = null;
        }
    }

    private bool TryOpenSelectedBankSbnk(out Stream fs, out FAT.FatEntry fe, out SBNK sbnk, int? overrideCount = null)
    {
        fs = Stream.Null;
        fe = default;
        sbnk = null!;

        if (SelectedBank is null || _lastInfo is null)
            return false;

        if (!_lastInfo.Sbnk.TryGetValue(SelectedBank.Id, out var sbnkInfo))
            return false;

        if (!TryReadFileFromFat(sbnkInfo.FileId, out var sbnkBytes))
            return false;

        fs = new MemoryStream(sbnkBytes, writable: false);
        fe = new FAT.FatEntry(0, (uint)sbnkBytes.Length);

        try
        {
            sbnk = SBNK.Read(fs, 0, (uint)sbnkBytes.Length, overrideCount);
            return true;
        }
        catch
        {
            fs.Dispose();
            fs = Stream.Null;
            throw;
        }
    }

    private async Task LoadSelectedSbnkInstsAsync()
    {
        Insts.Clear();
        _progToRow.Clear();

        int? overrideCount = (_sbnkInstrumentCount > 0) ? _sbnkInstrumentCount : null;
        if (!TryOpenSelectedBankSbnk(out var fs, out _, out var sbnk, overrideCount))
            return;

        await using (fs)
        {
            SetSbnkInstrumentCountInternal(sbnk.Header.InstrumentCount);

            for (int i = 0; i < sbnk.Records.Count; i++)
            {
                var r = sbnk.Records[i];
                var row = new InstRow(i, r.Type, InstrumentTypeToName(r.Type));

                if (row.IsSingleEditable && r.Articulation is SBNK.SingleInst sv)
                    row.SetFromSingle(sv);

                _progToRow[i] = row;
                Insts.Add(row);
            }

            PrewarmSwavCacheForSbnk(sbnk);
        }
    }

    private static string InstrumentTypeToName(SBNK.InstrumentType t) => t switch
    {
        SBNK.InstrumentType.Null => "Null",
        SBNK.InstrumentType.Pcm => "PCM Instrument",
        SBNK.InstrumentType.Psg => "PSG Instrument",
        SBNK.InstrumentType.Noise => "Noise Instrument",
        SBNK.InstrumentType.DirectPcm => "Direct PCM Instrument",
        SBNK.InstrumentType.NullInstrument => "Null Instrument",
        SBNK.InstrumentType.DrumSet => "Drum Set",
        SBNK.InstrumentType.KeySplit => "Key Split",
        _ => "?"
    };




    private void UpdateBlocksFrom(SDAT sdat)
    {
        Blocks.Clear();
        Blocks.Add(new BlockRow { Name = "SYMB", Offset = sdat.SymbOffset, Size = sdat.SymbSize });
        Blocks.Add(new BlockRow { Name = "INFO", Offset = sdat.InfoOffset, Size = sdat.InfoSize });
        Blocks.Add(new BlockRow { Name = "FAT", Offset = sdat.FatOffset, Size = sdat.FatSize });
        Blocks.Add(new BlockRow { Name = "FILE", Offset = sdat.FileBlockOffset, Size = sdat.FileBlockSize });
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    
    private uint _lastSdatSize;
    private ushort _lastSdatVersion;

    
    public uint LastSdatSize => _lastSdatSize;
    public ushort LastSdatVersion => _lastSdatVersion;

    private int _sbnkInstrumentCount;     
    private bool _sbnkInstCountInternalSet; 

    public int SbnkInstrumentCount
    {
        get => _sbnkInstrumentCount;
        set
        {
            if (!SetField(ref _sbnkInstrumentCount, Math.Clamp(value, 0, 32767))) return;
            SyncSbnkInstrumentCountDerived();
            if (_sbnkInstCountInternalSet) return; 
                                                    
            _ = LoadSelectedSbnkInstsAsync();
        }
    }
    private int _sbnkInstrumentCountValue;
    public int SbnkInstrumentCountValue
    {
        get => _sbnkInstrumentCountValue;
        set
        {
            SbnkInstrumentCount = value;
        }
    }

    private string _sbnkInstrumentCountText = "0";
    public string SbnkInstrumentCountText
    {
        get => _sbnkInstrumentCountText;
        set
        {
            if (_sbnkInstrumentCountText == value) return;
            _sbnkInstrumentCountText = value;
            OnPropertyChanged(nameof(SbnkInstrumentCountText));

            
            if (int.TryParse(value, out var n))
                SbnkInstrumentCount = n; 
        }
    }

    private void SyncSbnkInstrumentCountDerived()
    {
        int v = _sbnkInstrumentCount;

        if (_sbnkInstrumentCountValue != v)
        {
            _sbnkInstrumentCountValue = v;
            OnPropertyChanged(nameof(SbnkInstrumentCountValue));
        }

        string t = v.ToString();
        if (_sbnkInstrumentCountText != t)
        {
            _sbnkInstrumentCountText = t;
            OnPropertyChanged(nameof(SbnkInstrumentCountText));
        }
    }

    private void SetSbnkInstrumentCountInternal(int value)
    {
        _sbnkInstCountInternalSet = true;
        try
        {
            SbnkInstrumentCount = value;
        }
        finally
        {
            _sbnkInstCountInternalSet = false;
        }
    }

    public bool TryGetPcmArticulationBytes(int instIndex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        if (!TryOpenSelectedBankSbnk(out var fs, out var fe, out var sbnk))
            return false;

        using (fs)
            return sbnk.TryGetPcmArticulationBytes(instIndex, fs, fe.Offset, out bytes);
    }

    public bool TryGetPsgArticulationBytes(int instIndex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (!TryOpenSelectedBankSbnk(out var fs, out var fe, out var sbnk))
            return false;

        using (fs)
            return sbnk.TryGetPsgArticulationBytes(instIndex, fs, fe.Offset, out bytes);
    }

    public bool TryGetNoiseArticulationBytes(int instIndex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (!TryOpenSelectedBankSbnk(out var fs, out var fe, out var sbnk))
            return false;

        using (fs)
            return sbnk.TryGetNoiseArticulationBytes(instIndex, fs, fe.Offset, out bytes);
    }

    public bool TryGetDrumSetModel(int instIndex, out SBNK.DrumSet ds)
    {
        ds = null!;
        if (!TryOpenSelectedBankSbnk(out var fs, out _, out var sbnk))
            return false;

        using (fs)
            return sbnk.TryGetDrumSet(instIndex, out ds);
    }

    public bool TryGetKeySplitModel(int instIndex, out SBNK.KeySplit ks)
    {
        ks = null!;
        if (!TryOpenSelectedBankSbnk(out var fs, out _, out var sbnk))
            return false;

        using (fs)
            return sbnk.TryGetKeySplit(instIndex, out ks);
    }

    public int PianoMinNote { get; set; } = 0;
    public int PianoMaxNote { get; set; } = 127;

}
