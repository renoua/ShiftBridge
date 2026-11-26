using ShiftBridge.Core.Controllers.Decoders;
using ShiftBridge.Core.Controllers.Sram;
using ShiftBridge.Core.Input;
using ShiftBridge.Core.Localization;
using ShiftBridge.Core.Controllers;
using ShiftBridge.Platform.Ble;

namespace ShiftBridge.Platform.Controllers;

/// <summary>Mémoire des clés d'appareil AXS obtenues à l'appairage (par numéro de série).</summary>
public interface ISramKeyStore
{
    byte[]? Get(string serial);
    void Set(string serial, byte[] key);
    void Remove(string serial);
}

/// <summary>
/// SRAM AXS, mode AVANCÉ (expérimental) : appairage SRAMBond (échange de clés puis AES-EAX), puis lecture déchiffrée du pignon
/// arrière. Chaque changement de pignon devient un appui « vitesse + » / « vitesse − » selon son sens ; le levier utilisé n'est
/// pas connu et un passage en butée ne se voit pas. Le dérailleur passe aussi sa vitesse physiquement.
/// ATTENTION : un appairage change la clé du dérailleur ; l'appli SRAM devra se reconnecter ensuite.
/// Écrit d'après la rétro-ingénierie publique, sans matériel : chaque étape est journalisée pour le diagnostic.
/// </summary>
public sealed class SramAxsAdvancedController : IController
{
    private static readonly Guid BondService = new("d905ee51-90aa-4c7c-b036-1e01fb8eb7ee");
    private static readonly Guid BondCharacteristic = new("d905ee52-90aa-4c7c-b036-1e01fb8eb7ee");
    private static readonly Guid SerialCharacteristic = new("d905fe54-90aa-4c7c-b036-1e01fb8eb7ee");
    private static readonly Guid ConfigCharacteristic = new("d9050025-90aa-4c7c-b036-1e01fb8eb7ee");
    private static readonly Guid LiveChanged = new("d9050054-90aa-4c7c-b036-1e01fb8eb7ee");

    private readonly IBleCentral _central;
    private readonly ISramKeyStore _keys;
    private readonly SramAxsShiftTracker _tracker = new();
    private readonly SemaphoreSlim _tapGate = new(1, 1);
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly object _bondLock = new();
    private readonly List<byte> _bondBuffer = [];
    private CancellationTokenSource? _cts, _linkCts;
    private Task? _runTask;
    private IBleConnection? _connection;
    private IReadOnlyList<BleServiceInfo> _services = [];
    private TaskCompletionSource? _disconnected;
    private string _serial = "";
    private byte[]? _key;
    private bool _monitoring;
    private int _readPending;

    public SramAxsAdvancedController(IBleCentral central, DiscoveredController info, ISramKeyStore keys, bool autoReconnect = true)
    {
        _central = central;
        Info = info;
        _keys = keys;
        AutoReconnect = autoReconnect;
    }

    public DiscoveredController Info { get; }
    public bool AutoReconnect { get; set; }
    public bool VerboseFrames { get; set; }
    public int TapHoldMs { get; set; } = 60;
    /// <summary>Intervalle entre deux lectures d'entretien (sans elles, le dérailleur coupe une liaison inactive).</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    public ControllerState State { get; private set; } = ControllerState.Disconnected;
    public int? BatteryPercent => null;
    public string? FirmwareVersion => null;
    public bool CanVibrate => false;
    /// <summary>Connecté mais sans clé valable : l'appairage manuel est possible (<see cref="PairAsync"/>).</summary>
    public bool CanPair => State == ControllerState.Locked && _connection is not null;

    public event Action<ControllerState>? StateChanged;
    public event Action<IReadOnlyList<ButtonEvent>>? ButtonEvents;
    public event Action<int>? BatteryChanged { add { } remove { } }
    public event Action? InfoChanged { add { } remove { } }
    public event Action<string>? Frame;
    public event Action<string>? Log;

    public void Start()
    {
        if (_runTask is { IsCompleted: false }) return;
        _cts = new CancellationTokenSource();
        _runTask = RunAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_runTask is not null) { try { await _runTask; } catch { /* annulé */ } }
        await CleanupAsync();
        SetState(ControllerState.Disconnected);
    }

    public Task VibrateAsync(byte pattern = 0x20) => Task.CompletedTask;

    private async Task RunAsync(CancellationToken ct)
    {
        int delayMs = 1000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState(ControllerState.Connecting);
                await ConnectOnceAsync(ct);
                delayMs = 1000;
                await _disconnected!.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"{Info.Name} : {ex.Message}");
                SetState(ControllerState.Error);
            }

            await CleanupAsync();
            if (!AutoReconnect || ct.IsCancellationRequested) break;
            SetState(ControllerState.Disconnected);
            try { await Task.Delay(delayMs, ct); } catch (OperationCanceledException) { break; }
            delayMs = Math.Min(delayMs * 2, 15000);
        }
        SetState(ControllerState.Disconnected);
    }

    private (Guid Service, BleCharacteristicInfo Char)? Find(Guid characteristic)
    {
        foreach (var s in _services)
            foreach (var c in s.Characteristics)
                if (c.Uuid == characteristic) return (s.Uuid, c);
        return null;
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        _disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _monitoring = false;
        _tracker.Reset();
        _tracker.GearCount = SramAxsFrames.DefaultGearCount;

        try { _connection = await _central.ConnectAsync(Info.Address, new BleConnectOptions(HighPriority: true), ct); }
        catch (BleDeviceNotFoundException) { throw new IOException(Strings.Err_ControllerNotFound); }
        _connection.Disconnected += OnDisconnected;
        _connection.ValueChanged += OnValueChanged;
        _connection.Log += m => Log?.Invoke(m);

        _services = await _connection.DiscoverAsync(Controllers_IsAxs, strict: true, ct: ct);
        if (Find(SramAxsDecoder.LiveState) is null) throw new IOException(Strings.Err_ControllerServiceMissing);

        _linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _serial = await ReadSerialAsync();
        Log?.Invoke($"{Info.Name} : numéro de série {(_serial.Length > 0 ? _serial : "illisible")}");

        // Le gestionnaire est posé AVANT d'activer les notifications. Les échecs d'abonnement ne sont pas fatals.
        foreach (var chr in new[] { SramAxsDecoder.LiveState, LiveChanged, BondCharacteristic })
        {
            if (Find(chr) is not { } f) continue;
            var mode = f.Char.CanNotify ? BleNotifyMode.Notify : f.Char.CanIndicate ? BleNotifyMode.Indicate : BleNotifyMode.None;
            if (mode == BleNotifyMode.None) continue;
            try { await _connection.SetNotifyAsync(f.Service, chr, mode); }
            catch (Exception ex) { Log?.Invoke($"{Info.Name} : abonnement {SramAxsDecoder.IsAxsService(f.Service)} {BleInspector.ShortUuid(chr)} impossible ({ex.Message})"); }
        }

        _key = _serial.Length > 0 ? _keys.Get(_serial) : null;
        if (_key is null)
        {
            Log?.Invoke($"{Info.Name} : appairage requis. Maintenez le bouton AXS du dérailleur jusqu'au clignotement, puis lancez l'appairage.");
            SetState(ControllerState.Locked);
            return;
        }
        await StartMonitoringAsync();
    }

    private static bool Controllers_IsAxs(Guid uuid) => SramAxsDecoder.IsAxsService(uuid);

    private async Task<string> ReadSerialAsync()
    {
        try
        {
            if (Find(SerialCharacteristic) is not { } f || _connection is null) return "";
            var b = await _connection.ReadAsync(f.Service, SerialCharacteristic);
            return b.Length >= 4 ? BitConverter.ToUInt32(b, 0).ToString() : "";
        }
        catch (Exception ex) { Log?.Invoke($"{Info.Name} : numéro de série illisible ({ex.Message})"); return ""; }
    }

    // ---------- Appairage ----------

    /// <summary>
    /// Appairage SRAMBond v1 (le dérailleur doit être en mode appairage). Retourne vrai si la clé a été obtenue et mémorisée.
    /// Les valeurs de l'échange sont journalisées en longueur seulement, jamais en clair.
    /// </summary>
    public async Task<bool> PairAsync()
    {
        var connection = _connection;
        if (connection is null || Find(BondCharacteristic) is not { } bond)
        {
            Log?.Invoke($"{Info.Name} : appairage impossible (appareil non connecté ou service d'appairage absent)");
            return false;
        }
        if (_serial.Length == 0) { Log?.Invoke($"{Info.Name} : appairage impossible sans numéro de série"); return false; }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_linkCts?.Token ?? CancellationToken.None);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            Log?.Invoke($"{Info.Name} : appairage SRAMBond…");

            lock (_bondLock) _bondBuffer.Clear();
            await connection.WriteAsync(bond.Service, BondCharacteristic, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15], withResponse: true);

            var priv = SramCrypto.NewPrivateKey();
            await connection.WriteAsync(bond.Service, BondCharacteristic, SramCrypto.PublicKey(priv), withResponse: true);
            Log?.Invoke($"{Info.Name} : clé publique envoyée");

            var devicePub = await ReceiveBondAsync(connection, bond.Service, 16, timeout.Token);
            var shared = SramCrypto.SharedSecret(devicePub, priv);
            var blob = await ReceiveBondAsync(connection, bond.Service, 48, timeout.Token);
            var deviceKey = SramCrypto.DecryptBondBlob(shared, blob)
                ?? throw new IOException("réponse d'appairage invalide (étiquette refusée) : le dérailleur était-il en mode appairage ?");

            await connection.WriteAsync(bond.Service, BondCharacteristic, [0x73], withResponse: true);
            _keys.Set(_serial, deviceKey);
            _key = deviceKey;
            Log?.Invoke($"{Info.Name} : appairage réussi, clé mémorisée");
            await StartMonitoringAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"{Info.Name} : appairage échoué ({ex.Message})");
            return false;
        }
    }

    /// <summary>Valeur de l'échange : lecture de la caractéristique ; à défaut (échec ou longueur inattendue), notifications cumulées.</summary>
    private async Task<byte[]> ReceiveBondAsync(IBleConnection connection, Guid service, int expected, CancellationToken ct)
    {
        try
        {
            var read = await connection.ReadAsync(service, BondCharacteristic);
            if (read.Length == expected) return read;
            Log?.Invoke($"{Info.Name} : lecture d'appairage de {read.Length} octets (attendu {expected}), attente de notifications");
        }
        catch (Exception ex) { Log?.Invoke($"{Info.Name} : lecture d'appairage impossible ({ex.Message}), attente de notifications"); }

        var until = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < until)
        {
            ct.ThrowIfCancellationRequested();
            lock (_bondLock)
                if (_bondBuffer.Count >= expected)
                {
                    var value = _bondBuffer.Take(expected).ToArray();
                    _bondBuffer.RemoveRange(0, expected);
                    return value;
                }
            await Task.Delay(25, ct);
        }
        throw new IOException($"pas de réponse de l'appareil ({expected} octets attendus)");
    }

    /// <summary>Oublie la clé mémorisée (par exemple après un nouvel appairage par l'appli SRAM) : l'appairage redevient nécessaire.</summary>
    public void ForgetKey()
    {
        if (_serial.Length > 0) _keys.Remove(_serial);
        _key = null;
        _monitoring = false;
        if (_connection is not null) SetState(ControllerState.Locked);
    }

    // ---------- Surveillance du pignon ----------

    private async Task StartMonitoringAsync()
    {
        if (_monitoring) return;
        _monitoring = true;
        _tracker.Reset();

        if (Find(ConfigCharacteristic) is { } cfg && _connection is not null && _key is not null)
        {
            try
            {
                var plain = SramCrypto.DecryptFrame(_key, await _connection.ReadAsync(cfg.Service, ConfigCharacteristic));
                if (plain is not null && SramAxsFrames.TryParseGearCount(plain) is { } n) { _tracker.GearCount = n; Log?.Invoke($"{Info.Name} : {n} pignons"); }
            }
            catch (Exception ex) { Log?.Invoke($"{Info.Name} : configuration illisible ({ex.Message})"); }
        }

        SetState(ControllerState.Connected);
        await ReadLiveAsync();
        if (_linkCts is { } link) _ = KeepAliveLoopAsync(link.Token);
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(KeepAliveInterval, ct);
                await ReadLiveAsync();
            }
        }
        catch (OperationCanceledException) { /* liaison fermée */ }
    }

    private void OnValueChanged(Guid service, Guid characteristic, byte[] data)
    {
        if (characteristic == BondCharacteristic)
        {
            lock (_bondLock) _bondBuffer.AddRange(data);
            return;
        }
        if (!_monitoring) return;
        if (characteristic != SramAxsDecoder.LiveState && characteristic != LiveChanged) return;

        if (VerboseFrames) Frame?.Invoke($"{Info.Name} < {BleInspector.ShortUuid(characteristic)} {Convert.ToHexString(data)}");
        // Une notification est en principe une simple sonnette (un octet 0xFF) : l'état se LIT. Une trame complète est déchiffrée sur place.
        if (data.Length >= 33) HandlePlain(characteristic, data);
        else _ = ReadLiveAsync();
    }

    private async Task ReadLiveAsync()
    {
        // Les sonnettes arrivent en rafale : une seule lecture à la fois, les suivantes sont regroupées.
        if (Interlocked.Exchange(ref _readPending, 1) == 1) return;
        await _readGate.WaitAsync();
        try
        {
            Interlocked.Exchange(ref _readPending, 0);
            var connection = _connection;
            if (connection is null || Find(SramAxsDecoder.LiveState) is not { } live || !_monitoring) return;
            var frame = await connection.ReadAsync(live.Service, SramAxsDecoder.LiveState);
            HandlePlain(SramAxsDecoder.LiveState, frame);
        }
        catch (Exception ex) { Log?.Invoke($"{Info.Name} : lecture de l'état impossible ({ex.Message})"); }
        finally { _readGate.Release(); }
    }

    private void HandlePlain(Guid characteristic, byte[] frame)
    {
        if (_key is null) return;
        var plain = SramCrypto.DecryptFrame(_key, frame);
        if (plain is null)
        {
            // Étiquette refusée : clé périmée (le dérailleur a été réappairé ailleurs) ou trame corrompue. Une seule fois.
            if (State == ControllerState.Connected)
            {
                Log?.Invoke($"{Info.Name} : trame refusée, clé périmée ? Relancez l'appairage.");
                _monitoring = false;
                SetState(ControllerState.Locked);
            }
            return;
        }
        // Diagnostic gauche/droite : tous les champs déchiffrés (jamais la clé). Le texte clair ne contient que l'état du dérailleur.
        if (VerboseFrames) Frame?.Invoke($"{Info.Name} : {BleInspector.ShortUuid(characteristic)} champs {SramAxsFrames.DescribeFields(plain)}");
        if (!SramAxsFrames.TryParseDrivetrain(plain, out int rear, out int? front)) return;
        var taps = _tracker.Update(rear);
        if (VerboseFrames || taps.Count > 0) Frame?.Invoke($"{Info.Name} : pignon {rear}{(front is { } f ? $", plateau {f}" : "")}{(taps.Count > 0 ? $" → {string.Join(", ", taps)}" : "")}");
        if (taps.Count > 0) _ = EmitTapsAsync([.. taps.Select(n => ButtonKey.Controller(ControllerKind.SramAxs, n))]);
    }

    private async Task EmitTapsAsync(List<ButtonKey> taps)
    {
        long queued = Environment.TickCount64;
        await _tapGate.WaitAsync();
        long waited = Environment.TickCount64 - queued;
        if (waited >= 300) Log?.Invoke($"{Info.Name} : appui émis avec {waited} ms de retard (appui précédent pas encore terminé)");
        try
        {
            foreach (var key in taps)
            {
                ButtonEvents?.Invoke([new ButtonEvent(key, true)]);
                if (TapHoldMs > 0) await Task.Delay(TapHoldMs);
                ButtonEvents?.Invoke([new ButtonEvent(key, false)]);
            }
        }
        catch (Exception ex) { Log?.Invoke($"{Info.Name} : appui impossible ({ex.Message})"); }
        finally { _tapGate.Release(); }
    }

    private void OnDisconnected()
    {
        Log?.Invoke($"{Info.Name} : déconnectée");
        _disconnected?.TrySetResult();
    }

    private void SetState(ControllerState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    private async Task CleanupAsync()
    {
        _monitoring = false;
        try { _linkCts?.Cancel(); } catch { /* déjà libéré */ }
        _linkCts = null;
        var c = _connection;
        _connection = null;
        if (c is null) return;
        c.Disconnected -= OnDisconnected;
        c.ValueChanged -= OnValueChanged;
        try { await c.DisposeAsync(); } catch { /* déjà fermée */ }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
