using ShiftBridge.Core.Controllers;
using ShiftBridge.Core.Controllers.Decoders;
using ShiftBridge.Core.Input;
using ShiftBridge.Core.Localization;
using ShiftBridge.Core.Trainer;
using ShiftBridge.Platform.Ble;

namespace ShiftBridge.Platform.Controllers;

/// <summary>Ce qui distingue une manette BLE d'une autre : où elle notifie et comment lire ses trames.</summary>
/// <param name="Characteristics">Caractéristiques de notification du service (on s'abonne à celles qui existent).</param>
public sealed record ControllerSpec(
    ControllerKind Kind, Guid Service, IReadOnlyList<Guid> Characteristics, BleNotifyMode Mode, Func<IButtonDecoder> CreateDecoder)
{
    /// <summary>Caractéristique d'écriture (manettes interrogées, comme L-TWOO).</summary>
    public Guid? WriteCharacteristic { get; init; }
    /// <summary>Services supplémentaires à découvrir (pour le maintien de liaison).</summary>
    public Func<Guid, bool>? ExtraServiceFilter { get; init; }
    /// <summary>Caractéristique lue à intervalle régulier pour que l'appareil ne coupe pas une liaison inactive.</summary>
    public Guid? KeepAliveCharacteristic { get; init; }
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Spécification d'un modèle ; <paramref name="pin"/> ne sert qu'au L-TWOO (code à trois caractères du groupe).</summary>
    public static ControllerSpec? For(ControllerKind kind, string? pin = null) => kind switch
    {
        ControllerKind.ThinkriderVs200 => new(kind, ThinkriderVs200Decoder.Service, [ThinkriderVs200Decoder.Notify], BleNotifyMode.Notify, () => new ThinkriderVs200Decoder()),
        ControllerKind.CycplusBc2 => new(kind, BleUuids.NordicUartService, [BleUuids.NordicUartTx], BleNotifyMode.Notify, () => new CycplusBc2Decoder()),
        ControllerKind.OnelapKey => new(kind, OnelapKeyDecoder.Service, [OnelapKeyDecoder.Notify], BleNotifyMode.Notify, () => new OnelapKeyDecoder()),
        ControllerKind.ShimanoDi2 => new(kind, ShimanoDi2Decoder.Service, [ShimanoDi2Decoder.Channels], BleNotifyMode.Indicate, () => new ShimanoDi2Decoder()),
        ControllerKind.SramAxs => new(kind, SramAxsDecoder.Service, [SramAxsDecoder.Trigger], BleNotifyMode.Notify, () => new SramAxsDecoder())
        {
            ExtraServiceFilter = SramAxsDecoder.IsAxsService,
            KeepAliveCharacteristic = SramAxsDecoder.LiveState,
        },
        ControllerKind.WahooKickrBike => new(kind, WahooKickrDecoder.Service, [WahooKickrDecoder.Notify], BleNotifyMode.Notify, () => new WahooKickrDecoder()),
        ControllerKind.EliteSquare => new(kind, EliteSquareDecoder.Service, [EliteSquareDecoder.Notify43, EliteSquareDecoder.Notify45], BleNotifyMode.Notify, () => new EliteSquareDecoder()),
        ControllerKind.StagesSb20 => new(kind, StagesSb20Decoder.Service, [StagesSb20Decoder.Notify], BleNotifyMode.Notify, () => new StagesSb20Decoder()),
        ControllerKind.CycplusT7h => new(kind, BleUuids.NordicUartService, [BleUuids.NordicUartTx], BleNotifyMode.Notify, () => new CycplusT7hDecoder()),
        ControllerKind.WheeltopEds => new(kind, BleUuids.NordicUartService, [BleUuids.NordicUartTx], BleNotifyMode.Notify, () => new WheeltopEdsDecoder()),
        ControllerKind.Ltwoo => new(kind, BleUuids.NordicUartService, [BleUuids.NordicUartTx], BleNotifyMode.Notify, () => new LtwooDecoder(pin))
        {
            WriteCharacteristic = BleUuids.NordicUartRx,
        },
        _ => null,
    };
}

/// <summary>
/// Manette BLE décrite par un <see cref="ControllerSpec"/> (VS200, BC2, Onelap Key, Di2, AXS, KICKR Bike…), sans vibration.
/// Même cycle de vie que <see cref="ZwiftController"/> : reconnexion progressive (1 → 15 s), notifications abonnées après la pose
/// du gestionnaire, touches relâchées à chaque coupure. Les appuis « instantanés » (sans trame de relâchement) sont émis en
/// appui puis relâchement différé, sérialisés pour que deux appuis rapprochés ne se confondent pas côté appli.
/// </summary>
public sealed class BleButtonController : IController
{
    private static readonly Guid BatteryService = FtmsCodec.BaseUuid(0x180F);
    private static readonly Guid BatteryLevel = FtmsCodec.BaseUuid(0x2A19);
    private static readonly Guid DeviceInfoService = FtmsCodec.BaseUuid(0x180A);
    private static readonly Guid FirmwareRevision = FtmsCodec.BaseUuid(0x2A26);

    private readonly IBleCentral _central;
    private readonly ControllerSpec _spec;
    private readonly ButtonStateTracker _tracker = new();
    private readonly object _trackerLock = new();
    private readonly SemaphoreSlim _tapGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private IButtonDecoder _decoder;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _linkCts; // boucles d'interrogation et de maintien : une par liaison
    private Task? _runTask;
    private IBleConnection? _connection;
    private IReadOnlyList<BleServiceInfo> _services = [];
    private TaskCompletionSource? _disconnected;
    private long _frameCount;
    private string _lastDecodeError = "";

    public BleButtonController(IBleCentral central, DiscoveredController info, ControllerSpec spec, bool autoReconnect = true)
    {
        _central = central;
        Info = info;
        _spec = spec;
        _decoder = spec.CreateDecoder();
        AutoReconnect = autoReconnect;
    }

    public DiscoveredController Info { get; }
    public bool AutoReconnect { get; set; }
    public bool VerboseFrames { get; set; }

    /// <summary>Durée pendant laquelle un appui instantané reste « enfoncé » avant d'être relâché.</summary>
    public int TapHoldMs { get; set; } = 60;

    public ControllerState State { get; private set; } = ControllerState.Disconnected;
    public int? BatteryPercent { get; private set; }
    public string? FirmwareVersion { get; private set; }
    public bool CanVibrate => false;

    public event Action<ControllerState>? StateChanged;
    public event Action<IReadOnlyList<ButtonEvent>>? ButtonEvents;
    public event Action<int>? BatteryChanged;
    /// <summary>Une information affichée (version du micrologiciel) vient d'être lue.</summary>
    public event Action? InfoChanged;
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
        ReleaseAllButtons(); // une touche tenue pendant « Déconnecter » ne doit pas rester appuyée côté appli
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

            ReleaseAllButtons();
            await CleanupAsync();
            if (!AutoReconnect || ct.IsCancellationRequested) break;
            SetState(ControllerState.Disconnected);

            try { await Task.Delay(delayMs, ct); } catch (OperationCanceledException) { break; }
            delayMs = Math.Min(delayMs * 2, 15000); // 1 s, 2 s, 4 s … 15 s
        }
        SetState(ControllerState.Disconnected);
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        _disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AttachDecoder(_spec.CreateDecoder()); // état de trame remis à zéro à chaque liaison
        _lastDecodeError = "";

        try { _connection = await _central.ConnectAsync(Info.Address, new BleConnectOptions(HighPriority: _spec.KeepAliveCharacteristic is not null), ct); }
        catch (BleDeviceNotFoundException) { throw new IOException(Strings.Err_ControllerNotFound); }
        _connection.Disconnected += OnDisconnected;
        _connection.ValueChanged += OnValueChanged;
        _connection.Log += m => Log?.Invoke(m);

        _services = await _connection.DiscoverAsync(
            uuid => uuid == _spec.Service || (_spec.ExtraServiceFilter?.Invoke(uuid) ?? false), strict: true, ct: ct);
        var service = _services.FirstOrDefault(s => s.Uuid == _spec.Service);
        var present = service is null ? [] : _spec.Characteristics.Where(c => service.Characteristics.Any(x => x.Uuid == c)).ToList();
        if (service is null || present.Count == 0)
            throw new IOException(Strings.Err_ControllerServiceMissing);
        if (_spec.WriteCharacteristic is { } w && service.Characteristics.All(x => x.Uuid != w))
            throw new IOException(Strings.Err_ControllerServiceMissing);

        // Le gestionnaire ValueChanged est posé AVANT d'activer les notifications pour ne perdre aucune trame.
        foreach (var c in present) await _connection.SetNotifyAsync(_spec.Service, c, _spec.Mode);
        Log?.Invoke($"{Info.Name} : notifications activées");

        _linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_decoder is IPollingDecoder polling) await StartPollingAsync(polling, _linkCts.Token);
        if (_spec.KeepAliveCharacteristic is not null) _ = KeepAliveLoopAsync(_linkCts.Token);

        _ = ReadBatteryAsync(); // best-effort
        SetState(ControllerState.Connected);
    }

    private void AttachDecoder(IButtonDecoder decoder)
    {
        (_decoder as IDeferredDecoder)?.Dispose();
        _decoder = decoder;
        if (decoder is IDeferredDecoder deferred) deferred.Deferred += frame => Process(frame, null);
    }

    // ---------- Interrogation (L-TWOO) et maintien de liaison (AXS) ----------

    private async Task StartPollingAsync(IPollingDecoder polling, CancellationToken ct)
    {
        foreach (var f in polling.InitFrames) await WriteAsync(f);
        _ = Task.Run(async () =>
        {
            int i = 0;
            try
            {
                while (!ct.IsCancellationRequested && polling.PollFrames.Count > 0)
                {
                    await Task.Delay(polling.PollInterval, ct);
                    await WriteAsync(polling.PollFrames[i++ % polling.PollFrames.Count]);
                }
            }
            catch (OperationCanceledException) { /* liaison fermée */ }
            catch (Exception ex) { Log?.Invoke($"{Info.Name} : interrogation interrompue ({ex.Message})"); }
        }, ct);
    }

    private async Task WriteAsync(byte[] data)
    {
        var connection = _connection;
        if (connection is null || _spec.WriteCharacteristic is not { } chr) return;
        await _writeGate.WaitAsync();
        try
        {
            await connection.WriteAsync(_spec.Service, chr, data, withResponse: false);
            if (VerboseFrames) Frame?.Invoke($"{Info.Name} > {Convert.ToHexString(data)}");
        }
        finally { _writeGate.Release(); }
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_spec.KeepAliveInterval, ct);
                var connection = _connection;
                var svc = _services.FirstOrDefault(s => s.Characteristics.Any(c => c.Uuid == _spec.KeepAliveCharacteristic));
                if (connection is null || svc is null) continue;
                // Lecture seule : la valeur est chiffrée et ne nous sert pas, seul le trafic compte. Un échec est sans conséquence.
                try { await connection.ReadAsync(svc.Uuid, _spec.KeepAliveCharacteristic!.Value); } catch { /* best-effort */ }
            }
        }
        catch (OperationCanceledException) { /* liaison fermée */ }
    }

    /// <summary>Batterie (0x180F) et version du micrologiciel (0x180A), lues une fois par connexion, au mieux.</summary>
    private async Task ReadBatteryAsync()
    {
        try
        {
            var connection = _connection;
            if (connection is null) return;
            var found = await connection.DiscoverAsync(uuid => uuid == BatteryService || uuid == DeviceInfoService);
            if (found.Any(s => s.Uuid == BatteryService))
            {
                try
                {
                    byte[] b = await connection.ReadAsync(BatteryService, BatteryLevel);
                    if (b.Length > 0) { BatteryPercent = b[0]; BatteryChanged?.Invoke(b[0]); }
                }
                catch { /* la batterie n'est pas critique */ }
            }
            if (found.Any(s => s.Uuid == DeviceInfoService))
            {
                try
                {
                    byte[] v = await connection.ReadAsync(DeviceInfoService, FirmwareRevision);
                    string text = System.Text.Encoding.UTF8.GetString(v).Trim('\0', ' ');
                    if (text.Length > 0 && text.All(ch => !char.IsControl(ch))) { FirmwareVersion = text; InfoChanged?.Invoke(); }
                }
                catch { /* la version n'est pas critique */ }
            }
        }
        catch { /* ni l'une ni l'autre n'est critique */ }
    }

    // ---------- Trames ----------

    private void OnValueChanged(Guid service, Guid characteristic, byte[] data)
    {
        if (!_spec.Characteristics.Contains(characteristic)) return;
        _frameCount++;
        string hex = Convert.ToHexString(data);
        DecodedFrame? frame;
        try { frame = _decoder.Decode(characteristic, data); }
        catch (Exception ex)
        {
            // Une même erreur (p. ex. mauvais code PIN, répétée à chaque interrogation) n'est journalisée qu'une fois.
            if (_lastDecodeError != ex.Message) { _lastDecodeError = ex.Message; Log?.Invoke($"{Info.Name} : {ex.Message} ({hex})"); }
            return;
        }

        if (frame is null)
        {
            Frame?.Invoke($"{Info.Name} < {hex} (non reconnue)");
            return;
        }
        Process(frame, hex);
    }

    private void Process(DecodedFrame frame, string? hex)
    {
        List<ButtonEvent> events = [];
        if (frame.Held is { } held)
        {
            var keys = held.Select(n => ButtonKey.Controller(Info.Kind, n)).ToHashSet();
            lock (_trackerLock) events = _tracker.Update(keys);
        }
        var taps = frame.Taps.Select(n => ButtonKey.Controller(Info.Kind, n)).ToList();

        if (hex is not null && (VerboseFrames || events.Count > 0 || taps.Count > 0 || _frameCount == 1))
            Frame?.Invoke($"{Info.Name} < {hex}");
        else if (hex is null && taps.Count > 0)
            Frame?.Invoke($"{Info.Name} : {string.Join(", ", frame.Taps)}{(frame.Detail is { } detail ? $" ({detail})" : "")}");

        if (frame.BatteryPercent is { } pct) { BatteryPercent = pct; BatteryChanged?.Invoke(pct); }
        if (events.Count > 0) ButtonEvents?.Invoke(events);
        if (taps.Count > 0) _ = EmitTapsAsync(taps);
    }

    /// <summary>Appui puis relâchement différé de chaque touche, l'un après l'autre (sans trame de relâchement côté manette).</summary>
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

    private void ReleaseAllButtons()
    {
        List<ButtonEvent> events;
        lock (_trackerLock) events = _tracker.Reset();
        if (events.Count > 0) ButtonEvents?.Invoke(events);
    }

    private void SetState(ControllerState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    private async Task CleanupAsync()
    {
        try { _linkCts?.Cancel(); } catch { /* déjà libéré */ }
        _linkCts = null;
        (_decoder as IDeferredDecoder)?.Dispose();
        var c = _connection;
        _connection = null;
        if (c is null) return;
        c.Disconnected -= OnDisconnected;
        c.ValueChanged -= OnValueChanged;
        try { await c.DisposeAsync(); } catch { /* déjà fermée */ }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
