using System;

namespace NitroSynth.App.Audio
{
    internal enum VoiceKind
    {
        Pcm,
        Psg,
        Noise
    }

    internal interface IVoice
    {
        int Id { get; }
        int MidiNote { get; }
        bool Done { get; }
        VoiceKind Kind { get; }
        int Priority { get; }
        ushort AllowedHardwareChannelMask { get; }
        EngineChannelState Channel { get; }   
        float PortaViz { get; }
        bool IsReleasing { get; }
        float CurrentLevel { get; }
        void RenderSample(out int l, out int r);
        void NoteOff();
    }
    internal sealed class Voice : IVoice
    {
        public int Id { get; }
        public int MidiNote => _midiNote;
        public VoiceKind Kind => VoiceKind.Pcm;
        public int Priority => _priority;
        public ushort AllowedHardwareChannelMask => _allowedHardwareChannelMask;
        public EngineChannelState Channel => _channel;
        public float PortaViz => _portaViz;
        public bool IsReleasing => _envelope.IsReleasing;
        public float CurrentLevel => _currentLevel;

        private readonly short[] _pcm;
        private readonly int _srcRate;
        private readonly int _engineRate;

        private readonly bool _loop;
        private readonly int _ls;
        private readonly int _le;

        private readonly EngineChannelState _channel;
        private readonly byte _velocity;
        private readonly float _baseVolume;
        private readonly int _defaultPan;
        private readonly int _priority;
        private readonly ushort _allowedHardwareChannelMask;
        private readonly int _midiNote;

        private double _pos;
        private readonly double _step;

        private readonly double _portaInitialSemis;
        private readonly int _portaSamples;
        private int _portaElapsed;
        private float _portaViz;

        private readonly double _sweepInitialSemis;
        private readonly int _sweepSamples;
        private int _sweepElapsed;

        private readonly DsEnvelope _envelope = new();
        private bool _done;
        private float _currentLevel;

        public bool Done => _done;

        public Voice(
            int id,
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
            double portaInitialSemis,
            int portaSamples,
            double sweepInitialSemis,
            int sweepSamples,
            int priority,
            ushort allowedHardwareChannelMask)
        {
            Id = id;
            _channel = channel;
            _velocity = velocity;
            _baseVolume = baseVolume;
            _defaultPan = defaultPan;
            _priority = priority;
            _allowedHardwareChannelMask = allowedHardwareChannelMask;
            _midiNote = Math.Clamp(midiNote, 0, 127);

            _pcm = pcm16 ?? Array.Empty<short>();
            _srcRate = Math.Max(2000, srcRate);
            _engineRate = engineRate;

            _portaInitialSemis = portaInitialSemis;
            _portaSamples = Math.Max(0, portaSamples);
            _portaElapsed = 0;
            _sweepInitialSemis = sweepInitialSemis;
            _sweepSamples = Math.Max(0, sweepSamples);
            _sweepElapsed = 0;

            int len = _pcm.Length;

            _loop = loop && len > 0;
            if (_loop)
            {
                int ls = Math.Clamp(loopStart, 0, Math.Max(0, len - 1));
                int le;

                if (loopEnd <= 0 || loopEnd > len) le = len;
                else le = loopEnd;

                if (le <= ls) le = Math.Min(len, ls + 1);

                _ls = ls;
                _le = le;
            }
            else
            {
                _ls = 0;
                _le = len;
            }

            _step = (double)_srcRate / _engineRate;
            _pos = 0.0;

            _envelope.Init(attack, decay, sustain, release, _engineRate);
        }

        public void NoteOff()
        {
            _envelope.NoteOff();
        }

        public void RenderSample(out int l, out int r)
        {
            float envGain = (float)_envelope.Next();

            if (_envelope.IsDone || _pcm.Length == 0)
            {
                _done = true;
                _portaViz = 0f;
                _currentLevel = 0f;
                l = r = 0;
                return;
            }

            double portaSemis = 0.0;
            if (_portaSamples > 0 && _portaElapsed < _portaSamples)
            {
                double tt = (double)_portaElapsed / _portaSamples; 
                portaSemis = _portaInitialSemis * (1.0 - tt);
                _portaElapsed++;
            }
            _portaViz = (float)Math.Clamp(portaSemis / 12.0, -1.0, 1.0);

            double sweepSemis = 0.0;
            if (_sweepSamples > 0 && _sweepElapsed < _sweepSamples)
            {
                double tt = (double)_sweepElapsed / _sweepSamples;
                sweepSemis = _sweepInitialSemis * (1.0 - tt);
                _sweepElapsed++;
            }

            double semisOffset = _channel.CurrentPitchSemiOffset + portaSemis + sweepSemis;

            double pitchMul = Math.Pow(2.0, semisOffset / 12.0);
            double stepNow = _step * pitchMul;

            int pcmLen = _pcm.Length;
            int idx = (int)_pos;

            if (idx < 0)
            {
                idx = 0;
                _pos = 0.0;
            }

            if (_loop)
            {
                int ls = Math.Clamp(_ls, 0, Math.Max(0, pcmLen - 1));
                int le = Math.Clamp(_le, ls + 1, pcmLen);

                if (idx >= le)
                {
                    int span = Math.Max(1, le - ls);
                    int rel = idx - ls;
                    idx = ls + (rel % span);

                    if (idx < 0) idx = 0;
                    if (idx >= pcmLen) idx = pcmLen - 1;

                    _pos = idx;
                }
            }
            else
            {
                if (idx >= pcmLen)
                {
                    _done = true;
                    _portaViz = 0f;
                    _currentLevel = 0f;
                    l = r = 0;
                    return;
                }
            }

            short s = _pcm[idx];
            _pos += stepNow;

            float chGain = _channel.GetFinalVolume(_velocity, _baseVolume) * envGain;
            if (chGain <= 0f)
            {
                _portaViz = 0f;
                _currentLevel = 0f;
                l = r = 0;
                return;
            }

            float volLfoGain = _channel.CurrentVolumeMod;
            float gainNow = chGain * volLfoGain;
            if (gainNow > 1f) gainNow = 1f;
            _currentLevel = Math.Clamp(gainNow, 0f, 1f);
            if (gainNow <= 0f)
            {
                _portaViz = 0f;
                _currentLevel = 0f;
                l = r = 0;
                return;
            }

            int trackPan = Math.Clamp(_channel.Pan, 0, 127);
            int instPan = Math.Clamp(_defaultPan, 0, 127);
            int panValue = 64 + (trackPan - 64) + (instPan - 64) + _channel.CurrentPanMod;
            panValue = Math.Clamp(panValue, 0, 127);
            double panNorm = PanToNorm(panValue);

            double a = (panNorm + 1.0) * 0.5;
            double gl = Math.Cos(a * Math.PI * 0.5);
            double gr = Math.Sin(a * Math.PI * 0.5);

            double sample = s * gainNow;

            int li = (int)Math.Round(sample * gl);
            int ri = (int)Math.Round(sample * gr);

            if (li > short.MaxValue) li = short.MaxValue;
            else if (li < short.MinValue) li = short.MinValue;
            if (ri > short.MaxValue) ri = short.MaxValue;
            else if (ri < short.MinValue) ri = short.MinValue;

            l = li;
            r = ri;
        }

        private static double PanToNorm(int panValue)
        {
            int pan = Math.Clamp(panValue, 0, 127);
            return pan <= 64
                ? (pan - 64) / 64.0
                : (pan - 64) / 63.0;
        }
    }
}

