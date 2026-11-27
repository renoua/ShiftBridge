using ShiftBridge.Core.Localization;

namespace ShiftBridge.Mobile;

/// <summary>
/// Petit « ⓘ » à côté d'un titre : un appui affiche l'aide (<see cref="Message"/>) dans une boîte de dialogue. Remplace les légendes
/// grises permanentes (sur mobile, il n'y a pas d'infobulle au survol).
/// </summary>
public sealed class InfoButton : Label
{
    public static readonly BindableProperty MessageProperty = BindableProperty.Create(nameof(Message), typeof(string), typeof(InfoButton), "");
    public static readonly BindableProperty HeadingProperty = BindableProperty.Create(nameof(Heading), typeof(string), typeof(InfoButton), "");

    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    /// <summary>Titre de la boîte de dialogue (en général celui de la section).</summary>
    public string Heading { get => (string)GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }

    public InfoButton()
    {
        Text = Icons.Info;
        FontFamily = Icons.Font;
        FontSize = 20;
        TextColor = EqualsToColorConverter.Resource("TextMuted");
        VerticalOptions = LayoutOptions.Center;
        VerticalTextAlignment = TextAlignment.Center;
        HorizontalTextAlignment = TextAlignment.Center;
        Padding = new Thickness(8);
        MinimumWidthRequest = 48;  // cible tactile minimale recommandée (48 dp)
        MinimumHeightRequest = 48;
        SemanticProperties.SetDescription(this, Strings.Common_Info);
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (Window?.Page is { } page) await page.DisplayAlertAsync(Heading, Message, Strings.Common_Close);
        };
        GestureRecognizers.Add(tap);
    }
}
