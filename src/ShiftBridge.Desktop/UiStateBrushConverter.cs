using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ShiftBridge.Presentation;

namespace ShiftBridge.Desktop;

/// <summary>Couleur d'une pastille d'état (mêmes teintes que Windows et Android).</summary>
public sealed class UiStateBrushConverter : IValueConverter
{
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#2BD46A"));
    private static readonly IBrush Waiting = new SolidColorBrush(Color.Parse("#F5B041"));
    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#FF6B6F"));
    private static readonly IBrush Off = new SolidColorBrush(Color.Parse("#4A5460"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        UiState.Ok => Ok,
        UiState.Waiting or UiState.Warning => Waiting,
        UiState.Error => Error,
        _ => Off,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
