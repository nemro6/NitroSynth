using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using NitroSynth.app;

namespace NitroSynth.App.Controls
{
    public sealed class PianoKeyboard : Control
    {
        private static readonly IBrush LightThemeWhiteKeyBrush = new SolidColorBrush(Color.Parse("#D4D4D4"));

        public static readonly StyledProperty<int> MinNoteProperty =
            AvaloniaProperty.Register<PianoKeyboard, int>(nameof(MinNote), 0);
        public static readonly StyledProperty<int> MaxNoteProperty =
            AvaloniaProperty.Register<PianoKeyboard, int>(nameof(MaxNote), 127);
        public static readonly StyledProperty<IReadOnlyList<ushort>?> ActiveNoteChannelMasksProperty =
            AvaloniaProperty.Register<PianoKeyboard, IReadOnlyList<ushort>?>(nameof(ActiveNoteChannelMasks));
        public static readonly StyledProperty<IReadOnlyList<IBrush>?> ChannelBrushesProperty =
            AvaloniaProperty.Register<PianoKeyboard, IReadOnlyList<IBrush>?>(nameof(ChannelBrushes));

        public int MinNote { get => GetValue(MinNoteProperty); set => SetValue(MinNoteProperty, value); }
        public int MaxNote { get => GetValue(MaxNoteProperty); set => SetValue(MaxNoteProperty, value); }
        public IReadOnlyList<ushort>? ActiveNoteChannelMasks { get => GetValue(ActiveNoteChannelMasksProperty); set => SetValue(ActiveNoteChannelMasksProperty, value); }
        public IReadOnlyList<IBrush>? ChannelBrushes { get => GetValue(ChannelBrushesProperty); set => SetValue(ChannelBrushesProperty, value); }

        private readonly HashSet<int> _pressed = new();

        private readonly List<(Rect rect, int note, bool isBlack)> _white = new();
        private readonly List<(Rect rect, int note, bool isBlack)> _black = new();

        public event EventHandler<int>? NoteOn;
        public event EventHandler<int>? NoteOff;

        public PianoKeyboard()
        {
            MinHeight = 64;
            Cursor = new Cursor(StandardCursorType.Arrow);

            this.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty ||
                    e.Property == MinNoteProperty ||
                    e.Property == MaxNoteProperty ||
                    e.Property == ActiveNoteChannelMasksProperty ||
                    e.Property == ChannelBrushesProperty ||
                    e.Property.Name == "ActualThemeVariant" ||
                    e.Property.Name == "RequestedThemeVariant")
                {
                    UpdateGeometry();
                    InvalidateVisual();
                }
            };

            AddHandler(PointerPressedEvent, OnPointerPressed, handledEventsToo: true);
            AddHandler(PointerReleasedEvent, OnPointerReleased, handledEventsToo: true);
            AddHandler(PointerMovedEvent, OnPointerMoved, handledEventsToo: true);
            AddHandler(PointerCaptureLostEvent, (_, __) => ReleaseAll(), handledEventsToo: true);

            AddHandler(PointerExitedEvent, (_, __) => ReleaseAll(), handledEventsToo: true);
        }

        public override void Render(DrawingContext ctx)
        {
            base.Render(ctx);
            if (_white.Count == 0 && _black.Count == 0) UpdateGeometry();

            var bounds = Bounds;

            IBrush bg = this.FindResource("PianoRoll.Background") as IBrush
                        ?? new SolidColorBrush(Color.FromRgb(30, 30, 30));

            ctx.FillRectangle(bg, bounds);

            IBrush darkKeyBrush =
                this.FindResource("App.WindowBgBrush") as IBrush
                ?? this.FindResource("PianoRoll.Background") as IBrush
                ?? new SolidColorBrush(Color.Parse("#222222"));

            bool invertKeys = IsDarkThemeVariant();

            IBrush whiteBrush = invertKeys ? darkKeyBrush : LightThemeWhiteKeyBrush;
            var whitePen = new Pen(Brushes.Gray, 1);
            IBrush pressedWhite = new SolidColorBrush(Color.FromRgb(204, 232, 255)); 

            foreach (var k in _white)
            {
                IBrush brush = _pressed.Contains(k.note) ? pressedWhite : whiteBrush;
                ctx.FillRectangle(brush, k.rect);
                ctx.DrawRectangle(whitePen, k.rect);
            }

            IBrush blackBrush = invertKeys ? LightThemeWhiteKeyBrush : darkKeyBrush;
            IBrush pressedBlack = new SolidColorBrush(Color.FromRgb(64, 128, 192));

            foreach (var k in _black)
            {
                IBrush brush = _pressed.Contains(k.note) ? pressedBlack : blackBrush;
                ctx.FillRectangle(brush, k.rect);
                ctx.DrawRectangle(whitePen, k.rect);
            }

            foreach (var k in _white)
                DrawActiveNoteLine(ctx, k.note, k.rect);

            foreach (var k in _black)
                DrawActiveNoteLine(ctx, k.note, k.rect);
        }

        private void DrawActiveNoteLine(DrawingContext ctx, int note, Rect keyRect)
        {
            int ch = GetActiveChannel(note);
            if (ch < 0) return;

            IBrush line = GetChannelBrush(ch, Brushes.Transparent);
            const double lineThickness = 4.0;
            double h = Math.Min(lineThickness, keyRect.Height);
            var rect = new Rect(keyRect.X, keyRect.Bottom - h, keyRect.Width, h);
            ctx.FillRectangle(line, rect);
        }

        private IBrush GetChannelBrush(int channel, IBrush fallback)
        {
            var brushes = ChannelBrushes;
            if (brushes is null || (uint)channel >= (uint)brushes.Count) return fallback;
            return brushes[channel] ?? fallback;
        }

        private bool IsDarkThemeVariant()
        {
            return ActualThemeVariant != ThemeVariant.Light;
        }

        private int GetActiveChannel(int note)
        {
            var masks = ActiveNoteChannelMasks;
            if (masks is null || (uint)note >= (uint)masks.Count) return -1;

            ushort mask = masks[note];
            if (mask == 0) return -1;

            for (int ch = 0; ch < 16; ch++)
            {
                if ((mask & (1 << ch)) != 0)
                    return ch;
            }

            return -1;
        }

        private void UpdateGeometry()
        {
            _white.Clear();
            _black.Clear();

            int lo = Math.Clamp(MinNote, 0, 127);
            int hi = Math.Clamp(MaxNote, 0, 127);
            if (hi < lo) (lo, hi) = (hi, lo);

            int WhiteCount(int a, int b) => Enumerable.Range(a, b - a + 1).Count(n => !IsBlack(n));
            int whiteN = WhiteCount(lo, hi);
            if (whiteN == 0) return;

            var w = Bounds.Width;
            var h = Bounds.Height;
            double whiteW = w / whiteN;
            double whiteH = h;
            double blackW = whiteW * 0.6;
            double blackH = h * 0.6;

            int first = lo;
            while (first <= hi && IsBlack(first)) first++;
            if (first > hi) return;

            double x = 0;
            for (int n = first; n <= hi; n++)
            {
                if (IsBlack(n)) continue;
                var rect = new Rect(x, 0, whiteW, whiteH);
                _white.Add((rect, n, false));
                x += whiteW;
            }

            var wx = _white.ToDictionary(k => k.note, k => k.rect.X);

            foreach (var n in Enumerable.Range(lo, hi - lo + 1))
            {
                if (!IsBlack(n)) continue;

                int leftWhite = PrevWhite(n);
                int rightWhite = NextWhite(n);

                if (!wx.ContainsKey(leftWhite) || !wx.ContainsKey(rightWhite)) continue;

                double lx = wx[leftWhite];
                double rx = wx[rightWhite];
                double cx = (lx + rx + whiteW) * 0.5; 
                var rect = new Rect(cx - blackW / 2, 0, blackW, blackH);
                _black.Add((rect, n, true));
            }
        }

        private static bool IsBlack(int note)
        {
            int pc = ((note % 12) + 12) % 12;
            return pc is 1 or 3 or 6 or 8 or 10;
        }
        private static int PrevWhite(int note) { int n = note - 1; while (IsBlack(n)) n--; return n; }
        private static int NextWhite(int note) { int n = note + 1; while (IsBlack(n)) n++; return n; }

        private void OnPointerPressed(object? s, PointerPressedEventArgs e)
        {
            var p = e.GetPosition(this);
            var note = HitTestNote(p);
            if (note is null) return;
            e.Pointer.Capture(this);
            if (_pressed.Add(note.Value))
            {
                InvalidateVisual();
                NoteOn?.Invoke(this, note.Value);
            }
        }

        private void OnPointerReleased(object? s, PointerReleasedEventArgs e)
        {
            var p = e.GetPosition(this);
            var note = HitTestNote(p);
            if (note is not null && _pressed.Contains(note.Value))
            {
                _pressed.Remove(note.Value);
                InvalidateVisual();
                NoteOff?.Invoke(this, note.Value);
            }
            e.Pointer.Capture(null);
        }

        private void OnPointerMoved(object? s, PointerEventArgs e)
        {
            if (!_pressed.Any()) return;
            var p = e.GetPosition(this);
            var hit = HitTestNote(p);
            var current = _pressed.First(); 
            if (hit is null || hit.Value == current) return;

            _pressed.Clear();
            NoteOff?.Invoke(this, current);

            _pressed.Add(hit.Value);
            NoteOn?.Invoke(this, hit.Value);
            InvalidateVisual();
        }

        private void ReleaseAll()
        {
            if (_pressed.Count == 0) return;
            foreach (var n in _pressed.ToArray())
                NoteOff?.Invoke(this, n);
            _pressed.Clear();
            InvalidateVisual();
        }

        private int? HitTestNote(Point p)
        {
            var k = _black.FirstOrDefault(b => b.rect.Contains(p));
            if (k.rect != default) return k.note;
            var w = _white.FirstOrDefault(b => b.rect.Contains(p));
            if (w.rect != default) return w.note;
            return null;
        }
    }
}

