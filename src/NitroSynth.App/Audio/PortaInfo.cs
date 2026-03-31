namespace NitroSynth.App.Audio
{
    internal readonly struct PortaInfo
    {
        public static readonly PortaInfo None = new(0.0, 0, 0.0, 0);

        public readonly double PortaInitialSemis;
        public readonly int PortaSamples;
        public readonly double SweepInitialSemis;
        public readonly int SweepSamples;

        public bool PortaEnabled => PortaSamples > 0 && System.Math.Abs(PortaInitialSemis) > 1e-9;
        public bool SweepEnabled => SweepSamples > 0 && System.Math.Abs(SweepInitialSemis) > 1e-9;

        public PortaInfo(
            double portaInitialSemis,
            int portaSamples,
            double sweepInitialSemis,
            int sweepSamples)
        {
            PortaInitialSemis = portaInitialSemis;
            PortaSamples = portaSamples;
            SweepInitialSemis = sweepInitialSemis;
            SweepSamples = sweepSamples;
        }
    }
}

