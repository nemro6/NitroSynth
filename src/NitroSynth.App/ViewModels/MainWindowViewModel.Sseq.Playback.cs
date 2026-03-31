using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NitroSynth.App;
using NitroSynth.App.Audio;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    private enum SseqMuteMode
    {
        Off = 0,
        Keep = 1,
        Release = 2,
        Immediate = 3
    }

    private sealed class SseqTrackRuntime
    {
        public int TrackNo;
        public int StartOffset;
        public int Offset;
        public int WaitTicks;
        public int WaitVoiceId = -1;
        public long ActivateTick;
        public bool Finished;
        public bool NoteWait = true;
        public bool TieMode;
        public int TiedVoiceId = -1;
        public int TiedNote = -1;
        public SseqMuteMode MuteMode = SseqMuteMode.Off;
        public bool ConditionalFlag;
        public bool PendingIf;
        public ushort AllocatedTracksMask = 0x0001;
        public ushort? ChannelMaskOverride;
        public short[] LocalVariables { get; } = CreateInitialVariables();
        public short[] GlobalVariables { get; init; } = Array.Empty<short>();
        public Stack<int> CallStack { get; } = new();
        public Stack<LoopFrame> LoopStack { get; } = new();
    }

    private sealed class LoopFrame
    {
        public int StartOffset;
        public int RemainingCount;
    }

    private readonly struct PendingNoteOff
    {
        public PendingNoteOff(int trackNo, int midiNote, int voiceId, long offTick)
        {
            TrackNo = trackNo;
            MidiNote = midiNote;
            VoiceId = voiceId;
            OffTick = offTick;
        }

        public int TrackNo { get; }
        public int MidiNote { get; }
        public int VoiceId { get; }
        public long OffTick { get; }
    }

    private readonly struct LoopFrameSnapshot
    {
        public LoopFrameSnapshot(int startOffset, int remainingCount)
        {
            StartOffset = startOffset;
            RemainingCount = remainingCount;
        }

        public int StartOffset { get; }
        public int RemainingCount { get; }
    }

    private sealed class SseqTrackSnapshot
    {
        public int TrackNo;
        public int StartOffset;
        public int Offset;
        public int WaitTicks;
        public int WaitVoiceId = -1;
        public long ActivateTick;
        public bool Finished;
        public bool NoteWait = true;
        public bool TieMode;
        public int TiedVoiceId = -1;
        public int TiedNote = -1;
        public SseqMuteMode MuteMode = SseqMuteMode.Off;
        public bool ConditionalFlag;
        public bool PendingIf;
        public ushort AllocatedTracksMask = 0x0001;
        public ushort? ChannelMaskOverride;
        public short[] LocalVariables = CreateInitialVariables();
        public int[] CallStackBottomToTop = Array.Empty<int>();
        public LoopFrameSnapshot[] LoopStackBottomToTop = Array.Empty<LoopFrameSnapshot>();
    }

    private sealed class SseqChannelRuntimeState
    {
        public int ProgramId;
        public int PitchBend;
        public int ModDelayFrames;
        // Stored as SSEQ command units (64 == 1 semitone).
        public int SweepPitch;
        public Dictionary<byte, byte> ControlValues { get; } = new();

        public SseqChannelRuntimeState Clone()
        {
            var clone = new SseqChannelRuntimeState
            {
                ProgramId = ProgramId,
                PitchBend = PitchBend,
                ModDelayFrames = ModDelayFrames,
                SweepPitch = SweepPitch
            };

            foreach (var (cc, value) in ControlValues)
                clone.ControlValues[cc] = value;

            return clone;
        }
    }

    private sealed class SseqExecutionContext
    {
        public SseqExecutionContext(bool silent, SseqChannelRuntimeState[]? channelStates)
        {
            Silent = silent;
            ChannelStates = channelStates;
        }

        public bool Silent { get; }
        public SseqChannelRuntimeState[]? ChannelStates { get; }
    }

    private sealed class SseqPlaybackStartState
    {
        public long NowTick;
        public double Tempo = 120.0;
        public int ExecutedCommandCount;
        public int PlayheadCommandIndex;
        public short[] GlobalVariables = CreateInitialVariables();
        public List<SseqTrackSnapshot> Tracks { get; } = new();
        public SseqChannelRuntimeState[] ChannelStates { get; set; } = CreateDefaultChannelStates();
        public Random RuntimeRandom { get; set; } = new(0);
    }

    private sealed class SseqPlaybackRuntimeException : InvalidOperationException
    {
        public SseqPlaybackRuntimeException(int trackNo, int offset, byte op, Exception innerException)
            : base($"SSEQ runtime failed at track={trackNo}, offset=0x{offset:X6}, op=0x{op:X2}.", innerException)
        {
            TrackNo = trackNo;
            Offset = offset;
            Op = op;
        }

        public int TrackNo { get; }
        public int Offset { get; }
        public byte Op { get; }
    }

    private sealed class SseqProgressMap
    {
        public int[] CommandOffsets = Array.Empty<int>();
        public Dictionary<int, int> OffsetToCommandIndex { get; } = new();
        public int CommandCount => CommandOffsets.Length;
    }

    private static short[] CreateInitialVariables()
    {
        var vars = new short[16];
        Array.Fill(vars, (short)-1);
        return vars;
    }

    private static SseqChannelRuntimeState[] CreateDefaultChannelStates()
    {
        var channels = new SseqChannelRuntimeState[16];
        for (int i = 0; i < channels.Length; i++)
            channels[i] = new SseqChannelRuntimeState { ProgramId = 0, PitchBend = 0, ModDelayFrames = 0, SweepPitch = 0 };
        return channels;
    }

    private static SseqChannelRuntimeState[] CloneChannelStates(IReadOnlyList<SseqChannelRuntimeState> src)
    {
        var copy = new SseqChannelRuntimeState[src.Count];
        for (int i = 0; i < src.Count; i++)
            copy[i] = src[i].Clone();
        return copy;
    }

    private static SseqProgressMap BuildSseqProgressMap(ReadOnlySpan<byte> eventData)
    {
        var map = new SseqProgressMap();
        if (eventData.Length == 0)
            return map;

        var offsets = new List<int>(Math.Max(8, eventData.Length / 2));
        int offset = 0;
        while (offset < eventData.Length)
        {
            int index = offsets.Count;
            offsets.Add(offset);
            map.OffsetToCommandIndex[offset] = index;

            if (SseqEventDecoder.TryDecode(eventData, offset, out var decoded, out _))
                offset += decoded.Length;
            else
                offset++;
        }

        map.CommandOffsets = offsets.ToArray();
        return map;
    }

    private static int ResolvePlayheadCommandIndex(SseqProgressMap map, int offset)
    {
        if (map.CommandCount <= 0)
            return 0;

        if (offset <= 0)
            return 0;

        if (map.OffsetToCommandIndex.TryGetValue(offset, out int commandIndex))
            return commandIndex;

        int pos = Array.BinarySearch(map.CommandOffsets, offset);
        if (pos >= 0)
            return pos;

        int insertion = ~pos;
        if (insertion <= 0)
            return 0;

        if (insertion >= map.CommandCount)
            return map.CommandCount - 1;

        return insertion - 1;
    }

    private static SseqTrackSnapshot SnapshotTrackState(SseqTrackRuntime track)
    {
        var callStackBottomToTop = track.CallStack.Reverse().ToArray();
        var loopStackBottomToTop = track.LoopStack
            .Reverse()
            .Select(f => new LoopFrameSnapshot(f.StartOffset, f.RemainingCount))
            .ToArray();

        return new SseqTrackSnapshot
        {
            TrackNo = track.TrackNo,
            StartOffset = track.StartOffset,
            Offset = track.Offset,
            WaitTicks = track.WaitTicks,
            WaitVoiceId = track.WaitVoiceId,
            ActivateTick = track.ActivateTick,
            Finished = track.Finished,
            NoteWait = track.NoteWait,
            TieMode = track.TieMode,
            TiedVoiceId = track.TiedVoiceId,
            TiedNote = track.TiedNote,
            MuteMode = track.MuteMode,
            ConditionalFlag = track.ConditionalFlag,
            PendingIf = track.PendingIf,
            AllocatedTracksMask = track.AllocatedTracksMask,
            ChannelMaskOverride = track.ChannelMaskOverride,
            LocalVariables = (short[])track.LocalVariables.Clone(),
            CallStackBottomToTop = callStackBottomToTop,
            LoopStackBottomToTop = loopStackBottomToTop
        };
    }

    private static SseqTrackRuntime RestoreTrackRuntime(SseqTrackSnapshot snapshot, short[] sharedGlobalVariables)
    {
        var track = new SseqTrackRuntime
        {
            TrackNo = snapshot.TrackNo,
            StartOffset = snapshot.StartOffset,
            Offset = snapshot.Offset,
            WaitTicks = snapshot.WaitTicks,
            WaitVoiceId = snapshot.WaitVoiceId,
            ActivateTick = snapshot.ActivateTick,
            Finished = snapshot.Finished,
            NoteWait = snapshot.NoteWait,
            TieMode = snapshot.TieMode,
            TiedVoiceId = snapshot.TiedVoiceId,
            TiedNote = snapshot.TiedNote,
            MuteMode = snapshot.MuteMode,
            ConditionalFlag = snapshot.ConditionalFlag,
            PendingIf = snapshot.PendingIf,
            AllocatedTracksMask = snapshot.AllocatedTracksMask,
            ChannelMaskOverride = snapshot.ChannelMaskOverride,
            GlobalVariables = sharedGlobalVariables
        };

        Array.Copy(snapshot.LocalVariables, track.LocalVariables, Math.Min(snapshot.LocalVariables.Length, track.LocalVariables.Length));

        foreach (var addr in snapshot.CallStackBottomToTop)
            track.CallStack.Push(addr);

        foreach (var loop in snapshot.LoopStackBottomToTop)
        {
            track.LoopStack.Push(new LoopFrame
            {
                StartOffset = loop.StartOffset,
                RemainingCount = loop.RemainingCount
            });
        }

        return track;
    }

    private CancellationTokenSource? _sseqPlaybackCts;
    private Task? _sseqPlaybackTask;

    private byte[] _activeSseqPlaybackEventData = Array.Empty<byte>();
    private int _activeSseqPlaybackSeed;
    private SseqProgressMap _activeSseqProgressMap = new();
    private int _sseqExecutedCommandCount;
    private int _sseqPlayheadCommandIndex;
    private int _sseqTotalCommandCount;

    private byte[] _pausedSseqEventData = Array.Empty<byte>();
    private int _pausedSseqPlaybackSeed;
    private int _pausedSseqCommandIndex;
    private int _pausedSseqTrack0StartOffset;
    private int _pausedSseqSequencePriorityBase;
    private bool _sseqPauseRequested;
    private int _activeSseqTrack0StartOffset;
    private string _activeSseqPlaybackLabel = "SSEQ";
    private int _activeSseqSequencePriorityBase;
    private readonly Dictionary<int, ushort?> _sseqTrackChannelMaskOverrides = new();

    private bool _isSseqPlaying;
    public bool IsSseqPlaying
    {
        get => _isSseqPlaying;
        private set
        {
            if (!SetField(ref _isSseqPlaying, value))
                return;

            UpdateRightStatusBlinking();
            OnPropertyChanged(nameof(CanPlaySseq));
            OnPropertyChanged(nameof(CanStopSseq));
            OnPropertyChanged(nameof(CanPauseSseq));
            OnPropertyChanged(nameof(SseqPauseButtonText));
        }
    }

    private bool _isSseqPaused;
    public bool IsSseqPaused
    {
        get => _isSseqPaused;
        private set
        {
            if (!SetField(ref _isSseqPaused, value))
                return;

            OnPropertyChanged(nameof(CanPlaySseq));
            OnPropertyChanged(nameof(CanStopSseq));
            OnPropertyChanged(nameof(CanPauseSseq));
            OnPropertyChanged(nameof(SseqPauseButtonText));
        }
    }

    private double _sseqCommandProgress;
    public double SseqCommandProgress
    {
        get => _sseqCommandProgress;
        private set => SetField(ref _sseqCommandProgress, Math.Clamp(value, 0.0, 1.0));
    }

    public bool CanPlaySseq => !IsSseqPlaying && !IsSseqPaused && SelectedSseq is not null;
    public bool CanStopSseq => IsSseqPlaying || IsSseqPaused;
    public bool CanPauseSseq => IsSseqPlaying || IsSseqPaused;
    public string SseqPauseButtonText => IsSseqPaused ? "RESUME" : "PAUSE";

    private void UpdateSseqPlaybackProgress(int playheadCommandIndex, int totalCommands)
    {
        if (totalCommands <= 0)
        {
            SseqCommandProgress = 0.0;
            return;
        }

        int maxIndex = Math.Max(0, totalCommands - 1);
        if (maxIndex <= 0)
        {
            SseqCommandProgress = 0.0;
        }
        else
        {
            SseqCommandProgress = (double)Math.Clamp(playheadCommandIndex, 0, maxIndex) / maxIndex;
        }
    }

    private void CachePauseResumeState()
    {
        _pausedSseqEventData = _activeSseqPlaybackEventData.Length == 0
            ? Array.Empty<byte>()
            : (byte[])_activeSseqPlaybackEventData.Clone();
        _pausedSseqPlaybackSeed = _activeSseqPlaybackSeed;
        _pausedSseqCommandIndex = _sseqExecutedCommandCount;
        _pausedSseqTrack0StartOffset = _activeSseqTrack0StartOffset;
        _pausedSseqSequencePriorityBase = _activeSseqSequencePriorityBase;
    }

    private void ClearPauseResumeState()
    {
        _pausedSseqEventData = Array.Empty<byte>();
        _pausedSseqPlaybackSeed = 0;
        _pausedSseqCommandIndex = 0;
        _pausedSseqTrack0StartOffset = 0;
        _pausedSseqSequencePriorityBase = 0;
    }

    private async Task<bool> PrepareSelectedSseqPlaybackBankAsync()
    {
        if (SelectedSseqBank is null)
        {
            StatusMessage = "Select a playback SBNK before playing SSEQ.";
            return false;
        }

        SelectedBank = SelectedSseqBank;
        await LoadSelectedSbnkInstsAsync();
        return true;
    }

    public async Task StartSelectedSseqPlaybackAsync()
    {
        _sseqPauseRequested = false;
        IsSseqPaused = false;
        ClearPauseResumeState();

        await StopSelectedSseqPlaybackAsync();

        if (!TryGetSelectedSseqPlaybackEventData(out var playbackEventData, out var playbackError))
        {
            StatusMessage = playbackError ?? "SSEQ data is not available.";
            return;
        }

        if (!await PrepareSelectedSseqPlaybackBankAsync())
            return;

        int playbackSeed = Guid.NewGuid().GetHashCode();
        int sequencePriorityBase = ResolveSequencePriorityBase(SelectedSseqChannelPriority, SelectedSseqPlayerPriority);
        await RunSseqPlaybackSessionAsync(
            playbackEventData,
            startState: null,
            playbackSeed: playbackSeed,
            startStatus: $"SSEQ playback started: {SelectedSseq?.Display ?? "SSEQ"}",
            track0StartOffset: 0,
            playbackLabel: SelectedSseq?.Display ?? "SSEQ",
            sequencePriorityBase: sequencePriorityBase,
            sequenceMainVolume: SelectedSseqVolume);
    }

    public async Task ToggleSelectedSseqPauseAsync()
    {
        if (IsSseqPlaying)
        {
            if (_sseqPauseRequested)
                return;

            CachePauseResumeState();
            _sseqPauseRequested = true;
            _sseqPlaybackCts?.Cancel();
            StopAllActiveVoicesImmediate();
            StatusMessage = $"SSEQ pausing: {SelectedSseq?.Display ?? "SSEQ"}";
            return;
        }

        if (IsSseqPaused)
            await ResumePausedSseqPlaybackAsync();
    }

    private async Task ResumePausedSseqPlaybackAsync()
    {
        if (!IsSseqPaused)
            return;

        if (_pausedSseqEventData.Length == 0)
        {
            IsSseqPaused = false;
            StatusMessage = "No paused SSEQ state to resume.";
            return;
        }

        if (!await PrepareSelectedSseqPlaybackBankAsync())
            return;

        int targetCommand = Math.Max(0, _pausedSseqCommandIndex);
        if (!TryBuildSilentRestoreStartState(
            _pausedSseqEventData,
            targetCommand,
            _pausedSseqPlaybackSeed,
            _pausedSseqTrack0StartOffset,
            out var restoredState,
            out var restoreError))
        {
            IsSseqPaused = false;
            ClearPauseResumeState();
            StatusMessage = restoreError ?? "SSEQ pause restore failed.";
            return;
        }

        await RunSseqPlaybackSessionAsync(
            _pausedSseqEventData,
            restoredState,
            _pausedSseqPlaybackSeed,
            startStatus: $"SSEQ playback resumed: {SelectedSseq?.Display ?? "SSEQ"}",
            track0StartOffset: _pausedSseqTrack0StartOffset,
            playbackLabel: SelectedSseq?.Display ?? "SSEQ",
            sequencePriorityBase: _pausedSseqSequencePriorityBase,
            sequenceMainVolume: SelectedSseqVolume);
    }

    private async Task RunSseqPlaybackSessionAsync(
        byte[] playbackEventData,
        SseqPlaybackStartState? startState,
        int playbackSeed,
        string startStatus,
        int track0StartOffset = 0,
        string? playbackLabel = null,
        int sequencePriorityBase = 0,
        int sequenceMainVolume = 127)
    {
        ResetMidi();
        StopAllActiveVoicesImmediate();
        ApplyActiveSequencePriorityBase(sequencePriorityBase);

        if (startState is not null)
        {
            ApplyRestoredChannelStates(startState.ChannelStates);
        }
        else
        {
            ApplySseqMainVolumeToAllChannels(sequenceMainVolume, executionContext: null);
        }

        _activeSseqPlaybackEventData = (byte[])playbackEventData.Clone();
        _activeSseqPlaybackSeed = playbackSeed;
        _activeSseqTrack0StartOffset = playbackEventData.Length == 0
            ? 0
            : Math.Clamp(track0StartOffset, 0, playbackEventData.Length - 1);
        _activeSseqPlaybackLabel = string.IsNullOrWhiteSpace(playbackLabel)
            ? (SelectedSseq?.Display ?? "SSEQ")
            : playbackLabel;
        _activeSseqSequencePriorityBase = Math.Clamp(sequencePriorityBase, 0, 255);
        _activeSseqProgressMap = BuildSseqProgressMap(playbackEventData);
        _sseqTotalCommandCount = _activeSseqProgressMap.CommandCount;
        _sseqExecutedCommandCount = Math.Max(0, startState?.ExecutedCommandCount ?? 0);
        _sseqPlayheadCommandIndex = Math.Clamp(
            startState?.PlayheadCommandIndex ?? 0,
            0,
            Math.Max(0, _sseqTotalCommandCount - 1));
        UpdateSseqPlaybackProgress(_sseqPlayheadCommandIndex, _sseqTotalCommandCount);

        var cts = new CancellationTokenSource();
        _sseqPlaybackCts = cts;
        IsSseqPaused = false;
        IsSseqPlaying = true;
        SetRightStatusBlinkTempo(startState?.Tempo ?? 120.0);
        StatusMessage = startStatus;

        var runtimeRandom = startState?.RuntimeRandom ?? new Random(playbackSeed);
        _sseqPlaybackTask = RunSseqPlaybackAsync(
            playbackEventData,
            cts.Token,
            startState,
            runtimeRandom,
            _activeSseqTrack0StartOffset);

        bool completedNaturally = false;
        bool hadError = false;

        try
        {
            await _sseqPlaybackTask;
            completedNaturally = !cts.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            string? logPath = TryWriteSseqPlaybackErrorLog(ex);
            hadError = true;
            StatusMessage = string.IsNullOrWhiteSpace(logPath)
                ? $"SSEQ playback error: {ex.Message}"
                : $"SSEQ playback error: {ex.Message} (log: {logPath})";
        }
        finally
        {
            bool pauseRequested = _sseqPauseRequested;
            _sseqPauseRequested = false;

            if (ReferenceEquals(_sseqPlaybackCts, cts))
            {
                _sseqPlaybackCts?.Dispose();
                _sseqPlaybackCts = null;
                _sseqPlaybackTask = null;
            }

            StopAllActiveVoicesImmediate();
            ClearActiveSequencePriorityBase();
            IsSseqPlaying = false;

            if (pauseRequested)
            {
                IsSseqPaused = true;
                _pausedSseqCommandIndex = Math.Max(0, _sseqExecutedCommandCount);
                UpdateSseqPlaybackProgress(_sseqPlayheadCommandIndex, _sseqTotalCommandCount);
                StatusMessage = $"SSEQ paused: {SelectedSseq?.Display ?? "SSEQ"}";
            }
            else
            {
                IsSseqPaused = false;
                if (!completedNaturally && !hadError && cts.IsCancellationRequested)
                    StatusMessage = $"SSEQ playback stopped: {SelectedSseq?.Display ?? "SSEQ"}";
                else if (completedNaturally && !hadError)
                    StatusMessage = $"SSEQ playback finished: {SelectedSseq?.Display ?? "SSEQ"}";

                ClearPauseResumeState();
            }
        }
    }

    public void StopSelectedSseqPlayback()
    {
        _sseqPauseRequested = false;
        _sseqPlaybackCts?.Cancel();
        StopAllActiveVoicesImmediate();

        if (!IsSseqPlaying)
        {
            ClearActiveSequencePriorityBase();
            IsSseqPaused = false;
            ClearPauseResumeState();
            _sseqExecutedCommandCount = 0;
            _sseqPlayheadCommandIndex = 0;
            UpdateSseqPlaybackProgress(_sseqPlayheadCommandIndex, _sseqTotalCommandCount);
        }
    }

    public async Task StopSelectedSseqPlaybackAsync()
    {
        _sseqPauseRequested = false;

        var cts = _sseqPlaybackCts;
        var task = _sseqPlaybackTask;

        cts?.Cancel();
        StopAllActiveVoicesImmediate();

        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        if (!IsSseqPlaying)
        {
            ClearActiveSequencePriorityBase();
            IsSseqPaused = false;
            ClearPauseResumeState();
            _sseqExecutedCommandCount = 0;
            _sseqPlayheadCommandIndex = 0;
            UpdateSseqPlaybackProgress(_sseqPlayheadCommandIndex, _sseqTotalCommandCount);
        }
    }

    private void ApplyRestoredChannelStates(IReadOnlyList<SseqChannelRuntimeState> channelStates)
    {
        int channelCount = Math.Min(16, channelStates.Count);
        for (int ch = 0; ch < channelCount; ch++)
        {
            var state = channelStates[ch];
            SetSseqProgram(ch, state.ProgramId, executionContext: null);

            foreach (var ccPair in state.ControlValues)
                ApplySseqControlChange(ch, ccPair.Key, ccPair.Value, executionContext: null);

            SetSseqPitchBend(ch, state.PitchBend, executionContext: null);
            AudioEngine.Instance.SetModDelayFrames(ch, state.ModDelayFrames);
            SetSseqSweepPitch(ch, state.SweepPitch, executionContext: null);
        }
    }

    private bool TryBuildSilentRestoreStartState(
        byte[] eventData,
        int targetExecutedCommands,
        int playbackSeed,
        int track0StartOffset,
        out SseqPlaybackStartState state,
        out string? error)
    {
        state = null!;
        error = null;

        targetExecutedCommands = Math.Max(0, targetExecutedCommands);

        int track0Offset = eventData.Length == 0 ? 0 : Math.Clamp(track0StartOffset, 0, eventData.Length - 1);
        var globalVariables = CreateInitialVariables();
        var tracks = new Dictionary<int, SseqTrackRuntime>
        {
            [0] = new SseqTrackRuntime
            {
                TrackNo = 0,
                StartOffset = track0Offset,
                Offset = track0Offset,
                ActivateTick = 0,
                AllocatedTracksMask = 0x0001,
                ChannelMaskOverride = GetSseqTrackChannelMaskOverride(0),
                GlobalVariables = globalVariables
            }
        };

        var pendingNoteOffs = new List<PendingNoteOff>();
        double tempo = 120.0;
        long nowTick = 0;
        int executedCommands = 0;
        var progressMap = BuildSseqProgressMap(eventData);
        int playheadCommandIndex = ResolvePlayheadCommandIndex(progressMap, tracks[0].Offset);
        var runtimeRandom = new Random(playbackSeed);

        var silentChannels = CreateDefaultChannelStates();
        var executionContext = new SseqExecutionContext(silent: true, channelStates: silentChannels);

        const long maxOuterLoop = 8_000_000;
        long outerLoop = 0;

        while (executedCommands < targetExecutedCommands)
        {
            if (outerLoop++ > maxOuterLoop)
            {
                error = "SSEQ pause restore aborted: simulation exceeded guard limit.";
                return false;
            }

            foreach (var track in tracks.Values)
            {
                // Silent simulation has no active voices, so notewait-by-voice cannot be resolved.
                if (track.WaitVoiceId >= 0)
                    track.WaitVoiceId = -1;
            }

            int globalReadyPassGuard = 0;
            while (globalReadyPassGuard++ < 4096 && executedCommands < targetExecutedCommands)
            {
                var readyTracks = tracks.Values
                    .Where(t => !t.Finished && t.WaitTicks <= 0 && t.WaitVoiceId < 0 && t.ActivateTick <= nowTick)
                    .OrderBy(t => t.TrackNo)
                    .ToList();

                if (readyTracks.Count == 0)
                    break;

                bool progressed = false;

                foreach (var track in readyTracks)
                {
                    int guard = 0;
                    while (!track.Finished &&
                           track.WaitTicks <= 0 &&
                           track.WaitVoiceId < 0 &&
                           guard++ < 1024 &&
                           executedCommands < targetExecutedCommands)
                    {
                        int eventOffset = track.Offset;
                        byte eventOp = (uint)eventOffset < (uint)eventData.Length
                            ? eventData[eventOffset]
                            : (byte)0x00;

                        try
                        {
                            if (!TryExecuteSseqEvent(
                                eventData,
                                tracks,
                                track,
                                pendingNoteOffs,
                                nowTick,
                                ref tempo,
                                runtimeRandom,
                                executionContext))
                            {
                                track.Finished = true;
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            error = $"Silent restore failed at track={track.TrackNo}, offset=0x{eventOffset:X6}, op=0x{eventOp:X2}: {ex.Message}";
                            return false;
                        }

                        executedCommands++;
                        if (track.TrackNo == 0)
                            playheadCommandIndex = ResolvePlayheadCommandIndex(progressMap, track.Offset);
                        progressed = true;
                    }
                }

                if (!progressed)
                    break;
            }

            bool anyTrackActive = tracks.Values.Any(t => !t.Finished);
            if (!anyTrackActive)
                break;

            int advanceTicks = int.MaxValue;
            foreach (var track in tracks.Values)
            {
                if (!track.Finished && track.WaitTicks > 0)
                    advanceTicks = Math.Min(advanceTicks, track.WaitTicks);
            }

            if (advanceTicks == int.MaxValue)
            {
                bool anyWaitingVoice = tracks.Values.Any(t => !t.Finished && t.WaitVoiceId >= 0);
                if (anyWaitingVoice)
                    continue;

                advanceTicks = 1;
            }
            else if (advanceTicks <= 0)
            {
                advanceTicks = 1;
            }

            nowTick += advanceTicks;
            foreach (var track in tracks.Values)
            {
                if (!track.Finished && track.WaitTicks > 0)
                    track.WaitTicks = Math.Max(0, track.WaitTicks - advanceTicks);
            }
        }

        state = new SseqPlaybackStartState
        {
            NowTick = Math.Max(0, nowTick),
            Tempo = Math.Clamp(tempo, 1.0, 1023.0),
            ExecutedCommandCount = executedCommands,
            PlayheadCommandIndex = playheadCommandIndex,
            GlobalVariables = (short[])globalVariables.Clone(),
            RuntimeRandom = runtimeRandom,
            ChannelStates = CloneChannelStates(silentChannels)
        };

        foreach (var track in tracks.Values.OrderBy(t => t.TrackNo))
            state.Tracks.Add(SnapshotTrackState(track));

        return true;
    }

    private void StopAllActiveVoicesImmediate()
    {
        for (int ch = 0; ch < _activeVoices.Length; ch++)
        {
            var dict = _activeVoices[ch];
            foreach (var voiceId in dict.Values.ToArray())
                AudioEngine.Instance.StopVoice(voiceId);
            dict.Clear();

            if ((uint)ch < (uint)MixerStrips.Count)
            {
                MixerStrips[ch].ActiveNote = -1;
                MixerStrips[ch].Velocity = 0;
                MixerStrips[ch].Level = 0;
            }
        }

        ClearPianoMidiNotes();
    }

    private void ReleaseAllActiveVoicesForTrack(int trackNo)
    {
        if ((uint)trackNo >= (uint)_activeVoices.Length)
            return;

        var dict = _activeVoices[trackNo];
        foreach (var kv in dict.ToArray())
        {
            AudioEngine.Instance.NoteOff(kv.Value);
            SetPianoMidiNoteOff(trackNo, kv.Key);
        }
        dict.Clear();

        if ((uint)trackNo < (uint)MixerStrips.Count)
            MixerStrips[trackNo].ActiveNote = -1;
    }

    private void StopAllActiveVoicesForTrackImmediate(int trackNo)
    {
        if ((uint)trackNo >= (uint)_activeVoices.Length)
            return;

        var dict = _activeVoices[trackNo];
        foreach (var kv in dict.ToArray())
        {
            AudioEngine.Instance.StopVoice(kv.Value);
            SetPianoMidiNoteOff(trackNo, kv.Key);
        }
        dict.Clear();

        if ((uint)trackNo < (uint)MixerStrips.Count)
            MixerStrips[trackNo].ActiveNote = -1;
    }

    private static void RemovePendingNoteOffsForTrack(List<PendingNoteOff> pendingNoteOffs, int trackNo)
    {
        for (int i = pendingNoteOffs.Count - 1; i >= 0; i--)
        {
            if (pendingNoteOffs[i].TrackNo == trackNo)
                pendingNoteOffs.RemoveAt(i);
        }
    }

    public void SetSseqTrackChannelMaskOverride(int trackNo, ushort? channelMask)
    {
        if (trackNo < 0 || trackNo > 15)
            return;

        if (!channelMask.HasValue)
        {
            _sseqTrackChannelMaskOverrides.Remove(trackNo);
            return;
        }

        _sseqTrackChannelMaskOverrides[trackNo] = channelMask.Value;
    }

    public void ClearSseqTrackChannelMaskOverrides()
    {
        _sseqTrackChannelMaskOverrides.Clear();
    }

    private ushort? GetSseqTrackChannelMaskOverride(int trackNo)
    {
        return _sseqTrackChannelMaskOverrides.TryGetValue(trackNo, out ushort? channelMask)
            ? channelMask
            : null;
    }

    private ushort ResolveEffectiveTrackChannelMask(SseqTrackRuntime track)
    {
        if (track.ChannelMaskOverride.HasValue)
            return NormalizeChannelMask(track.ChannelMaskOverride.Value);

        return ResolveSelectedMixerPlayerChannelMask();
    }

    private static bool HasReachedCombinedNestingLimit(SseqTrackRuntime track)
    {
        return track.CallStack.Count + track.LoopStack.Count >= 3;
    }

    private static bool IsTrackAllocated(SseqTrackRuntime track, int trackNo)
    {
        if (trackNo == 0)
            return true;

        if ((uint)trackNo > 15u)
            return false;

        return (track.AllocatedTracksMask & (1u << trackNo)) != 0;
    }

    private static int ResolveSequencePriorityBase(int channelPriority, int playerPriority)
    {
        return Math.Clamp(channelPriority + playerPriority, 0, 255);
    }

    private void ApplySseqMainVolumeToAllChannels(int value, SseqExecutionContext? executionContext)
    {
        int clamped = Math.Clamp(value, 0, 127);
        for (int ch = 0; ch < 16; ch++)
            ApplySseqControlChange(ch, 12, clamped, executionContext);
    }

    private void ApplyActiveSequencePriorityBase(int sequencePriorityBase)
    {
        _activeSseqSequencePriorityBase = Math.Clamp(sequencePriorityBase, 0, 255);
        AudioEngine.Instance.SetSequencePriorityBaseAll(_activeSseqSequencePriorityBase);
    }

    private void ClearActiveSequencePriorityBase()
    {
        _activeSseqSequencePriorityBase = 0;
        AudioEngine.Instance.SetSequencePriorityBaseAll(0);
    }

    private async Task RunSseqPlaybackAsync(
        byte[] eventData,
        CancellationToken token,
        SseqPlaybackStartState? startState,
        Random runtimeRandom,
        int track0StartOffset)
    {
        short[] globalVariables;
        Dictionary<int, SseqTrackRuntime> tracks;
        double tempo;
        long nowTick;
        int executedCommands;
        int playheadCommandIndex;
        int track0Offset = eventData.Length == 0 ? 0 : Math.Clamp(track0StartOffset, 0, eventData.Length - 1);

        if (startState is null)
        {
            globalVariables = CreateInitialVariables();
            tracks = new Dictionary<int, SseqTrackRuntime>
            {
                [0] = new SseqTrackRuntime
                {
                    TrackNo = 0,
                    StartOffset = track0Offset,
                    Offset = track0Offset,
                    ActivateTick = 0,
                    AllocatedTracksMask = 0x0001,
                    ChannelMaskOverride = GetSseqTrackChannelMaskOverride(0),
                    GlobalVariables = globalVariables
                }
            };
            tempo = 120.0;
            nowTick = 0;
            executedCommands = 0;
            playheadCommandIndex = ResolvePlayheadCommandIndex(_activeSseqProgressMap, tracks[0].Offset);
        }
        else
        {
            globalVariables = (short[])startState.GlobalVariables.Clone();
            tracks = new Dictionary<int, SseqTrackRuntime>();
            foreach (var snapshot in startState.Tracks)
                tracks[snapshot.TrackNo] = RestoreTrackRuntime(snapshot, globalVariables);

            if (!tracks.ContainsKey(0))
            {
                tracks[0] = new SseqTrackRuntime
                {
                    TrackNo = 0,
                    StartOffset = track0Offset,
                    Offset = track0Offset,
                    ActivateTick = 0,
                    AllocatedTracksMask = 0x0001,
                    ChannelMaskOverride = GetSseqTrackChannelMaskOverride(0),
                    GlobalVariables = globalVariables
                };
            }

            tempo = startState.Tempo;
            nowTick = Math.Max(0, startState.NowTick);
            executedCommands = Math.Max(0, startState.ExecutedCommandCount);
            playheadCommandIndex = ResolvePlayheadCommandIndex(_activeSseqProgressMap, tracks[0].Offset);
        }

        _sseqExecutedCommandCount = executedCommands;
        _sseqPlayheadCommandIndex = playheadCommandIndex;
        UpdateSseqPlaybackProgress(_sseqPlayheadCommandIndex, _sseqTotalCommandCount);

        var pendingNoteOffs = new List<PendingNoteOff>();
        var executionContext = new SseqExecutionContext(silent: false, channelStates: null);

        const double soundFrameHz = 192.0;
        const double frameDurationMs = 1000.0 / soundFrameHz;
        var cancelSignal = Task.Delay(Timeout.Infinite, token);
        var wallClock = Stopwatch.StartNew();
        double scheduledTimeMs = 0.0;
        double tickAccumulator = 0.0;

        while (!token.IsCancellationRequested)
        {
            foreach (var track in tracks.Values)
            {
                if (track.WaitVoiceId >= 0 && !AudioEngine.Instance.IsVoiceActive(track.WaitVoiceId))
                    track.WaitVoiceId = -1;
            }

            for (int i = pendingNoteOffs.Count - 1; i >= 0; i--)
            {
                var pending = pendingNoteOffs[i];
                if (pending.OffTick > nowTick)
                    continue;

                if ((uint)pending.TrackNo < (uint)_activeVoices.Length &&
                    _activeVoices[pending.TrackNo].TryGetValue(pending.MidiNote, out var activeVoiceId) &&
                    activeVoiceId == pending.VoiceId)
                {
                    StopMidiNote(pending.TrackNo, pending.MidiNote);
                    SetPianoMidiNoteOff(pending.TrackNo, pending.MidiNote);
                }
                else
                {
                    AudioEngine.Instance.NoteOff(pending.VoiceId);
                }

                pendingNoteOffs.RemoveAt(i);
            }

            int globalReadyPassGuard = 0;
            while (globalReadyPassGuard++ < 4096)
            {
                var readyTracks = tracks.Values
                    .Where(t => !t.Finished && t.WaitTicks <= 0 && t.WaitVoiceId < 0 && t.ActivateTick <= nowTick)
                    .OrderBy(t => t.TrackNo)
                    .ToList();

                if (readyTracks.Count == 0)
                    break;

                bool progressed = false;

                foreach (var track in readyTracks)
                {
                    int guard = 0;
                    while (!track.Finished && track.WaitTicks <= 0 && track.WaitVoiceId < 0 && guard++ < 1024)
                    {
                        int eventOffset = track.Offset;
                        byte eventOp = (uint)eventOffset < (uint)eventData.Length
                            ? eventData[eventOffset]
                            : (byte)0x00;
                        try
                        {
                            if (!TryExecuteSseqEvent(
                                eventData,
                                tracks,
                                track,
                                pendingNoteOffs,
                                nowTick,
                                ref tempo,
                                runtimeRandom,
                                executionContext))
                            {
                                track.Finished = true;
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            throw new SseqPlaybackRuntimeException(track.TrackNo, eventOffset, eventOp, ex);
                        }

                        executedCommands++;
                        _sseqExecutedCommandCount = executedCommands;
                        if (track.TrackNo == 0)
                        {
                            int nextPlayheadIndex = ResolvePlayheadCommandIndex(_activeSseqProgressMap, track.Offset);
                            if (nextPlayheadIndex != playheadCommandIndex)
                            {
                                playheadCommandIndex = nextPlayheadIndex;
                                _sseqPlayheadCommandIndex = playheadCommandIndex;
                                UpdateSseqPlaybackProgress(_sseqPlayheadCommandIndex, _sseqTotalCommandCount);
                            }
                        }

                        progressed = true;
                    }
                }

                if (!progressed)
                    break;
            }

            bool anyTrackActive = tracks.Values.Any(t => !t.Finished);
            if (!anyTrackActive && pendingNoteOffs.Count == 0)
                break;

            scheduledTimeMs += frameDurationMs;
            double nowMs = wallClock.Elapsed.TotalMilliseconds;
            double remainingMs = scheduledTimeMs - nowMs;
            if (remainingMs <= 0.0)
            {
                scheduledTimeMs = nowMs;
            }
            else
            {
                var completed = await Task.WhenAny(
                    Task.Delay(TimeSpan.FromMilliseconds(remainingMs)),
                    cancelSignal);
                if (completed == cancelSignal)
                    break;
            }

            double ticksThisFrame = Math.Clamp(tempo, 1.0, 1023.0) / 240.0;
            tickAccumulator += ticksThisFrame;

            int advanceTicks = (int)Math.Floor(tickAccumulator);
            if (advanceTicks <= 0)
                continue;

            tickAccumulator -= advanceTicks;
            nowTick += advanceTicks;
            foreach (var track in tracks.Values)
            {
                if (!track.Finished && track.WaitTicks > 0)
                    track.WaitTicks = Math.Max(0, track.WaitTicks - advanceTicks);
            }
        }

        _sseqExecutedCommandCount = executedCommands;
        _sseqPlayheadCommandIndex = playheadCommandIndex;
        UpdateSseqPlaybackProgress(_sseqPlayheadCommandIndex, _sseqTotalCommandCount);

    }

    private bool TryExecuteSseqEvent(
        ReadOnlySpan<byte> data,
        Dictionary<int, SseqTrackRuntime> tracks,
        SseqTrackRuntime track,
        List<PendingNoteOff> pendingNoteOffs,
        long nowTick,
        ref double tempo,
        Random runtimeRandom,
        SseqExecutionContext executionContext)
    {
        if ((uint)track.Offset >= (uint)data.Length)
        {
            track.Finished = true;
            return true;
        }

        if (!SseqEventDecoder.TryDecode(data, track.Offset, out var decoded, out var decodeError))
        {
            byte op = data[track.Offset];
            throw new InvalidOperationException(
                $"track={track.TrackNo} offset=0x{track.Offset:X6} op=0x{op:X2}: {decodeError ?? "decode failed"}");
        }

        if (track.PendingIf)
        {
            bool executeNext = track.ConditionalFlag;
            track.PendingIf = false;
            if (!executeNext)
            {
                track.Offset += decoded.Length;
                return true;
            }
        }

        int ch = Math.Clamp(track.TrackNo, 0, 15);

        switch (decoded.Kind)
        {
            case SseqDecodedKind.Note:
                ExecuteNoteEvent(track, ch, decoded.Value0, decoded.Value1, decoded.Value2, tempo, pendingNoteOffs, nowTick, executionContext);
                track.Offset += decoded.Length;
                return true;

            case SseqDecodedKind.Wait:
                if (decoded.Value0 < 0)
                    throw new InvalidOperationException($"Negative wait is invalid: {decoded.Value0}");
                track.Offset += decoded.Length;
                track.WaitTicks = decoded.Value0;
                return true;

            case SseqDecodedKind.Program:
            {
                if (decoded.Value0 is < 0 or > 32767)
                    throw new InvalidOperationException($"prg raw value out of range [0..32767]: {decoded.Value0}");
                int program = decoded.Value0;
                SetSseqProgram(ch, program, executionContext);
                track.Offset += decoded.Length;
                return true;
            }
            case SseqDecodedKind.OpenTrack:
            {
                int newTrackNo = decoded.Value0 & 0x0F;
                if (!IsTrackAllocated(track, newTrackNo))
                    throw new InvalidOperationException($"opentrack target track {newTrackNo} is not allocated.");

                int dest = decoded.Value1;
                if ((uint)dest >= (uint)data.Length)
                    throw new InvalidOperationException($"opentrack destination is out of range: 0x{dest:X6}");

                long activateTick = nowTick;
                if (newTrackNo < track.TrackNo)
                    activateTick = nowTick + 1;

                if (tracks.TryGetValue(newTrackNo, out _))
                {
                    RemovePendingNoteOffsForTrack(pendingNoteOffs, newTrackNo);
                    if (!executionContext.Silent)
                        StopAllActiveVoicesForTrackImmediate(newTrackNo);
                }

                tracks[newTrackNo] = new SseqTrackRuntime
                {
                    TrackNo = newTrackNo,
                    StartOffset = dest,
                    Offset = dest,
                    ActivateTick = activateTick,
                    AllocatedTracksMask = track.AllocatedTracksMask,
                    ChannelMaskOverride = GetSseqTrackChannelMaskOverride(newTrackNo),
                    GlobalVariables = track.GlobalVariables
                };
                track.Offset += decoded.Length;
                return true;
            }
            case SseqDecodedKind.Jump:
            {
                int jump = decoded.Value0;
                if ((uint)jump >= (uint)data.Length)
                    throw new InvalidOperationException($"jump destination is out of range: 0x{jump:X6}");
                track.Offset = jump;
                return true;
            }
            case SseqDecodedKind.Call:
            {
                int callDest = decoded.Value0;
                if ((uint)callDest >= (uint)data.Length)
                    throw new InvalidOperationException($"call destination is out of range: 0x{callDest:X6}");
                if (HasReachedCombinedNestingLimit(track))
                    throw new InvalidOperationException("call exceeds max nesting depth (call+loop <= 3).");
                track.CallStack.Push(track.Offset + decoded.Length);
                track.Offset = callDest;
                return true;
            }
            case SseqDecodedKind.If:
                track.PendingIf = true;
                track.Offset += decoded.Length;
                return true;

            case SseqDecodedKind.Variable:
                ExecuteVariableCommand(track, decoded.Op, decoded.Value0, decoded.Signed0, runtimeRandom);
                track.Offset += decoded.Length;
                return true;

            case SseqDecodedKind.Random:
                ExecuteRandomWrapper(data, tracks, track, pendingNoteOffs, nowTick, ch, ref tempo, decoded, runtimeRandom, executionContext, decoded.Length);
                track.Offset += decoded.Length;
                return true;

            case SseqDecodedKind.FromVariable:
                ExecuteFromVariableWrapper(data, tracks, track, pendingNoteOffs, nowTick, ch, ref tempo, decoded, runtimeRandom, executionContext, decoded.Length);
                track.Offset += decoded.Length;
                return true;

            case SseqDecodedKind.SimpleByte:
                ExecuteSimpleByteCommand(track, ch, decoded.Op, decoded.Value0, executionContext, decoded.Length);
                track.Offset += decoded.Length;
                return true;

            case SseqDecodedKind.ModDelay:
                ApplySseqControlChange(ch, 26, decoded.Value0, executionContext);
                track.Offset += decoded.Length;
                return true;

            case SseqDecodedKind.Tempo:
                tempo = Math.Clamp(decoded.Value0, 1, 1023);
                SetRightStatusBlinkTempo(tempo);
                track.Offset += decoded.Length;
                return true;

            case SseqDecodedKind.SweepPitch:
                SetSseqSweepPitch(ch, decoded.Signed0, executionContext);
                track.Offset += decoded.Length;
                return true;

            case SseqDecodedKind.LoopEnd:
                if (track.LoopStack.Count == 0)
                {
                    track.Offset += decoded.Length;
                    return true;
                }
            {
                var frame = track.LoopStack.Peek();
                if (frame.RemainingCount == 0)
                {
                    track.Offset = frame.StartOffset;
                }
                else if (frame.RemainingCount > 1)
                {
                    frame.RemainingCount--;
                    track.Offset = frame.StartOffset;
                }
                else
                {
                    track.LoopStack.Pop();
                    track.Offset += decoded.Length;
                }
                return true;
            }
            case SseqDecodedKind.Return:
                if (track.LoopStack.Count > 0)
                    throw new InvalidOperationException("ret cannot be executed inside loop_start/loop_end scope.");
                if (track.CallStack.Count == 0)
                {
                    track.Finished = true;
                    return true;
                }
                track.Offset = track.CallStack.Pop();
                return true;

            case SseqDecodedKind.AllocateTrack:
            {
                if (track.TrackNo != 0)
                    throw new InvalidOperationException("alloctrack is only valid on track 0.");
                if (track.Offset != track.StartOffset)
                    throw new InvalidOperationException("alloctrack is only valid at sequence start.");

                ushort mask = (ushort)(decoded.Value0 | 0x0001);
                foreach (var runtimeTrack in tracks.Values)
                    runtimeTrack.AllocatedTracksMask = mask;

                track.Offset += decoded.Length;
                return true;
            }

            case SseqDecodedKind.EndTrack:
                track.Finished = true;
                return true;
        }

        throw new InvalidOperationException($"Unsupported decoded command kind: {decoded.Kind}");
    }

    private void ExecuteNoteEvent(
        SseqTrackRuntime track,
        int channel,
        int note,
        int velocity,
        int duration,
        double tempo,
        List<PendingNoteOff> pendingNoteOffs,
        long nowTick,
        SseqExecutionContext executionContext)
    {
        if (duration < 0)
            throw new InvalidOperationException($"Negative note duration is invalid: {duration}");

        track.WaitVoiceId = -1;
        bool muted = track.MuteMode != SseqMuteMode.Off;

        if (velocity > 0 && !executionContext.Silent)
        {
            if (!IsMixerOutputBlocked(channel) && !muted)
            {
                ushort channelMask = ResolveEffectiveTrackChannelMask(track);
                SetPianoMidiNoteOn(channel, note);
                if (track.TieMode)
                {
                    if (track.TiedVoiceId >= 0)
                        AudioEngine.Instance.StopVoice(track.TiedVoiceId);

                    if ((uint)channel < (uint)_activeVoices.Length)
                    {
                        foreach (var kv in _activeVoices[channel].ToArray())
                        {
                            AudioEngine.Instance.StopVoice(kv.Value);
                            SetPianoMidiNoteOff(channel, kv.Key);
                        }
                        _activeVoices[channel].Clear();
                    }
                }

                PlayMidiNoteOn(channel, note, velocity, channelMask, duration, tempo);

                if (_activeVoices[channel].TryGetValue(note, out int voiceId))
                {
                    if (track.TieMode)
                    {
                        track.TiedVoiceId = voiceId;
                        track.TiedNote = note;
                    }
                    else if (duration > 0)
                    {
                        pendingNoteOffs.Add(new PendingNoteOff(
                            trackNo: channel,
                            midiNote: note,
                            voiceId: voiceId,
                            offTick: nowTick + duration));
                    }
                    else if (track.NoteWait)
                    {
                        // length=0 waits until the currently playing voice ends.
                        track.WaitVoiceId = voiceId;
                    }
                }
                else
                {
                    SetPianoMidiNoteOff(channel, note);
                    track.TiedVoiceId = -1;
                    track.TiedNote = -1;
                }
            }
            else if (track.TieMode)
            {
                track.TiedVoiceId = -1;
                track.TiedNote = -1;
            }
        }

        if (track.NoteWait && duration > 0)
            track.WaitTicks = duration;
    }

    private void ExecuteSimpleByteCommand(
        SseqTrackRuntime track,
        int channel,
        byte op,
        int rawArg,
        SseqExecutionContext executionContext,
        int instructionLength)
    {
        int arg = rawArg & 0xFF;

        switch (op)
        {
            case 0xC0:
                ApplySseqControlChange(channel, 10, arg, executionContext);
                return;
            case 0xC1:
                ApplySseqControlChange(channel, 7, arg, executionContext);
                return;
            case 0xC2:
                ApplySseqMainVolumeToAllChannels(arg, executionContext);
                return;
            case 0xC3:
            {
                int transposeSigned = unchecked((sbyte)arg);
                ApplySseqControlChange(channel, 13, Math.Clamp(transposeSigned + 64, 0, 127), executionContext);
                return;
            }
            case 0xC4:
            {
                int bendSigned = unchecked((sbyte)arg);
                bendSigned = Math.Clamp(bendSigned, -127, 127);
                SetSseqPitchBend(channel, bendSigned, executionContext);
                return;
            }
            case 0xC5:
                ApplySseqControlChange(channel, 20, arg, executionContext);
                return;
            case 0xC6:
                ApplySseqControlChange(channel, 14, arg, executionContext);
                return;
            case 0xC7:
                track.NoteWait = arg != 0;
                return;
            case 0xC8:
            {
                bool tieOn = arg != 0;
                if (tieOn)
                {
                    track.TieMode = true;
                    track.TiedVoiceId = -1;
                    track.TiedNote = -1;
                    if (!executionContext.Silent)
                        ReleaseAllActiveVoicesForTrack(channel);
                }
                else
                {
                    if (!executionContext.Silent)
                    {
                        if (track.TiedVoiceId >= 0)
                            AudioEngine.Instance.NoteOff(track.TiedVoiceId);

                        if ((uint)channel < (uint)_activeVoices.Length && track.TiedNote >= 0)
                            _activeVoices[channel].Remove(track.TiedNote);
                    }

                    track.TieMode = false;
                    track.TiedVoiceId = -1;
                    track.TiedNote = -1;
                }
                return;
            }
            case 0xC9:
                ApplySseqControlChange(channel, 84, arg, executionContext);
                return;
            case 0xCA:
                ApplySseqControlChange(channel, 1, arg, executionContext);
                return;
            case 0xCB:
                ApplySseqControlChange(channel, 21, arg, executionContext);
                return;
            case 0xCC:
                ApplySseqControlChange(channel, 22, arg, executionContext);
                return;
            case 0xCD:
                ApplySseqControlChange(channel, 23, arg, executionContext);
                return;
            case 0xCE:
                ApplySseqControlChange(channel, 65, arg == 0 ? 0 : 127, executionContext);
                return;
            case 0xCF:
                ApplySseqControlChange(channel, 5, arg, executionContext);
                return;
            case 0xD0:
                ApplySseqControlChange(channel, 85, arg, executionContext);
                return;
            case 0xD1:
                ApplySseqControlChange(channel, 86, arg, executionContext);
                return;
            case 0xD2:
                ApplySseqControlChange(channel, 87, arg, executionContext);
                return;
            case 0xD3:
                ApplySseqControlChange(channel, 88, arg, executionContext);
                return;
            case 0xD4:
                if (HasReachedCombinedNestingLimit(track))
                    throw new InvalidOperationException("loop_start exceeds max nesting depth (call+loop <= 3).");
                track.LoopStack.Push(new LoopFrame
                {
                    StartOffset = track.Offset + Math.Max(1, instructionLength),
                    RemainingCount = arg
                });
                return;
            case 0xD5:
                ApplySseqControlChange(channel, 11, arg, executionContext);
                return;
            case 0xD6:
                WriteSseqPrintVar(track, arg, executionContext);
                return;
            case 0xD7:
            {
                var muteMode = (SseqMuteMode)Math.Clamp(arg, 0, 3);
                track.MuteMode = muteMode;

                if (executionContext.Silent)
                    return;

                switch (muteMode)
                {
                    case SseqMuteMode.Off:
                    case SseqMuteMode.Keep:
                        break;

                    case SseqMuteMode.Release:
                        ReleaseAllActiveVoicesForTrack(channel);
                        break;

                    case SseqMuteMode.Immediate:
                        StopAllActiveVoicesForTrackImmediate(channel);
                        break;
                }

                if (muteMode != SseqMuteMode.Off)
                {
                    track.TiedVoiceId = -1;
                    track.TiedNote = -1;
                }
                return;
            }
            default:
                throw new InvalidOperationException($"Unsupported simple-byte opcode 0x{op:X2}");
        }
    }

    private void WriteSseqPrintVar(
        SseqTrackRuntime track,
        int rawVariableId,
        SseqExecutionContext executionContext)
    {
        int variableId = rawVariableId & 0xFF;
        short value;

        if ((uint)variableId < 16u)
        {
            value = track.LocalVariables[variableId];
        }
        else if ((uint)variableId < 32u)
        {
            if (track.GlobalVariables.Length < 16)
                throw new InvalidOperationException("Global variable storage is not initialized.");

            value = track.GlobalVariables[variableId - 16];
        }
        else
        {
            throw new InvalidOperationException($"printvar variable index out of range: {variableId}");
        }

        if (executionContext.Silent)
            return;

        Debug.WriteLine($"#{variableId}[{value}]: printvar No.{variableId} = {value}");
    }

    private void ExecuteVariableCommand(
        SseqTrackRuntime track,
        byte op,
        int variableId,
        short value,
        Random runtimeRandom)
    {
        var (vars, idx) = ResolveVariableStorage(track, variableId, op);
        short current = vars[idx];

        switch (op)
        {
            case 0xB0:
                vars[idx] = value;
                return;
            case 0xB1:
                vars[idx] = unchecked((short)(current + value));
                return;
            case 0xB2:
                vars[idx] = unchecked((short)(current - value));
                return;
            case 0xB3:
                vars[idx] = unchecked((short)(current * value));
                return;
            case 0xB4:
                if (value == 0)
                    throw new InvalidOperationException("divvar with zero divisor is invalid.");
                vars[idx] = unchecked((short)(current / value));
                return;
            case 0xB5:
            {
                if (value >= 0)
                {
                    int count = Math.Min(31, (int)value);
                    vars[idx] = unchecked((short)(current << count));
                }
                else
                {
                    int count = Math.Min(31, -(int)value);
                    vars[idx] = unchecked((short)(current >> count));
                }
                return;
            }
            case 0xB6:
                vars[idx] = NextInclusive(runtimeRandom, value);
                return;
            case 0xB7:
                return;
            case 0xB8:
                track.ConditionalFlag = current == value;
                return;
            case 0xB9:
                track.ConditionalFlag = current >= value;
                return;
            case 0xBA:
                track.ConditionalFlag = current > value;
                return;
            case 0xBB:
                track.ConditionalFlag = current <= value;
                return;
            case 0xBC:
                track.ConditionalFlag = current < value;
                return;
            case 0xBD:
                track.ConditionalFlag = current != value;
                return;
            default:
                throw new InvalidOperationException($"Unsupported variable opcode 0x{op:X2}");
        }
    }

    private void ExecuteRandomWrapper(
        ReadOnlySpan<byte> data,
        Dictionary<int, SseqTrackRuntime> tracks,
        SseqTrackRuntime track,
        List<PendingNoteOff> pendingNoteOffs,
        long nowTick,
        int channel,
        ref double tempo,
        SseqDecodedEvent decoded,
        Random runtimeRandom,
        SseqExecutionContext executionContext,
        int containerEventLength)
    {
        int min = decoded.Signed0;
        int max = decoded.Signed1;
        if (max < min)
            (min, max) = (max, min);

        int value = runtimeRandom.Next(min, max + 1);
        ExecuteWrappedSubtype(
            data,
            tracks,
            track,
            pendingNoteOffs,
            nowTick,
            channel,
            ref tempo,
            decoded.SubType,
            value,
            decoded.WrapperArgCount,
            decoded.WrapperArg0,
            decoded.WrapperArg1,
            decoded.WrapperArg2,
            decoded.WrapperArg3,
            runtimeRandom,
            executionContext,
            containerEventLength);
    }

    private void ExecuteFromVariableWrapper(
        ReadOnlySpan<byte> data,
        Dictionary<int, SseqTrackRuntime> tracks,
        SseqTrackRuntime track,
        List<PendingNoteOff> pendingNoteOffs,
        long nowTick,
        int channel,
        ref double tempo,
        SseqDecodedEvent decoded,
        Random runtimeRandom,
        SseqExecutionContext executionContext,
        int containerEventLength)
    {
        if (decoded.SubType >= 0xB0 && decoded.SubType <= 0xBD)
        {
            int targetVariable = decoded.Signed0;
            int sourceVariable = decoded.Signed1;
            short sourceValue = ResolveVariableValue(track, sourceVariable, "fromvar");
            ExecuteVariableCommand(track, decoded.SubType, targetVariable, sourceValue, runtimeRandom);
            return;
        }

        int variableId = decoded.Signed0;
        short value = ResolveVariableValue(track, variableId, "fromvar");
        ExecuteWrappedSubtype(
            data,
            tracks,
            track,
            pendingNoteOffs,
            nowTick,
            channel,
            ref tempo,
            decoded.SubType,
            value,
            decoded.WrapperArgCount,
            decoded.WrapperArg0,
            decoded.WrapperArg1,
            decoded.WrapperArg2,
            decoded.WrapperArg3,
            runtimeRandom,
            executionContext,
            containerEventLength);
    }

    private void ExecuteWrappedSubtype(
        ReadOnlySpan<byte> data,
        Dictionary<int, SseqTrackRuntime> tracks,
        SseqTrackRuntime track,
        List<PendingNoteOff> pendingNoteOffs,
        long nowTick,
        int channel,
        ref double tempo,
        byte subType,
        int overriddenValue,
        int wrapperArgCount,
        byte wrapperArg0,
        byte wrapperArg1,
        byte wrapperArg2,
        byte wrapperArg3,
        Random runtimeRandom,
        SseqExecutionContext executionContext,
        int containerEventLength)
    {
        if (subType <= 0x7F)
        {
            if (wrapperArgCount < 1)
                throw new InvalidOperationException($"Wrapped note 0x{subType:X2} is missing velocity.");

            int velocity = wrapperArg0 & 0x7F;
            ExecuteNoteEvent(
                track,
                channel,
                subType,
                velocity,
                overriddenValue,
                tempo,
                pendingNoteOffs,
                nowTick,
                executionContext);
            return;
        }

        switch (subType)
        {
            case 0x80:
                if (overriddenValue < 0)
                    throw new InvalidOperationException($"Wrapped wait has negative duration: {overriddenValue}");
                track.WaitTicks = overriddenValue;
                return;
            case 0x81:
            {
                if (overriddenValue < 0)
                    throw new InvalidOperationException($"Wrapped prg has negative raw value: {overriddenValue}");
                if (overriddenValue > 32767)
                    throw new InvalidOperationException($"Wrapped prg raw value is out of range [0..32767]: {overriddenValue}");
                int program = overriddenValue;
                SetSseqProgram(channel, program, executionContext);
                return;
            }
            case >= 0xB0 and <= 0xBD:
                if (wrapperArgCount < 1)
                    throw new InvalidOperationException($"Wrapped variable command 0x{subType:X2} is missing varNo.");
                ExecuteVariableCommand(track, subType, wrapperArg0, unchecked((short)overriddenValue), runtimeRandom);
                return;

            case >= 0xC0 and <= 0xD7:
                if (subType is 0xC3 or 0xC4)
                {
                    if (overriddenValue < sbyte.MinValue || overriddenValue > sbyte.MaxValue)
                        throw new InvalidOperationException($"Wrapped command 0x{subType:X2} has out-of-range signed byte arg: {overriddenValue}");
                    ExecuteSimpleByteCommand(track, channel, subType, unchecked((byte)(sbyte)overriddenValue), executionContext, containerEventLength);
                    return;
                }

                if (overriddenValue < 0 || overriddenValue > 255)
                    throw new InvalidOperationException($"Wrapped command 0x{subType:X2} has out-of-range byte arg: {overriddenValue}");
                ExecuteSimpleByteCommand(track, channel, subType, overriddenValue, executionContext, containerEventLength);
                return;

            case 0xE0:
                if (overriddenValue < 0 || overriddenValue > ushort.MaxValue)
                    throw new InvalidOperationException($"Wrapped mod_delay has out-of-range arg: {overriddenValue}");
                ApplySseqControlChange(channel, 26, overriddenValue, executionContext);
                return;

            case 0xE1:
                tempo = Math.Clamp(overriddenValue, 1, 1023);
                SetRightStatusBlinkTempo(tempo);
                return;

            case 0xE2:
            case 0xE3:
                SetSseqSweepPitch(channel, overriddenValue, executionContext);
                return;

            default:
                throw new InvalidOperationException($"Unsupported wrapped subtype 0x{subType:X2}");
        }
    }

    private static (short[] vars, int idx) ResolveVariableStorage(
        SseqTrackRuntime track,
        int variableId,
        byte op)
    {
        if ((uint)variableId < 16)
            return (track.LocalVariables, variableId);

        if ((uint)variableId < 32)
        {
            if (track.GlobalVariables.Length < 16)
                throw new InvalidOperationException("Global variable storage is not initialized.");
            return (track.GlobalVariables, variableId - 16);
        }

        throw new InvalidOperationException($"Variable index out of range in opcode 0x{op:X2}: {variableId}");
    }

    private static short ResolveVariableValue(SseqTrackRuntime track, int variableId, string context)
    {
        if ((uint)variableId < 16)
            return track.LocalVariables[variableId];

        if ((uint)variableId < 32)
        {
            if (track.GlobalVariables.Length < 16)
                throw new InvalidOperationException("Global variable storage is not initialized.");
            return track.GlobalVariables[variableId - 16];
        }

        throw new InvalidOperationException($"{context}: variable index out of range: {variableId}");
    }

    private string? TryWriteSseqPlaybackErrorLog(Exception exception)
    {
        try
        {
            DateTime now = DateTime.Now;
            string logDir = ResolveSseqPlaybackLogDirectory();
            Directory.CreateDirectory(logDir);

            string stamp = now.ToString("yyyyMMdd-HHmmss-fff");
            string logPath = Path.Combine(logDir, $"sseq-playback-error-{stamp}.log");
            string dataPath = Path.Combine(logDir, $"sseq-playback-error-{stamp}.bin");

            var sb = new StringBuilder(4096);
            sb.AppendLine("=== SSEQ Playback Error ===");
            sb.AppendLine($"Time: {now:yyyy-MM-dd HH:mm:ss.fff zzz}");
            sb.AppendLine($"PlaybackLabel: {_activeSseqPlaybackLabel}");
            sb.AppendLine($"SelectedSSEQ: {SelectedSseq?.Display ?? "(none)"}");
            sb.AppendLine($"SelectedSBNK: {SelectedSseqBank?.Display ?? "(none)"}");
            sb.AppendLine($"Track0StartOffset: 0x{_activeSseqTrack0StartOffset:X6}");
            sb.AppendLine($"EventDataLength: {_activeSseqPlaybackEventData.Length}");
            sb.AppendLine($"ExceptionType: {exception.GetType().FullName}");
            sb.AppendLine($"ExceptionMessage: {exception.Message}");
            if (exception.InnerException is not null)
            {
                sb.AppendLine($"InnerType: {exception.InnerException.GetType().FullName}");
                sb.AppendLine($"InnerMessage: {exception.InnerException.Message}");
            }

            if (TryExtractRuntimeFault(exception, out int trackNo, out int offset, out byte op))
            {
                sb.AppendLine($"FaultTrack: {trackNo}");
                sb.AppendLine($"FaultOffset: 0x{offset:X6}");
                sb.AppendLine($"FaultOp: 0x{op:X2}");
                AppendHexSlice(sb, _activeSseqPlaybackEventData, offset, 128);
            }
            else
            {
                AppendHexSlice(sb, _activeSseqPlaybackEventData, _activeSseqTrack0StartOffset, 128);
            }

            sb.AppendLine("StackTrace:");
            sb.AppendLine(exception.ToString());
            sb.AppendLine($"BinarySnapshot: {dataPath}");
            sb.AppendLine();

            File.AppendAllText(logPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllBytes(dataPath, _activeSseqPlaybackEventData);
            return logPath;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveSseqPlaybackLogDirectory()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = AppContext.BaseDirectory;
        return Path.Combine(root, "NitroSynth", "Logs");
    }

    private static bool TryExtractRuntimeFault(Exception exception, out int trackNo, out int offset, out byte op)
    {
        if (exception is SseqPlaybackRuntimeException runtime)
        {
            trackNo = runtime.TrackNo;
            offset = runtime.Offset;
            op = runtime.Op;
            return true;
        }

        if (exception.InnerException is SseqPlaybackRuntimeException innerRuntime)
        {
            trackNo = innerRuntime.TrackNo;
            offset = innerRuntime.Offset;
            op = innerRuntime.Op;
            return true;
        }

        trackNo = 0;
        offset = 0;
        op = 0;
        return false;
    }

    private static void AppendHexSlice(StringBuilder sb, ReadOnlySpan<byte> data, int centerOffset, int radiusBytes)
    {
        if (data.Length == 0)
        {
            sb.AppendLine("HexSlice: (empty)");
            return;
        }

        int clampedCenter = Math.Clamp(centerOffset, 0, data.Length - 1);
        int radius = Math.Max(1, radiusBytes);
        int start = Math.Max(0, clampedCenter - radius);
        int endExclusive = Math.Min(data.Length, clampedCenter + radius + 1);
        int rowStart = start & ~0x0F;
        int rowEndExclusive = (endExclusive + 0x0F) & ~0x0F;

        sb.AppendLine($"HexSliceCenter: 0x{clampedCenter:X6}");
        sb.AppendLine($"HexSliceRange:  0x{start:X6}..0x{Math.Max(start, endExclusive - 1):X6}");

        for (int row = rowStart; row < rowEndExclusive; row += 16)
        {
            sb.Append($"{row:X6}: ");

            for (int i = 0; i < 16; i++)
            {
                int index = row + i;
                if (index < start || index >= endExclusive || index >= data.Length)
                {
                    sb.Append("   ");
                    continue;
                }

                sb.Append(index == clampedCenter ? '[' : ' ');
                sb.Append(data[index].ToString("X2"));
                sb.Append(index == clampedCenter ? ']' : ' ');
            }

            sb.Append(" |");
            for (int i = 0; i < 16; i++)
            {
                int index = row + i;
                if (index < start || index >= endExclusive || index >= data.Length)
                {
                    sb.Append(' ');
                    continue;
                }

                byte b = data[index];
                sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
            }

            sb.AppendLine("|");
        }
    }

    private static short NextInclusive(Random random, short maxAbs)
    {
        if (maxAbs >= 0)
            return unchecked((short)random.Next(0, maxAbs + 1));

        return unchecked((short)random.Next(maxAbs, 1));
    }

    private void SetSseqProgram(int channel, int program, SseqExecutionContext? executionContext)
    {
        program = Math.Clamp(program, 0, 32767);

        if (executionContext?.Silent == true)
        {
            if (executionContext.ChannelStates is { } channelStates &&
                (uint)channel < (uint)channelStates.Length)
            {
                channelStates[channel].ProgramId = program;
            }
            return;
        }

        if ((uint)channel < (uint)MixerStrips.Count)
        {
            MixerStrips[channel].ProgramId = program;
            UpdateStripInstrumentType(channel);
        }
    }

    private void SetSseqSweepPitch(int channel, int sweepPitchCommandUnits, SseqExecutionContext? executionContext)
    {
        int clamped = Math.Clamp(sweepPitchCommandUnits, short.MinValue, short.MaxValue);

        if (executionContext?.Silent == true)
        {
            if (executionContext.ChannelStates is { } channelStates &&
                (uint)channel < (uint)channelStates.Length)
            {
                channelStates[channel].SweepPitch = clamped;
            }
            return;
        }

        AudioEngine.Instance.SetSweepPitch(channel, clamped / 64.0);
    }

    private void SetSseqPitchBend(int channel, int signedValue, SseqExecutionContext? executionContext)
    {
        signedValue = Math.Clamp(signedValue, -127, 127);

        if (executionContext?.Silent == true)
        {
            if (executionContext.ChannelStates is { } channelStates &&
                (uint)channel < (uint)channelStates.Length)
            {
                channelStates[channel].PitchBend = signedValue;
            }
            return;
        }

        if ((uint)channel < (uint)MixerStrips.Count)
            MixerStrips[channel].PitchBend = signedValue;

        AudioEngine.Instance.SetPitchBend(channel, signedValue);
    }

    private void ApplySseqControlChange(
        int channel,
        byte cc,
        int value,
        SseqExecutionContext? executionContext)
    {
        if (cc == 26)
        {
            int modDelayFrames = Math.Clamp(value, 0, 32767);

            if (executionContext?.Silent == true)
            {
                if (executionContext.ChannelStates is { } channelStates &&
                    (uint)channel < (uint)channelStates.Length)
                {
                    channelStates[channel].ModDelayFrames = modDelayFrames;
                }
                return;
            }

            if ((uint)channel < (uint)MixerStrips.Count)
                MixerStrips[channel].ModDelay = modDelayFrames;

            AudioEngine.Instance.SetModDelayFrames(channel, modDelayFrames);
            return;
        }

        bool envelopeOverride = cc is 85 or 86 or 87 or 88;
        value = envelopeOverride
            ? Math.Clamp(value, 0, 255)
            : Math.Clamp(value, 0, 127);

        if (executionContext?.Silent == true)
        {
            if (executionContext.ChannelStates is { } channelStates &&
                (uint)channel < (uint)channelStates.Length)
            {
                channelStates[channel].ControlValues[cc] = (byte)value;
            }
            return;
        }

        if ((uint)channel < (uint)MixerStrips.Count)
        {
            switch (cc)
            {
                case 7:
                    MixerStrips[channel].Volume = value;
                    break;
                case 10:
                    MixerStrips[channel].Pan = value - 64;
                    break;
                case 11:
                    MixerStrips[channel].Volume2 = value;
                    break;
                case 1:
                    MixerStrips[channel].Modulation = value;
                    break;
                case 20:
                    MixerStrips[channel].BendRange = value;
                    break;
                case 21:
                    MixerStrips[channel].ModSpeed = value;
                    break;
                case 22:
                    MixerStrips[channel].ModType = value;
                    break;
                case 23:
                    MixerStrips[channel].ModRange = value;
                    break;
                case 27:
                    MixerStrips[channel].ModDelay = value * 10;
                    break;
                case 5:
                    MixerStrips[channel].Portamento = value;
                    break;
                case 65:
                    MixerStrips[channel].PortaEnabled = value >= 64;
                    break;
                case 84:
                    MixerStrips[channel].PortaEnabled = true;
                    break;
            }
        }

        var ev = ControlChangeEvent.FromMidi(0, (byte)channel, cc, (byte)value);
        AudioEngine.Instance.ApplyControlChange(ev);
    }

    private static int NormalizeEnvelopeByte(byte raw)
    {
        return raw == 0xFF ? 127 : raw;
    }

}
