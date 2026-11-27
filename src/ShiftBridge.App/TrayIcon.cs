using Microsoft.Win32;
using ShiftBridge.Core.Localization;
using ShiftBridge.Presentation;

namespace ShiftBridge.App;

/// <summary>
/// Icône de la zone de notification : info-bulle avec la vitesse courante, et un menu pour changer de vitesse, activer le virtual
/// shifting, afficher ou déverrouiller la superposition (verrouillée, elle laisse passer les clics : c'est le seul moyen simple
/// de la déverrouiller sans ouvrir la fenêtre) sans quitter le jeu. Double-clic ou « Ouvrir » restaure la fenêtre.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu = new();
    private readonly MainViewModel _vm;
    private readonly System.Windows.Forms.ToolStripMenuItem _proxy, _overlay, _lock;

    public event Action? OpenRequested;
    public event Action? QuitRequested;

    public TrayIcon(MainViewModel vm)
    {
        _vm = vm;
        var open = new System.Windows.Forms.ToolStripMenuItem(Strings.Tray_Open, null, (_, _) => OpenRequested?.Invoke());
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold); // action du double-clic
        _menu.Items.Add(open);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add(Strings.Btn_ShiftUp, null, (_, _) => vm.ShiftUpCommand.Execute(null));
        _menu.Items.Add(Strings.Btn_ShiftDown, null, (_, _) => vm.ShiftDownCommand.Execute(null));
        _menu.Items.Add(Strings.Action_CenterGear, null, (_, _) => vm.CenterGearCommand.Execute(null));
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _proxy = new System.Windows.Forms.ToolStripMenuItem("", null, (_, _) => { if (vm.ToggleProxyCommand.CanExecute(null)) vm.ToggleProxyCommand.Execute(null); });
        _overlay = new System.Windows.Forms.ToolStripMenuItem(Strings.Tray_ShowOverlay, null, (_, _) => vm.ShowOverlay = !vm.ShowOverlay);
        _lock = new System.Windows.Forms.ToolStripMenuItem(Strings.Tray_LockOverlay, null, (_, _) => vm.OverlayLocked = !vm.OverlayLocked);
        _menu.Items.Add(_proxy);
        _menu.Items.Add(_overlay);
        _menu.Items.Add(_lock);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add(Strings.Tray_Quit, null, (_, _) => QuitRequested?.Invoke());
        _menu.Opening += (_, _) => RefreshMenu();

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        SetTooltip(vm.TrayTooltip);
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>Libellés et coches relus à chaque ouverture : le menu reflète toujours l'état courant.</summary>
    private void RefreshMenu()
    {
        _proxy.Text = _vm.ProxyButtonText;
        _proxy.Enabled = _vm.ToggleProxyCommand.CanExecute(null);
        _overlay.Checked = _vm.ShowOverlay;
        _lock.Checked = _vm.OverlayLocked;
        _lock.Enabled = _vm.ShowOverlay;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } icon) return icon;
        }
        catch { /* repli sur l'icône générique */ }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>L'info-bulle d'une NotifyIcon est limitée à 63 caractères.</summary>
    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}

/// <summary>« Lancer avec Windows » : valeur de la clé Run de l'utilisateur (aucun droit administrateur requis).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ShiftBridge";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe) key.SetValue(ValueName, $"\"{exe}\" --minimized");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* registre inaccessible : le réglage reste sans effet, rien de grave */ }
    }
}
