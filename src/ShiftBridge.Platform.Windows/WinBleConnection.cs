using ShiftBridge.Core;
using ShiftBridge.Core.Localization;
using ShiftBridge.Platform.Ble;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Security.Cryptography;

namespace ShiftBridge.Platform.Windows;

internal sealed class WinBleCentral : IBleCentral
{
    /// <summary>
    /// Délai par défaut de chaque opération GATT, comme sous Android et macOS : sans lui, un appel WinRT qui ne revient jamais
    /// bloquait la connexion, et « Déconnecter » attendait indéfiniment.
    /// </summary>
    internal static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(15);

    public async Task<IBleConnection> ConnectAsync(BleDeviceId address, BleConnectOptions options, CancellationToken ct = default)
    {
        if (address.MacAddress is not { } mac) throw new BleDeviceNotFoundException(Strings.Err_BtBadAddress);
        var timeout = options.OperationTimeout ?? DefaultOperationTimeout;
        var device = await WinBleConnection.With(BluetoothLEDevice.FromBluetoothAddressAsync(mac).AsTask(ct),
            timeout, "ouverture de l'appareil");
        if (device is null) throw new BleDeviceNotFoundException(Strings.Err_BleDeviceNotFound);

        try
        {
            // Maintient la liaison active : sans cela Windows peut la couper entre deux échanges.
            var session = await WinBleConnection.With(GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(ct),
                timeout, "ouverture de la session GATT");
            session.MaintainConnection = true;
            return new WinBleConnection(device, session, timeout);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Liaison GATT WinRT. Tous les objets (appareil, session, services, caractéristiques ET leurs « …Result ») restent
/// référencés tant que la liaison vit : si le ramasse-miettes les libère, Windows cesse de livrer les notifications SANS
/// erreur ni déconnexion (constaté sur les manettes Zwift : trames qui s'arrêtent alors que les écritures fonctionnent encore).
/// </summary>
internal sealed class WinBleConnection : IBleConnection
{
    private readonly BluetoothLEDevice _device;
    private readonly GattSession _session;
    private readonly TimeSpan? _timeout;
    private readonly Dictionary<Guid, BleServiceInfo> _infos = [];
    private readonly Dictionary<Guid, GattDeviceService> _services = [];
    private readonly Dictionary<(Guid Service, Guid Char), GattCharacteristic> _chars = [];
    private readonly Dictionary<(Guid Service, Guid Char), TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>> _listening = [];
    private readonly List<object> _keepAlive = []; // GattCharacteristicsResult conservés volontairement
    /// <summary>
    /// Protège les tables ci-dessus : une découverte (lecture batterie en arrière-plan) peut tourner pendant qu'une boucle
    /// d'interrogation (L-TWOO) ou une vibration cherche sa caractéristique. Android et macOS sérialisent déjà leurs opérations.
    /// </summary>
    private readonly object _lock = new();
    /// <summary>Une découverte à la fois : deux découvertes simultanées interrogeraient deux fois le même service.</summary>
    private readonly SemaphoreSlim _discoverGate = new(1, 1);
    private GattDeviceServicesResult? _all;
    private bool _disposed;
    private int _disconnectRaised;

    public WinBleConnection(BluetoothLEDevice device, GattSession session, TimeSpan? timeout)
    {
        _device = device;
        _session = session;
        _timeout = timeout;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public bool IsConnected => !_disposed && _device.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public event Action? Disconnected;
    public event Action<Guid, Guid, byte[]>? ValueChanged;
    public event Action<string>? Log;

    public async Task<IReadOnlyList<BleServiceInfo>> DiscoverAsync(Func<Guid, bool>? serviceFilter = null, bool strict = false, CancellationToken ct = default)
    {
        ThrowIfClosed();
        await _discoverGate.WaitAsync(ct);
        try
        {
            // Copie locale : une fermeture concurrente remet _all à null pendant la découverte (« liaison fermée », pas de NRE).
            GattDeviceServicesResult? services;
            lock (_lock) services = _all;
            if (services is null)
            {
                var all = await With(_device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct), _timeout, Strings.Op_DiscoverServices);
                if (all.Status != GattCommunicationStatus.Success)
                    throw new IOException(Describe(Strings.Op_GattServicesUnavailable, all.Status, all.ProtocolError));
                lock (_lock)
                {
                    if (_disposed) throw new IOException(Strings.Err_LinkClosed);
                    _all = services = all;
                }
            }

            var result = new List<BleServiceInfo>();
            foreach (var svc in services.Services)
            {
                if (serviceFilter is not null && !serviceFilter(svc.Uuid)) continue;
                BleServiceInfo? info;
                lock (_lock) _infos.TryGetValue(svc.Uuid, out info);
                if (info is null)
                {
                    var cr = await With(svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct), _timeout, Loc.Fmt(Strings.Op_CharsOf, svc.Uuid));
                    if (cr.Status != GattCommunicationStatus.Success)
                    {
                        if (strict) throw new IOException(Describe(Loc.Fmt(Strings.Op_CharsUnavailable, svc.Uuid), cr.Status, cr.ProtocolError));
                        Log?.Invoke($"Service {svc.Uuid} ignoré ({cr.Status})");
                        continue;
                    }
                    info = new BleServiceInfo(svc.Uuid,
                        [.. cr.Characteristics.Select(c => new BleCharacteristicInfo(c.Uuid, (uint)c.CharacteristicProperties))]);
                    lock (_lock)
                    {
                        if (_disposed) throw new IOException(Strings.Err_LinkClosed);
                        _keepAlive.Add(cr);
                        _services[svc.Uuid] = svc;
                        foreach (var c in cr.Characteristics) _chars[(svc.Uuid, c.Uuid)] = c;
                        _infos[svc.Uuid] = info;
                    }
                }
                result.Add(info);
            }
            return result;
        }
        finally { _discoverGate.Release(); }
    }

    public async Task<byte[]> ReadAsync(Guid service, Guid characteristic)
    {
        var ch = Find(service, characteristic);
        var r = await With(ch.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(), _timeout, Loc.Fmt(Strings.Op_Read, characteristic));
        if (r.Status != GattCommunicationStatus.Success) throw new IOException(Describe(Strings.Op_ReadRefused, r.Status, r.ProtocolError));
        CryptographicBuffer.CopyToByteArray(r.Value, out byte[] data);
        return data;
    }

    public async Task WriteAsync(Guid service, Guid characteristic, byte[] value, bool withResponse)
    {
        var ch = Find(service, characteristic);
        var option = withResponse ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        var r = await With(ch.WriteValueWithResultAsync(CryptographicBuffer.CreateFromByteArray(value), option).AsTask(),
            _timeout, Loc.Fmt(Strings.Op_Write, characteristic));
        if (r.Status != GattCommunicationStatus.Success) throw new IOException(Describe(Strings.Op_WriteRefused, r.Status, r.ProtocolError));
    }

    public async Task SetNotifyAsync(Guid service, Guid characteristic, BleNotifyMode mode)
    {
        var ch = Find(service, characteristic);
        var key = (service, characteristic);

        // Le gestionnaire est posé AVANT d'activer les notifications pour ne perdre aucune trame.
        if (mode != BleNotifyMode.None)
        {
            TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler = (_, args) =>
            {
                CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out byte[] data);
                ValueChanged?.Invoke(service, characteristic, data);
            };
            bool added;
            lock (_lock) added = _listening.TryAdd(key, handler);
            if (added) ch.ValueChanged += handler;
        }

        var value = mode switch
        {
            BleNotifyMode.Notify => GattClientCharacteristicConfigurationDescriptorValue.Notify,
            BleNotifyMode.Indicate => GattClientCharacteristicConfigurationDescriptorValue.Indicate,
            _ => GattClientCharacteristicConfigurationDescriptorValue.None,
        };
        var status = await With(ch.WriteClientCharacteristicConfigurationDescriptorAsync(value).AsTask(), _timeout, Loc.Fmt(Strings.Op_Subscribe, characteristic));
        if (status != GattCommunicationStatus.Success) throw new IOException(Loc.Fmt(Strings.Err_SubscribeRefused, status));
    }

    // ---------- Internes ----------

    private GattCharacteristic Find(Guid service, Guid characteristic)
    {
        ThrowIfClosed();
        GattCharacteristic? c;
        lock (_lock) _chars.TryGetValue((service, characteristic), out c);
        return c ?? throw new IOException(Loc.Fmt(Strings.Err_UnknownCharacteristic, characteristic));
    }

    private void ThrowIfClosed()
    {
        if (_disposed) throw new IOException(Strings.Err_LinkClosed);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected || _disposed) return;
        if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0) Disconnected?.Invoke();
    }

    /// <summary>Les API WinRT n'ont pas de délai propre : on les borne quand l'appelant le demande.</summary>
    internal static async Task<T> With<T>(Task<T> operation, TimeSpan? timeout, string what)
    {
        if (timeout is null) return await operation;
        try { return await operation.WaitObservedAsync(timeout.Value); }
        catch (TimeoutException) { throw new IOException(Loc.Fmt(Strings.Err_Timeout, what)); }
    }

    private static string Describe(string what, GattCommunicationStatus status, byte? protocolError) => status switch
    {
        GattCommunicationStatus.Unreachable => Loc.Fmt(Strings.Gatt_Unreachable, what),
        GattCommunicationStatus.AccessDenied => Loc.Fmt(Strings.Gatt_AccessDenied, what),
        GattCommunicationStatus.ProtocolError => Loc.Fmt(Strings.Gatt_ProtocolError, what, protocolError ?? 0),
        _ => $"{what} ({status})",
    };

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            foreach (var (key, handler) in _listening)
                if (_chars.TryGetValue(key, out var c)) c.ValueChanged -= handler;
            if (_all is not null)
                foreach (var s in _all.Services) { try { s.Dispose(); } catch { } }
            _services.Clear(); _chars.Clear(); _listening.Clear(); _keepAlive.Clear(); _infos.Clear();
            _all = null;
        }
        try { _session.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
