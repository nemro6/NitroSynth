using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NitroSynth.App.Controls
{
    public class MasterLevelMeter : Control
    {
        public static readonly StyledProperty<double> LevelProperty =
            AvaloniaProperty.Register<MasterLevelMeter, double>(nameof(Level));

        public static readonly StyledProperty<double> PeakProperty =
            AvaloniaProperty.Register<MasterLevelMeter, double>(nameof(Peak));

        public static readonly StyledProperty<bool> IsClippedProperty =
            AvaloniaProperty.Register<MasterLevelMeter, bool>(nameof(IsClipped));

        public static readonly StyledProperty<IBrush?> MeterBackgroundBrushProperty =
            AvaloniaProperty.Register<MasterLevelMeter, IBrush?>(nameof(MeterBackgroundBrush));

        public static readonly StyledProperty<IBrush?> LowBrushProperty =
            AvaloniaProperty.Register<MasterLevelMeter, IBrush?>(nameof(LowBrush));

        public static readonly StyledProperty<IBrush?> MidBrushProperty =
            AvaloniaProperty.Register<MasterLevelMeter, IBrush?>(nameof(MidBrush));

        public static readonly StyledProperty<IBrush?> HighBrushProperty =
            AvaloniaProperty.Register<MasterLevelMeter, IBrush?>(nameof(HighBrush));

        public static readonly StyledProperty<IBrush?> PeakBrushProperty =
            AvaloniaProperty.Register<MasterLevelMeter, IBrush?>(nameof(PeakBrush));

        public static readonly StyledProperty<IBrush?> MeterBorderBrushProperty =
            AvaloniaProperty.Register<MasterLevelMeter, IBrush?>(nameof(MeterBorderBrush));

        public static readonly StyledProperty<IBrush?> ClipBorderBrushProperty =
            AvaloniaProperty.Register<MasterLevelMeter, IBrush?>(nameof(ClipBorderBrush));

        static MasterLevelMeter()
        {
            AffectsRender<MasterLevelMeter>(
                LevelProperty, PeakProperty, IsClippedProperty,
                MeterBackgroundBrushProperty,
                LowBrushProperty, MidBrushProperty, HighBrushProperty,
                PeakBrushProperty,
                MeterBorderBrushProperty, ClipBorderBrushProperty
            );
        }

        public double Level
        {
            get => GetValue(LevelProperty);
            set => SetValue(LevelProperty, value);
        }

        public double Peak
        {
            get => GetValue(PeakProperty);
            set => SetValue(PeakProperty, value);
        }

        public bool IsClipped
        {
            get => GetValue(IsClippedProperty);
            set => SetValue(IsClippedProperty, value);
        }

        public IBrush? MeterBackgroundBrush
        {
            get => GetValue(MeterBackgroundBrushProperty);
            set => SetValue(MeterBackgroundBrushProperty, value);
        }

        public IBrush? LowBrush
        {
            get => GetValue(LowBrushProperty);
            set => SetValue(LowBrushProperty, value);
        }

        public IBrush? MidBrush
        {
            get => GetValue(MidBrushProperty);
            set => SetValue(MidBrushProperty, value);
        }

        public IBrush? HighBrush
        {
            get => GetValue(HighBrushProperty);
            set => SetValue(HighBrushProperty, value);
        }

        public IBrush? PeakBrush
        {
            get => GetValue(PeakBrushProperty);
            set => SetValue(PeakBrushProperty, value);
        }

        public IBrush? MeterBorderBrush
        {
            get => GetValue(MeterBorderBrushProperty);
            set => SetValue(MeterBorderBrushProperty, value);
        }

        public IBrush? ClipBorderBrush
        {
            get => GetValue(ClipBorderBrushProperty);
            set => SetValue(ClipBorderBrushProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var r = new Rect(Bounds.Size);
            if (r.Width <= 0 || r.Height <= 0) return;
            var inner = r.Deflate(1.0);
            if (inner.Width <= 0 || inner.Height <= 0)
                inner = r;

            var bg = MeterBackgroundBrush ?? new SolidColorBrush(Color.Parse("#111111"));
            var low = LowBrush ?? Brushes.LimeGreen;
            var mid = MidBrush ?? Brushes.Gold;
            var high = HighBrush ?? Brushes.Red;
            var peakBrush = PeakBrush ?? Brushes.White;

            var border = MeterBorderBrush ?? new SolidColorBrush(Color.Parse("#2E2E2E"));
            var clipBorder = ClipBorderBrush ?? Brushes.Red;

            context.FillRectangle(bg, r);

            double level = Math.Clamp(Level, 0.0, 1.0);
            double peak = Math.Clamp(Peak, 0.0, 1.0);

            const double greenEnd = 0.70;
            const double yellowEnd = 0.90;

            double xBase = inner.X;
            double yBase = inner.Y;
            double w = inner.Width;
            double h = inner.Height;

            void FillSegment(double x0, double x1, IBrush brush)
            {
                if (x1 <= x0) return;
                context.FillRectangle(brush, new Rect(xBase + x0, yBase, x1 - x0, h));
            }

            double gx = w * Math.Min(level, greenEnd);
            FillSegment(0, gx, low);

            if (level > greenEnd)
            {
                double y0 = w * greenEnd;
                double y1 = w * Math.Min(level, yellowEnd);
                FillSegment(y0, y1, mid);
            }

            if (level > yellowEnd)
            {
                double r0 = w * yellowEnd;
                double r1 = w * level;
                FillSegment(r0, r1, high);
            }

            double px = xBase + (w * peak);
            context.DrawLine(new Pen(peakBrush, 1), new Point(px, yBase), new Point(px, yBase + h));

            var borderBrush = IsClipped ? clipBorder : border;
            context.DrawRectangle(new Pen(borderBrush, 1), r.Deflate(0.5));
        }
    }
}
