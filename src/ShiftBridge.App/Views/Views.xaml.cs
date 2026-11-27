using System.Windows;
using System.Windows.Controls;

namespace ShiftBridge.App.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();
}

public partial class ButtonsView : UserControl
{
    public ButtonsView() => InitializeComponent();
}

public partial class TrainerView : UserControl
{
    public TrainerView() => InitializeComponent();
}

public partial class AppsView : UserControl
{
    public AppsView() => InitializeComponent();
}

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}

public partial class DiagnosticView : UserControl
{
    /// <summary>Vrai tant que l'utilisateur est en bas du journal : les nouvelles lignes y défilent alors d'elles-mêmes.</summary>
    private bool _followTail = true;

    public DiagnosticView()
    {
        InitializeComponent();
        LogBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            // Défilement de l'utilisateur (et non ajout de texte) : on retient s'il est revenu en bas.
            if (e.ExtentHeightChange == 0) _followTail = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 4;
        }));
        LogBox.TextChanged += (_, _) => { if (_followTail) LogBox.ScrollToEnd(); };
        IsVisibleChanged += (_, _) => { if (IsVisible && _followTail) LogBox.ScrollToEnd(); };
    }
}

public partial class OnboardingView : UserControl
{
    public OnboardingView() => InitializeComponent();
}

public partial class DevicesView : UserControl
{
    public DevicesView() => InitializeComponent();

    /// <summary>Ouvre le menu « ⋯ » (Vibrer, Oublier) sous le bouton.</summary>
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            // Sous le bouton, bord droit aligné sur le sien
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Custom;
            menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [
                new System.Windows.Controls.Primitives.CustomPopupPlacement(
                    new Point(targetSize.Width - popupSize.Width, targetSize.Height),
                    System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)
            ];
            menu.IsOpen = true;
        }
    }
}
