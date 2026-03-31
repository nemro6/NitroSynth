using System;
using System.Collections.Generic;

namespace NitroSynth.App.Audio
{
    public sealed class PolyChannel
    {
        private sealed class Voice
        {
            public Channel Ch = null!;
            public int MidiNote;
            public long Id; 
        }

        private readonly List<Voice> _voices = new();
        private readonly int _maxVoices;
        private readonly object _lock = new();
        private long _voiceCounter;

        public PolyChannel(int maxVoices = 8)
        {
            _maxVoices = Math.Max(1, maxVoices);
        }

        public void PlaySwav(
            SWAV swav,
            int midiNote,
            int baseKey,
            int attack,
            int decay,
            int sustain,
            int release,
            int pan,
            float volume)
        {
            lock (_lock)
            {
                for (int i = _voices.Count - 1; i >= 0; i--)
                {
                    if (_voices[i].MidiNote == midiNote)
                    {
                        _voices[i].Ch.Stop();
                        _voices.RemoveAt(i);
                    }
                }

                if (_voices.Count >= _maxVoices)
                {
                    int oldestIndex = 0;
                    long oldestId = _voices[0].Id;
                    for (int i = 1; i < _voices.Count; i++)
                    {
                        if (_voices[i].Id < oldestId)
                        {
                            oldestId = _voices[i].Id;
                            oldestIndex = i;
                        }
                    }
                    _voices[oldestIndex].Ch.Stop();
                    _voices.RemoveAt(oldestIndex);
                }

                var ch = new Channel();
                ch.PlaySwav(swav, midiNote, baseKey,
                    attack, decay, sustain, release, pan, volume);

                _voices.Add(new Voice
                {
                    Ch = ch,
                    MidiNote = midiNote,
                    Id = ++_voiceCounter
                });
            }
        }

        public void StopNote(int midiNote)
        {
            lock (_lock)
            {
                for (int i = _voices.Count - 1; i >= 0; i--)
                {
                    if (_voices[i].MidiNote == midiNote)
                    {
                        _voices[i].Ch.Stop();
                        _voices.RemoveAt(i);
                    }
                }
            }
        }

        public void StopAll()
        {
            lock (_lock)
            {
                foreach (var v in _voices)
                    v.Ch.Stop();
                _voices.Clear();
            }
        }
    }
}

