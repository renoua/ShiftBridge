using System.IO;
using System.Threading;
using System.Windows;
using ShiftBridge.Core.Config;
using ShiftBridge.Core.Localization;

namespace ShiftBridge.App;

/// <summary>
/// Démarrage de l'application : une seule instance (deux instances décaleraient silencieusement les ports et se disputeraient
/// les manettes) et un journal d'erreurs <c>crash.log</c> pour les exceptions qui n'appartiennent à aucun appelant.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;

    /// <summary>Lancée avec --minimized (démarrage avec Windows) : la fenêtre ne s'affiche pas.</summary>
    public static bool LaunchedMinimized { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // La langue doit être fixée avant toute fenêtre / ViewModel (et avant le MessageBox ci-dessous).
        AppLanguage.Apply(ConfigStore.Load().Language);
        LaunchedMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\ShiftBridge.SingleInstance", out bool first);
        if (!first && (e.Args.Contains("--after-update", StringComparer.OrdinalIgnoreCase) || e.Args.Contains("--restart", StringComparer.OrdinalIgnoreCase)))
        {
            // Relancé par la mise à jour (ou après un import de réglages) : l'ancienne instance finit de se fermer, on attend
            // qu'elle libère l'instance unique.
            try { first = _singleInstance.WaitOne(TimeSpan.FromSeconds(30)); } // l'arrêt peut prendre 5 s, plus la libération du Bluetooth
            catch (AbandonedMutexException) { first = true; } // l'ancienne instance s'est arrêtée sans libérer : le mutex nous appartient
        }
        if (first) Updates.GitHubUpdater.CleanupOld();
        if (!first)
        {
            MessageBox.Show(Strings.App_AlreadyRunning, "ShiftBridge",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write("Exception de l'interface", args.Exception);
            args.Handled = true; // un bug d'affichage ne doit pas couper les manettes en pleine séance
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write("Tâche en échec non observée", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write("Exception fatale", args.ExceptionObject as Exception);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch { /* pas propriétaire */ }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Écrit les erreurs inattendues dans %APPDATA%\ShiftBridge\crash.log (à joindre à un rapport de bug).</summary>
internal static class CrashLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShiftBridge", "crash.log");

    public static void Write(string what, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes) File.Delete(Path);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {what}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch { /* le journal d'erreurs ne doit jamais provoquer d'erreur */ }
    }
}
