using System.Reflection;
using AppKit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Foundation;
using ShiftBridge.Core.Localization;
using ShiftBridge.Platform.MacOS;
using ShiftBridge.Presentation;

namespace ShiftBridge.MacOS;

internal sealed class AvaloniaDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}

internal sealed class MacAppHost(Window owner, MacBlePlatform bluetooth) : IAppHost
{
    private static readonly Version CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new(0, 0, 0);

    public string AppVersion => CurrentVersion.ToString(3);
    public IAppUpdateChecker UpdateChecker { get; } = new MacReleaseChecker(CurrentVersion);
    public bool IsBluetoothOn => bluetooth.State is MacBluetoothState.Available or MacBluetoothState.Starting;
    public bool PromptForBluetoothOnAutomatically => false;
    public void RequestBluetoothOn() => OpenUrl("x-apple.systempreferences:com.apple.BluetoothSettings");
    public void OpenAppSettings() => OpenUrl("x-apple.systempreferences:com.apple.preference.security?Privacy_Bluetooth");
    public void OpenLocalNetworkSettings() => OpenUrl("x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork");
    public void OpenReleaseDownloads() => OpenUrl(MacReleaseChecker.ReleasesPage);

    public async void CopyText(string text)
    {
        try
        {
            if (owner.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch { /* Clipboard access is best effort. */ }
    }

    public void OpenUrl(string url)
    {
        try
        {
            using var address = new NSUrl(url);
            NSWorkspace.SharedWorkspace.OpenUrl(address);
        }
        catch { /* Opening System Settings or the browser is best effort. */ }
    }

    // Export / import des réglages : boîtes du système (code commun dans ShiftBridge.Desktop). Pas de relance automatique :
    // l'utilisateur quitte et rouvre l'application (message affiché par la page Réglages).
    public Task<bool> SaveTextFileAsync(string suggestedName, string content) =>
        ShiftBridge.Desktop.DesktopFiles.SaveTextAsync(owner, suggestedName, content);

    public Task<string?> OpenTextFileAsync() => ShiftBridge.Desktop.DesktopFiles.OpenTextAsync(owner);

    public async Task<bool> ConfirmAsync(string message) => await ShowDialogAsync(message, null) is not null;

    public async Task<bool> ConfirmAsync(string message, string confirmLabel) => await ShowDialogAsync(message, null, confirmLabel) is not null;

    public Task<string?> PromptTextAsync(string message, string initial) => ShowDialogAsync(message, initial);

    private Task<string?> ShowDialogAsync(string message, string? initial, string? acceptLabel = null)
    {
        var dialog = new Window
        {
            Title = owner.Title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        TextBox? input = null;
        if (initial is not null)
        {
            input = new TextBox { Text = initial };
            content.Children.Add(input);
            dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        }

        var cancel = new Button { Content = Strings.Buttons_Cancel, IsCancel = true };
        var accept = new Button { Content = acceptLabel ?? Strings.State_Ok, IsDefault = true };
        cancel.Click += (_, _) => dialog.Close(null);
        accept.Click += (_, _) => dialog.Close(input?.Text ?? "");
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, accept }
        });
        dialog.Content = content;
        return dialog.ShowDialog<string?>(owner);
    }
}
