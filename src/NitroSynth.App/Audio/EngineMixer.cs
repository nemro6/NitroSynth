using System;
using System.Collections.Generic;
using System.Numerics;
using NAudio.Wave;

namespace NitroSynth.App.Audio
{
    internal sealed class EngineMixer : IWaveProvider
    {
        private const int HardwareChannelCount = 16;

        private sealed class VoiceAllocation
        {
            public VoiceAllocation(IVoice voice, int slot, long startOrder)
            {
                Voice = voice;
                Slot = slot;
                StartOrder = startOrder;
            }

            public IVoice Voice { get; }
            public int Slot { get; }
            public long StartOrder { get; }
        }

        private readonly object _lockObj = new();
        private readonly List<VoiceAllocation> _voices = new();
        private int _nextVoiceId = 0;
        private long _nextVoiceStartOrder = 0;

        private readonly EngineChannelState[] _channels;

        public int SampleRate { get; }
        public WaveFormat WaveFormat { get; }

        private volatile float _masterVolume = 1.0f;
        private volatile int _monoOutput;

        private volatile float _meterLevel;  
        private volatile float _meterPeak;
        private volatile int _meterClip;     

        private volatile float _meterLevelL;
        private volatile float _meterLevelR;
        private volatile float _meterPeakL;
        private volatile float _meterPeakR;
        private volatile int _meterClipL;
        private volatile int _meterClipR;


        private readonly float[] _chMeterLevel;
        private readonly float[] _chMeterPeak;
        private readonly int[] _chMeterClip;

        private readonly float[] _chBlockMaxAbs;
        private readonly int[] _chBlockClip;
        private readonly float[] _chPortaViz;

        private readonly int[] _chSumL;
        private readonly int[] _chSumR;
        private int _hardwareChannelEnabledMask = 0xFFFF;
        private int _trackOutputEnabledMask = 0xFFFF;
        private ushort _trackStealMask;

        public EngineMixer(int sampleRate, EngineChannelState[] channels)
        {
            SampleRate = sampleRate;
            _channels = channels ?? throw new ArgumentNullException(nameof(channels));

            WaveFormat = WaveFormat.CreateCustomFormat(
                WaveFormatEncoding.Pcm,
                sampleRate,
                2,
                sampleRate * 4,
                4,
                16);

            int chCount = _channels.Length;

            _chMeterLevel = new float[chCount];
            _chMeterPeak = new float[chCount];
            _chMeterClip = new int[chCount];

            _chBlockMaxAbs = new float[chCount];
            _chBlockClip = new int[chCount];
            _chPortaViz = new float[chCount];

            _chSumL = new int[chCount];
            _chSumR = new int[chCount];

        }

        private static ushort NormalizeChannelMask(ushort mask)
        {
            return mask == 0 ? (ushort)0xFFFF : mask;
        }

        private static bool IsSlotCompatible(int slot, VoiceKind kind, ushort allowedMask)
        {
            if ((uint)slot >= HardwareChannelCount)
                return false;

            if (((allowedMask >> slot) & 0x1) == 0)
                return false;

            return kind switch
            {
                VoiceKind.Pcm => true,
                VoiceKind.Psg => slot >= 8 && slot <= 13,
                VoiceKind.Noise => slot >= 14 && slot <= 15,
                _ => false
            };
        }

        private void RemoveDoneVoicesNoLock()
        {
            for (int i = _voices.Count - 1; i >= 0; i--)
            {
                if (_voices[i].Voice.Done)
                    _voices.RemoveAt(i);
            }
        }

        private int FindFreeCompatibleSlotNoLock(VoiceKind kind, ushort allowedMask)
        {
            for (int slot = 0; slot < HardwareChannelCount; slot++)
            {
                if (!IsSlotCompatible(slot, kind, allowedMask))
                    continue;

                bool used = false;
                for (int i = 0; i < _voices.Count; i++)
                {
                    if (_voices[i].Slot == slot)
                    {
                        used = true;
                        break;
                    }
                }

                if (!used)
                    return slot;
            }

            return -1;
        }

        private int FindVictimIndexNoLock(IVoice incomingVoice)
        {
            int victimIndex = -1;
            bool victimIsReleasing = false;
            float victimLevel = 0f;
            int victimPriority = 0;
            long victimStartOrder = 0;

            for (int i = 0; i < _voices.Count; i++)
            {
                var entry = _voices[i];
                if (!IsSlotCompatible(entry.Slot, incomingVoice.Kind, NormalizeChannelMask(incomingVoice.AllowedHardwareChannelMask)))
                    continue;

                bool isReleasing = entry.Voice.IsReleasing;
                float level = entry.Voice.CurrentLevel;
                int priority = entry.Voice.Priority;
                long startOrder = entry.StartOrder;

                if (victimIndex < 0)
                {
                    victimIndex = i;
                    victimIsReleasing = isReleasing;
                    victimLevel = level;
                    victimPriority = priority;
                    victimStartOrder = startOrder;
                    continue;
                }

                if (victimIsReleasing)
                {
                    if (!isReleasing)
                        continue;

                    bool hasLowerLevel = level < victimLevel;
                    bool sameLevelOlder = Math.Abs(level - victimLevel) <= 1e-6f && startOrder < victimStartOrder;
                    if (hasLowerLevel || sameLevelOlder)
                    {
                        victimIndex = i;
                        victimLevel = level;
                        victimPriority = priority;
                        victimStartOrder = startOrder;
                    }

                    continue;
                }

                if (isReleasing)
                {
                    victimIndex = i;
                    victimIsReleasing = true;
                    victimLevel = level;
                    victimPriority = priority;
                    victimStartOrder = startOrder;
                    continue;
                }

                bool hasLowerPriority = priority < victimPriority;
                bool samePriorityOlder = priority == victimPriority && startOrder < victimStartOrder;
                if (hasLowerPriority || samePriorityOlder)
                {
                    victimIndex = i;
                    victimPriority = priority;
                    victimStartOrder = startOrder;
                }
            }

            if (victimIndex < 0)
                return -1;

            if (victimIsReleasing)
                return victimIndex;

            int incomingPriority = incomingVoice.Priority;
            if (incomingPriority < victimPriority)
                return -1;

            return victimIndex;
        }

        public int AddVoice(IVoice v)
        {
            lock (_lockObj)
            {
                RemoveDoneVoicesNoLock();

                ushort allowedMask = NormalizeChannelMask(v.AllowedHardwareChannelMask);
                int slot = FindFreeCompatibleSlotNoLock(v.Kind, allowedMask);
                if (slot < 0)
                {
                    int victimIndex = FindVictimIndexNoLock(v);
                    if (victimIndex < 0)
                        return -1;

                    var victimVoice = _voices[victimIndex].Voice;
                    if (!victimVoice.IsReleasing && victimVoice.CurrentLevel > 0.02f)
                    {
                        int victimTrack = victimVoice.Channel.ChannelIndex;
                        if ((uint)victimTrack < HardwareChannelCount)
                            _trackStealMask |= (ushort)(1 << victimTrack);
                    }

                    slot = _voices[victimIndex].Slot;
                    _voices.RemoveAt(victimIndex);
                }

                _voices.Add(new VoiceAllocation(v, slot, _nextVoiceStartOrder++));
            }
            return v.Id;
        }

        public void NoteOff(int id)
        {
            lock (_lockObj)
            {
                for (int i = 0; i < _voices.Count; i++)
                {
                    if (_voices[i].Voice.Id == id)
                    {
                        _voices[i].Voice.NoteOff();
                        break;
                    }
                }
            }
        }

        public void StopVoice(int id)
        {
            lock (_lockObj)
            {
                _voices.RemoveAll(entry => entry.Voice.Id == id);
            }
        }

        public bool IsVoiceActive(int id)
        {
            lock (_lockObj)
            {
                for (int i = 0; i < _voices.Count; i++)
                {
                    if (_voices[i].Voice.Id == id)
                        return true;
                }

                return false;
            }
        }

        public AudioEngine.VoiceUsageSnapshot GetVoiceUsage()
        {
            lock (_lockObj)
            {
                RemoveDoneVoicesNoLock();

                int pcm = 0;
                int psg = 0;
                int noise = 0;
                int notePcm = 0;
                int notePsg = 0;
                int noteNoise = 0;
                ushort slotActiveMask = 0;
                ushort trackStealMask = _trackStealMask;
                _trackStealMask = 0;
                ulong slotTrackNibbles = 0;
                ulong slotMidiNotesLo = 0;
                ulong slotMidiNotesHi = 0;

                for (int i = 0; i < _voices.Count; i++)
                {
                    var entry = _voices[i];
                    var voice = entry.Voice;

                    if ((uint)entry.Slot <= 15u)
                    {
                        slotActiveMask |= (ushort)(1 << entry.Slot);

                        int slot = entry.Slot;
                        int track = Math.Clamp(voice.Channel.ChannelIndex, 0, 15);
                        int note = Math.Clamp(voice.MidiNote, 0, 127);

                        slotTrackNibbles |= ((ulong)track & 0xFUL) << (slot * 4);
                        if (slot < 8)
                            slotMidiNotesLo |= ((ulong)note & 0xFFUL) << (slot * 8);
                        else
                            slotMidiNotesHi |= ((ulong)note & 0xFFUL) << ((slot - 8) * 8);
                    }

                    switch (voice.Kind)
                    {
                        case VoiceKind.Pcm:
                            pcm++;
                            if (!voice.IsReleasing)
                                notePcm++;
                            break;
                        case VoiceKind.Psg:
                            psg++;
                            if (!voice.IsReleasing)
                                notePsg++;
                            break;
                        case VoiceKind.Noise:
                            noise++;
                            if (!voice.IsReleasing)
                                noteNoise++;
                            break;
                    }
                }

                return new AudioEngine.VoiceUsageSnapshot(
                    total: pcm + psg + noise,
                    pcm: pcm,
                    psg: psg,
                    noise: noise,
                    noteTotal: notePcm + notePsg + noteNoise,
                    notePcm: notePcm,
                    notePsg: notePsg,
                    noteNoise: noteNoise,
                    slotActiveMask: slotActiveMask,
                    trackStealMask: trackStealMask,
                    slotTrackNibbles: slotTrackNibbles,
                    slotMidiNotesLo: slotMidiNotesLo,
                    slotMidiNotesHi: slotMidiNotesHi);
            }
        }

        public IVoice CreatePcmVoice(
            EngineChannelState channel,
            byte velocity,
            int midiNote,
            short[] pcm16,
            int srcRate,
            int engineRate,
            bool loop,
            int loopStart,
            int loopEnd,
            int attack,
            int decay,
            int sustain,
            int release,
            int defaultPan,
            float baseVolume,
            PortaInfo porta,
            int priority,
            ushort allowedHardwareChannelMask)
        {
            int id = _nextVoiceId++;
            return new Voice(
                id, channel, velocity,
                midiNote,
                pcm16, srcRate, engineRate,
                loop, loopStart, loopEnd,
                attack, decay, sustain, release,
                defaultPan, baseVolume,
                porta.PortaInitialSemis, porta.PortaSamples,
                porta.SweepInitialSemis, porta.SweepSamples,
                priority, allowedHardwareChannelMask
            );
        }

        public IVoice CreatePsgVoice(
            EngineChannelState channel,
            byte velocity,
            int midiNote,
            int baseKey,
            int dutyIndex,
            int attack,
            int decay,
            int sustain,
            int release,
            int defaultPan,
            float baseVolume,
            PortaInfo porta,
            int priority,
            ushort allowedHardwareChannelMask)
        {
            int id = _nextVoiceId++;
            return new PsgVoice(
                id, channel, velocity,
                midiNote, baseKey, dutyIndex,
                WaveFormat.SampleRate,
                attack, decay, sustain, release,
                defaultPan, baseVolume,
                porta.PortaInitialSemis, porta.PortaSamples,
                porta.SweepInitialSemis, porta.SweepSamples,
                priority, allowedHardwareChannelMask
            );
        }

        public IVoice CreateNoiseVoice(
            EngineChannelState channel,
            byte velocity,
            int midiNote,
            int baseKye,
            bool use7bit,
            double clockHz,
            int attack,
            int decay,
            int sustain,
            int release,
            int defaultPan,
            float baseVolume,
            PortaInfo porta,
            int priority,
            ushort allowedHardwareChannelMask)
        {
            int id = _nextVoiceId++;
            return new NoiseVoice(
                id, channel, velocity, midiNote,
                use7bit, clockHz, WaveFormat.SampleRate,
                baseKye, attack, decay, sustain, release,
                defaultPan, baseVolume,
                porta.PortaInitialSemis, porta.PortaSamples,
                porta.SweepInitialSemis, porta.SweepSamples,
                priority, allowedHardwareChannelMask
            );
        }

        public AudioEngine.ChannelMeterSnapshot GetChannelMeter(int ch)
        {
            if ((uint)ch >= (uint)_chMeterPeak.Length) return default;

            return new AudioEngine.ChannelMeterSnapshot(
                level: _chMeterLevel[ch],
                peak: _chMeterPeak[ch],
                clip: _chMeterClip[ch] != 0
            );
        }

        public float GetChannelPortaViz(int ch)
        {
            if ((uint)ch >= (uint)_chPortaViz.Length) return 0f;
            return _chPortaViz[ch];
        }
        public void SetMasterVolume(float v)
        {
            int masterValue = DsVolumeCurve.ToMidi7Bit(v);
            int vol = DsVolumeCurve.SustainAttenuation(masterValue);
            _masterVolume = DsVolumeCurve.GetChannelVolume(vol) / 127f;
        }

        public void SetMonoOutput(bool enabled)
        {
            _monoOutput = enabled ? 1 : 0;
        }

        public AudioEngine.MasterMeterSnapshot GetMasterMeter()
        {
            return new AudioEngine.MasterMeterSnapshot(
                level: _meterLevel,
                peak: _meterPeak,
                clip: _meterClip != 0
            );
        }

        public AudioEngine.MasterMeterSnapshotLR GetMasterMeterLR()
        {
            return new AudioEngine.MasterMeterSnapshotLR(
                levelL: _meterLevelL,
                levelR: _meterLevelR,
                peakL: _meterPeakL,
                peakR: _meterPeakR,
                clipL: _meterClipL != 0,
                clipR: _meterClipR != 0
            );
        }

        public void SetHardwareChannelEnabled(int channel, bool enabled)
        {
            if ((uint)channel >= HardwareChannelCount)
                return;

            lock (_lockObj)
            {
                int bit = 1 << channel;
                if (enabled)
                    _hardwareChannelEnabledMask |= bit;
                else
                    _hardwareChannelEnabledMask &= ~bit;
            }
        }

        public void SetHardwareChannelEnabledMask(ushort mask)
        {
            lock (_lockObj)
            {
                _hardwareChannelEnabledMask = mask;
            }
        }

        public void SetTrackOutputEnabled(int track, bool enabled)
        {
            if ((uint)track >= HardwareChannelCount)
                return;

            lock (_lockObj)
            {
                int bit = 1 << track;
                if (enabled)
                    _trackOutputEnabledMask |= bit;
                else
                    _trackOutputEnabledMask &= ~bit;
            }
        }

        public void SetTrackOutputEnabledMask(ushort mask)
        {
            lock (_lockObj)
            {
                _trackOutputEnabledMask = mask;
            }
        }


        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / 4; 
            int writtenFrames = 0;

            float maxAbsL = 0f;
            float maxAbsR = 0f;
            bool clipThisBlockL = false;
            bool clipThisBlockR = false;


            lock (_lockObj)
            {
                Array.Clear(_chBlockMaxAbs, 0, _chBlockMaxAbs.Length);
                Array.Clear(_chBlockClip, 0, _chBlockClip.Length);
                Array.Clear(_chPortaViz, 0, _chPortaViz.Length);
                int enabledSlotMask = _hardwareChannelEnabledMask;
                int enabledTrackMask = _trackOutputEnabledMask;

                for (int i = 0; i < frames; i++)
                {
                    if (_voices.Count > 0)
                    {
                        int sr = WaveFormat.SampleRate;
                        for (int ch = 0; ch < _channels.Length; ch++)
                            _channels[ch].AdvanceLfo(sr);
                    }

                    int sumL = 0;
                    int sumR = 0;

                    uint usedMask = 0u; 

                    for (int vi = _voices.Count - 1; vi >= 0; vi--)
                    {
                        var entry = _voices[vi];
                        var v = entry.Voice;
                        if (v.Done)
                        {
                            _voices.RemoveAt(vi);
                            continue;
                        }

                        v.RenderSample(out int l, out int r);
                        int ch = v.Channel.ChannelIndex;
                        bool trackEnabled =
                            (uint)ch < HardwareChannelCount &&
                            ((enabledTrackMask >> ch) & 0x1) != 0;
                        if (((enabledSlotMask >> entry.Slot) & 0x1) == 0 || !trackEnabled)
                        {
                            l = 0;
                            r = 0;
                        }
                        sumL += l;
                        sumR += r;

                        if ((uint)ch < (uint)_chSumL.Length)
                        {
                            usedMask |= (1u << ch);
                            _chSumL[ch] += l;
                            _chSumR[ch] += r;

                            float portaViz = v.PortaViz;
                            if (MathF.Abs(portaViz) > MathF.Abs(_chPortaViz[ch]))
                                _chPortaViz[ch] = portaViz;
                        }
                    }

                    uint mask = usedMask;
                    while (mask != 0u)
                    {
                        int ch = BitOperations.TrailingZeroCount(mask);
                        mask &= (mask - 1u);

                        int cl = _chSumL[ch];
                        int cr = _chSumR[ch];
                        _chSumL[ch] = 0;
                        _chSumR[ch] = 0;

                        if (cl > short.MaxValue || cl < short.MinValue ||
                            cr > short.MaxValue || cr < short.MinValue)
                        {
                            _chBlockClip[ch] = 1;
                        }

                        int scl = cl;
                        if (scl > short.MaxValue) scl = short.MaxValue;
                        else if (scl < short.MinValue) scl = short.MinValue;

                        int scr = cr;
                        if (scr > short.MaxValue) scr = short.MaxValue;
                        else if (scr < short.MinValue) scr = short.MinValue;

                        float chAbsL = MathF.Abs(scl) / short.MaxValue;
                        float chAbsR = MathF.Abs(scr) / short.MaxValue;
                        float chAbs = MathF.Max(chAbsL, chAbsR);

                        if (chAbs > _chBlockMaxAbs[ch]) _chBlockMaxAbs[ch] = chAbs;
                    }

                    float mv = _masterVolume;

                    int outL = (int)MathF.Round(sumL * mv);
                    int outR = (int)MathF.Round(sumR * mv);

                    if (_monoOutput != 0)
                    {
                        int mono = outL + outR;
                        outL = mono;
                        outR = mono;
                    }

                    if (outL > short.MaxValue || outL < short.MinValue) clipThisBlockL = true;
                    if (outR > short.MaxValue || outR < short.MinValue) clipThisBlockR = true;

                    if (outL > short.MaxValue) outL = short.MaxValue;
                    else if (outL < short.MinValue) outL = short.MinValue;

                    if (outR > short.MaxValue) outR = short.MaxValue;
                    else if (outR < short.MinValue) outR = short.MinValue;

                    float absL = MathF.Abs(outL) / short.MaxValue;
                    float absR = MathF.Abs(outR) / short.MaxValue;

                    if (absL > maxAbsL) maxAbsL = absL;
                    if (absR > maxAbsR) maxAbsR = absR;

                    int o = offset + writtenFrames * 4;
                    buffer[o + 0] = (byte)(outL & 0xFF);
                    buffer[o + 1] = (byte)((outL >> 8) & 0xFF);
                    buffer[o + 2] = (byte)(outR & 0xFF);
                    buffer[o + 3] = (byte)((outR >> 8) & 0xFF);

                    writtenFrames++;
                }

                for (int ch = 0; ch < _chMeterPeak.Length; ch++)
                {
                    float p = Math.Clamp(_chBlockMaxAbs[ch], 0f, 1f);
                    _chMeterPeak[ch] = p;
                    _chMeterLevel[ch] = p;        
                    _chMeterClip[ch] = _chBlockClip[ch];
                }

                _meterLevelL = maxAbsL;
                _meterLevelR = maxAbsR;

                _meterPeakL = maxAbsL;
                _meterPeakR = maxAbsR;

                _meterClipL = clipThisBlockL ? 1 : 0;
                _meterClipR = clipThisBlockR ? 1 : 0;

                float masterAbs = MathF.Max(maxAbsL, maxAbsR);
                _meterLevel = masterAbs;
                _meterPeak = masterAbs;
                _meterClip = (clipThisBlockL || clipThisBlockR) ? 1 : 0;
            }

            return writtenFrames * 4;
        }


    }
}

