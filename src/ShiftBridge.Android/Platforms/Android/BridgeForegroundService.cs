using ShiftBridge.Core.Localization;
using System.ComponentModel;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;
using ShiftBridge.Presentation;

namespace ShiftBridge.Mobile;

/// <summary>
/// Garde le pont vivant en arrière-plan (écran éteint, appli d'entraînement au premier plan). Il détient aussi :
///  - le verrou multicast Wi-Fi, sans lequel Android filtre le mDNS (annonce et découverte du trainer) ;
///  - un wake lock partiel, pour que le CPU ne s'endorme pas entre deux notifications BLE.
/// La notification affiche la vitesse virtuelle et l'état des manettes en direct.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
public class BridgeForegroundService : Service
{
    private const string ChannelId = "shiftbridge_bridge";
    private const int NotificationId = 1;
    private const string ActionStop = "com.antoinerenou.shiftbridge.STOP";
    private const string ActionShiftUp = "com.antoinerenou.shiftbridge.SHIFT_UP";
    private const string ActionShiftDown = "com.antoinerenou.shiftbridge.SHIFT_DOWN";
    private static readonly long WakeLockMaxMs = (long)TimeSpan.FromHours(6).TotalMilliseconds; // filet de sécurité contre une fuite

    private WifiManager.MulticastLock? _multicast;
    private PowerManager.WakeLock? _wake;
    private MainViewModel? _observed;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            _ = StopEverythingAsync();
            return StartCommandResult.NotSticky;
        }

        EnsureChannel();
        var notification = BuildNotification();
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
                StartForeground(NotificationId, notification, ForegroundService.TypeConnectedDevice);
            else
                StartForeground(NotificationId, notification);
        }
        catch (Exception ex)
        {
            // Permission de type refusée, ou démarrage interdit en arrière-plan : on ne plante pas, le pont reste actif dans l'activité.
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
                AppRuntime.ViewModel.ReportError(Loc.Fmt(Strings.Err_ForegroundService, ex.Message)));
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        AcquireLocks();
        Observe();

        // Boutons « Vitesse + / − » de la notification : le démarrage ci-dessus est idempotent (le service peut avoir été relancé
        // par ce simple appui), puis la vitesse change comme avec une manette.
        if (intent?.Action is ActionShiftUp or ActionShiftDown)
        {
            bool up = intent.Action == ActionShiftUp;
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                var vm = AppRuntime.ViewModel;
                (up ? vm.ShiftUpCommand : vm.ShiftDownCommand).Execute(null);
            });
        }

        // Redémarrage « sticky » après une mort du processus : l'activité n'est pas là pour relancer le pont.
        // Sur le thread principal : le ViewModel modifie des propriétés observables et son journal, qui ne sont pas thread-safe.
        Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(async () =>
        {
            try { await AppRuntime.StartAsync(); } catch { /* journalisé par le ViewModel */ }
        });
        return StartCommandResult.Sticky;
    }

    private void Observe()
    {
        if (_observed is not null) return;
        _observed = AppRuntime.ViewModel;
        _observed.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.GearText) or nameof(MainViewModel.ControllersSummary) or nameof(MainViewModel.AppSummary))
            ScheduleNotificationUpdate();
    }

    private bool _notifyScheduled;

    /// <summary>
    /// Android ignore les mises à jour de notification trop rapprochées : en rafale, la dernière (la bonne) pouvait se perdre.
    /// On regroupe donc les changements et on publie une seule fois, avec la valeur finale, au plus toutes les 500 ms.
    /// </summary>
    private void ScheduleNotificationUpdate()
    {
        if (_notifyScheduled) return;
        _notifyScheduled = true;
        _ = Task.Delay(500).ContinueWith(_ => Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
        {
            _notifyScheduled = false;
            try { (GetSystemService(NotificationService) as NotificationManager)?.Notify(NotificationId, BuildNotification()); }
            catch { /* service en cours d'arrêt */ }
        }), TaskScheduler.Default);
    }

    private Notification BuildNotification()
    {
        var vm = AppRuntime.ViewModel;
        var open = PendingIntent.GetActivity(this, 0,
            new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.NewTask),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var stop = PendingIntent.GetService(this, 1,
            new Intent(this, typeof(BridgeForegroundService)).SetAction(ActionStop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var shiftUp = PendingIntent.GetService(this, 2,
            new Intent(this, typeof(BridgeForegroundService)).SetAction(ActionShiftUp),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var shiftDown = PendingIntent.GetService(this, 3,
            new Intent(this, typeof(BridgeForegroundService)).SetAction(ActionShiftDown),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        string title = string.IsNullOrWhiteSpace(vm.GearText) ? "ShiftBridge" : Loc.Fmt(Strings.Notif_GearTitle, vm.GearText);
        // Android 8+ : le canal porte l'importance. Avant, c'est la priorité de la notification (basse : pas de son).
#pragma warning disable CA1422 // constructeur sans canal et SetPriority : obsolètes en 26+, utilisés seulement avant
        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this).SetPriority((int)NotificationPriority.Low)!;
#pragma warning restore CA1422
        return builder
            .SetContentTitle(title)!
            .SetContentText($"{vm.ControllersSummary} · {vm.AppSummary}")!
            .SetSmallIcon(Resource.Drawable.ic_stat_shiftbridge)!
            .SetContentIntent(open)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .AddAction(new Notification.Action.Builder(null, Strings.Notif_ShiftDown, shiftDown).Build())!
            .AddAction(new Notification.Action.Builder(null, Strings.Notif_ShiftUp, shiftUp).Build())!
            .AddAction(new Notification.Action.Builder(null, Strings.Notif_Quit, stop).Build())!
            .Build()!;
    }

    private void EnsureChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return; // pas de canaux avant Android 8
        if (GetSystemService(NotificationService) is not NotificationManager nm) return;
        // Importance basse : pas de son à chaque changement de rapport. Recréer un canal existant met à jour son nom et sa
        // description : ils suivent ainsi la langue choisie (l'importance, elle, n'est pas modifiée).
        nm.CreateNotificationChannel(new NotificationChannel(ChannelId, Strings.Notif_ChannelName, NotificationImportance.Low)
        {
            Description = Strings.Notif_ChannelDesc,
        });
    }

    private void AcquireLocks()
    {
        try
        {
            if (_multicast is null && GetSystemService(WifiService) is WifiManager wifi)
            {
                _multicast = wifi.CreateMulticastLock("shiftbridge-mdns");
                _multicast?.SetReferenceCounted(false);
            }
            if (_multicast is { IsHeld: false }) _multicast.Acquire();

            if (_wake is null && GetSystemService(PowerService) is PowerManager pm)
                _wake = pm.NewWakeLock(WakeLockFlags.Partial, "shiftbridge:bridge");
            if (_wake is { IsHeld: false }) _wake.Acquire(WakeLockMaxMs);
        }
        catch (Exception ex) { AppRuntime.ViewModel.ReportError(Loc.Fmt(Strings.Err_LocksUnavailable, ex.Message)); }
    }

    private void ReleaseLocks()
    {
        try { if (_multicast is { IsHeld: true }) _multicast.Release(); } catch { }
        try { if (_wake is { IsHeld: true }) _wake.Release(); } catch { }
    }

    private async Task StopEverythingAsync()
    {
        await AppRuntime.StopAsync();
        if (OperatingSystem.IsAndroidVersionAtLeast(24)) StopForeground(StopForegroundFlags.Remove);
#pragma warning disable CA1422 // StopForeground(bool) : obsolète en 24+, utilisé seulement sur Android 6
        else StopForeground(true);
#pragma warning restore CA1422
        StopSelf();
        Process.KillProcess(Process.MyPid()); // « Quitter » : on ne laisse pas un processus vide en mémoire
    }

    public override void OnDestroy()
    {
        if (_observed is not null) _observed.PropertyChanged -= OnViewModelChanged;
        _observed = null;
        ReleaseLocks();
        base.OnDestroy();
    }
}
