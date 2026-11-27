using ShiftBridge.Core.Localization;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using ShiftBridge.Platform.Android;

namespace ShiftBridge.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private const int PermissionRequestCode = 4001;
    internal const int BluetoothEnableRequestCode = 4002;

    private BluetoothStateReceiver? _bluetoothReceiver;
    private TaskCompletionSource? _permissionRequest;

    /// <summary>Activité affichée (demande de permissions lancée par l'assistant de démarrage).</summary>
    internal static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;

        // Suit l'état du Bluetooth : la demande d'activation, les réglages rapides et les réglages système aboutissent tous ici.
        _bluetoothReceiver = new BluetoothStateReceiver();
        RegisterReceiver(_bluetoothReceiver, new IntentFilter(global::Android.Bluetooth.BluetoothAdapter.ActionStateChanged));

        var missing = BridgePermissions.Missing(this);
        UpdatePermissionBanner();
        if (missing.Count == 0) StartBridge();
        // Premier lancement : l'assistant explique d'abord pourquoi, puis demande (RequestBridgePermissionsAsync).
        else if (!AppRuntime.ViewModel.IsOnboarding) RequestPermissions([.. missing], PermissionRequestCode);
    }

    /// <summary>Demande les permissions manquantes (assistant de démarrage) ; terminé quand l'utilisateur a répondu.</summary>
    internal Task RequestBridgePermissionsAsync()
    {
        var missing = BridgePermissions.Missing(this);
        if (missing.Count == 0) { StartBridge(); return Task.CompletedTask; }
        var request = _permissionRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestPermissions([.. missing], PermissionRequestCode);
        return request.Task;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        // On démarre quoi qu'il arrive : si le Bluetooth est refusé, le journal l'explique (« permission Bluetooth refusée »)
        // et l'utilisateur peut encore utiliser le réseau et le diagnostic.
        if (requestCode == PermissionRequestCode)
        {
            UpdatePermissionBanner();
            StartBridge();
            _permissionRequest?.TrySetResult();
            _permissionRequest = null;
        }
    }

    protected override void OnDestroy()
    {
        if (Current == this) Current = null;
        _permissionRequest?.TrySetResult(); // l'assistant n'attend pas une réponse qui ne viendra plus
        if (_bluetoothReceiver is not null)
        {
            try { UnregisterReceiver(_bluetoothReceiver); } catch { /* déjà désenregistré */ }
            _bluetoothReceiver = null;
        }
        base.OnDestroy();
    }

    private sealed class BluetoothStateReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action != global::Android.Bluetooth.BluetoothAdapter.ActionStateChanged) return;
            var state = (global::Android.Bluetooth.State)intent.GetIntExtra(global::Android.Bluetooth.BluetoothAdapter.ExtraState, -1);
            if (state == global::Android.Bluetooth.State.On) AppRuntime.ViewModel.OnBluetoothTurnedOn();
            else if (state == global::Android.Bluetooth.State.Off) AppRuntime.ViewModel.SetBluetoothOff(true);
        }
    }

    private bool _bluetoothWasMissing;

    /// <summary>
    /// Au retour des réglages système (l'utilisateur a pu accorder la permission) : on met le bandeau à jour et, si le Bluetooth
    /// est maintenant autorisé, on relance la recherche des manettes qui n'avait rien pu faire.
    /// </summary>
    protected override void OnResume()
    {
        base.OnResume();
        bool wasMissing = _bluetoothWasMissing;
        UpdatePermissionBanner();
        if (wasMissing && !_bluetoothWasMissing) AppRuntime.ViewModel.StartScanCommand.Execute(null);
    }

    private void UpdatePermissionBanner()
    {
        string? problem = BridgePermissions.BluetoothProblem(this);
        _bluetoothWasMissing = problem is not null;
        AppRuntime.ViewModel.SetPermissionProblem(problem);
        AppRuntime.ViewModel.SetBluetoothOff(!AndroidBlePlatform.IsBluetoothOn(this));
    }

    private bool _bridgeStarted;

    private void StartBridge()
    {
        if (_bridgeStarted) return; // l'assistant et le retour de la demande de permissions peuvent tous deux le demander
        _bridgeStarted = true;
        try
        {
            var intent = new Intent(this, typeof(BridgeForegroundService));
            // Avant Android 8, StartService suffit : le service passe lui-même au premier plan (StartForeground).
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) StartForegroundService(intent);
            else StartService(intent);
        }
        catch (Exception ex) { AppRuntime.ViewModel.ReportError(Loc.Fmt(Strings.Err_ForegroundService, ex.Message)); }
        _ = StartRuntimeAsync();
    }

    private static async Task StartRuntimeAsync()
    {
        try { await AppRuntime.StartAsync(); }
        catch (Exception ex) { AppRuntime.ViewModel.ReportError(ex.Message); }
    }
}
