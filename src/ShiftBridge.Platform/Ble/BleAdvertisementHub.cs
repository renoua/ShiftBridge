namespace ShiftBridge.Platform.Ble;

/// <summary>
/// UN seul scanner BLE pour toute l'application, partagé entre plusieurs consommateurs (manettes Zwift, trainers). Le scan
/// actif consomme du temps radio et pénalise les liaisons déjà ouvertes : il ne doit donc tourner que tant qu'au moins un
/// consommateur est abonné.
/// </summary>
public sealed class BleAdvertisementHub : IDisposable
{
    private readonly object _lock = new();
    private readonly List<Action<BleAdvertisement>> _handlers = [];
    private readonly IBleScanner _scanner;
    private bool _running;
    private bool _disposed;
    private int _restartAttempts;

    /// <summary>Délai avant la première relance d'un scan arrêté par la plateforme (doublé à chaque échec, 30 s au plus).</summary>
    public TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(1);

    public BleAdvertisementHub(IBleScanner scanner)
    {
        _scanner = scanner;
        _scanner.Received += OnReceived;
        _scanner.Stopped += OnScannerStopped;
    }

    /// <summary>
    /// Le système a arrêté le scan (Bluetooth coupé puis rallumé, erreur de la pile, watcher interrompu) : tant que des
    /// consommateurs attendent des annonces, on le relance, avec une attente croissante. Avant, les manettes connues
    /// n'étaient plus jamais reconnectées automatiquement jusqu'au redémarrage de l'appli.
    /// </summary>
    private void OnScannerStopped(string reason)
    {
        bool restart;
        lock (_lock)
        {
            _running = false;
            restart = !_disposed && _handlers.Count > 0;
        }
        // Bluetooth coupé durablement : les relances suivantes échouent de la même façon, inutile de remplir le journal.
        if (Volatile.Read(ref _restartAttempts) < 3) Log?.Invoke($"Scan BLE arrêté : {reason}");
        if (restart) _ = RestartLaterAsync();
    }

    private async Task RestartLaterAsync()
    {
        int attempt = Interlocked.Increment(ref _restartAttempts);
        double seconds = Math.Min(30, RestartDelay.TotalSeconds * Math.Pow(2, Math.Min(attempt - 1, 6)));
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        bool failed = false;
        lock (_lock)
        {
            if (_running || _disposed || _handlers.Count == 0) return;
            _running = true;
            try { _scanner.Start(); }
            catch (Exception ex) { _running = false; failed = true; Log?.Invoke($"Relance du scan BLE impossible : {ex.Message}"); }
        }
        if (failed) { _ = RestartLaterAsync(); return; }
        if (attempt <= 3) Log?.Invoke("Scan BLE relancé");
    }

    public event Action<string>? Log;

    public bool IsScanning => _scanner.IsRunning;

    /// <summary>S'abonne aux annonces ; libérer l'objet retourné pour se désabonner (le scan s'arrête avec le dernier abonné).</summary>
    public IDisposable Subscribe(Action<BleAdvertisement> handler)
    {
        lock (_lock)
        {
            _handlers.Add(handler);
            if (!_running)
            {
                _running = true;
                _scanner.Start();
                Log?.Invoke("Scan BLE démarré");
            }
        }
        return new Subscription(() => Unsubscribe(handler));
    }

    private void Unsubscribe(Action<BleAdvertisement> handler)
    {
        lock (_lock)
        {
            _handlers.Remove(handler);
            if (_handlers.Count > 0 || !_running) return;
            _running = false;
            try { _scanner.Stop(); } catch { /* déjà arrêté */ }
            Log?.Invoke("Scan BLE arrêté (plus aucun consommateur)");
        }
    }

    private void OnReceived(BleAdvertisement adv)
    {
        if (Volatile.Read(ref _restartAttempts) != 0) Interlocked.Exchange(ref _restartAttempts, 0); // le scan tourne de nouveau
        Action<BleAdvertisement>[] snapshot;
        lock (_lock) snapshot = [.. _handlers];
        foreach (var h in snapshot)
        {
            try { h(adv); }
            catch { /* un consommateur défaillant ne doit pas priver les autres des annonces */ }
        }
    }

    public void Dispose()
    {
        lock (_lock) { _handlers.Clear(); _running = false; _disposed = true; }
        _scanner.Received -= OnReceived;
        _scanner.Stopped -= OnScannerStopped;
        _scanner.Dispose();
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose(); }
    }
}
