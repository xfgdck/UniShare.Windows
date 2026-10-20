using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UniShare.UI
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public bool Inverse { get; set; } = false;

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool isTrue = value is true;
            if (Inverse)
            {
                isTrue = !isTrue;
            }
            return isTrue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Visibility v)
            {
                bool isVisible = v == Visibility.Visible;
                return Inverse ? !isVisible : isVisible;
            }
            return false;
        }
    }

    public static class Converters
    {
        public static readonly IValueConverter BoolToVisible = new BoolToVisibilityConverter { Inverse = false };
        public static readonly IValueConverter InverseBoolToVisible = new BoolToVisibilityConverter { Inverse = true };
    }
}
