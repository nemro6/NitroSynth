using NitroSynth.App;

namespace NitroSynth.Tests;

public sealed class SseqPlaybackSemanticsTests
{
    [Fact]
    public void LeadingRestTrack_IsNotPulledForward()
    {
        byte[] data =
        [
            0x93, 0x01, 0x0B, 0x00, 0x00, // opentrack 1, 0x00000B
            0x80, 0x04,                   // wait 4
            0x3C, 0x64, 0x04,             // note C4
            0xFF,                         // fin (track0)
            0x80, 0x08,                   // wait 8 (track1)
            0x3E, 0x64, 0x04,             // note D4
            0xFF                          // fin (track1)
        ];

        var notes = Simulate(data);

        var track0 = Assert.Single(notes.Where(n => n.TrackNo == 0));
        var track1 = Assert.Single(notes.Where(n => n.TrackNo == 1));

        Assert.Equal(4, track0.Tick);
        Assert.Equal(8, track1.Tick);
    }

    [Fact]
    public void IfFalse_SkipsJumpCommand()
    {
        byte[] data =
        [
            0xB8, 0x00, 0x01, 0x00, // cmp_eq 0,1 (false because var0 initial=-1)
            0xA2,                   // if
            0x94, 0x0F, 0x00, 0x00, // jump 0x00000F (must be skipped)
            0x80, 0x05,             // wait 5
            0x3C, 0x64, 0x04,       // C4
            0xFF,                   // fin
            0x3E, 0x64, 0x04,       // D4 (jump target)
            0xFF                    // fin
        ];

        var notes = Simulate(data);

        var first = Assert.Single(notes);
        Assert.Equal(0, first.TrackNo);
        Assert.Equal(0x3C, first.Note);
        Assert.Equal(5, first.Tick);
    }

    [Fact]
    public void CallLoopRet_ExecutesLoopBeforeReturning()
    {
        byte[] data =
        [
            0x95, 0x05, 0x00, 0x00, // call sub
            0xFF,                   // fin
            0xD4, 0x02,             // loop_start 2
            0x3C, 0x64, 0x01,       // C4
            0xFC,                   // loop_end
            0xFD                    // ret
        ];

        var notes = Simulate(data);

        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.Equal(0x3C, n.Note));
        Assert.Equal(0, notes[0].Tick);
        Assert.Equal(1, notes[1].Tick);
    }

    [Fact]
    public void OpenTrack_ToLowerNumber_StartsNextFrame()
    {
        byte[] data =
        [
            0x93, 0x03, 0x06, 0x00, 0x00, // opentrack 3, 0x000006
            0xFF,                         // fin (track0)
            0x3C, 0x64, 0x01,             // track3: note C4, len1
            0x93, 0x01, 0x11, 0x00, 0x00, // track3: opentrack 1, 0x000011
            0x80, 0x01,                   // track3: wait 1
            0xFF,                         // track3: fin
            0x3E, 0x64, 0x01,             // track1: note D4, len1
            0xFF                          // track1: fin
        ];

        var notes = Simulate(data);

        var c4 = notes.First(n => n.Note == 0x3C);
        var d4 = notes.First(n => n.Note == 0x3E);

        Assert.Equal(0, c4.Tick);
        Assert.Equal(2, d4.Tick);
    }

    private static List<SimulatedNote> Simulate(byte[] data)
    {
        var globalVariables = CreateInitialVariables();
        var tracks = new Dictionary<int, SimTrack>
        {
            [0] = new SimTrack
            {
                TrackNo = 0,
                Offset = 0,
                GlobalVariables = globalVariables
            }
        };
        var notes = new List<SimulatedNote>();
        var random = new Random(1);

        int nowTick = 0;
        int guard = 0;

        while (guard++ < 10000)
        {
            bool progressedPass;
            do
            {
                progressedPass = false;
                var ready = tracks.Values
                    .Where(t => !t.Finished && t.WaitTicks <= 0 && t.ActivateTick <= nowTick)
                    .OrderBy(t => t.TrackNo)
                    .ToList();

                foreach (var track in ready)
                {
                    if (ExecuteSingle(data, tracks, track, nowTick, notes, random))
                        progressedPass = true;
                }
            } while (progressedPass);

            if (!tracks.Values.Any(t => !t.Finished))
                return notes;

            int step = tracks.Values
                .Where(t => !t.Finished && t.WaitTicks > 0)
                .Select(t => t.WaitTicks)
                .DefaultIfEmpty(int.MaxValue)
                .Min();

            if (step == int.MaxValue)
                throw new InvalidOperationException("Simulation stalled.");

            nowTick += step;
            foreach (var track in tracks.Values)
            {
                if (!track.Finished && track.WaitTicks > 0)
                    track.WaitTicks -= step;
            }
        }

        throw new InvalidOperationException("Simulation guard was exceeded.");
    }

    private static bool ExecuteSingle(
        ReadOnlySpan<byte> data,
        Dictionary<int, SimTrack> tracks,
        SimTrack track,
        int nowTick,
        List<SimulatedNote> notes,
        Random random)
    {
        if ((uint)track.Offset >= (uint)data.Length)
        {
            track.Finished = true;
            return true;
        }

        if (!SseqEventDecoder.TryDecode(data, track.Offset, out var ev, out var error))
            throw new InvalidOperationException(error ?? "decode failed");

        if (track.PendingIf)
        {
            bool execute = track.ConditionalFlag;
            track.PendingIf = false;
            if (!execute)
            {
                track.Offset += ev.Length;
                return true;
            }
        }

        switch (ev.Kind)
        {
            case SseqDecodedKind.Note:
                notes.Add(new SimulatedNote(track.TrackNo, ev.Value0, nowTick));
                track.Offset += ev.Length;
                if (track.NoteWait)
                    track.WaitTicks = ev.Value2;
                return true;

            case SseqDecodedKind.Wait:
                track.Offset += ev.Length;
                track.WaitTicks = ev.Value0;
                return true;

            case SseqDecodedKind.OpenTrack:
            {
                int newTrackNo = ev.Value0 & 0x0F;
                int activateTick = nowTick + (newTrackNo < track.TrackNo ? 1 : 0);
                tracks[newTrackNo] = new SimTrack
                {
                    TrackNo = newTrackNo,
                    Offset = ev.Value1,
                    ActivateTick = activateTick,
                    GlobalVariables = track.GlobalVariables
                };
                track.Offset += ev.Length;
                return true;
            }

            case SseqDecodedKind.Jump:
                track.Offset = ev.Value0;
                return true;

            case SseqDecodedKind.Call:
                track.CallStack.Push(track.Offset + ev.Length);
                track.Offset = ev.Value0;
                return true;

            case SseqDecodedKind.Return:
                if (track.CallStack.Count == 0)
                {
                    track.Finished = true;
                    return true;
                }
                track.Offset = track.CallStack.Pop();
                return true;

            case SseqDecodedKind.If:
                track.PendingIf = true;
                track.Offset += ev.Length;
                return true;

            case SseqDecodedKind.Variable:
                ExecuteVariable(track, ev.Op, ev.Value0, ev.Signed0, random);
                track.Offset += ev.Length;
                return true;

            case SseqDecodedKind.SimpleByte:
                if (ev.Op == 0xC7)
                    track.NoteWait = ev.Value0 != 0;
                else if (ev.Op == 0xD4)
                    track.LoopStack.Push(new SimLoop { StartOffset = track.Offset + 2, RemainingCount = ev.Value0 });
                track.Offset += ev.Length;
                return true;

            case SseqDecodedKind.LoopEnd:
                if (track.LoopStack.Count == 0)
                {
                    track.Offset += ev.Length;
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
                    track.Offset += ev.Length;
                }
                return true;
            }
            case SseqDecodedKind.EndTrack:
                track.Finished = true;
                return true;

            case SseqDecodedKind.AllocateTrack:
            case SseqDecodedKind.Program:
            case SseqDecodedKind.Random:
            case SseqDecodedKind.FromVariable:
            case SseqDecodedKind.ModDelay:
            case SseqDecodedKind.Tempo:
            case SseqDecodedKind.SweepPitch:
                track.Offset += ev.Length;
                return true;
        }

        return false;
    }

    private static void ExecuteVariable(SimTrack track, byte op, int varNo, short value, Random random)
    {
        var (vars, idx) = ResolveVariableStorage(track, varNo);

        short current = vars[idx];

        switch (op)
        {
            case 0xB0:
                vars[idx] = value;
                break;
            case 0xB1:
                vars[idx] = unchecked((short)(current + value));
                break;
            case 0xB2:
                vars[idx] = unchecked((short)(current - value));
                break;
            case 0xB3:
                vars[idx] = unchecked((short)(current * value));
                break;
            case 0xB4:
                if (value == 0)
                    throw new InvalidOperationException("division by zero");
                vars[idx] = unchecked((short)(current / value));
                break;
            case 0xB5:
                vars[idx] = value >= 0
                    ? unchecked((short)(current << Math.Min(31, (int)value)))
                    : unchecked((short)(current >> Math.Min(31, -(int)value)));
                break;
            case 0xB6:
                vars[idx] = value >= 0
                    ? unchecked((short)random.Next(0, value + 1))
                    : unchecked((short)random.Next(value, 1));
                break;
            case 0xB8:
                track.ConditionalFlag = current == value;
                break;
            case 0xB9:
                track.ConditionalFlag = current >= value;
                break;
            case 0xBA:
                track.ConditionalFlag = current > value;
                break;
            case 0xBB:
                track.ConditionalFlag = current <= value;
                break;
            case 0xBC:
                track.ConditionalFlag = current < value;
                break;
            case 0xBD:
                track.ConditionalFlag = current != value;
                break;
        }
    }

    private static (short[] vars, int idx) ResolveVariableStorage(SimTrack track, int varNo)
    {
        if ((uint)varNo < 16)
            return (track.LocalVariables, varNo);
        if ((uint)varNo < 32)
            return (track.GlobalVariables, varNo - 16);
        throw new InvalidOperationException($"variable index out of range: {varNo}");
    }

    private static short[] CreateInitialVariables()
    {
        var vars = new short[16];
        Array.Fill(vars, (short)-1);
        return vars;
    }

    private sealed class SimTrack
    {
        public int TrackNo;
        public int Offset;
        public int WaitTicks;
        public int ActivateTick;
        public bool Finished;
        public bool NoteWait = true;
        public bool ConditionalFlag;
        public bool PendingIf;
        public short[] LocalVariables { get; } = CreateInitialVariables();
        public short[] GlobalVariables { get; init; } = Array.Empty<short>();
        public Stack<int> CallStack { get; } = new();
        public Stack<SimLoop> LoopStack { get; } = new();
    }

    private sealed class SimLoop
    {
        public int StartOffset;
        public int RemainingCount;
    }

    private readonly record struct SimulatedNote(int TrackNo, int Note, int Tick);
}
