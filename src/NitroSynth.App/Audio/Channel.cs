using System;
using NAudio.Wave;

namespace NitroSynth.App.Audio
{
    internal sealed class VoiceProvider : IWaveProvider
    {
        private readonly short[] _pcm;              
        private readonly int _sr;                   
        private readonly bool _loop;
        private readonly int _ls, _le;              
        private int _pos;                           
        private readonly float _vol;
        private readonly float _lg, _rg;            

        private enum EnvState { Attack, Decay, Sustain, Release, Done }
        private EnvState _st;
        private double _gain;                       
        private readonly int _atkSamples;
        private int _atkCount;                      
        private readonly double _decayMul;          
        private readonly double _releaseMul;        
        private readonly double _sustainGain;       
        private bool _noteOff;

        public WaveFormat WaveFormat { get; }

        private bool _looping;           
        private bool _tailing;           

        public VoiceProvider(
            short[] pcm16, int sampleRate,
            bool loop, int loopStart, int loopEnd,
            int attack, int decay, int sustain, int release, int pan, float volume)
        {
            _pcm = pcm16 ?? Array.Empty<short>();
            _sr = Math.Max(2000, sampleRate);
            _loop = loop && loopEnd > loopStart && _pcm.Length > 0;
            _ls = Math.Clamp(loopStart, 0, _pcm.Length);
            _le = Math.Clamp(loopEnd, _ls, _pcm.Length);
            _pos = 0;

            _vol = LevelCurve.Apply(volume);
            float p = Math.Clamp((pan - 64) / 63f * 0.5f + 0.5f, 0f, 1f); 
            _lg = (float)Math.Cos(p * Math.PI * 0.5) * _vol;
            _rg = (float)Math.Sin(p * Math.PI * 0.5) * _vol;

            double atkMs = EnvelopeTables.AttackMs[Math.Clamp(attack, 0, 127)];
            _atkSamples = (int)Math.Round(atkMs * _sr / 1000.0);
            if (_atkSamples <= 0) { _atkSamples = 1; _gain = 1.0; _st = EnvState.Decay; }
            else { _gain = 0.0; _st = EnvState.Attack; }

            double dDb = EnvelopeTables.DRSpeedDbPerMs[Math.Clamp(decay, 0, 127)];
            double rDb = EnvelopeTables.DRSpeedDbPerMs[Math.Clamp(release, 0, 127)];
            _decayMul = EnvelopeTables.DbPerMsToPerSampleMul(dDb, _sr);
            _releaseMul = EnvelopeTables.DbPerMsToPerSampleMul(rDb, _sr);
            _sustainGain = EnvelopeTables.SustainToGain(Math.Clamp(sustain, 0, 127));

            WaveFormat = WaveFormat.CreateCustomFormat(WaveFormatEncoding.Pcm, _sr, 2, _sr * 4, 4, 16);

            _looping = loop && loopEnd > loopStart && _pcm.Length > 0;

        }
        public void NoteOff()
        {
            _noteOff = true;
            _looping = false;   
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_st == EnvState.Done || _pcm.Length == 0) return 0;
            int framesReq = count / 4; 
            int written = 0;

            while (written < framesReq)
            {
                if (_pos >= (_looping ? _le : _pcm.Length))
                {
                    if (_looping)
                    {
                        int span = Math.Max(1, _le - _ls);
                        int over = _pos - _ls;
                        _pos = _ls + (over % span);
                    }
                    else
                    {
                        _tailing = true;              
                        _noteOff = true;              
                        if (_st != EnvState.Release) _st = EnvState.Release;
                    }
                }

                switch (_st)
                {
                    case EnvState.Attack:
                        _atkCount++;
                        _gain = Math.Min(1.0, (double)_atkCount / _atkSamples);
                        if (_atkCount >= _atkSamples) _st = EnvState.Decay;
                        break;

                    case EnvState.Decay:
                        if (_gain > _sustainGain)
                        {
                            _gain *= _decayMul;               
                            if (_gain <= _sustainGain) _gain = _sustainGain;
                        }
                        if (_noteOff) _st = EnvState.Release;
                        break;

                    case EnvState.Sustain:
                        if (_noteOff) _st = EnvState.Release;
                        break;

                    case EnvState.Release:
                        _gain *= _releaseMul;                 
                        if (_gain <= 0.00024) { _st = EnvState.Done; } 
                        break;
                }

                if (_st == EnvState.Done) break;

                if (_st == EnvState.Decay && _gain <= _sustainGain) _st = EnvState.Sustain;

                short s = _tailing ? (short)0
                           : _pcm[_pos++];


                int l = (int)Math.Round(s * _gain * _lg);
                int r = (int)Math.Round(s * _gain * _rg);
                if (l > short.MaxValue) l = short.MaxValue; else if (l < short.MinValue) l = short.MinValue;
                if (r > short.MaxValue) r = short.MaxValue; else if (r < short.MinValue) r = short.MinValue;

                int o = offset + written * 4;
                buffer[o + 0] = (byte)(l & 0xFF);
                buffer[o + 1] = (byte)((l >> 8) & 0xFF);
                buffer[o + 2] = (byte)(r & 0xFF);
                buffer[o + 3] = (byte)((r >> 8) & 0xFF);

                written++;
            }

            return written * 4;
        }
    }

    public sealed class Channel : IDisposable
    {
        private WaveOutEvent? _out;
        private VoiceProvider? _voice;

        public bool IsPlaying { get; private set; }

        public void Stop()
        {
            _out?.Stop();
            _out?.Dispose();
            _out = null;
            _voice = null;
            IsPlaying = false;
        }

        public void NoteOff() => _voice?.NoteOff();

        public void PlaySwav(
            SWAV swav, int midiNote, int baseKey,
            int attack, int decay, int sustain, int release, int pan,
            float volume = 1.0f)
        {
            Stop();

            double semis = midiNote - baseKey;
            double pitch = Math.Pow(2.0, semis / 12.0);
            int playRate = (int)Math.Round(swav.SampleRate * pitch);
            if (playRate < 2000) playRate = 2000;

            _voice = new VoiceProvider(
                swav.PCM16, playRate,
                swav.Loop, swav.LoopStartSample, swav.LoopEndSample,
                attack, decay, sustain, release, pan, Math.Clamp(volume, 0f, 1f));

            _out = new WaveOutEvent();
            _out.Init(_voice);
            _out.PlaybackStopped += (_, __) => { IsPlaying = false; };
            _out.Play();
            IsPlaying = true;
        }

        public void PlayNote(SWAV swav, int midiNote, int baseKey = 60, float volume = 1.0f)
            => PlaySwav(swav, midiNote, baseKey, 0, 64, 127, 64, 64, volume);

        public void Dispose() => Stop();
    }
}

