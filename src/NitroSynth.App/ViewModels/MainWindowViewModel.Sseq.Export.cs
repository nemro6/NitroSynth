using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NAudio.Midi;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    public enum SseqExportKind
    {
        Sseq,
        Smft,
        Midi
    }

    private readonly record struct PendingMidiNoteOff(int Channel, int Note, long OffTick);
    private readonly record struct ScheduledMidiEvent(long Tick, int Order, MidiEvent Event);

    public string GetSelectedSseqExportBaseName()
    {
        string? raw = SelectedSseq?.Name;
        if (string.IsNullOrWhiteSpace(raw))
            raw = SelectedSseq is null ? "sequence" : $"sseq_{SelectedSseq.Id:D3}";

        foreach (char c in Path.GetInvalidFileNameChars())
            raw = raw.Replace(c, '_');

        raw = raw.Trim();
        return raw.Length == 0 ? "sequence" : raw;
    }

    public void NotifySseqImportNotImplemented()
    {
        StatusMessage = "REPLACE is not implemented yet.";
    }

    public void NotifySseqSaveNotImplemented()
    {
        StatusMessage = "SAVE is not implemented yet.";
    }

    public void NotifySseqExportFailed(string reason)
    {
        StatusMessage = $"EXPORT failed: {reason}";
    }

    public bool ExportSelectedSseq(string outputPath, SseqExportKind kind)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            StatusMessage = "EXPORT failed: output path is empty.";
            return false;
        }

        try
        {
            switch (kind)
            {
                case SseqExportKind.Sseq:
                    return ExportSelectedAsSseq(outputPath);
                case SseqExportKind.Smft:
                    return ExportSelectedAsSmft(outputPath);
                case SseqExportKind.Midi:
                    return ExportSelectedAsMidi(outputPath);
                default:
                    StatusMessage = "EXPORT failed: unknown format.";
                    return false;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"EXPORT failed: {ex.Message}";
            return false;
        }
    }

    private bool ExportSelectedAsSseq(string outputPath)
    {
        if (!TryGetSelectedSseqData(out var source, out _))
        {
            StatusMessage = "EXPORT failed: SSEQ data is not available.";
            return false;
        }

        if (!TryGetSelectedSseqPlaybackEventData(out var eventData, out var compileError))
        {
            StatusMessage = compileError ?? "EXPORT failed: could not compile SMFT.";
            return false;
        }

        var bytes = BuildSseqBinary(source.Header, eventData);
        File.WriteAllBytes(outputPath, bytes);
        StatusMessage = $"Exported SSEQ: {outputPath}";
        return true;
    }

    private bool ExportSelectedAsSmft(string outputPath)
    {
        string text = SseqDecompilerText ?? string.Empty;
        File.WriteAllText(outputPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        StatusMessage = $"Exported SMFT: {outputPath}";
        return true;
    }

    private bool ExportSelectedAsMidi(string outputPath)
    {
        if (!TryGetSelectedSseqPlaybackEventData(out var eventData, out var compileError))
        {
            StatusMessage = compileError ?? "EXPORT failed: could not compile SMFT.";
            return false;
        }

        if (!TryBuildSseqMidiEventCollection(eventData, out var collection, out var midiError, out var midiWarning))
        {
            StatusMessage = midiError ?? "EXPORT failed: could not convert to MIDI.";
            return false;
        }

        MidiFile.Export(outputPath, collection);
        StatusMessage = string.IsNullOrWhiteSpace(midiWarning)
            ? $"Exported MIDI: {outputPath}"
            : $"Exported MIDI (truncated): {outputPath} ({midiWarning})";
        return true;
    }

    private static byte[] BuildSseqBinary(SSEQ.HeaderInfo header, byte[] eventData)
    {
        int dataOffset = (int)Math.Max(0x1Cu, header.DataOffset);
        int fileSize = checked(dataOffset + eventData.Length);
        uint dataSize = checked((uint)Math.Max(0, fileSize - 0x10));

        var bytes = new byte[fileSize];

        WriteAscii(bytes, 0x00, "SSEQ");
        WriteU16(bytes, 0x04, header.ByteOrder);
        WriteU16(bytes, 0x06, header.Version);
        WriteU32(bytes, 0x08, (uint)fileSize);
        WriteU16(bytes, 0x0C, header.HeaderSize == 0 ? (ushort)0x10 : header.HeaderSize);
        WriteU16(bytes, 0x0E, header.BlockCount == 0 ? (ushort)1 : header.BlockCount);
        WriteAscii(bytes, 0x10, "DATA");
        WriteU32(bytes, 0x14, dataSize);
        WriteU32(bytes, 0x18, (uint)dataOffset);

        Buffer.BlockCopy(eventData, 0, bytes, dataOffset, eventData.Length);
        return bytes;
    }

    private bool TryBuildSseqMidiEventCollection(
        byte[] eventData,
        out MidiEventCollection collection,
        out string? error,
        out string? warning)
    {
        collection = new MidiEventCollection(1, 48);
        error = null;
        warning = null;

        var globalVariables = CreateInitialVariables();
        var tracks = new Dictionary<int, SseqTrackRuntime>
        {
            [0] = new SseqTrackRuntime
            {
                TrackNo = 0,
                StartOffset = 0,
                Offset = 0,
                ActivateTick = 0,
                AllocatedTracksMask = 0x0001,
                GlobalVariables = globalVariables
            }
        };

        var events = new List<ScheduledMidiEvent>(4096);
        var pendingNoteOffs = new List<PendingMidiNoteOff>(2048);
        var runtimeRandom = new Random(0x534D4654); // SMFT
        var firstVisitTicks = new Dictionary<(int TrackNo, int Offset), long>();
        bool loopStartMarkerWritten = false;
        bool loopEndMarkerWritten = false;

        long nowTick = 0;
        int executedCount = 0;
        bool stopByLimit = false;

        while (true)
        {
            if (nowTick > 2_000_000)
            {
                warning = "tick limit reached";
                stopByLimit = true;
                break;
            }

            bool anyTrackActive = tracks.Values.Any(t => !t.Finished);
            if (!anyTrackActive)
                break;

            for (int i = pendingNoteOffs.Count - 1; i >= 0; i--)
            {
                var pending = pendingNoteOffs[i];
                if (pending.OffTick > nowTick)
                    continue;

                events.Add(new ScheduledMidiEvent(
                    pending.OffTick,
                    2,
                    new NoteEvent(pending.OffTick, pending.Channel, MidiCommandCode.NoteOff, pending.Note, 0)));
                pendingNoteOffs.RemoveAt(i);
            }

            bool progressedAny = false;
            int passGuard = 0;

            while (passGuard++ < 4096)
            {
                var readyTracks = tracks.Values
                    .Where(t => !t.Finished && t.WaitTicks <= 0 && t.ActivateTick <= nowTick)
                    .OrderBy(t => t.TrackNo)
                    .ToList();

                if (readyTracks.Count == 0)
                    break;

                bool progressed = false;

                foreach (var track in readyTracks)
                {
                    int trackGuard = 0;
                    while (!track.Finished && track.WaitTicks <= 0 && track.ActivateTick <= nowTick && trackGuard++ < 1024)
                    {
                        executedCount++;
                        if (executedCount > 500_000)
                        {
                            warning = "event limit reached";
                            stopByLimit = true;
                            break;
                        }

                        if (!TryExecuteSseqEventForMidi(
                            eventData,
                            tracks,
                            track,
                            pendingNoteOffs,
                            nowTick,
                            events,
                            runtimeRandom,
                            firstVisitTicks,
                            ref loopStartMarkerWritten,
                            ref loopEndMarkerWritten,
                            out error))
                        {
                            return false;
                        }

                        progressed = true;
                        progressedAny = true;
                    }

                    if (stopByLimit)
                        break;

                    if (trackGuard >= 1024)
                    {
                        warning = $"track {track.TrackNo} exceeded step guard";
                        stopByLimit = true;
                        break;
                    }
                }

                if (stopByLimit)
                    break;

                if (!progressed)
                    break;
            }

            if (stopByLimit)
                break;

            int advanceTicks = int.MaxValue;

            foreach (var track in tracks.Values)
            {
                if (!track.Finished && track.WaitTicks > 0)
                    advanceTicks = Math.Min(advanceTicks, track.WaitTicks);

                if (!track.Finished && track.ActivateTick > nowTick)
                {
                    long remain = track.ActivateTick - nowTick;
                    if (remain > 0 && remain < advanceTicks)
                        advanceTicks = (int)Math.Min(remain, int.MaxValue);
                }
            }

            foreach (var pending in pendingNoteOffs)
            {
                long remain = pending.OffTick - nowTick;
                if (remain > 0 && remain < advanceTicks)
                    advanceTicks = (int)Math.Min(remain, int.MaxValue);
            }

            if (advanceTicks == int.MaxValue)
            {
                if (!progressedAny)
                    break;
                continue;
            }

            if (advanceTicks <= 0)
                advanceTicks = 1;

            nowTick += advanceTicks;
            foreach (var track in tracks.Values)
            {
                if (!track.Finished && track.WaitTicks > 0)
                    track.WaitTicks = Math.Max(0, track.WaitTicks - advanceTicks);
            }
        }

        foreach (var pending in pendingNoteOffs)
        {
            events.Add(new ScheduledMidiEvent(
                pending.OffTick,
                2,
                new NoteEvent(pending.OffTick, pending.Channel, MidiCommandCode.NoteOff, pending.Note, 0)));
        }

        if (!events.Any(e => e.Event is TempoEvent))
            events.Add(new ScheduledMidiEvent(0, 0, new TempoEvent(500000, 0))); // 120 BPM

        var sorted = events
            .OrderBy(e => e.Tick)
            .ThenBy(e => e.Order)
            .ToList();

        long lastTick = 0;
        foreach (var item in sorted)
        {
            item.Event.AbsoluteTime = item.Tick;
            collection.AddEvent(item.Event, 0);
            if (item.Tick > lastTick)
                lastTick = item.Tick;
        }

        collection.AddEvent(new MetaEvent(MetaEventType.EndTrack, 0, lastTick + 1), 0);
        return true;
    }

    private bool TryExecuteSseqEventForMidi(
        ReadOnlySpan<byte> data,
        Dictionary<int, SseqTrackRuntime> tracks,
        SseqTrackRuntime track,
        List<PendingMidiNoteOff> pendingNoteOffs,
        long nowTick,
        List<ScheduledMidiEvent> midiEvents,
        Random runtimeRandom,
        Dictionary<(int TrackNo, int Offset), long> firstVisitTicks,
        ref bool loopStartMarkerWritten,
        ref bool loopEndMarkerWritten,
        out string? error)
    {
        error = null;

        if ((uint)track.Offset >= (uint)data.Length)
        {
            track.Finished = true;
            return true;
        }

        firstVisitTicks.TryAdd((track.TrackNo, track.Offset), nowTick);

        if (!SseqEventDecoder.TryDecode(data, track.Offset, out var decoded, out var decodeError))
        {
            error = $"MIDI export decode failed at track={track.TrackNo} off=0x{track.Offset:X6}: {decodeError}";
            return false;
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

        int channel = ToNaudioChannel(track.TrackNo);

        try
        {
            switch (decoded.Kind)
            {
                case SseqDecodedKind.Note:
                    EmitMidiNote(track, channel, decoded.Value0, decoded.Value1, decoded.Value2, nowTick, pendingNoteOffs, midiEvents);
                    track.Offset += decoded.Length;
                    return true;

                case SseqDecodedKind.Wait:
                    track.Offset += decoded.Length;
                    track.WaitTicks = Math.Max(0, decoded.Value0);
                    return true;

                case SseqDecodedKind.Program:
                    midiEvents.Add(new ScheduledMidiEvent(nowTick, 1, new PatchChangeEvent(nowTick, channel, Math.Clamp(decoded.Value0, 0, 32767) & 0x7F)));
                    track.Offset += decoded.Length;
                    return true;

                case SseqDecodedKind.OpenTrack:
                {
                    int newTrackNo = decoded.Value0 & 0x0F;
                    if (!IsTrackAllocated(track, newTrackNo))
                    {
                        error = $"MIDI export: opentrack target {newTrackNo} is not allocated.";
                        return false;
                    }
                    int dest = decoded.Value1;
                    if ((uint)dest >= (uint)data.Length)
                    {
                        error = $"MIDI export: opentrack destination out of range 0x{dest:X6}.";
                        return false;
                    }

                    long activateTick = nowTick;
                    if (newTrackNo < track.TrackNo)
                        activateTick = nowTick + 1;

                    tracks[newTrackNo] = new SseqTrackRuntime
                    {
                        TrackNo = newTrackNo,
                        StartOffset = dest,
                        Offset = dest,
                        ActivateTick = activateTick,
                        AllocatedTracksMask = track.AllocatedTracksMask,
                        GlobalVariables = track.GlobalVariables
                    };

                    track.Offset += decoded.Length;
                    return true;
                }

                case SseqDecodedKind.Jump:
                    if ((uint)decoded.Value0 >= (uint)data.Length)
                    {
                        error = $"MIDI export: jump destination out of range 0x{decoded.Value0:X6}.";
                        return false;
                    }

                    // One-pass export: treat backward/self jump as loop termination.
                    if (decoded.Value0 <= track.Offset)
                    {
                        if (!loopStartMarkerWritten)
                        {
                            long loopStartTick = nowTick;
                            if (firstVisitTicks.TryGetValue((track.TrackNo, decoded.Value0), out var firstTick))
                                loopStartTick = firstTick;

                            midiEvents.Add(new ScheduledMidiEvent(
                                loopStartTick,
                                8,
                                new TextEvent("Loop_Start", MetaEventType.Marker, loopStartTick)));
                            loopStartMarkerWritten = true;
                        }

                        if (!loopEndMarkerWritten)
                        {
                            midiEvents.Add(new ScheduledMidiEvent(
                                nowTick,
                                9,
                                new TextEvent("Loop_End", MetaEventType.Marker, nowTick)));
                            loopEndMarkerWritten = true;
                        }
                        track.Finished = true;
                        return true;
                    }

                    track.Offset = decoded.Value0;
                    return true;

                case SseqDecodedKind.Call:
                    if ((uint)decoded.Value0 >= (uint)data.Length)
                    {
                        error = $"MIDI export: call destination out of range 0x{decoded.Value0:X6}.";
                        return false;
                    }
                    if (HasReachedCombinedNestingLimit(track))
                    {
                        error = "MIDI export: call exceeds max nesting depth (call+loop <= 3).";
                        return false;
                    }
                    track.CallStack.Push(track.Offset + decoded.Length);
                    track.Offset = decoded.Value0;
                    return true;

                case SseqDecodedKind.If:
                    track.PendingIf = true;
                    track.Offset += decoded.Length;
                    return true;

                case SseqDecodedKind.Variable:
                    ExecuteVariableCommand(track, decoded.Op, decoded.Value0, decoded.Signed0, runtimeRandom);
                    track.Offset += decoded.Length;
                    return true;

                case SseqDecodedKind.Random:
                    ExecuteRandomWrapperForMidi(
                        track,
                        channel,
                        decoded,
                        nowTick,
                        pendingNoteOffs,
                        midiEvents,
                        runtimeRandom,
                        ref loopStartMarkerWritten);
                    track.Offset += decoded.Length;
                    return true;

                case SseqDecodedKind.FromVariable:
                    ExecuteFromVariableWrapperForMidi(
                        track,
                        channel,
                        decoded,
                        nowTick,
                        pendingNoteOffs,
                        midiEvents,
                        runtimeRandom,
                        ref loopStartMarkerWritten);
                    track.Offset += decoded.Length;
                    return true;

                case SseqDecodedKind.SimpleByte:
                    ExecuteSimpleByteForMidi(track, channel, decoded.Op, decoded.Value0, nowTick, midiEvents, ref loopStartMarkerWritten);
                    track.Offset += decoded.Length;
                    return true;

                case SseqDecodedKind.ModDelay:
                    EmitControlChange(nowTick, channel, 26, decoded.Value0, midiEvents);
                    track.Offset += decoded.Length;
                    return true;

                case SseqDecodedKind.Tempo:
                    EmitTempo(nowTick, decoded.Value0, midiEvents);
                    track.Offset += decoded.Length;
                    return true;

                case SseqDecodedKind.SweepPitch:
                    EmitControlChange(nowTick, channel, 28, decoded.Signed0 + 64, midiEvents);
                    track.Offset += decoded.Length;
                    return true;

                case SseqDecodedKind.LoopEnd:
                    if (!loopEndMarkerWritten)
                    {
                        midiEvents.Add(new ScheduledMidiEvent(
                            nowTick,
                            9,
                            new TextEvent("Loop_End", MetaEventType.Marker, nowTick)));
                        loopEndMarkerWritten = true;
                    }
                    if (track.LoopStack.Count == 0)
                    {
                        track.Offset += decoded.Length;
                        return true;
                    }
                    // MIDI export is intentionally one-pass: do not loop, continue toward 'fin'.
                    track.LoopStack.Pop();
                    track.Offset += decoded.Length;
                    return true;

                case SseqDecodedKind.Return:
                    if (track.LoopStack.Count > 0)
                    {
                        error = "MIDI export: ret cannot be executed inside loop scope.";
                        return false;
                    }
                    if (track.CallStack.Count == 0)
                    {
                        track.Finished = true;
                        return true;
                    }
                    track.Offset = track.CallStack.Pop();
                    return true;

                case SseqDecodedKind.AllocateTrack:
                    if (track.TrackNo != 0 || track.Offset != track.StartOffset)
                    {
                        error = "MIDI export: alloctrack is only valid at track 0 start.";
                        return false;
                    }
                    ushort allocatedMask = (ushort)(decoded.Value0 | 0x0001);
                    foreach (var runtimeTrack in tracks.Values)
                        runtimeTrack.AllocatedTracksMask = allocatedMask;
                    track.Offset += decoded.Length;
                    return true;

                case SseqDecodedKind.EndTrack:
                    track.Finished = true;
                    return true;
            }
        }
        catch (Exception ex)
        {
            error = $"MIDI export runtime error at track={track.TrackNo}, off=0x{track.Offset:X6}, op=0x{decoded.Op:X2}: {ex.Message}";
            return false;
        }

        error = $"MIDI export: unsupported decoded command kind {decoded.Kind}.";
        return false;
    }

    private static void EmitMidiNote(
        SseqTrackRuntime track,
        int channel,
        int note,
        int velocity,
        int duration,
        long nowTick,
        List<PendingMidiNoteOff> pendingNoteOffs,
        List<ScheduledMidiEvent> midiEvents)
    {
        int noteNo = Math.Clamp(note, 0, 127);
        int vel = Math.Clamp(velocity, 0, 127);
        int safeDuration = duration > 0 ? duration : 1;

        if (vel > 0)
        {
            midiEvents.Add(new ScheduledMidiEvent(nowTick, 3, new NoteOnEvent(nowTick, channel, noteNo, vel, safeDuration)));
            pendingNoteOffs.Add(new PendingMidiNoteOff(channel, noteNo, nowTick + safeDuration));
        }

        if (track.NoteWait)
        {
            if (duration > 0)
                track.WaitTicks = duration;
            else if (vel > 0)
                track.WaitTicks = 1;
        }
    }

    private static void EmitControlChange(long tick, int channel, int cc, int value, List<ScheduledMidiEvent> events)
    {
        int v = Math.Clamp(value, 0, 127);
        events.Add(new ScheduledMidiEvent(
            tick,
            1,
            new ControlChangeEvent(tick, channel, (MidiController)Math.Clamp(cc, 0, 127), v)));
    }

    private static void EmitTempo(long tick, int tempoValue, List<ScheduledMidiEvent> events)
    {
        int tempo = Math.Clamp(tempoValue, 1, 1023);
        int micros = (int)Math.Round(60000000.0 / tempo);
        events.Add(new ScheduledMidiEvent(tick, 0, new TempoEvent(micros, tick)));
    }

    private static void EmitPitchBend(long tick, int channel, int signedValue, List<ScheduledMidiEvent> events)
    {
        int bend = Math.Clamp(signedValue, -127, 127);
        int midiValue = Math.Clamp(8192 + bend * 64, 0, 16383);
        events.Add(new ScheduledMidiEvent(tick, 1, new PitchWheelChangeEvent(tick, channel, midiValue)));
    }

    private void ExecuteSimpleByteForMidi(
        SseqTrackRuntime track,
        int channel,
        byte op,
        int rawArg,
        long nowTick,
        List<ScheduledMidiEvent> events,
        ref bool loopStartMarkerWritten)
    {
        int arg = rawArg & 0xFF;

        switch (op)
        {
            case 0xC0: EmitControlChange(nowTick, channel, 10, arg, events); return;
            case 0xC1: EmitControlChange(nowTick, channel, 7, arg, events); return;
            case 0xC2: EmitControlChange(nowTick, channel, 12, arg, events); return;
            case 0xC3: EmitControlChange(nowTick, channel, 13, Math.Clamp(unchecked((sbyte)arg) + 64, 0, 127), events); return;
            case 0xC4: EmitPitchBend(nowTick, channel, unchecked((sbyte)arg), events); return;
            case 0xC5: EmitControlChange(nowTick, channel, 20, arg, events); return;
            case 0xC6: EmitControlChange(nowTick, channel, 14, arg, events); return;
            case 0xC7: track.NoteWait = arg != 0; return;
            case 0xC8: return;
            case 0xC9: EmitControlChange(nowTick, channel, 84, arg, events); return;
            case 0xCA: EmitControlChange(nowTick, channel, 1, arg, events); return;
            case 0xCB: EmitControlChange(nowTick, channel, 21, arg, events); return;
            case 0xCC: EmitControlChange(nowTick, channel, 22, arg, events); return;
            case 0xCD: EmitControlChange(nowTick, channel, 23, arg, events); return;
            case 0xCE: EmitControlChange(nowTick, channel, 65, arg == 0 ? 0 : 127, events); return;
            case 0xCF: EmitControlChange(nowTick, channel, 5, arg, events); return;
            case 0xD0: EmitControlChange(nowTick, channel, 85, NormalizeEnvelopeByte((byte)arg), events); return;
            case 0xD1: EmitControlChange(nowTick, channel, 86, NormalizeEnvelopeByte((byte)arg), events); return;
            case 0xD2: EmitControlChange(nowTick, channel, 87, NormalizeEnvelopeByte((byte)arg), events); return;
            case 0xD3: EmitControlChange(nowTick, channel, 88, NormalizeEnvelopeByte((byte)arg), events); return;
            case 0xD4:
                if (!loopStartMarkerWritten)
                {
                    events.Add(new ScheduledMidiEvent(
                        nowTick,
                        8,
                        new TextEvent("Loop_Start", MetaEventType.Marker, nowTick)));
                    loopStartMarkerWritten = true;
                }
                track.LoopStack.Push(new LoopFrame
                {
                    StartOffset = track.Offset + 2,
                    RemainingCount = arg
                });
                return;
            case 0xD5: EmitControlChange(nowTick, channel, 11, arg, events); return;
            case 0xD6: return;
            case 0xD7: return;
        }
    }

    private void ExecuteRandomWrapperForMidi(
        SseqTrackRuntime track,
        int channel,
        SseqDecodedEvent decoded,
        long nowTick,
        List<PendingMidiNoteOff> pendingNoteOffs,
        List<ScheduledMidiEvent> events,
        Random runtimeRandom,
        ref bool loopStartMarkerWritten)
    {
        int min = decoded.Signed0;
        int max = decoded.Signed1;
        if (max < min)
            (min, max) = (max, min);

        int value = runtimeRandom.Next(min, max + 1);
        ExecuteWrappedSubtypeForMidi(
            track,
            channel,
            decoded.SubType,
            value,
            decoded.WrapperArgCount,
            decoded.WrapperArg0,
            nowTick,
            pendingNoteOffs,
            events,
            runtimeRandom,
            ref loopStartMarkerWritten);
    }

    private void ExecuteFromVariableWrapperForMidi(
        SseqTrackRuntime track,
        int channel,
        SseqDecodedEvent decoded,
        long nowTick,
        List<PendingMidiNoteOff> pendingNoteOffs,
        List<ScheduledMidiEvent> events,
        Random runtimeRandom,
        ref bool loopStartMarkerWritten)
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
        ExecuteWrappedSubtypeForMidi(
            track,
            channel,
            decoded.SubType,
            value,
            decoded.WrapperArgCount,
            decoded.WrapperArg0,
            nowTick,
            pendingNoteOffs,
            events,
            runtimeRandom,
            ref loopStartMarkerWritten);
    }

    private void ExecuteWrappedSubtypeForMidi(
        SseqTrackRuntime track,
        int channel,
        byte subType,
        int overriddenValue,
        int wrapperArgCount,
        byte wrapperArg0,
        long nowTick,
        List<PendingMidiNoteOff> pendingNoteOffs,
        List<ScheduledMidiEvent> events,
        Random runtimeRandom,
        ref bool loopStartMarkerWritten)
    {
        if (subType <= 0x7F)
        {
            if (wrapperArgCount >= 1)
            {
                int velocity = wrapperArg0 & 0x7F;
                EmitMidiNote(track, channel, subType, velocity, overriddenValue, nowTick, pendingNoteOffs, events);
            }
            return;
        }

        switch (subType)
        {
            case 0x80:
                track.WaitTicks = Math.Max(0, overriddenValue);
                return;
            case 0x81:
                events.Add(new ScheduledMidiEvent(nowTick, 1, new PatchChangeEvent(nowTick, channel, overriddenValue & 0x7F)));
                return;
            case >= 0xB0 and <= 0xBD:
                if (wrapperArgCount >= 1)
                    ExecuteVariableCommand(track, subType, wrapperArg0, unchecked((short)overriddenValue), runtimeRandom);
                return;
            case >= 0xC0 and <= 0xD7:
                if (subType is 0xC3 or 0xC4)
                {
                    if (overriddenValue is >= sbyte.MinValue and <= sbyte.MaxValue)
                    {
                        ExecuteSimpleByteForMidi(
                            track,
                            channel,
                            subType,
                            unchecked((byte)(sbyte)overriddenValue),
                            nowTick,
                            events,
                            ref loopStartMarkerWritten);
                    }
                    return;
                }

                if (overriddenValue is >= 0 and <= 255)
                    ExecuteSimpleByteForMidi(track, channel, subType, overriddenValue, nowTick, events, ref loopStartMarkerWritten);
                return;
            case 0xE0:
                EmitControlChange(nowTick, channel, 26, overriddenValue, events);
                return;
            case 0xE1:
                EmitTempo(nowTick, overriddenValue, events);
                return;
            case 0xE2:
            case 0xE3:
                EmitControlChange(nowTick, channel, 28, overriddenValue + 64, events);
                return;
        }
    }

    private static void WriteAscii(byte[] bytes, int offset, string text)
    {
        var raw = Encoding.ASCII.GetBytes(text);
        Buffer.BlockCopy(raw, 0, bytes, offset, Math.Min(raw.Length, 4));
    }

    private static int ToNaudioChannel(int trackNo)
    {
        return Math.Clamp(trackNo, 0, 15) + 1;
    }

    private static void WriteU16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteU32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
        bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
        bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
