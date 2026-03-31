using System;

namespace NitroSynth.App.Audio
{
    internal sealed class Psg
    {
        private int _sampleRate;
        private int _baseMidiNote = 60; 
        private double _phase;
        private double _baseStep;
        private int _dutyIndex;
        private float _gain = 1.0f;

        public double PitchOffsetSemi { get; set; }

        public void Prepare(int sampleRate)
        {
            _sampleRate = Math.Max(8000, sampleRate);
            _phase = 0.0;
            RecalcBaseStep();
        }

        public void SetBaseMidiNote(int midiNote)
        {
            _baseMidiNote = Math.Clamp(midiNote, 0, 127);
            RecalcBaseStep();
        }

        public void SetDutyIndex(int dutyIndex)
        {
            _dutyIndex = Math.Clamp(dutyIndex, 0, 6);
        }

        public void SetGain(float linearGain)
        {
            _gain = linearGain;
        }

        private void RecalcBaseStep()
        {
            double freq = 440.0 * Math.Pow(2.0, (_baseMidiNote - 69) / 12.0);
            _baseStep = freq / _sampleRate;
        }

        private static double DutyFromIndex(int idx)
        {
            return idx switch
            {
                0 => 0.125, 
                1 => 0.25,  
                2 => 0.375, 
                3 => 0.5,   
                4 => 0.625, 
                5 => 0.75,
                6 => 0.875,
                _ => 0.5
            };
        }

        public float ProcessSample()
        {
            double step = _baseStep;
            if (Math.Abs(PitchOffsetSemi) > 1e-6)
            {
                double mul = Math.Pow(2.0, PitchOffsetSemi / 12.0);
                step *= mul;
            }

            _phase += step;
            if (_phase >= 1.0)
                _phase -= Math.Floor(_phase);

            double duty = DutyFromIndex(_dutyIndex);
            float s = (_phase < duty) ? 1.0f : -1.0f;

            return s * _gain; 
        }
    }
}

