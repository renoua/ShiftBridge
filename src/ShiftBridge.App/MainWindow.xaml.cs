using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ShiftBridge.Platform.Windows;
using ShiftBridge.Platform.Windows.Input;
using ShiftBridge.Presentation;

namespace ShiftBridge.App;

public partial class MainWindow : Window
{
    private readonly WpfAppHost _host = new();
    private readonly MainViewModel _vm;
    private bool _closing;
    private GearOverlayWindow? _overlay;

    public MainWindow()
    {
        _vm = new(new WindowsBlePlatform(), new WpfDispatcher(), _host, new WindowsLocalInputProvider());
        InitializeComponent();
        DataContext = _vm;
        VersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.ShowOverlay): SyncOverlay(); break;
                case nameof(MainViewModel.MinimizeToTray): SyncTray(); break;
                case nameof(MainViewModel.StartWithWindows): StartupRegistration.Apply(_vm.StartWithWindows); break;
                case nameof(MainViewModel.GearText):
                case nameof(MainViewModel.ProxyActive): _tray?.SetTooltip(_vm.TrayTooltip); break;
            }
        };
        // Démarrage réduit (réglage ou lancement avec Windows) : réduite avant le premier affichage.
        bool startMinimized = _vm.ShouldStartMinimized || App.LaunchedMinimized;
        if (startMinimized) WindowState = WindowState.Minimized;
        Loaded += async (_, _) =>
        {
            SyncTray();
            StartupRegistration.Apply(_vm.StartWithWindows); // garde la valeur du registre alignée (exe déplacé, case décochée ailleurs)
            if (startMinimized && _vm.MinimizeToTray) Hide();
            SyncOverlay();
            // Radio Bluetooth suivie avant le démarrage : la première recherche sait déjà si elle est éteinte.
            await _host.Radio.StartAsync();
            _host.Radio.StateChanged += on => { if (on) _vm.OnBluetoothTurnedOn(); else _vm.SetBluetoothOff(true); };
            if (!_host.Radio.IsOn) _vm.SetBluetoothOff(true);
            try { await _vm.StartAsync(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "ShiftBridge", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Barre de titre sombre, cohérente avec le thème (repli silencieux si le système ne le gère pas).</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (19 sur les premières versions de Windows 10 2004)
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch { /* cosmétique : sans effet si indisponible */ }
    }

    private TrayIcon? _tray;

    /// <summary>L'icône de la zone de notification n'existe que si « Réduire dans la zone de notification » est cochée.</summary>
    private void SyncTray()
    {
        if (_vm.MinimizeToTray)
        {
            if (_tray is not null) return;
            _tray = new TrayIcon(_vm);
            _tray.OpenRequested += RestoreFromTray;
            _tray.QuitRequested += () => Dispatcher.Invoke(Close);
        }
        else if (_tray is { } t)
        {
            _tray = null;
            t.Dispose();
            if (!IsVisible) RestoreFromTray(); // case décochée alors que la fenêtre est cachée : on ne la perd pas
        }
    }

    private void RestoreFromTray() => Dispatcher.Invoke(() =>
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    });

    /// <summary>Réduire cache la fenêtre dans la zone de notification (la croix, elle, ferme toujours l'application).</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _vm.MinimizeToTray && _tray is not null) Hide();
    }

    /// <summary>Affiche ou ferme la superposition selon la case de l'onglet Virtual shifting.</summary>
    private void SyncOverlay()
    {
        if (_vm.ShowOverlay)
        {
            if (_overlay is not null) return;
            _overlay = new GearOverlayWindow(_vm);
            _overlay.Show();
        }
        else if (_overlay is { } o)
        {
            _overlay = null;
            o.Close();
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closing) return;
        if (_overlay is { } overlay) { overlay.ClosingWithApp = true; overlay.Close(); _overlay = null; } // sinon elle garderait l'application ouverte
        // On libère proprement les connexions BLE avant de quitter (sinon la manette reste « occupée »).
        e.Cancel = true;
        _closing = true;
        // Une liaison BLE Windows qui ne répond plus à sa fermeture ne doit pas empêcher de quitter : au bout de 5 s on part quand même.
        try { await _vm.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* délai dépassé ou erreur : on quitte quand même */ }
        _host.Radio.Dispose();
        _tray?.Dispose(); // sinon l'icône reste affichée jusqu'au passage de la souris
        _tray = null;
        Close();
    }
}
