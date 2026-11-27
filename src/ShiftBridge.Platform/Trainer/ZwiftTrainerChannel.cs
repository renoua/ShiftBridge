using ShiftBridge.Core.Trainer;
using ShiftBridge.Core.Zwift;

namespace ShiftBridge.Platform.Trainer;

/// <summary>
/// Canal NATIF Zwift vers un trainer « Zwift Ready » : le trainer applique lui-même le rapport de vitesse (voir
/// <see cref="ZwiftTrainerCodec"/>). Passe par la liaison existante (Bluetooth ou DirCon) : une seule connexion au trainer,
/// FTMS et canal Zwift cohabitent. Tout échec fait retourner null à <see cref="TryOpenAsync"/> (ou lever <see cref="Failed"/>
/// ensuite) : l'appelant se replie alors sur la mise à l'échelle FTMS.
/// Protocole rétro-ingénierié par des tiers, jamais confirmé par Zwift : prudence, journal détaillé.
/// </summary>
public sealed class ZwiftTrainerChannel : IAsyncDisposable
{
    private readonly ITrainerLink _link;
    private readonly Guid _service;
    private double _riderKg, _bikeKg; // modifiables à chaud (voir SendWeightsAsync) ; relus à chaque ouverture de session
    private readonly TimeSpan _initGap, _keepAlive, _handshakeTimeout;
    private readonly int _attempts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();

    private TaskCompletionSource<bool>? _rideOnReply;
    private SimulationParams? _lastSim;
    private SimulationParams? _lastFullSim;
    private double _gearRatio;
    private bool _simActive;
    private bool _sawRidingData;
    private int _failedRaised;
    private volatile bool _disposed;
    private long _lastOtherReplyLogTick;

    // Changements de rapport en rafale (voir SendGearAsync) et rapports en attente de confirmation du trainer (FIFO).
    private double? _pendingGear;
    private List<TaskCompletionSource<bool>> _gearWaiters = [];
    private bool _gearPumping;
    private readonly List<(double Ratio, long Tick)> _expected = [];
    private double _lastConfirmed;
    private long _lastConfirmedTick;

    /// <summary>Délai pendant lequel une seconde confirmation du même rapport, sans requête en attente, est un doublon.</summary>
    internal const int DuplicateReplyWindowMs = 500;

    /// <summary>Mesures « rapport envoyé → confirmation du trainer » (optionnel).</summary>
    public LatencyTracker? Latency { get; set; }

    private ZwiftTrainerChannel(ITrainerLink link, Guid service, double gearRatio, double riderKg, double bikeKg,
        TimeSpan initGap, TimeSpan keepAlive, TimeSpan handshakeTimeout, int attempts)
    {
        _link = link; _service = service; _gearRatio = gearRatio; _riderKg = riderKg; _bikeKg = bikeKg;
        _initGap = initGap; _keepAlive = keepAlive; _handshakeTimeout = handshakeTimeout; _attempts = attempts;
    }

    public bool IsActive { get; private set; }

    /// <summary>Levé une seule fois quand une écriture échoue : le canal n'est plus utilisable.</summary>
    public event Action? Failed;
    public event Action<string>? Log;
    public event Action<ZwiftRidingData>? RidingData;
    /// <summary>Rapport que le trainer annonce avoir retenu (réponse à la requête de rapport).</summary>
    public event Action<double>? GearConfirmed;

    /// <summary>
    /// Détecte le service Zwift du trainer, ouvre la session (« RideOn » + 02 01, jusqu'à 3 essais de 4 s) et joue la séquence
    /// d'initialisation. Retourne null si le trainer n'expose pas le service ou ne répond pas : rien n'a alors été modifié côté résistance.
    /// </summary>
    public static async Task<ZwiftTrainerChannel?> TryOpenAsync(ITrainerLink link, double gearRatio, double riderKg, double bikeKg,
        SimulationParams? currentSim, Action<string>? log = null, TimeSpan? initGap = null, TimeSpan? keepAlive = null,
        TimeSpan? handshakeTimeout = null, int attempts = 3, CancellationToken ct = default)
    {
        List<Guid> services;
        try { services = await link.DiscoverServicesAsync(); }
        catch (Exception ex) { log?.Invoke($"Services du trainer illisibles : {ex.Message}"); return null; }

        // Firmware récent : FC82 ; sinon le service historique.
        Guid service = services.Contains(ZwiftConstants.RideCustomService) ? ZwiftConstants.RideCustomService
                     : services.Contains(ZwiftConstants.CustomService) ? ZwiftConstants.CustomService : Guid.Empty;
        if (service == Guid.Empty) { log?.Invoke("Le trainer n'expose pas le service Zwift (virtual shifting natif indisponible)."); return null; }

        List<(Guid Uuid, byte Props)> chars;
        try { chars = await link.DiscoverCharacteristicsAsync(service); }
        catch (Exception ex) { log?.Invoke($"Caractéristiques Zwift illisibles : {ex.Message}"); return null; }
        if (!HasAll(chars)) { log?.Invoke("Service Zwift incomplet (caractéristiques 0002/0003/0004 attendues)."); return null; }

        var channel = new ZwiftTrainerChannel(link, service, gearRatio, riderKg, bikeKg,
            initGap ?? TimeSpan.FromMilliseconds(150), keepAlive ?? TimeSpan.FromSeconds(5),
            handshakeTimeout ?? TimeSpan.FromSeconds(4), attempts);
        if (log is not null) channel.Log += log;
        channel._lastSim = currentSim;

        if (!await channel.OpenSessionAsync(ct))
        {
            await channel.DisposeAsync();
            return null;
        }
        channel.StartKeepAlive();
        return channel;
    }

    private static bool HasAll(List<(Guid Uuid, byte Props)> chars) =>
        chars.Any(c => c.Uuid == ZwiftConstants.AsyncCharacteristic)
        && chars.Any(c => c.Uuid == ZwiftConstants.SyncRxCharacteristic)
        && chars.Any(c => c.Uuid == ZwiftConstants.SyncTxCharacteristic);

    /// <summary>Handshake + initialisation (aussi utilisé après une reconnexion du trainer). Vrai si le trainer a répondu.</summary>
    public async Task<bool> ReopenAsync(CancellationToken ct = default)
    {
        IsActive = false;
        return await OpenSessionAsync(ct);
    }

    private async Task<bool> OpenSessionAsync(CancellationToken ct)
    {
        // Canal libéré pendant une reconnexion : il ne doit pas se rabonner à la liaison (il recevrait encore les trames).
        if (_disposed) return false;
        _link.Notification -= OnNotification;
        _link.Notification += OnNotification; // posé AVANT d'activer les abonnements : aucune réponse perdue

        try
        {
            await _link.EnableNotificationsAsync(ZwiftConstants.AsyncCharacteristic);
            await _link.EnableNotificationsAsync(ZwiftConstants.SyncTxCharacteristic);
        }
        catch (Exception ex) { Log?.Invoke($"Abonnement au canal Zwift impossible : {ex.Message}"); return false; }

        // Handshake : « RideOn » + 02 01 ; la réponse commence par « RideOn ».
        bool replied = false;
        for (int attempt = 1; attempt <= _attempts && !replied; attempt++)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) _rideOnReply = tcs;
            try { await RawWriteAsync(ZwiftTrainerCodec.RideOnTrainer); }
            catch (Exception ex) { Log?.Invoke($"Handshake Zwift : écriture refusée ({ex.Message})"); return false; }

            replied = await Task.WhenAny(tcs.Task, Task.Delay(_handshakeTimeout, ct)) == tcs.Task;
            if (!replied) Log?.Invoke($"Handshake Zwift : pas de réponse (essai {attempt}/{_attempts})");
        }
        lock (_lock) _rideOnReply = null;
        if (!replied) return false;
        Log?.Invoke("Handshake Zwift accepté par le trainer.");

        // Séquence d'initialisation (TrainerHUD), 150 ms entre chaque trame.
        SimulationParams sim = _lastSim ?? new SimulationParams(0, 0, 40, 51); // neutre : pente 0, coefficients Zwift par défaut
        try
        {
            await RawWriteAsync(ZwiftTrainerCodec.InitFrame);                                     await Gap(ct);
            await RawWriteAsync(ZwiftTrainerCodec.EncodePhysical(_gearRatio, _bikeKg, _riderKg)); await Gap(ct);
            await RawWriteAsync(ZwiftTrainerCodec.InfoRequest);                                   await Gap(ct);
            await RawWriteAsync(ZwiftTrainerCodec.EncodeSimulation(sim, full: true));             await Gap(ct);
            await RawWriteAsync(ZwiftTrainerCodec.EncodePhysical(_gearRatio));                    await Gap(ct);
            lock (_lock) { _expected.Clear(); _expected.Add((_gearRatio, Environment.TickCount64)); }
            await RawWriteAsync(ZwiftTrainerCodec.GearQuery);
        }
        catch (Exception ex) { Log?.Invoke($"Initialisation du canal Zwift interrompue : {ex.Message}"); return false; }

        lock (_lock) { _lastFullSim = sim; }
        if (_disposed) { _link.Notification -= OnNotification; return false; }
        IsActive = true;
        _failedRaised = 0;
        Log?.Invoke($"Canal Zwift actif (rapport {_gearRatio:0.00}, coureur {_riderKg:0.#} kg, vélo {_bikeKg:0.#} kg).");
        return true;
    }

    private Task Gap(CancellationToken ct) => _initGap > TimeSpan.Zero ? Task.Delay(_initGap, ct) : Task.CompletedTask;

    // ---------- Commandes ----------

    /// <summary>
    /// Simulation de l'appli, valeurs BRUTES (le trainer applique le rapport). Complète si les coefficients ont changé depuis
    /// la dernière trame complète, sinon vent et pente seulement.
    /// </summary>
    public async Task<bool> SendSimulationAsync(SimulationParams p)
    {
        bool full;
        lock (_lock)
        {
            _lastSim = p;
            _simActive = true;
            full = _lastFullSim is not { } f || f.CwRaw != p.CwRaw || f.CrrRaw != p.CrrRaw;
            if (full) _lastFullSim = p;
        }
        return await TryWriteAsync(ZwiftTrainerCodec.EncodeSimulation(p, full));
    }

    /// <summary>
    /// Change de rapport (valeur de la table), puis demande au trainer le rapport qu'il a retenu (réponse journalisée).
    /// Les changements en rafale passent par UNE boucle d'envoi : ordre garanti, « la dernière valeur gagne » (un rapport
    /// remplacé avant son écriture n'est pas envoyé) et la requête de rapport n'est faite qu'après la valeur finale.
    /// Le résultat vaut vrai quand la valeur (ou celle qui l'a remplacée) a été écrite.
    /// </summary>
    public Task<bool> SendGearAsync(double ratio)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool start;
        lock (_lock)
        {
            _gearRatio = ratio;
            _pendingGear = ratio;
            _gearWaiters.Add(tcs);
            start = !_gearPumping;
            _gearPumping = true;
        }
        if (start) _ = Task.Run(PumpGearsAsync);
        return tcs.Task;
    }

    private async Task PumpGearsAsync()
    {
        while (true)
        {
            double ratio;
            List<TaskCompletionSource<bool>> waiters;
            lock (_lock)
            {
                if (_pendingGear is not { } r) { _gearPumping = false; return; }
                ratio = r;
                _pendingGear = null;
                waiters = _gearWaiters;
                _gearWaiters = [];
            }

            long sent = Environment.TickCount64;
            bool ok = await TryWriteAsync(ZwiftTrainerCodec.EncodePhysical(ratio));
            if (ok)
            {
                bool superseded;
                lock (_lock) superseded = _pendingGear is not null;
                if (!superseded)
                {
                    lock (_lock) { _expected.Add((ratio, sent)); if (_expected.Count > 16) _expected.RemoveAt(0); }
                    ok = await TryWriteAsync(ZwiftTrainerCodec.GearQuery);
                }
            }
            foreach (var w in waiters) w.TrySetResult(ok);
        }
    }

    /// <summary>
    /// Nouveaux poids (coureur, vélo) : renvoyés aussitôt au trainer, avec le rapport courant (la trame « physique » les porte
    /// tous les trois, comme à l'initialisation), et retenus pour une réouverture de session.
    /// </summary>
    public Task<bool> SendWeightsAsync(double riderKg, double bikeKg)
    {
        double ratio;
        lock (_lock) { _riderKg = riderKg; _bikeKg = bikeKg; ratio = _gearRatio; }
        return TryWriteAsync(ZwiftTrainerCodec.EncodePhysical(ratio, bikeKg, riderKg));
    }

    /// <summary>Le maintien ne renvoie la simulation que si la dernière cible de l'appli en est une (pas en ERG).</summary>
    public void SetSimulationActive(bool active) { lock (_lock) _simActive = active; }

    /// <summary>Libère le virtual shifting du trainer (rapport 0, d'après TrainerHUD). Au mieux : ignoré si la liaison est perdue.</summary>
    public async Task ReleaseAsync()
    {
        if (!IsActive) return;
        try { await RawWriteAsync(ZwiftTrainerCodec.EncodePhysical(0)); Log?.Invoke("Virtual shifting natif libéré (rapport 0)."); }
        catch { /* trainer déjà parti */ }
    }

    private async Task<bool> TryWriteAsync(byte[] frame)
    {
        if (!IsActive || _disposed) return false;
        try { await RawWriteAsync(frame); return true; }
        catch (Exception ex)
        {
            IsActive = false;
            Log?.Invoke($"Écriture sur le canal Zwift impossible : {ex.Message}");
            if (Interlocked.Exchange(ref _failedRaised, 1) == 0) Failed?.Invoke();
            return false;
        }
    }

    private async Task RawWriteAsync(byte[] frame)
    {
        await _writeLock.WaitAsync();
        try { await _link.WriteAsync(ZwiftConstants.SyncRxCharacteristic, frame); }
        finally { _writeLock.Release(); }
    }

    // ---------- Réception et maintien ----------

    private void OnNotification(Guid ch, byte[] value)
    {
        if (ch == ZwiftConstants.SyncTxCharacteristic)
        {
            if (ZwiftTrainerCodec.IsRideOnReply(value))
            {
                Log?.Invoke($"Réponse RideOn du trainer : {Convert.ToHexString(value)}");
                TaskCompletionSource<bool>? tcs; lock (_lock) tcs = _rideOnReply;
                tcs?.TrySetResult(true);
            }
            else if (ZwiftTrainerCodec.TryDecodeGearReply(value, out var confirmed))
            {
                // Les réponses arrivent dans l'ordre mais en retard : on la rapproche de la requête qui l'a provoquée (et non du
                // dernier rapport demandé). Une réponse qui correspond à une requête plus récente écarte les plus anciennes.
                // Le trainer répond souvent deux fois au même rapport : un doublon de la dernière confirmation, arrivé peu après
                // et qui ne correspond à aucune requête en attente, est ignoré. Sinon il consommerait la requête suivante (fausse
                // alerte « a retenu… », latence faussée) et la vraie réponse à celle-ci n'aurait plus rien à rapprocher.
                double asked; long sentTick = 0;
                long now = Environment.TickCount64;
                lock (_lock)
                {
                    int idx = _expected.FindIndex(e => e.Ratio > 0 && Math.Abs(confirmed / e.Ratio - 1) < 0.01);
                    if (idx < 0 && _lastConfirmed > 0 && Math.Abs(confirmed / _lastConfirmed - 1) < 0.01
                        && now - _lastConfirmedTick < DuplicateReplyWindowMs)
                    {
                        _lastConfirmedTick = now;
                        return;
                    }
                    if (idx >= 0) { (asked, sentTick) = _expected[idx]; _expected.RemoveRange(0, idx + 1); }
                    else if (_expected.Count > 0) { (asked, sentTick) = _expected[0]; _expected.RemoveAt(0); }
                    else asked = _gearRatio;
                    _lastConfirmed = confirmed; _lastConfirmedTick = now;
                }
                bool same = asked > 0 && Math.Abs(confirmed / asked - 1) < 0.01;
                if (same && sentTick > 0) Latency?.Add("natif", Environment.TickCount64 - sentTick);
                Log?.Invoke(same
                    ? $"Le trainer confirme le rapport {confirmed:0.00}."
                    : $"Le trainer a retenu le rapport {confirmed:0.00} alors que {asked:0.00} était demandé (borné ou ignoré ?).");
                GearConfirmed?.Invoke(confirmed);
            }
            else
            {
                // Infos, accusés… : utiles au diagnostic mais réguliers ; au plus une ligne par seconde.
                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastOtherReplyLogTick) >= 1000)
                {
                    Interlocked.Exchange(ref _lastOtherReplyLogTick, now);
                    Log?.Invoke($"Réponse du trainer (canal Zwift) : {Convert.ToHexString(value)}");
                }
            }
        }
        else if (ch == ZwiftConstants.AsyncCharacteristic && ZwiftTrainerCodec.TryDecodeRidingData(value, out var ride))
        {
            if (!_sawRidingData) { _sawRidingData = true; Log?.Invoke($"Premières données de conduite Zwift : {Convert.ToHexString(value)} ({ride.Power} W, {ride.Cadence} tr/min)"); }
            RidingData?.Invoke(ride);
        }
    }

    private void StartKeepAlive() => _ = KeepAliveAsync(_cts.Token);

    private async Task KeepAliveAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_keepAlive, ct);
                SimulationParams? sim; bool active;
                lock (_lock) { sim = _lastSim; active = _simActive; }
                if (IsActive && active && sim is { } s)
                    await TryWriteAsync(ZwiftTrainerCodec.EncodeSimulation(s, full: false));
            }
        }
        catch (OperationCanceledException) { }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        IsActive = false;
        _cts.Cancel();
        _link.Notification -= OnNotification;
        return ValueTask.CompletedTask;
    }
}
