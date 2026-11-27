namespace ShiftBridge.Core.Trainer;

/// <summary>
/// Arbitre du Control Point FTMS. Le protocole n'admet qu'une procédure à la fois : on garde donc UNE seule commande
/// « en vol », dont on connaît le propriétaire (appli ou proxy), et on route la réponse (0x80) à son émetteur.
///  - commandes de l'appli : file FIFO, prioritaires ;
///  - commandes de rejeu du proxy : file FIFO, servies avant l'appli (elles précèdent ce que l'appli enverra ensuite) ;
///  - ré-applications du proxy (changement de rapport) : « la dernière gagne », espacées d'un écart minimal.
/// Une réponse sans commande en vol correspondante est signalée comme étrangère (un autre client écrit sur le trainer).
/// </summary>
public sealed class ControlPointArbiter : IDisposable
{
    public enum Owner { App, Proxy }

    private readonly Func<byte[], CancellationToken, Task> _send;
    private readonly TimeSpan _proxyMinGap;
    private readonly TimeSpan _responseTimeout;
    private readonly object _lock = new();
    private readonly Queue<byte[]> _proxyFifo = new();
    private readonly Queue<(byte[] Cp, Func<byte[], byte[]>? Adjust, TaskCompletionSource<bool> Done)> _appQueue = new();
    /// <summary>
    /// Commandes expirées dont la réponse peut encore arriver (opcode, heure d'expiration) : une réponse tardive leur est
    /// attribuée et ignorée, au lieu d'être prise pour celle d'un autre client.
    /// </summary>
    private readonly List<(byte Op, long Tick)> _orphans = [];
    /// <summary>Au-delà, une commande expirée n'attend plus sa réponse.</summary>
    private const long OrphanLifetimeMs = 5000;
    /// <summary>Code de résultat FTMS « Operation Failed ».</summary>
    public const byte ResultOperationFailed = 0x04;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private byte[]? _pendingProxy;
    private (Owner Owner, byte Op)? _inFlight;
    private TaskCompletionSource? _resolved;
    private long _lastProxySendTick;
    private long _inFlightSentTick;
    private double _responseEmaMs = 240; // départ : écart de 120 ms

    /// <summary>Sans écart imposé, l'écart entre ré-applications du proxy suit le temps de réponse du trainer (moitié, 80–300 ms).</summary>
    private readonly bool _adaptiveGap;

    public ControlPointArbiter(Func<byte[], CancellationToken, Task> send,
        TimeSpan? proxyMinGap = null, TimeSpan? responseTimeout = null)
    {
        _send = send;
        _adaptiveGap = proxyMinGap is null;
        _proxyMinGap = proxyMinGap ?? TimeSpan.FromMilliseconds(120);
        _responseTimeout = responseTimeout ?? TimeSpan.FromMilliseconds(1500);
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Délai (ms) entre l'envoi d'une commande et sa réponse, pour chaque commande répondue.</summary>
    public event Action<double>? ResponseLatency;

    /// <summary>Écart minimal courant entre deux ré-applications du proxy.</summary>
    public TimeSpan CurrentProxyGap
    {
        get
        {
            if (!_adaptiveGap) return _proxyMinGap;
            lock (_lock) return TimeSpan.FromMilliseconds(Math.Clamp(_responseEmaMs / 2, 80, 300));
        }
    }

    /// <summary>Réponse à une commande de l'APPLI : à lui transmettre.</summary>
    public event Action<byte[]>? ResponseForApp;
    /// <summary>Réponse sans commande en vol correspondante (autre client, ou réponse tardive).</summary>
    public event Action<byte[]>? ForeignResponse;
    public event Action<byte>? Timeout;
    public event Action<string>? Log;

    /// <summary>
    /// Met en file une commande de l'appli. Le résultat vaut vrai quand elle a été ENVOYÉE au trainer.
    /// <paramref name="adjust"/> : réécriture appliquée au moment de l'envoi (et non de la mise en file), pour qu'un changement
    /// de rapport survenu pendant l'attente soit pris en compte.
    /// </summary>
    public Task<bool> SubmitFromAppAsync(byte[] cp, Func<byte[], byte[]>? adjust = null)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_cts.IsCancellationRequested) return Task.FromResult(false);
            _appQueue.Enqueue((cp, adjust, done));
            // Une nouvelle cible de l'appli remplace toute ré-application du proxy encore en attente.
            if (cp.Length > 0 && FtmsCodec.IsTargetCommand(cp[0])) _pendingProxy = null;
        }
        _signal.Release();
        return done.Task;
    }

    /// <summary>Ré-application d'un changement de rapport : la dernière remplace celle qui attend encore.</summary>
    public void SubmitFromProxy(byte[] cp)
    {
        lock (_lock) _pendingProxy = cp;
        _signal.Release();
    }

    /// <summary>Séquence de rejeu (ordre conservé, aucune fusion).</summary>
    public void SubmitProxySequence(IEnumerable<byte[]> commands)
    {
        lock (_lock) foreach (var c in commands) _proxyFifo.Enqueue(c);
        _signal.Release();
    }

    /// <summary>À appeler pour chaque indication du Control Point reçue du trainer.</summary>
    public void OnResponse(byte[] indication)
    {
        Owner? owner = null;
        TaskCompletionSource? resolved = null;
        double elapsed = 0;
        bool late = false;
        lock (_lock)
        {
            if (_inFlight is { } f && FtmsCodec.IsResponseTo(indication, f.Op))
            {
                owner = f.Owner;
                resolved = _resolved;
                _inFlight = null;
                _resolved = null;
                elapsed = Environment.TickCount64 - _inFlightSentTick;
                _responseEmaMs = _responseEmaMs * 0.7 + elapsed * 0.3;
            }
            else
            {
                long now = Environment.TickCount64;
                _orphans.RemoveAll(o => now - o.Tick > OrphanLifetimeMs);
                int i = _orphans.FindIndex(o => FtmsCodec.IsResponseTo(indication, o.Op));
                if (i >= 0) { _orphans.RemoveAt(i); late = true; }
            }
        }

        if (late) { Log?.Invoke($"Réponse tardive du trainer à une commande expirée ({Convert.ToHexString(indication)}) : ignorée."); return; }
        if (owner is null) { ForeignResponse?.Invoke(indication); return; }
        ResponseLatency?.Invoke(elapsed);
        if (owner == Owner.App) ResponseForApp?.Invoke(indication); // la réponse à une commande du proxy n'est pas transmise
        resolved?.TrySetResult();
        _signal.Release();
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct);
                await DrainAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[]? cp = null;
            Owner owner = Owner.App;
            TaskCompletionSource<bool>? done = null;
            TimeSpan wait = TimeSpan.Zero;
            TaskCompletionSource? resolved = null;

            lock (_lock)
            {
                if (_inFlight is not null) return; // on attend sa réponse (OnResponse relance la boucle)

                if (_proxyFifo.TryDequeue(out var replay)) { cp = replay; owner = Owner.Proxy; }
                else if (_appQueue.TryDequeue(out var a))
                {
                    cp = a.Cp; owner = Owner.App; done = a.Done;
                    if (a.Adjust is { } adjust)
                    {
                        try { cp = adjust(cp); } catch (Exception ex) { Log?.Invoke($"Réécriture de la commande impossible : {ex.Message}"); }
                    }
                }
                else if (_pendingProxy is { } p)
                {
                    long since = Environment.TickCount64 - _lastProxySendTick;
                    var gap = _adaptiveGap ? TimeSpan.FromMilliseconds(Math.Clamp(_responseEmaMs / 2, 80, 300)) : _proxyMinGap;
                    if (since < gap.TotalMilliseconds) wait = gap - TimeSpan.FromMilliseconds(since);
                    else { cp = p; owner = Owner.Proxy; _pendingProxy = null; }
                }
                else return;

                if (cp is not null)
                {
                    // « En vol » AVANT l'envoi : en BLE, l'indication peut arriver avant le retour de l'écriture.
                    _inFlight = (owner, cp[0]);
                    resolved = _resolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _inFlightSentTick = Environment.TickCount64;
                    if (owner == Owner.Proxy) _lastProxySendTick = Environment.TickCount64;
                }
            }

            if (cp is null)
            {
                // Attente d'espacement, interrompue dès qu'une commande de l'appli arrive (elle est prioritaire).
                await _signal.WaitAsync(wait, ct);
                continue;
            }

            try
            {
                await _send(cp, ct);
                done?.TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                done?.TrySetResult(false); // arrêt : l'appli ne doit pas attendre indéfiniment
                throw;
            }
            catch (Exception ex)
            {
                lock (_lock) { _inFlight = null; _resolved = null; }
                Log?.Invoke($"Envoi Control Point (0x{cp[0]:X2}) impossible : {ex.Message}");
                done?.TrySetResult(false);
                continue;
            }

            // Attente de la réponse, avec délai : un trainer muet ne doit pas bloquer toutes les commandes suivantes.
            var finished = await Task.WhenAny(resolved!.Task, Task.Delay(_responseTimeout, ct));
            if (finished != resolved.Task)
            {
                bool stillInFlight;
                lock (_lock)
                {
                    stillInFlight = _inFlight is { } f && f.Op == cp[0];
                    if (stillInFlight)
                    {
                        _inFlight = null; _resolved = null;
                        _orphans.Add((cp[0], Environment.TickCount64)); // sa réponse peut encore arriver
                    }
                }
                if (stillInFlight)
                {
                    Timeout?.Invoke(cp[0]);
                    // L'appli attend une réponse à SA commande : sans elle, certaines restent bloquées. On lui répond « échec ».
                    if (owner == Owner.App) ResponseForApp?.Invoke([FtmsCodec.OpResponse, cp[0], ResultOperationFailed]);
                }
            }
        }
    }

    public void Dispose()
    {
        List<TaskCompletionSource<bool>> waiting;
        lock (_lock)
        {
            if (_cts.IsCancellationRequested) return;
            _cts.Cancel();
            // Les commandes de l'appli encore en file ne partiront jamais : leurs émetteurs ne doivent pas attendre indéfiniment.
            waiting = [.. _appQueue.Select(a => a.Done)];
            _appQueue.Clear();
        }
        foreach (var d in waiting) d.TrySetResult(false);
        try { _loop.Wait(500); } catch { }
    }
}
