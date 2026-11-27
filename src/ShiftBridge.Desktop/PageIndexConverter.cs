using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using ShiftBridge.Presentation;

namespace ShiftBridge.Desktop;

public sealed class PageIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // L'assistant n'a pas d'onglet : l'onglet affiché ne change pas (sinon, sans onglet valide, le retour de la liaison
        // ramènerait aussitôt à l'Accueil et fermerait l'assistant).
        if (Equals(value, Pages.Onboarding)) return BindingOperations.DoNothing;
        for (int i = 0; i < Pages.All.Count; i++)
            if (Equals(Pages.All[i], value)) return i;

        return 0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index && index >= 0 && index < Pages.All.Count ? Pages.All[index] : Pages.Home;
}
