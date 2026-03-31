using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NitroSynth.App.InstrumentEditor;

public sealed class SwavWaveformView : Control
{
    private static readonly ISolidColorBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#101010"));
    private static readonly Pen BorderPen = new(new SolidColorBrush(Color.Parse("#505050")), 1);
    private static readonly Pen WaveformPen = new(new SolidColorBrush(Color.Parse("#8FC9FF")), 1);
    private static readonly Pen LoopStartPen = new(new SolidColorBrush(Color.Parse("#2ED67A")), 1);
    private static readonly Pen LoopEndPen = new(new SolidColorBrush(Color.Parse("#F28E2B")), 1);
    private static readonly Pen PlayheadPen = new(new SolidColorBrush(Color.Parse("#FFFFFF")), 1);

    private short[] _samples = Array.Empty<short>();
    private int _loopStartSample;
    private int _loopEndSample;
    private int _playheadSample;
    private bool _showLoopMarkers;
    private int _waveformVersion;
    private int _cachedWaveformVersion = -1;
    private int _cachedPixelWidth = -1;
    private int[] _cachedMinPerPixel = Array.Empty<int>();
    private int[] _cachedMaxPerPixel = Array.Empty<int>();
    private int _lastPlayheadPixel = -1;

    public short[] Samples
    {
        get => _samples;
        set
        {
            _samples = value ?? Array.Empty<short>();
            unchecked { _waveformVersion++; }
            _cachedWaveformVersion = -1;
            _cachedPixelWidth = -1;
            _cachedMinPerPixel = Array.Empty<int>();
            _cachedMaxPerPixel = Array.Empty<int>();
            _lastPlayheadPixel = -1;
            InvalidateVisual();
        }
    }

    public int LoopStartSample
    {
        get => _loopStartSample;
        set
        {
            _loopStartSample = Math.Max(0, value);
            InvalidateVisual();
        }
    }

    public int LoopEndSample
    {
        get => _loopEndSample;
        set
        {
            _loopEndSample = Math.Max(0, value);
            InvalidateVisual();
        }
    }

    public int PlayheadSample
    {
        get => _playheadSample;
        set
        {
            int clamped = Math.Max(0, value);
            if (_playheadSample == clamped)
                return;

            _playheadSample = clamped;

            int pixel = ToPixel(clamped, _samples.Length, Math.Max(1, Bounds.Width - 2));
            if (pixel == _lastPlayheadPixel)
                return;

            _lastPlayheadPixel = pixel;
            InvalidateVisual();
        }
    }

    public bool ShowLoopMarkers
    {
        get => _showLoopMarkers;
        set
        {
            _showLoopMarkers = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        context.FillRectangle(BackgroundBrush, bounds);
        context.DrawRectangle(null, BorderPen, bounds.Deflate(0.5));

        if (_samples.Length == 0)
            return;

        double width = Math.Max(1, bounds.Width - 2);
        double height = Math.Max(1, bounds.Height - 2);
        double left = bounds.X + 1;
        double top = bounds.Y + 1;
        double centerY = top + height * 0.5;
        double amp = height * 0.48;

        int pixelWidth = Math.Max(1, (int)Math.Round(width));
        EnsureWaveformCache(pixelWidth);
        for (int x = 0; x < pixelWidth; x++)
        {
            int min = _cachedMinPerPixel[x];
            int max = _cachedMaxPerPixel[x];

            double xPos = left + x;
            double y1 = centerY - (max / 32768.0) * amp;
            double y2 = centerY - (min / 32768.0) * amp;
            context.DrawLine(WaveformPen, new Point(xPos, y1), new Point(xPos, y2));
        }

        if (_showLoopMarkers)
        {
            double loopStartX = left + ToX(_loopStartSample, _samples.Length, width);
            double loopEndX = left + ToX(_loopEndSample, _samples.Length, width);
            context.DrawLine(LoopStartPen, new Point(loopStartX, top), new Point(loopStartX, top + height));
            context.DrawLine(LoopEndPen, new Point(loopEndX, top), new Point(loopEndX, top + height));
        }

        double playheadX = left + ToX(_playheadSample, _samples.Length, width);
        _lastPlayheadPixel = ToPixel(_playheadSample, _samples.Length, width);
        context.DrawLine(PlayheadPen, new Point(playheadX, top), new Point(playheadX, top + height));
    }

    private void EnsureWaveformCache(int pixelWidth)
    {
        if (_samples.Length == 0)
        {
            _cachedMinPerPixel = Array.Empty<int>();
            _cachedMaxPerPixel = Array.Empty<int>();
            _cachedPixelWidth = pixelWidth;
            _cachedWaveformVersion = _waveformVersion;
            return;
        }

        if (_cachedWaveformVersion == _waveformVersion &&
            _cachedPixelWidth == pixelWidth &&
            _cachedMinPerPixel.Length == pixelWidth &&
            _cachedMaxPerPixel.Length == pixelWidth)
        {
            return;
        }

        _cachedMinPerPixel = new int[pixelWidth];
        _cachedMaxPerPixel = new int[pixelWidth];

        for (int x = 0; x < pixelWidth; x++)
        {
            int start = x * _samples.Length / pixelWidth;
            int end = ((x + 1) * _samples.Length) / pixelWidth;
            if (end <= start)
                end = Math.Min(start + 1, _samples.Length);
            if (start >= _samples.Length)
            {
                _cachedMinPerPixel[x] = 0;
                _cachedMaxPerPixel[x] = 0;
                continue;
            }

            int min = short.MaxValue;
            int max = short.MinValue;
            for (int i = start; i < end; i++)
            {
                int s = _samples[i];
                if (s < min) min = s;
                if (s > max) max = s;
            }

            _cachedMinPerPixel[x] = min;
            _cachedMaxPerPixel[x] = max;
        }

        _cachedPixelWidth = pixelWidth;
        _cachedWaveformVersion = _waveformVersion;
    }

    private static int ToPixel(int sample, int totalSamples, double width)
    {
        if (totalSamples <= 1 || width <= 1)
            return 0;
        int clamped = Math.Clamp(sample, 0, totalSamples - 1);
        return (int)Math.Round(clamped * (width - 1) / (totalSamples - 1));
    }

    private static double ToX(int sample, int totalSamples, double width)
    {
        if (totalSamples <= 1)
            return 0;
        int clamped = Math.Clamp(sample, 0, totalSamples - 1);
        return clamped * (width - 1) / (totalSamples - 1);
    }
}
