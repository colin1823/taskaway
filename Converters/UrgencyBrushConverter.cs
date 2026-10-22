using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TranslucentTodo.Converters
{
    /// <summary>
    /// Maps deadline progress (0..1) to the card fill: the normal translucent white at 0,
    /// shading linearly to bright red on the deadline day.
    /// </summary>
    public sealed class UrgencyBrushConverter : IValueConverter
    {
        // Matches the GlassColor resource, so a card without a deadline looks like every other glass surface.
        private static readonly Color Calm = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        private static readonly Color Urgent = Color.FromArgb(0xE6, 0xFF, 0x3B, 0x30);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double t = value is double d ? Math.Clamp(d, 0, 1) : 0;
            var brush = new SolidColorBrush(Color.FromArgb(
                Lerp(Calm.A, Urgent.A, t), Lerp(Calm.R, Urgent.R, t),
                Lerp(Calm.G, Urgent.G, t), Lerp(Calm.B, Urgent.B, t)));
            brush.Freeze();
            return brush;
        }

        private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
