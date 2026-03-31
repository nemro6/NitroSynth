using System;

namespace NitroSynth.App.Audio
{
    internal sealed class PsgVoice : IVoice
    {
        public int Id { get; }
        public int MidiNote => _midiNote;
        public VoiceKind Kind => VoiceKind.Psg;
        public int Priority => _priority;
        public ushort AllowedHardwareChannelMask => _allowedHardwareChannelMask;
        public EngineChannelState Channel => _channel;
        public float PortaViz => _portaViz;
        public bool IsReleasing => _envelope.IsReleasing;
        public float CurrentLevel => _currentLevel;

        private readonly Psg _psg;
        private readonly EngineChannelState _channel;
        private readonly byte _velocity;
        private readonly float _baseVolume;
        private readonly int _defaultPan;
        private readonly int _priority;
        private readonly ushort _allowedHardwareChannelMask;
        private readonly int _midiNote;
        private readonly int _baseKey;

        private readonly double _portaInitialSemis;
        private readonly int _portaSamples;
        private int _portaElapsed;
        private float _portaViz;

        private readonly double _sweepInitialSemis;
        private readonly int _sweepSamples;
        private int _sweepElapsed;

        private readonly DsEnvelope _envelope = new();
        private readonly int _engineRate;
        private bool _done;
        private float _currentLevel;

        public bool Done => _done;

        public PsgVoice(
            int id,
            EngineChannelState channel,
            byte velocity,
            int midiNote,
            int baseKey,
            int dutyIndex,
            int engineRate,
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
            _baseKey = Math.Clamp(baseKey, 0, 127);
            _engineRate = Math.Max(1, engineRate);

            _portaInitialSemis = portaInitialSemis;
            _portaSamples = Math.Max(0, portaSamples);
            _portaElapsed = 0;
            _sweepInitialSemis = sweepInitialSemis;
            _sweepSamples = Math.Max(0, sweepSamples);
            _sweepElapsed = 0;

            _psg = new Psg();
            _psg.Prepare(engineRate);
            _psg.SetDutyIndex(dutyIndex);
            _psg.SetBaseMidiNote(_baseKey);
            _psg.SetGain(1.0f);

            _envelope.Init(attack, decay, sustain, release, _engineRate);
        }

        public void NoteOff() => _envelope.NoteOff();

        public void RenderSample(out int l, out int r)
        {
            float envGain = (float)_envelope.Next();

            if (_envelope.IsDone)
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

            double semisFromInst = (_midiNote - _baseKey) + _channel.Transpose;
            double totalOffset = semisFromInst + _channel.CurrentPitchSemiOffset + portaSemis + sweepSemis;
            _psg.PitchOffsetSemi = totalOffset;

            float ps = _psg.ProcessSample();

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

            double sample = ps * gainNow;

            int li = (int)Math.Round(sample * gl * short.MaxValue);
            int ri = (int)Math.Round(sample * gr * short.MaxValue);

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
