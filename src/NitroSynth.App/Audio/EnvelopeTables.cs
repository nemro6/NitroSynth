using System;

namespace NitroSynth.App.Audio
{
    internal static class EnvelopeTables
    {
        // NITRO Composer BankDataManual 8.1.1 (attack value -> attack time [ms])
        public static readonly double[] AttackMs = new double[128]
        {
            8606.1, 4756.3, 3339.3, 2594.4, 2130.7, 1807.7, 1573.3, 1401.4,
            1255.5, 1140.9, 1047.1, 963.8, 896.0, 838.7, 786.6, 745.0,
            703.3, 666.8, 630.4, 599.1, 578.3, 547.0, 526.2, 505.3,
            484.5, 468.9, 448.0, 437.6, 416.8, 406.4, 395.9, 385.5,
            369.9, 359.5, 349.0, 338.6, 328.2, 323.0, 312.6, 307.4,
            297.0, 291.7, 286.5, 276.1, 270.9, 265.7, 260.5, 255.3,
            250.1, 244.9, 239.6, 234.4, 229.2, 224.0, 218.8, 213.6,
            213.6, 208.4, 203.2, 203.2, 198.0, 198.0, 192.8, 192.8,
            182.3, 182.3, 177.1, 177.1, 171.9, 171.9, 166.7, 166.7,
            161.5, 161.5, 156.3, 156.3, 151.1, 151.1, 145.9, 145.9,
            145.9, 145.9, 140.7, 140.7, 140.7, 130.2, 130.2, 130.2,
            125.0, 125.0, 125.0, 125.0, 119.8, 119.8, 119.8, 114.6,
            114.6, 114.6, 114.6, 109.4, 109.4, 109.4, 109.4, 109.4,
            104.2, 104.2, 104.2, 104.2,  99.0,  93.8,  88.6,  83.4,
             78.2,  72.9,  67.7,  62.5,  57.3,  52.1,  46.9,  41.7,
             36.5,  31.3,  26.1,  20.8,  15.6,  10.4,  10.4,   0.0
        };

        // NITRO Composer BankDataManual 8.1.2 (decay/release value -> speed [dB/ms])
        public static readonly double[] DRSpeedDbPerMs = new double[128]
        {
            -0.0002, -0.0005, -0.0008, -0.0011, -0.0014, -0.0017, -0.0020, -0.0023,
            -0.0026, -0.0029, -0.0032, -0.0035, -0.0038, -0.0041, -0.0044, -0.0047,
            -0.0050, -0.0053, -0.0056, -0.0059, -0.0062, -0.0065, -0.0068, -0.0071,
            -0.0074, -0.0077, -0.0080, -0.0083, -0.0086, -0.0089, -0.0092, -0.0095,
            -0.0098, -0.0101, -0.0104, -0.0107, -0.0110, -0.0113, -0.0116, -0.0119,
            -0.0122, -0.0125, -0.0128, -0.0131, -0.0134, -0.0137, -0.0140, -0.0143,
            -0.0146, -0.0149, -0.0152, -0.0154, -0.0156, -0.0158, -0.0160, -0.0163,
            -0.0165, -0.0167, -0.0170, -0.0172, -0.0175, -0.0178, -0.0180, -0.0183,
            -0.0186, -0.0189, -0.0192, -0.0196, -0.0199, -0.0202, -0.0206, -0.0210,
            -0.0214, -0.0218, -0.0222, -0.0226, -0.0231, -0.0235, -0.0240, -0.0245,
            -0.0251, -0.0256, -0.0262, -0.0268, -0.0275, -0.0281, -0.0288, -0.0296,
            -0.0304, -0.0312, -0.0321, -0.0330, -0.0339, -0.0350, -0.0361, -0.0372,
            -0.0385, -0.0398, -0.0412, -0.0427, -0.0444, -0.0462, -0.0481, -0.0502,
            -0.0524, -0.0549, -0.0577, -0.0607, -0.0641, -0.0679, -0.0721, -0.0769,
            -0.0824, -0.0888, -0.0962, -0.1049, -0.1154, -0.1282, -0.1442, -0.1648,
            -0.1923, -0.2308, -0.2885, -0.3846, -0.5769, -1.1538, -2.2897, -9.8460
        };

        private static readonly double[] SustainGain = BuildSustainGain();

        public static double DbPerMsToPerSampleMul(double dbPerMs, int sampleRate)
        {
            double msPerSample = 1000.0 / sampleRate;
            return Math.Pow(10.0, (dbPerMs * msPerSample) / 20.0);
        }

        public static double SustainToGain(int sustain127)
        {
            return SustainGain[Math.Clamp(sustain127, 0, 127)];
        }

        private static double[] BuildSustainGain()
        {
            var table = new double[128];
            for (int i = 0; i < table.Length; i++)
            {
                int attenuation = DsVolumeCurve.SustainAttenuation(i);
                table[i] = DsVolumeCurve.GetChannelVolume(attenuation) / 127.0;
            }
            return table;
        }
    }

    internal sealed class DsEnvelope
    {
        private enum EnvState { Attack, Decay, Sustain, Release, Done }
        private const double ReleaseStopGain = 0.00024266100969583025; // 10^(-72.3/20)

        private EnvState _state;
        private double _gain;

        private int _atkSamples;
        private int _atkCount;
        private double _atkPhase;
        private double _atkCurvePower;

        private double _decayMul;
        private double _releaseMul;
        private double _sustainGain;
        private bool _releaseDisabled;

        private bool _noteOff;
        private int _sampleRate;

        public bool IsDone => _state == EnvState.Done;
        public bool IsReleasing => _state == EnvState.Release;

        public void Init(int attack, int decay, int sustain, int release, int sampleRate)
        {
            _sampleRate = Math.Max(2000, sampleRate);

            int atkIdx = Math.Clamp(attack, 0, 127);
            _atkCurvePower = AttackCurvePower(atkIdx);

            double atkMs = EnvelopeTables.AttackMs[atkIdx];

            if (atkMs <= 0.0 || atkIdx == 127)
            {
                _state = EnvState.Decay;
                _gain = 1.0;
                _atkSamples = 0;
                _atkCount = 0;
                _atkPhase = 1.0;
            }
            else
            {
                double totalSamples = atkMs * _sampleRate / 1000.0;
                _atkSamples = (int)Math.Max(1.0, Math.Round(totalSamples));
                _atkCount = 0;
                _atkPhase = 0.0;
                _gain = 0.0;
                _state = EnvState.Attack;
            }

            int dIdx = Math.Clamp(decay, 0, 127);
            double dDbPerMs = EnvelopeTables.DRSpeedDbPerMs[dIdx];
            _decayMul = EnvelopeTables.DbPerMsToPerSampleMul(dDbPerMs, _sampleRate);

            int sIdx = Math.Clamp(sustain, 0, 127);
            _sustainGain = EnvelopeTables.SustainToGain(sIdx);

            _releaseDisabled = release >= 128;
            if (_releaseDisabled)
            {
                _releaseMul = 1.0;
            }
            else
            {
                int rParam = Math.Clamp(release, 0, 127);
                double rDb = EnvelopeTables.DRSpeedDbPerMs[rParam];
                _releaseMul = EnvelopeTables.DbPerMsToPerSampleMul(rDb, _sampleRate);
            }

            _noteOff = false;
        }

        public void NoteOff()
        {
            if (_releaseDisabled)
                return;

            _noteOff = true;
            if (_state is EnvState.Attack or EnvState.Decay or EnvState.Sustain)
            {
                _state = EnvState.Release;
            }
        }

        public double Next()
        {
            switch (_state)
            {
                case EnvState.Attack:
                    {
                        if (_noteOff)
                        {
                            _state = EnvState.Release;
                            break;
                        }

                        _atkCount++;
                        _atkPhase = Math.Min(1.0, (double)_atkCount / _atkSamples);

                        _gain = Math.Pow(_atkPhase, _atkCurvePower);

                        if (_atkPhase >= 1.0)
                        {
                            _gain = 1.0;
                            _state = EnvState.Decay;
                        }
                        break;
                    }

                case EnvState.Decay:
                    if (_gain > _sustainGain)
                    {
                        _gain *= _decayMul;
                        if (_gain <= _sustainGain)
                            _gain = _sustainGain;
                    }
                    if (_noteOff)
                    {
                        _state = EnvState.Release;
                    }
                    else if (_gain <= _sustainGain + 1e-6)
                    {
                        _state = EnvState.Sustain;
                    }
                    break;

                case EnvState.Sustain:
                    _gain = _sustainGain;
                    if (_noteOff)
                    {
                        _state = EnvState.Release;
                    }
                    break;

                case EnvState.Release:
                    _gain *= _releaseMul;
                    if (_gain <= ReleaseStopGain)
                    {
                        _gain = 0.0;
                        _state = EnvState.Done;
                    }
                    break;

                case EnvState.Done:
                    _gain = 0.0;
                    break;
            }

            return _gain;
        }

        private static double AttackCurvePower(int attack)
        {
            // Manual 8.1.1 notes that same-time attacks still differ in rise shape.
            // Use an attack-index-dependent curvature so larger attack values rise faster.
            double t = Math.Clamp(attack, 0, 127) / 127.0;
            return 1.65 - (1.2 * t); // 1.65 .. 0.45
        }
    }
}

