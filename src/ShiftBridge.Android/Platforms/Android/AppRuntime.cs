using ShiftBridge.Platform.Android;
using ShiftBridge.Presentation;

namespace ShiftBridge.Mobile;

/// <summary>
/// Le pont (ViewModel + BridgeService) vit au niveau du PROCESSUS, pas de l'activité : il continue de tourner écran éteint ou
/// quand l'appli d'entraînement est au premier plan, grâce au service de premier plan qui garde le processus en vie.
/// </summary>
internal static class AppRuntime
{
    private static readonly object Gate = new();
    private static MainViewModel? _viewModel;
    private static int _started;

    public static MainViewModel ViewModel
    {
        get
        {
            lock (Gate)
            {
                if (_viewModel is not null) return _viewModel;
                var vm = new MainViewModel(
                    new AndroidBlePlatform(global::Android.App.Application.Context), new MauiDispatcher(), new MauiAppHost());
                // Le journal de l'appli est aussi dans logcat (tag « ShiftBridge ») : utile pour diagnostiquer une coupure
                // quand l'écran est éteint ou que l'appareil est branché en adb.
                vm.LineLogged += line => global::Android.Util.Log.Info("ShiftBridge", line);
                return _viewModel = vm;
            }
        }
    }

    /// <summary>Démarre le pont une seule fois (l'activité et le redémarrage du service peuvent tous deux le demander).</summary>
    public static async Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        try { await ViewModel.StartAsync(); }
        catch { Interlocked.Exchange(ref _started, 0); throw; }
    }

    /// <summary>Arrête le pont et libère les connexions (sinon la manette reste « occupée »).</summary>
    public static async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0) return;
        // Au-delà de 5 s on quitte quand même : une liaison qui ne répond plus ne doit pas laisser le processus en vie.
        try { await ViewModel.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* délai dépassé ou erreur : on quitte quand même */ }
    }
}

internal sealed class MauiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        if (MainThread.IsMainThread) action();
        else MainThread.BeginInvokeOnMainThread(action);
    }
}

internal sealed class MauiAppHost : IAppHost
{
    public string AppVersion => AppInfo.Current.VersionString;

    public bool IsTouch => true;

    public bool AsksPermissions => true;

    public Task RequestPermissionsAsync() =>
        MainThread.InvokeOnMainThreadAsync(() => MainActivity.Current?.RequestBridgePermissionsAsync() ?? Task.CompletedTask);

    private static Page? CurrentPage => Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;

    public Task<bool> ConfirmAsync(string message) => ConfirmAsync(message, ShiftBridge.Core.Localization.Strings.State_Ok);

    public Task<bool> ConfirmAsync(string message, string confirmLabel) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
            CurrentPage is { } page && await page.DisplayAlertAsync("ShiftBridge", message, confirmLabel, ShiftBridge.Core.Localization.Strings.Buttons_Cancel));

    public Task<string?> PromptTextAsync(string message, string initial) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
            CurrentPage is { } page
                ? await page.DisplayPromptAsync("ShiftBridge", message, ShiftBridge.Core.Localization.Strings.State_Ok,
                    ShiftBridge.Core.Localization.Strings.Buttons_Cancel, initialValue: initial, maxLength: 40)
                : null);

    public void CopyText(string text)
    {
        try { MainThread.BeginInvokeOnMainThread(async () => { try { await Clipboard.Default.SetTextAsync(text); } catch { } }); }
        catch { /* presse-papiers indisponible */ }
    }

    /// <summary>Réglages de l'application (là où l'utilisateur accorde « Appareils à proximité » après un refus définitif).</summary>
    public void OpenAppSettings()
    {
        try
        {
            var context = global::Android.App.Application.Context;
            var intent = new global::Android.Content.Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings,
                global::Android.Net.Uri.FromParts("package", context.PackageName, null));
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch { /* aucun écran de réglages disponible */ }
    }

    /// <summary>Liste des exclusions d'optimisation de la batterie : sans elle, certains fabricants arrêtent le service de premier plan.</summary>
    public void OpenBatterySettings()
    {
        try
        {
            var intent = new global::Android.Content.Intent(global::Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings);
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch { /* écran absent sur cet appareil */ }
    }

    public bool IsBluetoothOn => AndroidBlePlatform.IsBluetoothOn(global::Android.App.Application.Context);

    private static long _lastBluetoothRequestTicks;

    /// <summary>Demande système « Autoriser ShiftBridge à activer le Bluetooth ? ». Anti-rebond : pas de seconde fenêtre empilée.</summary>
    public void RequestBluetoothOn()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Exchange(ref _lastBluetoothRequestTicks, now) < 2000) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var intent = new global::Android.Content.Intent(global::Android.Bluetooth.BluetoothAdapter.ActionRequestEnable);
                if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is { } activity) activity.StartActivityForResult(intent, MainActivity.BluetoothEnableRequestCode);
                else
                {
                    intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
                    global::Android.App.Application.Context.StartActivity(intent);
                }
            }
            catch { /* BLUETOOTH_CONNECT refusée (le bandeau de permission l'explique) ou écran absent */ }
        });
    }

    public bool IsWifiOn
    {
        get
        {
            try
            {
                var cm = global::Android.App.Application.Context.GetSystemService(global::Android.Content.Context.ConnectivityService)
                    as global::Android.Net.ConnectivityManager;
                var caps = cm?.GetNetworkCapabilities(cm.ActiveNetwork);
                return caps is not null && (caps.HasTransport(global::Android.Net.TransportType.Wifi) ||
                                            caps.HasTransport(global::Android.Net.TransportType.Ethernet));
            }
            catch { return true; } // état illisible : on ne bloque pas l'utilisateur
        }
    }

    public void RequestWifiOn()
    {
        try
        {
            string action = OperatingSystem.IsAndroidVersionAtLeast(29)
                ? global::Android.Provider.Settings.Panel.ActionWifi
                : global::Android.Provider.Settings.ActionWifiSettings;
            var intent = new global::Android.Content.Intent(action);
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch { /* aucun écran Wi-Fi disponible */ }
    }

    public void OpenUrl(string url)
    {
        try
        {
            var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView, global::Android.Net.Uri.Parse(url));
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch { /* aucun navigateur disponible */ }
    }

    /// <summary>Export : fichier écrit dans le cache de l'appli puis proposé au partage (Drive, Fichiers, e-mail…).</summary>
    public Task<bool> SaveTextFileAsync(string suggestedName, string content) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            string path = System.IO.Path.Combine(FileSystem.CacheDirectory, suggestedName);
            await System.IO.File.WriteAllTextAsync(path, content, new System.Text.UTF8Encoding(false));
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = ShiftBridge.Core.Localization.Strings.Backup_Title,
                File = new ShareFile(path, "application/json"),
            });
            return true;
        });

    /// <summary>Import : sélecteur de fichiers du système.</summary>
    public Task<string?> OpenTextFileAsync() =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = ShiftBridge.Core.Localization.Strings.Backup_Title });
            if (file is null) return null;
            await using var stream = await file.OpenReadAsync();
            using var reader = new System.IO.StreamReader(stream);
            return (string?)await reader.ReadToEndAsync();
        });
}
