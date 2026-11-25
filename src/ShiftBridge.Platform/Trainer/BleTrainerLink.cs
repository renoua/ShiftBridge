using ShiftBridge.Core.Localization;
using ShiftBridge.Core.Trainer;
using ShiftBridge.Platform.Ble;

namespace ShiftBridge.Platform.Trainer;

/// <summary>
/// Liaison Bluetooth vers un trainer (rôle central), indépendante de la plateforme : tout l'accès GATT passe par
/// <see cref="IBleConnection"/>. Volontairement mince : la logique (reconnexion, relais, arbitre, rejeu) est dans les couches
/// testées. Le Control Point FTMS est en indication et s'écrit AVEC réponse ; les mesures sont en notification.
/// </summary>
public sealed class BleTrainerLink : ITrainerLink
{
    /// <summary>Chaque appel GATT est borné : les API de plateforme n'ont pas de délai propre.</summary>
    private static readonly BleConnectOptions Options = new(TimeSpan.FromSeconds(6));

    private readonly IBleCentral _central;
    private readonly BleDeviceId _address;
    private readonly Dictionary<Guid, List<BleCharacteristicInfo>> _chars = [];
    private readonly Dictionary<Guid, Guid> _serviceOf = []; // caractéristique → service
    private IBleConnection? _connection;
    private bool _disposed;
    private int _disconnectRaised;

    public BleTrainerLink(IBleCentral central, BleDeviceId address, string name)
    {
        _central = central;
        _address = address;
        Name = name;
    }

    public string Name { get; }
    public bool IsConnected => !_disposed && _connection?.IsConnected == true;

    public event Action<Guid, byte[]>? Notification;
    public event Action? Disconnected;
    public event Action<string>? Log;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        try { _connection = await _central.ConnectAsync(_address, Options, ct); }
        catch (BleDeviceNotFoundException)
        {
            throw new IOException(Strings.Err_TrainerNotFound);
        }
        _connection.Disconnected += OnDisconnected;
        _connection.ValueChanged += OnValueChanged;
        _connection.Log += m => Log?.Invoke(m);

        // Services génériques (accès générique / attribut générique) : sans intérêt, et inutilisables à relayer.
        var services = await _connection.DiscoverAsync(
            uuid => uuid != FtmsCodec.BaseUuid(0x1800) && uuid != FtmsCodec.BaseUuid(0x1801), ct: ct);
        foreach (var svc in services)
        {
            _chars[svc.Uuid] = [.. svc.Characteristics];
            foreach (var c in svc.Characteristics) _serviceOf.TryAdd(c.Uuid, svc.Uuid);
        }
        Log?.Invoke($"Trainer Bluetooth connecté : {_chars.Count} service(s)");
    }

    // ---------- ITrainerLink ----------

    public Task<List<Guid>> DiscoverServicesAsync() => Task.FromResult(_chars.Keys.ToList());

    public Task<List<(Guid Uuid, byte Props)>> DiscoverCharacteristicsAsync(Guid service)
    {
        if (!_chars.TryGetValue(service, out var list)) return Task.FromResult(new List<(Guid, byte)>());
        return Task.FromResult(list.Select(c => (c.Uuid, GattRelayPolicy.GattToDirConProps(c.Properties))).ToList());
    }

    public Task<byte[]> ReadAsync(Guid characteristic)
    {
        var (svc, _) = Find(characteristic);
        return _connection!.ReadAsync(svc, characteristic);
    }

    public Task WriteAsync(Guid characteristic, byte[] value)
    {
        var (svc, info) = Find(characteristic);
        // Avec réponse dès que la caractéristique le permet (Control Point FTMS) : on sait ainsi si le trainer a accepté.
        return _connection!.WriteAsync(svc, characteristic, value, withResponse: info.CanWrite);
    }

    public Task EnableNotificationsAsync(Guid characteristic, bool enable = true)
    {
        var (svc, info) = Find(characteristic);
        if (enable && !info.CanNotify && !info.CanIndicate) throw new IOException(Loc.Fmt(Strings.Err_NoNotify, characteristic));

        // Le Control Point FTMS répond par INDICATION ; le reste en notification.
        var mode = !enable ? BleNotifyMode.None
            : (characteristic == FtmsCodec.ControlPoint && info.CanIndicate) || !info.CanNotify
                ? BleNotifyMode.Indicate
                : BleNotifyMode.Notify;
        return _connection!.SetNotifyAsync(svc, characteristic, mode);
    }

    // ---------- Internes ----------

    private (Guid Service, BleCharacteristicInfo Info) Find(Guid uuid)
    {
        if (_disposed || _connection is null) throw new IOException(Strings.Err_LinkClosed);
        if (_serviceOf.TryGetValue(uuid, out var svc))
            foreach (var c in _chars[svc]) if (c.Uuid == uuid) return (svc, c);
        throw new IOException(Loc.Fmt(Strings.Err_UnknownCharacteristic, uuid));
    }

    private void OnValueChanged(Guid service, Guid characteristic, byte[] data) => Notification?.Invoke(characteristic, data);

    private void OnDisconnected()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0) Disconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_connection is { } c)
        {
            c.Disconnected -= OnDisconnected;
            c.ValueChanged -= OnValueChanged;
            try { await c.DisposeAsync(); } catch { }
        }
        _chars.Clear(); _serviceOf.Clear();
    }
}
