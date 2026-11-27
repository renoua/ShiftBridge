using Avalonia.Controls;

namespace ShiftBridge.Desktop.Views;

public sealed partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();
}

public sealed partial class DevicesView : UserControl
{
    public DevicesView() => InitializeComponent();
}

public sealed partial class ButtonsView : UserControl
{
    public ButtonsView() => InitializeComponent();
}

public sealed partial class TrainerView : UserControl
{
    public TrainerView() => InitializeComponent();
}

public sealed partial class AppsView : UserControl
{
    public AppsView() => InitializeComponent();
}

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}

public sealed partial class DiagnosticsView : UserControl
{
    public DiagnosticsView()
    {
        InitializeComponent();
        // Les nouvelles lignes défilent d'elles-mêmes, sauf pendant que l'utilisateur lit ou sélectionne dans le journal.
        LogBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !LogBox.IsKeyboardFocusWithin) LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        };
    }
}

public sealed partial class OnboardingView : UserControl
{
    public OnboardingView() => InitializeComponent();
}
