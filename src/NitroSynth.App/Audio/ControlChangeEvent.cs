using System;

namespace NitroSynth.App.Audio
{
    public sealed class ControlChangeEvent
    {
        public int Tick { get; }
        public byte Channel { get; }
        public byte ControllerNumber { get; }
        public byte Value { get; }

        public Command Kind { get; }

        public enum Command
        {
            Unknown,

            ModDepth,           
            PortaTime,          
            DataEntry,          
            Volume,             
            Pan,                
            Volume2,            
            MainVolume,         
            Transpose,          
            Priority,           
            BendRange,          
            ModSpeed,           
            ModType,            
            ModRange,           
            ModDelay,           
            ModDelayTimes10,    
            SweepPitch,         
            SweepPitchTimes24,  
            PortaOnOff,         
            Porta,              
            Attack,             
            Decay,              
            Sustain,            
            Release,            
            LoopStart,          
            LoopEnd,            
            RpnLsb,             
            RpnMsb,             
            PitchBend,          
        }

        private ControlChangeEvent(
            int tick,
            byte channel,
            byte controllerNumber,
            byte value,
            Command kind)
        {
            Tick = tick;
            Channel = channel;
            ControllerNumber = controllerNumber;
            Value = value;
            Kind = kind;
        }

        public static ControlChangeEvent FromMidi(
            int tick,
            byte channel,
            byte controllerNumber,
            byte value)
        {
            return new ControlChangeEvent(
                tick,
                channel,
                controllerNumber,
                value,
                MapController(controllerNumber)
            );
        }

        public static Command MapController(byte cc)
        {
            return cc switch
            {
                1 => Command.ModDepth,
                5 => Command.PortaTime,
                6 => Command.DataEntry,
                7 => Command.Volume,
                10 => Command.Pan,
                11 => Command.Volume2,
                12 => Command.MainVolume,
                13 => Command.Transpose,
                14 => Command.Priority,
                20 => Command.BendRange,
                21 => Command.ModSpeed,
                22 => Command.ModType,
                23 => Command.ModRange,
                26 => Command.ModDelay,
                27 => Command.ModDelayTimes10,
                28 => Command.SweepPitch,
                29 => Command.SweepPitchTimes24,
                65 => Command.PortaOnOff,
                69 => Command.Porta,
                70 => Command.Attack,
                71 => Command.Decay,
                84 => Command.Porta,   
                85 => Command.Attack,
                86 => Command.Decay,
                87 => Command.Sustain,
                88 => Command.Release,
                89 => Command.LoopStart,
                90 => Command.LoopEnd,
                100 => Command.RpnLsb,
                101 => Command.RpnMsb,
                130 => Command.PitchBend,
                _ => Command.Unknown,
            };
        }

        public float GetNormalizedValue()
        {
            return Value / 127f;
        }
    }
}

