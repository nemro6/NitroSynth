using System;

namespace NitroSynth.App.Audio
{
    internal sealed class Noise
    {
        public enum LfsrMode
        {
            Bit15,
            Bit7
        }

        private const double DsTimerBaseHz = 33513982.0 / 2.0;

        private int _sampleRate;

        private double _tickAcc;

        private double _baseClockHz = 8000.0;

        private LfsrMode _mode = LfsrMode.Bit15;
        private float _gain = 1.0f;

        private ushort _x15 = 0x7FFF;
        private byte _x7 = 0x7F;

        private float _lastOut = 1.0f;

        public double PitchOffsetSemi { get; set; }

        public void Prepare(int sampleRate)
        {
            _sampleRate = Math.Max(8000, sampleRate);
            _tickAcc = 0.0;

            _x15 = 0x7FFF;
            _x7 = 0x7F;
            _lastOut = 1.0f;
            PitchOffsetSemi = 0.0;
        }

        public void SetMode(LfsrMode mode)
        {
            if (_mode == mode) return;

            _mode = mode;
            _tickAcc = 0.0;
            _x15 = 0x7FFF;
            _x7 = 0x7F;
            _lastOut = 1.0f;
        }

        public void SetClockRate(double clockHz)
        {
            if (double.IsNaN(clockHz) || double.IsInfinity(clockHz))
                clockHz = 0.0;

            _baseClockHz = Math.Clamp(clockHz, 0.0, DsTimerBaseHz);
        }

        public void SetGain(float linearGain)
        {
            if (float.IsNaN(linearGain) || float.IsInfinity(linearGain))
                linearGain = 0f;

            _gain = Math.Clamp(linearGain, 0f, 4f);
        }

        public static double DsTimerToClockHz(ushort timer)
        {
            int denom = 0x10000 - timer;
            if (denom <= 0) denom = 1;
            return DsTimerBaseHz / denom;
        }

        public float ProcessSample()
        {
            double clockHz = _baseClockHz;
            if (Math.Abs(PitchOffsetSemi) > 1e-9)
                clockHz *= Math.Pow(2.0, PitchOffsetSemi / 12.0);

            if (clockHz <= 0.0)
                return 0.0f;

            _tickAcc += clockHz / _sampleRate;

            int steps = (int)_tickAcc; 
            if (steps <= 0)
            {
                return _lastOut * _gain;
            }

            _tickAcc -= steps;

            if (steps > 4096)
                steps = 4096;

            // NDS PSG-noise toggles between full HIGH/LOW levels at the selected sample rate.
            // Averaging many sub-steps inside one output sample artificially attenuates volume
            // at high pitches, so we advance state and output the final held level.
            for (int i = 0; i < steps; i++)
            {
                StepLfsr_DSStyle();
            }

            return _lastOut * _gain;
        }

        private void StepLfsr_DSStyle()
        {
            if (_mode == LfsrMode.Bit7)
            {
                int carry = _x7 & 1;
                _x7 = (byte)(_x7 >> 1);

                if (carry != 0)
                    _x7 ^= 0x60; 

                if (_x7 == 0) _x7 = 0x7F;

                _lastOut = (carry != 0) ? -1.0f : 1.0f;
            }
            else
            {
                int carry = _x15 & 1;
                _x15 = (ushort)(_x15 >> 1);

                if (carry != 0)
                    _x15 ^= 0x6000;

                _x15 &= 0x7FFF;
                if (_x15 == 0) _x15 = 0x7FFF;

                _lastOut = (carry != 0) ? -1.0f : 1.0f;
            }
        }

        public static double ParamToClockHz(int param0to127)
        {
            int p = Math.Clamp(param0to127, 0, 127);

            const double min = 200.0;
            const double max = 2_000_000.0;

            double t = 1.0 - (p / 127.0); 
            return min * Math.Pow(max / min, t);
        }
    }
}

