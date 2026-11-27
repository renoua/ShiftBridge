using System.Windows;
using ShiftBridge.Presentation;

namespace ShiftBridge.App;

/// <summary>Marshalisation vers le thread UI WPF.</summary>
internal sealed class WpfDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) action(); else d.BeginInvoke(action);
    }
}

/// <summary>Services de l'application WPF pour le ViewModel partagé.</summary>
internal sealed class WpfAppHost : IAppHost
{
    public string AppVersion { get; } = typeof(WpfAppHost).Assembly.GetName().Version?.ToString(3) ?? "?";

    public IAppUpdater? Updater { get; } = new Updates.GitHubUpdater(typeof(WpfAppHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    /// <summary>Radio Bluetooth du PC : bandeau « Bluetooth éteint » de l'Accueil (démarrée par la fenêtre principale).</summary>
    public ShiftBridge.Platform.Windows.WindowsBluetoothRadio Radio { get; } = new();
    public bool IsBluetoothOn => Radio.IsOn;
    public void RequestBluetoothOn() => _ = Radio.RequestOnAsync();
    /// <summary>Sur PC, on n'ouvre pas les Paramètres d'office : le bandeau de l'Accueil propose de l'allumer.</summary>
    public bool PromptForBluetoothOnAutomatically => false;
    public bool SupportsTray => true;

    public bool Confirm(string message) =>
        MessageBox.Show(message, "ShiftBridge", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>Petite fenêtre de saisie d'une ligne de texte ; null si annulée.</summary>
    public Task<string?> PromptTextAsync(string message, string initial)
    {
        var box = new System.Windows.Controls.TextBox { Text = initial, MaxLength = 40, Margin = new Thickness(0, 8, 0, 14) };
        var ok = new System.Windows.Controls.Button { Content = ShiftBridge.Core.Localization.Strings.State_Ok, IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = ShiftBridge.Core.Localization.Strings.Buttons_Cancel, IsCancel = true, MinWidth = 80 };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var owner = Application.Current?.MainWindow;
        var window = new Window
        {
            Title = "ShiftBridge", Content = panel, Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            Owner = owner is { IsVisible: true } ? owner : null,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return Task.FromResult(window.ShowDialog() == true ? box.Text : null);
    }

    public void CopyText(string text)
    {
        try { Clipboard.SetText(text); } catch { /* presse-papiers occupé */ }
    }

    public void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* aucun navigateur disponible */ }
    }

    private const string JsonFilter = "JSON (*.json)|*.json|*.*|*.*";

    public Task<bool> SaveTextFileAsync(string suggestedName, string content)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = suggestedName, DefaultExt = ".json", Filter = JsonFilter, AddExtension = true };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true) return Task.FromResult(false);
        System.IO.File.WriteAllText(dialog.FileName, content, new System.Text.UTF8Encoding(false));
        return Task.FromResult(true);
    }

    public Task<string?> OpenTextFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { DefaultExt = ".json", Filter = JsonFilter, CheckFileExists = true };
        return Task.FromResult(dialog.ShowDialog(Application.Current?.MainWindow) == true ? System.IO.File.ReadAllText(dialog.FileName) : null);
    }

    /// <summary>
    /// Lance une nouvelle instance (qui attend que celle-ci libère l'instance unique) puis ferme proprement celle-ci : les
    /// manettes et le trainer sont libérés avant que la nouvelle ne les reprenne.
    /// </summary>
    public bool Restart()
    {
        if (Environment.ProcessPath is not { } exe) return false;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--restart") { UseShellExecute = false })?.Dispose(); }
        catch { return false; }
        Application.Current?.MainWindow?.Close();
        return true;
    }
}
