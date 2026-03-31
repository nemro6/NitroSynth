using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace NitroSynth.App.ViewModels
{
    public sealed class UnsignedToWidthConverter : IValueConverter
    {
        public double Max { get; set; } = 127;
        public double Width { get; set; } = 62; 

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is null) return 0d;

            double v;
            try { v = System.Convert.ToDouble(value, culture); }
            catch { return 0d; }

            if (Max <= 0) return 0d;
            v = Math.Clamp(v, 0, Max);
            return (v / Max) * Width;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public sealed class SignedToHalfWidthConverter : IValueConverter
    {
        public double MaxAbs { get; set; } = 127;
        public double HalfWidth { get; set; } = 31; 
        public bool Positive { get; set; } = true;

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is null) return 0d;

            double v;
            try { v = System.Convert.ToDouble(value, culture); }
            catch { return 0d; }

            if (MaxAbs <= 0) return 0d;
            v = Math.Clamp(v, -MaxAbs, MaxAbs);

            double part = Positive ? Math.Max(0, v) : Math.Max(0, -v);
            return (part / MaxAbs) * HalfWidth;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}


