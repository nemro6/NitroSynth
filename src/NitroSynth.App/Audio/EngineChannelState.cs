using System;
using static NitroSynth.App.ViewModels.MainWindowViewModel;

namespace NitroSynth.App.Audio
{

    internal sealed class EngineChannelState
    {
        public float Volume { get; set; } = 1.0f;  
        public float Volume2 { get; set; } = 1.0f; 
        public float MainVolume { get; set; } = 1.0f;

        public int Pan { get; set; } = 64;

        public byte ModDepth { get; set; } = 0;   
        public byte ModRange { get; set; } = 1;   
        public byte ModSpeed { get; set; } = 16;  
        public byte ModType { get; set; } = 0;   
        public int ModDelayFrames { get; set; } = 0; 

        public double SweepPitchSemitones { get; set; }

        public bool PortaEnabled { get; set; }
        public float PortaTime { get; set; } 
        public int PortaTimeRaw { get; set; } 

        public int PitchBend { get; set; } = 0; 
        public float BendRange { get; set; } = 2.0f; 

        public int Transpose { get; set; }
        public int Priority { get; set; } = 64;
        public int SequencePriorityBase { get; set; }

        public int Attack { get; set; } = 255;
        public int Decay { get; set; } = 255;
        public int Sustain { get; set; } = 255;
        public int Release { get; set; } = 255;

        public int LoopStart { get; set; }
        public int LoopEnd { get; set; }

        public byte RpnMsb { get; set; }
        public byte RpnLsb { get; set; }
        public byte DataEntry { get; set; }

        private double _lfoPhase;
        private int _samplesSinceStart;
        private int _cachedDelaySamples = -1;
        private const double LfoStartPhaseRad = Math.PI * 0.5;

        private double _vibratoSemis = 0.0;

        public double CurrentPitchSemiOffset { get; private set; }
        public float CurrentVolumeMod { get; private set; } = 1.0f;
        public int CurrentPanMod { get; private set; } = 0;

        public int LastNote { get; private set; } = -1;
        public bool HasLastNote => LastNote >= 0;
        private int _portaStartNote = -1;


        public int ChannelIndex { get; set; }

        public int GetEffectivePriority()
        {
            return Math.Clamp(SequencePriorityBase + Priority, 0, 255);
        }

        public double GetPitchBendSemis()
        {
            int pb = Math.Clamp(PitchBend, -128, 127);
            double norm = pb / 127.0; 
            double range = Math.Clamp(BendRange, 0f, 127f);
            return norm * range;
        }

        public void Apply(ControlChangeEvent ev)
        {
            switch (ev.Kind)
            {
                case ControlChangeEvent.Command.Volume: Volume = ev.Value / 127f; break;
                case ControlChangeEvent.Command.Volume2: Volume2 = ev.Value / 127f; break;
                case ControlChangeEvent.Command.MainVolume: MainVolume = ev.Value / 127f; break;
                case ControlChangeEvent.Command.Pan: Pan = ev.Value; break;

                case ControlChangeEvent.Command.ModDepth: ModDepth = ev.Value; break;
                case ControlChangeEvent.Command.ModRange: ModRange = ev.Value; break;
                case ControlChangeEvent.Command.ModSpeed: ModSpeed = ev.Value; break;
                case ControlChangeEvent.Command.ModType: ModType = ev.Value; break;

                case ControlChangeEvent.Command.ModDelay:
                    SetModDelayFrames(ev.Value);
                    break;

                case ControlChangeEvent.Command.ModDelayTimes10:
                    SetModDelayFrames(ev.Value * 10);
                    break;

                case ControlChangeEvent.Command.SweepPitch:
                    // CC#28 maps to sweep_pitch command units, where 64 == 1 semitone.
                    SweepPitchSemitones = (ev.Value - 64) / 64.0;
                    break;

                case ControlChangeEvent.Command.SweepPitchTimes24:
                    // CC#29 multiplies the centered value by 24 in command units.
                    SweepPitchSemitones = ((ev.Value - 64) * 24) / 64.0;
                    break;

                case ControlChangeEvent.Command.PortaOnOff:
                    PortaEnabled = ev.Value >= 64;
                    if (PortaEnabled)
                    {
                        // porta_on uses the previous note as source, not an explicit key.
                        _portaStartNote = -1;
                    }
                    else
                    {
                        _portaStartNote = -1;
                    }
                    break;

                case ControlChangeEvent.Command.Porta:
                    PortaEnabled = true;
                    _portaStartNote = Math.Clamp((int)ev.Value, 0, 127);
                    break;

                case ControlChangeEvent.Command.PortaTime:
                    PortaTimeRaw = ev.Value;
                    PortaTime = ev.Value / 255f;
                    Console.WriteLine($"[PortaCC] Value={ev.Value} PortaTimeRaw={PortaTimeRaw}");
                    break;

                case ControlChangeEvent.Command.BendRange:
                    BendRange = ev.Value;
                    break;

                case ControlChangeEvent.Command.Transpose:
                    Transpose = ev.Value - 64;
                    break;

                case ControlChangeEvent.Command.Priority:
                    Priority = ev.Value;
                    break;

                case ControlChangeEvent.Command.Attack: Attack = ev.Value; break;
                case ControlChangeEvent.Command.Decay: Decay = ev.Value; break;
                case ControlChangeEvent.Command.Sustain: Sustain = ev.Value; break;
                case ControlChangeEvent.Command.Release: Release = ev.Value; break;

                case ControlChangeEvent.Command.LoopStart: LoopStart = ev.Value; break;
                case ControlChangeEvent.Command.LoopEnd: LoopEnd = ev.Value; break;

                case ControlChangeEvent.Command.RpnMsb: RpnMsb = ev.Value; break;
                case ControlChangeEvent.Command.RpnLsb: RpnLsb = ev.Value; break;
                case ControlChangeEvent.Command.DataEntry: DataEntry = ev.Value; break;

                case ControlChangeEvent.Command.PitchBend:
                    {
                        short val = (short)ev.Value; 
                        val -= 64;                   
                        PitchBend = val;             
                    }
                    break;

                default:
                    break;
            }
        }

        public float GetFinalVolume(byte velocity, float baseVolume, int envelopeAttenuation = 0)
        {
            int trackVolume = DsVolumeCurve.ToMidi7Bit(Volume);
            int expression = DsVolumeCurve.ToMidi7Bit(Volume2);
            int mainVolume = DsVolumeCurve.ToMidi7Bit(MainVolume);
            int instrumentVolume = DsVolumeCurve.ToMidi7Bit(baseVolume);

            return DsVolumeCurve.GetComposedGain(
                velocity,
                trackVolume,
                expression,
                mainVolume,
                instrumentVolume,
                envelopeAttenuation);
        }

        public void AdvanceLfo(int sampleRate)
        {
            _vibratoSemis = 0.0;

            if (ModDepth == 0 || ModRange == 0 || ModSpeed == 0)
            {
                CurrentVolumeMod = 1.0f;
                CurrentPanMod = 0;
                CurrentLfoWave = 0f;
            }
            else
            {
                if (_cachedDelaySamples < 0)
                {
                    int samplesPerFrame = sampleRate / 200; 
                    _cachedDelaySamples = Math.Max(0, ModDelayFrames) * samplesPerFrame;
                    _samplesSinceStart = 0;
                }

                if (_samplesSinceStart < _cachedDelaySamples)
                {
                    _samplesSinceStart++;
                    CurrentVolumeMod = 1.0f;
                    CurrentPanMod = 0;
                    CurrentLfoWave = 0f;
                }
                else
                {
                    _samplesSinceStart++;

                    double freqHz = ModSpeed * (50.0 / 127.0);
                    double phaseInc = 2.0 * Math.PI * freqHz / sampleRate;

                    double wave = TriangleWave(_lfoPhase);
                    CurrentLfoWave = (float)wave;
                    double depthNorm = ModDepth / 127.0;
                    double range = ModRange;

                    _lfoPhase += phaseInc;
                    if (_lfoPhase > Math.PI * 2.0)
                        _lfoPhase -= Math.PI * 2.0;

                    switch (ModType)
                    {
                        case 1: 
                            {
                                double maxDb = 6.0 * range;
                                double db = wave * depthNorm * maxDb;
                                double gain = Math.Pow(10.0, db / 20.0);
                                CurrentVolumeMod = (float)gain;
                                CurrentPanMod = 0;
                            }
                            break;

                        case 2: 
                            {
                                // Keep PAN modulation symmetric around zero.
                                double span = 63.0 * range;
                                int delta = (int)Math.Round(wave * depthNorm * span);
                                if (delta < -63) delta = -63;
                                if (delta > 63) delta = 63;
                                CurrentPanMod = delta;
                                CurrentVolumeMod = 1.0f;
                            }
                            break;

                        case 0: 
                        default:
                            _vibratoSemis = wave * depthNorm * range;
                            CurrentVolumeMod = 1.0f;
                            CurrentPanMod = 0;
                            break;
                    }
                }
            }

            double bendSemis = GetPitchBendSemis();

            CurrentPitchSemiOffset = _vibratoSemis + bendSemis;
        }

        public void SetModDelayFrames(int frames)
        {
            ModDelayFrames = Math.Clamp(frames, 0, 32767);
            _lfoPhase = LfoStartPhaseRad;
            _cachedDelaySamples = -1;
            _samplesSinceStart = 0;
            ResetModulationOutputsToNeutral();
        }

        private static double TriangleWave(double phaseRad)
        {
            double cycle = phaseRad / (2.0 * Math.PI);
            cycle -= Math.Floor(cycle);
            return cycle < 0.5
                ? -1.0 + (cycle * 4.0)
                : 3.0 - (cycle * 4.0);
        }

        private const double PortaMinSecPerOct = 0.001;  
        private const double PortaMaxSecPerOct = 1.0;  

        public double PortaGlobalScale { get; set; } = 1.0;  

        private double PortaRawToSecPerOct(int raw0to255)
        {
            int raw = Math.Clamp(raw0to255, 0, 255);

            double t = raw / 255.0;  

            double sec = PortaMinSecPerOct + t * (PortaMaxSecPerOct - PortaMinSecPerOct);

            sec *= Math.Clamp(PortaGlobalScale, 0.01, 10.0);

            return sec;
        }
        private int BuildTransitionSamples(double semitoneDistance, int engineSampleRate, int noteDurationSamples)
        {
            if (semitoneDistance <= 0.0)
                return 0;

            // Sequence spec: porta_time=0 means transition over the note length.
            if (PortaTimeRaw <= 0)
            {
                int byNoteLength = Math.Max(0, noteDurationSamples);
                return byNoteLength > 0 ? byNoteLength : 0;
            }

            double secPerOct = PortaRawToSecPerOct(PortaTimeRaw);
            double transitionSec = secPerOct * (semitoneDistance / 12.0);
            transitionSec = Math.Clamp(transitionSec, 0.0, 10.0);

            int samples = (int)Math.Round(transitionSec * engineSampleRate);
            if (samples <= 0)
                samples = 1;
            return samples;
        }

        public PortaInfo BuildPortaInfoForNoteOn(int newMidiNote, int engineSampleRate, int noteDurationSamples = 0)
        {
            newMidiNote = Math.Clamp(newMidiNote, 0, 127);

            double portaInitialSemis = 0.0;
            int portaSamples = 0;

            if (PortaEnabled)
            {
                int startNote = _portaStartNote >= 0 ? _portaStartNote : LastNote;
                _portaStartNote = -1;

                if (startNote >= 0 && startNote != newMidiNote)
                {
                    int deltaSemis = startNote - newMidiNote;
                    portaInitialSemis = deltaSemis;
                    portaSamples = BuildTransitionSamples(Math.Abs(deltaSemis), engineSampleRate, noteDurationSamples);
                }
            }
            else
            {
                _portaStartNote = -1;
            }

            double sweepInitialSemis = SweepPitchSemitones;
            int sweepSamples = BuildTransitionSamples(Math.Abs(sweepInitialSemis), engineSampleRate, noteDurationSamples);

            return new PortaInfo(
                portaInitialSemis,
                portaSamples,
                sweepInitialSemis,
                sweepSamples);
        }

        public PortaInfo OnNoteOn(int midiNote, int engineSampleRate, int noteDurationSamples = 0)
        {
            var info = BuildPortaInfoForNoteOn(midiNote, engineSampleRate, noteDurationSamples);

            LastNote = Math.Clamp(midiNote, 0, 127);
            RestartModDelayForNewNote();

            return info;
        }

        private void RestartModDelayForNewNote()
        {
            _lfoPhase = LfoStartPhaseRad;
            _samplesSinceStart = 0;
            ResetModulationOutputsToNeutral();
        }

        private void ResetModulationOutputsToNeutral()
        {
            CurrentVolumeMod = 1.0f;
            CurrentPanMod = 0;
            CurrentLfoWave = 0f;
            _vibratoSemis = 0.0;
            CurrentPitchSemiOffset = GetPitchBendSemis();
        }

        public float CurrentLfoWave { get; private set; } = 0f; 
        public double GetEffectiveLfoHz()
        {
            if (ModDepth == 0 || ModRange == 0 || ModSpeed == 0) return 0.0;
            return ModSpeed * (50.0 / 127.0);
        }

        public void ResetAllControllersToDefault()
        {
            Volume = 1.0f;
            Volume2 = 1.0f;
            MainVolume = 1.0f;
            Pan = 64;  

            ModDepth = 0;
            ModRange = 1;
            ModSpeed = 16;
            ModType = 0;
            ModDelayFrames = 0;
            SweepPitchSemitones = 0.0;
            PortaEnabled = false;
            PortaTime = 0.0f;
            PortaTimeRaw = 0;
            _portaStartNote = -1;
            PitchBend = 0;
            BendRange = 2.0f;
            Transpose = 0;
            Priority = 64;
            SequencePriorityBase = 0;

            Attack = 255;
            Decay = 255;
            Sustain = 255;
            Release = 255;

            LoopStart = 0;
            LoopEnd = 0;

            RpnMsb = 0;
            RpnLsb = 0;
            DataEntry = 0;

            _lfoPhase = LfoStartPhaseRad;
            _samplesSinceStart = 0;
            _cachedDelaySamples = -1;

            _vibratoSemis = 0.0;

            CurrentPitchSemiOffset = 0.0;
            CurrentVolumeMod = 1.0f;
            CurrentPanMod = 0;

            LastNote = -1;
        }

    }
}

