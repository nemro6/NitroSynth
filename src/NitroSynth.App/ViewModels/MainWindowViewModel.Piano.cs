using System.Collections.Generic;
using Avalonia.Media;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    private static readonly IBrush[] ChannelBrushPalette =
    {
        new SolidColorBrush(Color.Parse("#A94949")),
        new SolidColorBrush(Color.Parse("#A96149")),
        new SolidColorBrush(Color.Parse("#A97A49")),
        new SolidColorBrush(Color.Parse("#A99249")),

        new SolidColorBrush(Color.Parse("#8EA949")),
        new SolidColorBrush(Color.Parse("#72A949")),
        new SolidColorBrush(Color.Parse("#55A949")),
        new SolidColorBrush(Color.Parse("#49A95D")),

        new SolidColorBrush(Color.Parse("#49A97A")),
        new SolidColorBrush(Color.Parse("#49A998")),
        new SolidColorBrush(Color.Parse("#499DA9")),
        new SolidColorBrush(Color.Parse("#4980A9")),

        new SolidColorBrush(Color.Parse("#4963A9")),
        new SolidColorBrush(Color.Parse("#5A49A9")),
        new SolidColorBrush(Color.Parse("#7849A9")),
        new SolidColorBrush(Color.Parse("#9649A9")),
    };

    public IReadOnlyList<IBrush> PianoChannelBrushes => ChannelBrushPalette;

    private ushort[] _pianoNoteChannelMasks = new ushort[128];
    public IReadOnlyList<ushort> PianoNoteChannelMasks => _pianoNoteChannelMasks;

    private static bool IsValidMidiNote(int note) => (uint)note <= 127;

    private void SetPianoMidiNoteOn(int channel, int note)
    {
        if ((uint)channel > 15 || !IsValidMidiNote(note)) return;

        ushort bit = (ushort)(1 << channel);
        ushort current = _pianoNoteChannelMasks[note];
        ushort next = (ushort)(current | bit);
        if (current == next) return;

        var updated = (ushort[])_pianoNoteChannelMasks.Clone();
        updated[note] = next;
        _pianoNoteChannelMasks = updated;
        OnPropertyChanged(nameof(PianoNoteChannelMasks));
    }

    private void SetPianoMidiNoteOff(int channel, int note)
    {
        if ((uint)channel > 15 || !IsValidMidiNote(note)) return;

        ushort bit = (ushort)(1 << channel);
        ushort current = _pianoNoteChannelMasks[note];
        ushort next = (ushort)(current & ~bit);
        if (current == next) return;

        var updated = (ushort[])_pianoNoteChannelMasks.Clone();
        updated[note] = next;
        _pianoNoteChannelMasks = updated;
        OnPropertyChanged(nameof(PianoNoteChannelMasks));
    }

    private void ClearPianoMidiNotes()
    {
        bool any = false;
        for (int i = 0; i < _pianoNoteChannelMasks.Length; i++)
        {
            if (_pianoNoteChannelMasks[i] == 0) continue;
            any = true;
            break;
        }

        if (!any) return;

        _pianoNoteChannelMasks = new ushort[128];
        OnPropertyChanged(nameof(PianoNoteChannelMasks));
    }
}
