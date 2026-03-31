using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace NitroSynth.App.ViewModels
{
    public sealed class PosFracConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            double v = ToDouble(value);
            double max = ToMax(parameter, 127.0);
            if (max <= 0) return 0.0;

            if (v <= 0) return 0.0;
            return Math.Clamp(v / max, 0.0, 1.0);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static double ToDouble(object? value) => value switch
        {
            sbyte sb => sb,
            byte b => b,
            short s => s,
            ushort us => us,
            int i => i,
            uint ui => ui,
            long l => l,
            ulong ul => ul,
            float f => f,
            double d => d,
            decimal m => (double)m,
            _ => 0.0
        };

        private static double ToMax(object? parameter, double fallback)
        {
            if (parameter is null) return fallback;
            return double.TryParse(parameter.ToString(), out var m) ? m : fallback;
        }
    }

    public sealed class NegFracConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            double v = ToDouble(value);
            double max = ToMax(parameter, 127.0);
            if (max <= 0) return 0.0;

            if (v >= 0) return 0.0;
            return Math.Clamp((-v) / max, 0.0, 1.0);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static double ToDouble(object? value) => value switch
        {
            sbyte sb => sb,
            byte b => b,
            short s => s,
            ushort us => us,
            int i => i,
            uint ui => ui,
            long l => l,
            ulong ul => ul,
            float f => f,
            double d => d,
            decimal m => (double)m,
            _ => 0.0
        };

        private static double ToMax(object? parameter, double fallback)
        {
            if (parameter is null) return fallback;
            return double.TryParse(parameter.ToString(), out var m) ? m : fallback;
        }
    }
}
