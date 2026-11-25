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

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Suit l'état du Bluetooth : la demande d'activation, les réglages rapides et les réglages système aboutissent tous ici.
        _bluetoothReceiver = new BluetoothStateReceiver();
        RegisterReceiver(_bluetoothReceiver, new IntentFilter(global::Android.Bluetooth.BluetoothAdapter.ActionStateChanged));

        var missing = BridgePermissions.Missing(this);
        UpdatePermissionBanner();
        if (missing.Count == 0) StartBridge();
        else RequestPermissions([.. missing], PermissionRequestCode);
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
        }
    }

    protected override void OnDestroy()
    {
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

    private void StartBridge()
    {
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
