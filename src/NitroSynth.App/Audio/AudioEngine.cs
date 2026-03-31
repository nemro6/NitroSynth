using System;
using System.Linq;
using NAudio.Wave;
using static NitroSynth.App.ViewModels.MainWindowViewModel;

namespace NitroSynth.App.Audio
{
    internal sealed class AudioEngine : IDisposable
    {
        private static readonly Lazy<AudioEngine> _instance =
            new(() => new AudioEngine());
        public static AudioEngine Instance => _instance.Value;

        private readonly object _outLock = new();
        private EngineMixer _mixer;
        private WaveOutEvent _out;
        private int _outputDeviceNumber = -1;
        private int _outputDesiredLatencyMs = 48;
        private int _outputSampleRate = 48000;
        private int _hardwareChannelEnabledMask = 0xFFFF;
        private int _trackOutputEnabledMask = 0xFFFF;
        private float _masterVolume = 1.0f;
        private bool _monoOutputEnabled;
        private bool _disposed;

        private readonly EngineChannelState[] _channels =
            Enumerable.Range(0, 16)
                .Select(i => new EngineChannelState { ChannelIndex = i })
                .ToArray();
        public int SampleRate => _outputSampleRate;

        private static int ResolveEnvelopeOverride(int channelValue, int instrumentValue)
        {
            if (channelValue is >= 0 and <= 127)
                return channelValue;

            return Math.Clamp(instrumentValue, 0, 127);
        }


        public void ResetMidiAllChannels()
        {
            for (int ch = 0; ch < 16; ch++)
                _channels[ch].ResetAllControllersToDefault();

        }

        public EngineChannelState GetChannelState(int ch) => _channels[ch];

        public void SetPitchBend(int midiChannel, int bend127)
        {
            if (midiChannel < 0 || midiChannel >= _channels.Length)
                return;

            _channels[midiChannel].PitchBend = Math.Clamp(bend127, -128, 127);
        }

        public void SetModDelayFrames(int midiChannel, int frames)
        {
            if (midiChannel < 0 || midiChannel >= _channels.Length)
                return;

            _channels[midiChannel].SetModDelayFrames(frames);
        }

        public void SetSequencePriorityBase(int midiChannel, int priorityBase)
        {
            if (midiChannel < 0 || midiChannel >= _channels.Length)
                return;

            _channels[midiChannel].SequencePriorityBase = Math.Clamp(priorityBase, 0, 255);
        }

        public void SetSequencePriorityBaseAll(int priorityBase)
        {
            int normalized = Math.Clamp(priorityBase, 0, 255);
            for (int ch = 0; ch < _channels.Length; ch++)
                _channels[ch].SequencePriorityBase = normalized;
        }

        public void SetSweepPitch(int midiChannel, double sweepPitchSemitones)
        {
            if (midiChannel < 0 || midiChannel >= _channels.Length)
                return;

            const double minSweepSemitones = short.MinValue / 64.0;
            const double maxSweepSemitones = short.MaxValue / 64.0;
            _channels[midiChannel].SweepPitchSemitones = Math.Clamp(sweepPitchSemitones, minSweepSemitones, maxSweepSemitones);
        }

        private AudioEngine()
        {
            _mixer = CreateMixer(_outputSampleRate);
            _out = CreateOutput(-1, _outputDesiredLatencyMs);
            _out.Init(_mixer);
            _out.Play();
        }

        private EngineMixer CreateMixer(int sampleRate)
        {
            var mixer = new EngineMixer(sampleRate, _channels);
            mixer.SetMasterVolume(_masterVolume);
            mixer.SetMonoOutput(_monoOutputEnabled);
            mixer.SetHardwareChannelEnabledMask((ushort)_hardwareChannelEnabledMask);
            mixer.SetTrackOutputEnabledMask((ushort)_trackOutputEnabledMask);
            return mixer;
        }

        private static WaveOutEvent CreateOutput(int deviceNumber, int desiredLatencyMs)
        {
            return new WaveOutEvent
            {
                DeviceNumber = deviceNumber,
                DesiredLatency = desiredLatencyMs,
                NumberOfBuffers = 8
            };
        }

        public int PlaySwav(
            int midiChannel,
            byte velocity,
            short[] pcm16, int sampleRate,
            int midiNote, int baseKey,
            bool loop, int loopStart, int loopEnd,
            int attack, int decay, int sustain, int release,
            int defaultPan,
            float baseVolume = 1.0f,
            int? priorityOverride = null,
            ushort allowedHardwareChannelMask = 0,
            int noteDurationSamples = 0)
        {
            if (midiChannel < 0 || midiChannel >= _channels.Length)
                midiChannel = 0;

            var chState = _channels[midiChannel];
            PortaInfo porta = chState.OnNoteOn(midiNote, _mixer.WaveFormat.SampleRate, noteDurationSamples);
            int priority = Math.Clamp(priorityOverride ?? chState.GetEffectivePriority(), 0, 255);
            int resolvedAttack = ResolveEnvelopeOverride(chState.Attack, attack);
            int resolvedDecay = ResolveEnvelopeOverride(chState.Decay, decay);
            int resolvedSustain = ResolveEnvelopeOverride(chState.Sustain, sustain);
            int resolvedRelease = ResolveEnvelopeOverride(chState.Release, release);

            double semis = (midiNote - baseKey) + chState.Transpose; 
            double pitch = Math.Pow(2.0, semis / 12.0);
            int playRate = (int)Math.Round(sampleRate * pitch);
            if (playRate < 2000) playRate = 2000;

            var voice = _mixer.CreatePcmVoice(
                chState, velocity,
                midiNote,
                pcm16, playRate,
                _mixer.WaveFormat.SampleRate,
                loop, loopStart, loopEnd,
                resolvedAttack, resolvedDecay, resolvedSustain, resolvedRelease,
                Math.Clamp(defaultPan, 0, 127), baseVolume,
                porta,
                priority,
                allowedHardwareChannelMask
            );
            return _mixer.AddVoice(voice);

        }

        public int PlayPsg(
            int midiChannel,
            byte velocity,
            int midiNote,
            int baseKey,
            int dutyIndex,
            int attack,
            int decay,
            int sustain,
            int release,
            int defaultPan,
            float baseVolume = 1.0f,
            int? priorityOverride = null,
            ushort allowedHardwareChannelMask = 0,
            int noteDurationSamples = 0)
        {
            if (midiChannel < 0 || midiChannel >= _channels.Length)
                midiChannel = 0;

            var chState = _channels[midiChannel];
            PortaInfo porta = chState.OnNoteOn(midiNote, _mixer.WaveFormat.SampleRate, noteDurationSamples);
            int priority = Math.Clamp(priorityOverride ?? chState.GetEffectivePriority(), 0, 255);
            int resolvedAttack = ResolveEnvelopeOverride(chState.Attack, attack);
            int resolvedDecay = ResolveEnvelopeOverride(chState.Decay, decay);
            int resolvedSustain = ResolveEnvelopeOverride(chState.Sustain, sustain);
            int resolvedRelease = ResolveEnvelopeOverride(chState.Release, release);

            var voice = _mixer.CreatePsgVoice(
                chState, velocity,
                midiNote, baseKey, dutyIndex,
                resolvedAttack, resolvedDecay, resolvedSustain, resolvedRelease,
                Math.Clamp(defaultPan, 0, 127), baseVolume,
                porta,
                priority,
                allowedHardwareChannelMask
            );
            return _mixer.AddVoice(voice);
        }

        public int PlayNoise(
            int midiChannel,
            byte velocity,
            int midiNote,
            int baseKey,
            bool use7bit,
            int attack,
            int decay,
            int sustain,
            int release,
            int defaultPan,
            float baseVolume = 1.0f,
            int? priorityOverride = null,
            ushort allowedHardwareChannelMask = 0,
            int noteDurationSamples = 0)
        {
            if (midiChannel < 0 || midiChannel >= _channels.Length)
                midiChannel = 0;

            var chState = _channels[midiChannel];
            PortaInfo porta = chState.OnNoteOn(midiNote, _mixer.WaveFormat.SampleRate, noteDurationSamples);
            int priority = Math.Clamp(priorityOverride ?? chState.GetEffectivePriority(), 0, 255);
            int resolvedAttack = ResolveEnvelopeOverride(chState.Attack, attack);
            int resolvedDecay = ResolveEnvelopeOverride(chState.Decay, decay);
            int resolvedSustain = ResolveEnvelopeOverride(chState.Sustain, sustain);
            int resolvedRelease = ResolveEnvelopeOverride(chState.Release, release);

            // DS PSG uses an 8-step duty cycle (fundamental = sampleRate/8).
            // To keep note/originalKey behavior consistent, noise clock uses
            // the same note pitch basis scaled by 8x.
            int clampedBaseKey = Math.Clamp(baseKey, 0, 127);
            double baseClockHz = 440.0 * Math.Pow(2.0, (clampedBaseKey - 69) / 12.0) * 8.0;
            if (baseClockHz < 1.0)
                baseClockHz = 1.0;


            var voice = _mixer.CreateNoiseVoice(
                chState, velocity, midiNote, baseKey,
                use7bit, baseClockHz,
                resolvedAttack, resolvedDecay, resolvedSustain, resolvedRelease,
                Math.Clamp(defaultPan, 0, 127), baseVolume,
                porta,
                priority,
                allowedHardwareChannelMask
            );
            return _mixer.AddVoice(voice);
        }


        public void ApplyControlChange(ControlChangeEvent ev)
        {
            if (ev.Channel < 0 || ev.Channel >= _channels.Length)
                return;

            _channels[ev.Channel].Apply(ev);
        }

        public void NoteOff(int voiceId) => _mixer.NoteOff(voiceId);
        public void StopVoice(int voiceId) => _mixer.StopVoice(voiceId);
        public bool IsVoiceActive(int voiceId) => _mixer.IsVoiceActive(voiceId);
        public VoiceUsageSnapshot GetVoiceUsage() => _mixer.GetVoiceUsage();
        public void SetHardwareChannelEnabled(int channel, bool enabled)
        {
            _mixer.SetHardwareChannelEnabled(channel, enabled);

            if ((uint)channel >= 16u)
                return;

            int bit = 1 << channel;
            if (enabled)
                _hardwareChannelEnabledMask |= bit;
            else
                _hardwareChannelEnabledMask &= ~bit;
        }

        public void SetHardwareChannelEnabledMask(ushort mask)
        {
            _mixer.SetHardwareChannelEnabledMask(mask);
            _hardwareChannelEnabledMask = mask == 0 ? 0xFFFF : mask;
        }

        public void SetTrackOutputEnabled(int track, bool enabled)
        {
            _mixer.SetTrackOutputEnabled(track, enabled);

            if ((uint)track >= 16u)
                return;

            int bit = 1 << track;
            if (enabled)
                _trackOutputEnabledMask |= bit;
            else
                _trackOutputEnabledMask &= ~bit;
        }

        public void SetTrackOutputEnabledMask(ushort mask)
        {
            _mixer.SetTrackOutputEnabledMask(mask);
            _trackOutputEnabledMask = mask == 0 ? 0xFFFF : mask;
        }

        public void Dispose()
        {
            lock (_outLock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _out.Stop();
                _out.Dispose();
            }
        }

        public readonly struct ChannelMeterSnapshot
        {
            public ChannelMeterSnapshot(double level, double peak, bool clip)
            {
                Level = level; Peak = peak; Clip = clip;
            }
            public double Level { get; }
            public double Peak { get; }
            public bool Clip { get; }
        }

        public readonly struct VoiceUsageSnapshot
        {
            public VoiceUsageSnapshot(
                int total,
                int pcm,
                int psg,
                int noise,
                int noteTotal,
                int notePcm,
                int notePsg,
                int noteNoise,
                ushort slotActiveMask,
                ushort trackStealMask,
                ulong slotTrackNibbles,
                ulong slotMidiNotesLo,
                ulong slotMidiNotesHi)
            {
                Total = total;
                Pcm = pcm;
                Psg = psg;
                Noise = noise;
                NoteTotal = noteTotal;
                NotePcm = notePcm;
                NotePsg = notePsg;
                NoteNoise = noteNoise;
                SlotActiveMask = slotActiveMask;
                TrackStealMask = trackStealMask;
                SlotTrackNibbles = slotTrackNibbles;
                SlotMidiNotesLo = slotMidiNotesLo;
                SlotMidiNotesHi = slotMidiNotesHi;
            }

            public int Total { get; }
            public int Pcm { get; }
            public int Psg { get; }
            public int Noise { get; }

            public int NoteTotal { get; }
            public int NotePcm { get; }
            public int NotePsg { get; }
            public int NoteNoise { get; }

            public ushort SlotActiveMask { get; }
            public ushort TrackStealMask { get; }
            public ulong SlotTrackNibbles { get; }
            public ulong SlotMidiNotesLo { get; }
            public ulong SlotMidiNotesHi { get; }
        }

        public float GetModLfoVizWave(int ch)
        {
            if (ch < 0 || ch >= _channels.Length) return 0f;
            return _channels[ch].CurrentLfoWave;
        }

        public float GetPortaVizWave(int ch)
        {
            if (ch < 0 || ch >= _channels.Length) return 0f;
            return _mixer.GetChannelPortaViz(ch);
        }

        public double GetModLfoVizHz(int ch)
        {
            if (ch < 0 || ch >= _channels.Length) return 0.0;
            return _channels[ch].GetEffectiveLfoHz();
        }


        public ChannelMeterSnapshot GetChannelMeter(int ch)
        {
            return _mixer.GetChannelMeter(ch);
        }

        public readonly struct MasterMeterSnapshot
        {
            public MasterMeterSnapshot(double level, double peak, bool clip)
            {
                Level = level; Peak = peak; Clip = clip;
            }
            public double Level { get; }
            public double Peak { get; }
            public bool Clip { get; }
        }

        public readonly struct MasterMeterSnapshotLR
        {
            public MasterMeterSnapshotLR(double levelL, double levelR, double peakL, double peakR, bool clipL, bool clipR)
            {
                LevelL = levelL; LevelR = levelR;
                PeakL = peakL; PeakR = peakR;
                ClipL = clipL; ClipR = clipR;
            }
            public double LevelL { get; }
            public double LevelR { get; }
            public double PeakL { get; }
            public double PeakR { get; }
            public bool ClipL { get; }
            public bool ClipR { get; }
        }

        public MasterMeterSnapshotLR GetMasterMeterLR()
        {
            return _mixer.GetMasterMeterLR();
        }


        public void SetMasterVolume(float v)
        {
            _masterVolume = v;
            _mixer.SetMasterVolume(v);
        }

        public void SetMonoOutput(bool enabled)
        {
            _monoOutputEnabled = enabled;
            _mixer.SetMonoOutput(enabled);
        }

        public bool TrySetOutputDevice(int deviceNumber, out string? error)
        {
            error = null;

            lock (_outLock)
            {
                if (_outputDeviceNumber == deviceNumber)
                    return true;

                WaveOutEvent? next = null;
                try
                {
                    next = CreateOutput(deviceNumber, _outputDesiredLatencyMs);
                    next.Init(_mixer);
                    next.Play();

                    var current = _out;
                    _out = next;
                    _outputDeviceNumber = deviceNumber;
                    current.Stop();
                    current.Dispose();
                    return true;
                }
                catch (Exception ex)
                {
                    next?.Dispose();
                    error = ex.Message;
                    return false;
                }
            }
        }

        public bool TrySetOutputBufferLatency(int desiredLatencyMs, out string? error)
        {
            error = null;
            if (desiredLatencyMs < 16)
            {
                error = "Buffer size must be 16 ms or larger.";
                return false;
            }

            lock (_outLock)
            {
                if (_outputDesiredLatencyMs == desiredLatencyMs)
                    return true;

                WaveOutEvent? next = null;
                try
                {
                    next = CreateOutput(_outputDeviceNumber, desiredLatencyMs);
                    next.Init(_mixer);
                    next.Play();

                    var current = _out;
                    _out = next;
                    _outputDesiredLatencyMs = desiredLatencyMs;
                    current.Stop();
                    current.Dispose();
                    return true;
                }
                catch (Exception ex)
                {
                    next?.Dispose();
                    error = ex.Message;
                    return false;
                }
            }
        }

        public bool TrySetOutputSampleRate(int sampleRate, out string? error)
        {
            error = null;
            if (sampleRate < 8000 || sampleRate > 192000)
            {
                error = "Sample rate must be between 8000 and 192000 Hz.";
                return false;
            }

            lock (_outLock)
            {
                if (_outputSampleRate == sampleRate)
                    return true;

                WaveOutEvent? nextOut = null;
                try
                {
                    var nextMixer = CreateMixer(sampleRate);
                    nextOut = CreateOutput(_outputDeviceNumber, _outputDesiredLatencyMs);
                    nextOut.Init(nextMixer);
                    nextOut.Play();

                    var currentOut = _out;
                    _out = nextOut;
                    _mixer = nextMixer;
                    _outputSampleRate = sampleRate;
                    currentOut.Stop();
                    currentOut.Dispose();
                    return true;
                }
                catch (Exception ex)
                {
                    nextOut?.Dispose();
                    error = ex.Message;
                    return false;
                }
            }
        }
    }
}

