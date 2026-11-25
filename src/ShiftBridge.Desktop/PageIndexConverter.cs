using System.Globalization;
using Avalonia.Data.Converters;
using ShiftBridge.Presentation;

namespace ShiftBridge.Desktop;

public sealed class PageIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        for (int i = 0; i < Pages.All.Count; i++)
            if (Equals(Pages.All[i], value)) return i;

        return 0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index && index >= 0 && index < Pages.All.Count ? Pages.All[index] : Pages.Home;
}
