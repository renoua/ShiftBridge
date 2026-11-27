using ShiftBridge.Core.Localization;

namespace ShiftBridge.Platform.Trainer;

public enum LinkState { Connecting, Connected, Reconnecting, Failed }

/// <summary>
/// Enveloppe une liaison trainer pour qu'elle survive aux coupures (fréquentes en Bluetooth) :
///  - reconnexion automatique avec attente croissante (1 s → 15 s) ;
///  - arbre de services FIGÉ à la première découverte : après reconnexion l'appli voit exactement le même trainer ;
///  - abonnements aux notifications rétablis à chaque reconnexion ;
///  - chien de garde : si aucune notification n'arrive pendant <c>silenceTimeout</c> alors qu'on est abonné, la liaison est
///    considérée morte (une liaison Bluetooth peut se figer sans signaler de déconnexion).
/// Le contrôle FTMS est perdu à la coupure : c'est au proxy de le rejouer à la réception de <see cref="Reconnected"/>.
/// </summary>
public sealed class ReconnectingTrainerLink : IReconnectingLink
{
    private readonly Func<CancellationToken, Task<ITrainerLink>> _factory;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;
    private readonly TimeSpan _silenceTimeout;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<Guid, List<(Guid Uuid, byte Props)>> _charsCache = [];
    private readonly HashSet<Guid> _subscribed = [];
    private List<Guid>? _servicesCache;
    private ITrainerLink? _inner;
    private bool _reconnecting;
    private long _lastNotificationTick;
    private bool _disposed;

    public ReconnectingTrainerLink(Func<CancellationToken, Task<ITrainerLink>> factory,
        TimeSpan? initialDelay = null, TimeSpan? maxDelay = null, TimeSpan? silenceTimeout = null)
    {
        _factory = factory;
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(1);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(15);
        _silenceTimeout = silenceTimeout ?? TimeSpan.FromSeconds(12);
    }

    public LinkState State { get; private set; } = LinkState.Connecting;
    public int ReconnectCount { get; private set; }
    public bool IsConnected { get { lock (_lock) return State == LinkState.Connected && _inner?.IsConnected == true; } }

    public event Action<Guid, byte[]>? Notification;
    public event Action? Disconnected;
    public event Action? Reconnected;
    public event Action? Dropped;
    public event Action<LinkState>? StateChanged;
    public event Action<string>? Log;

    /// <summary>Première connexion : doit réussir (l'erreur est remontée à l'utilisateur) ; les suivantes sont automatiques.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(LinkState.Connecting);
        ITrainerLink link;
        try { link = await _factory(ct); }
        catch { SetState(LinkState.Failed); throw; }
        lock (_lock) { _inner = link; Attach(link); _lastNotificationTick = Environment.TickCount64; }
        SetState(LinkState.Connected);
        _ = WatchdogAsync();
    }

    // ---------- ITrainerLink ----------

    public async Task<List<Guid>> DiscoverServicesAsync()
    {
        lock (_lock) if (_servicesCache is not null) return [.. _servicesCache];
        var result = await Current().DiscoverServicesAsync();
        lock (_lock) _servicesCache = [.. result];
        return result;
    }

    public async Task<List<(Guid Uuid, byte Props)>> DiscoverCharacteristicsAsync(Guid service)
    {
        lock (_lock) if (_charsCache.TryGetValue(service, out var cached)) return [.. cached];
        var result = await Current().DiscoverCharacteristicsAsync(service);
        lock (_lock) _charsCache[service] = [.. result];
        return result;
    }

    public async Task<byte[]> ReadAsync(Guid characteristic)
    {
        var value = await Current().ReadAsync(characteristic);
        _probe ??= characteristic; // une caractéristique lisible : sert à vérifier qu'un trainer silencieux est encore là
        return value;
    }

    private Guid? _probe;
    public Task WriteAsync(Guid characteristic, byte[] value) => Current().WriteAsync(characteristic, value);

    public async Task EnableNotificationsAsync(Guid characteristic, bool enable = true)
    {
        lock (_lock) { if (enable) _subscribed.Add(characteristic); else _subscribed.Remove(characteristic); }
        ITrainerLink? inner; bool up;
        lock (_lock) { inner = _inner; up = State == LinkState.Connected; }
        // Pendant une coupure on se contente de mémoriser : l'abonnement est rétabli à la reconnexion.
        if (up && inner is not null) await inner.EnableNotificationsAsync(characteristic, enable);
    }

    private ITrainerLink Current()
    {
        lock (_lock)
        {
            if (State != LinkState.Connected || _inner is null) throw new IOException(Strings.Err_TrainerNotConnected);
            return _inner;
        }
    }

    // ---------- Surveillance et reconnexion ----------

    private void Attach(ITrainerLink l)
    {
        l.Notification += OnInnerNotification;
        l.Disconnected += OnInnerDisconnected;
        l.Log += OnInnerLog;
    }

    private void Detach(ITrainerLink l)
    {
        l.Notification -= OnInnerNotification;
        l.Disconnected -= OnInnerDisconnected;
        l.Log -= OnInnerLog;
    }

    private void OnInnerNotification(Guid ch, byte[] value)
    {
        Interlocked.Exchange(ref _lastNotificationTick, Environment.TickCount64);
        Notification?.Invoke(ch, value);
    }

    private void OnInnerLog(string m) => Log?.Invoke(m);

    private void OnInnerDisconnected()
    {
        if (_disposed) return;
        BeginReconnect("liaison fermée par le trainer");
    }

    private async Task WatchdogAsync()
    {
        var period = TimeSpan.FromMilliseconds(Math.Max(50, _silenceTimeout.TotalMilliseconds / 4));
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(period, _cts.Token);
                bool watch; ITrainerLink? inner;
                lock (_lock) { watch = State == LinkState.Connected && _subscribed.Count > 0; inner = _inner; }
                if (!watch || Environment.TickCount64 - Interlocked.Read(ref _lastNotificationTick) <= _silenceTimeout.TotalMilliseconds)
                    continue;
                // Silence : un trainer à l'arrêt ou en veille peut cesser d'envoyer sans être perdu. Une lecture qui répond
                // prouve que la liaison vit : on ne la coupe pas (ce qui ferait perdre le contrôle FTMS à l'appli).
                if (_probe is { } probe && inner is not null && await ProbeAsync(inner, probe))
                {
                    Interlocked.Exchange(ref _lastNotificationTick, Environment.TickCount64);
                    continue;
                }
                BeginReconnect($"aucune notification depuis {_silenceTimeout.TotalSeconds:0} s");
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> ProbeAsync(ITrainerLink inner, Guid characteristic)
    {
        try
        {
            await inner.ReadAsync(characteristic).WaitAsync(TimeSpan.FromMilliseconds(Math.Max(500, _silenceTimeout.TotalMilliseconds / 4)), _cts.Token);
            return true;
        }
        catch { return false; }
    }

    private void BeginReconnect(string reason)
    {
        lock (_lock)
        {
            if (_reconnecting || _disposed) return;
            _reconnecting = true;
        }
        Log?.Invoke($"Liaison trainer perdue ({reason}) : reconnexion…");
        SetState(LinkState.Reconnecting);
        Dropped?.Invoke();
        _ = ReconnectLoopAsync();
    }

    private async Task ReconnectLoopAsync()
    {
        ITrainerLink? old;
        lock (_lock) { old = _inner; _inner = null; }
        if (old is not null) { Detach(old); try { await old.DisposeAsync(); } catch { } }

        var delay = _initialDelay;
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, ct);
                var next = await _factory(ct);
                if (_disposed) { try { await next.DisposeAsync(); } catch { } return; } // arrêt pendant la connexion : ne pas laisser fuir la liaison
                Attach(next);
                try
                {
                    Guid[] toRestore; lock (_lock) toRestore = [.. _subscribed];
                    foreach (var ch in toRestore) await next.EnableNotificationsAsync(ch);
                }
                catch { Detach(next); try { await next.DisposeAsync(); } catch { } throw; }

                bool closed;
                lock (_lock)
                {
                    closed = _disposed;
                    if (!closed) { _inner = next; _reconnecting = false; ReconnectCount++; }
                }
                if (closed) { Detach(next); try { await next.DisposeAsync(); } catch { } return; }
                Interlocked.Exchange(ref _lastNotificationTick, Environment.TickCount64);
                SetState(LinkState.Connected);
                Log?.Invoke($"Trainer reconnecté (reconnexion n°{ReconnectCount})");
                Reconnected?.Invoke();
                // Coupure survenue pendant que _reconnecting était encore vrai : son signal a été ignoré, on la traite ici.
                if (!next.IsConnected) BeginReconnect("liaison perdue juste après la reconnexion");
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log?.Invoke($"Reconnexion au trainer impossible : {ex.Message} (nouvel essai dans {delay.TotalSeconds:0.#} s)");
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _maxDelay.TotalMilliseconds));
            }
        }
    }

    private void SetState(LinkState s)
    {
        lock (_lock) { if (State == s) return; State = s; }
        StateChanged?.Invoke(s);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _cts.Cancel();
        ITrainerLink? inner;
        lock (_lock) { inner = _inner; _inner = null; }
        if (inner is not null) { Detach(inner); try { await inner.DisposeAsync(); } catch { } }
        Disconnected?.Invoke();
    }
}
