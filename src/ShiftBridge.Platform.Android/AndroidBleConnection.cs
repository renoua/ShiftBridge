using ShiftBridge.Core.Localization;
using System.Runtime.Versioning;
using Android.Bluetooth;
using Android.Content;
using ShiftBridge.Platform.Ble;

namespace ShiftBridge.Platform.Android;

internal sealed class AndroidBleCentral(Context context) : IBleCentral
{
    public async Task<IBleConnection> ConnectAsync(BleDeviceId address, BleConnectOptions options, CancellationToken ct = default)
    {
        if (address.MacAddress is not { } mac) throw new BleDeviceNotFoundException(Strings.Err_BtBadAddress);
        var adapter = AndroidBle.Adapter(context);
        if (adapter is null || !adapter.IsEnabled) throw new IOException(Strings.Err_BtOff);

        BluetoothDevice? device;
        try { device = adapter.GetRemoteDevice(BleAddress.ToText(mac)); }
        catch (Java.Lang.IllegalArgumentException) { device = null; }
        if (device is null) throw new BleDeviceNotFoundException(Strings.Err_BtBadAddress);

        var connection = new AndroidBleConnection(context, device, options.OperationTimeout ?? TimeSpan.FromSeconds(10));
        try { await connection.ConnectAsync(ct); }
        catch { await connection.DisposeAsync(); throw; }
        return connection;
    }
}

/// <summary>
/// Liaison GATT Android (rôle central). Particularités gérées ici :
///  - Android n'accepte qu'UNE opération GATT à la fois (lecture, écriture, descripteur, découverte) : tout passe par
///    <see cref="RunAsync"/>, sérialisé, et chaque opération est bornée par un délai ;
///  - un client GATT non fermé fuit un emplacement (limite d'environ 7) et provoque ensuite des erreurs 133 : on appelle
///    toujours Close() ;
///  - le cache GATT d'Android est vidé avant la découverte (équivalent du mode « sans cache » de Windows), sinon un Click v2
///    qui vient d'être déverrouillé pourrait garder l'ancienne liste de services ;
///  - les callbacks arrivent sur un thread du système : les TaskCompletionSource continuent en asynchrone.
/// </summary>
internal sealed class AndroidBleConnection : BluetoothGattCallback, IBleConnection
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private const int PreferredMtu = 247;

    private readonly Context _context;
    private readonly BluetoothDevice _device;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, BleServiceInfo> _infos = [];
    private BluetoothGatt? _gatt;
    private TaskCompletionSource<bool>? _connect;
    private TaskCompletionSource<GattStatus>? _op;
    private byte[]? _readValue;
    private volatile bool _connected;
    private volatile bool _disposed;
    private bool _discovered;
    private int _disconnectRaised;

    public AndroidBleConnection(Context context, BluetoothDevice device, TimeSpan timeout)
    {
        _context = context;
        _device = device;
        _timeout = timeout;
    }

    public bool IsConnected => !_disposed && _connected;

    public event Action? Disconnected;
    public event Action<Guid, Guid, byte[]>? ValueChanged;
    public event Action<string>? Log;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _connect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _gatt = _device.ConnectGatt(_context, false, this, BluetoothTransports.Le);
        }
        catch (Java.Lang.SecurityException)
        {
            throw new IOException(Strings.Err_PermConnect);
        }
        if (_gatt is null) throw new IOException(Strings.Err_GattOpenRefused);

        using (ct.Register(() => _connect.TrySetCanceled(ct)))
        {
            try { await _connect.Task.WaitAsync(ConnectTimeout); }
            catch (TimeoutException) { throw new BleDeviceNotFoundException(Strings.Err_ConnectTimeout); }
        }

        // Priorité haute : intervalle de connexion court (latence des boutons) ; MTU large (App Info, trames Zwift).
        try { _gatt.RequestConnectionPriority(GattConnectionPriority.High); } catch { /* optionnel */ }
        try { await RunAsync(() => _gatt!.RequestMtu(PreferredMtu), Strings.Op_NegotiateMtu); }
        catch (IOException) { /* certains appareils ne répondent pas : le MTU par défaut convient */ }
    }

    public async Task<IReadOnlyList<BleServiceInfo>> DiscoverAsync(Func<Guid, bool>? serviceFilter = null, bool strict = false, CancellationToken ct = default)
    {
        ThrowIfClosed();
        if (!_discovered)
        {
            RefreshCache(_gatt!);
            var status = await RunAsync(() => _gatt!.DiscoverServices(), Strings.Op_DiscoverServices);
            if (status != GattStatus.Success) throw new IOException(Loc.Fmt(Strings.Err_GattServicesStatus, (int)status));
            _discovered = true;
        }

        var result = new List<BleServiceInfo>();
        foreach (var svc in _gatt!.Services ?? [])
        {
            var uuid = AndroidBle.ToGuid(svc.Uuid!);
            if (serviceFilter is not null && !serviceFilter(uuid)) continue;
            if (!_infos.TryGetValue(uuid, out var info))
            {
                info = new BleServiceInfo(uuid, [.. (svc.Characteristics ?? [])
                    .Select(c => new BleCharacteristicInfo(AndroidBle.ToGuid(c.Uuid!), (uint)(int)c.Properties))]);
                _infos[uuid] = info;
            }
            result.Add(info);
        }
        return result;
    }

    public async Task<byte[]> ReadAsync(Guid service, Guid characteristic)
    {
        var ch = Find(service, characteristic);
        _readValue = null;
        var status = await RunAsync(() => _gatt!.ReadCharacteristic(ch), Loc.Fmt(Strings.Op_Read, characteristic));
        if (status != GattStatus.Success) throw new IOException(Loc.Fmt(Strings.Err_ReadStatus, (int)status));
        return _readValue ?? [];
    }

    public async Task WriteAsync(Guid service, Guid characteristic, byte[] value, bool withResponse)
    {
        var ch = Find(service, characteristic);
        var type = withResponse ? GattWriteType.Default : GattWriteType.NoResponse;
        var status = await RunAsync(() => WriteCharacteristic(ch, value, type), Loc.Fmt(Strings.Op_Write, characteristic));
        if (status != GattStatus.Success) throw new IOException(Loc.Fmt(Strings.Err_WriteStatus, (int)status));
    }

    public async Task SetNotifyAsync(Guid service, Guid characteristic, BleNotifyMode mode)
    {
        var ch = Find(service, characteristic);
        // Côté local d'abord : aucune trame n'est perdue entre l'activation distante et l'abonnement.
        if (!_gatt!.SetCharacteristicNotification(ch, mode != BleNotifyMode.None))
            throw new IOException(Loc.Fmt(Strings.Err_LocalSubscribeRefused, characteristic));

        var cccd = ch.GetDescriptor(AndroidBle.ToUuid(AndroidBle.Cccd))
                   ?? throw new IOException(Loc.Fmt(Strings.Err_NoCccd, characteristic));
        byte[] value = mode switch
        {
            BleNotifyMode.Notify => [0x01, 0x00],
            BleNotifyMode.Indicate => [0x02, 0x00],
            _ => [0x00, 0x00],
        };
        var status = await RunAsync(() => WriteDescriptor(cccd, value), Loc.Fmt(Strings.Op_Subscribe, characteristic));
        if (status != GattStatus.Success) throw new IOException(Loc.Fmt(Strings.Err_SubscribeStatus, (int)status));
    }

    // ---------- Opérations Android (API 33+ et anciennes) ----------

    private bool WriteCharacteristic(BluetoothGattCharacteristic ch, byte[] value, GattWriteType type)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            return _gatt!.WriteCharacteristic(ch, value, (int)type) == 0; // BluetoothStatusCodes.SUCCESS
#pragma warning disable CA1422 // API dépréciée, seule disponible avant Android 13
        ch.WriteType = type;
        ch.SetValue(value);
        return _gatt!.WriteCharacteristic(ch);
#pragma warning restore CA1422
    }

    private bool WriteDescriptor(BluetoothGattDescriptor descriptor, byte[] value)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            return _gatt!.WriteDescriptor(descriptor, value) == 0;
#pragma warning disable CA1422
        descriptor.SetValue(value);
        return _gatt!.WriteDescriptor(descriptor);
#pragma warning restore CA1422
    }

    /// <summary>Vide le cache GATT du système (méthode cachée refresh()) ; sans effet si indisponible.</summary>
    private void RefreshCache(BluetoothGatt gatt)
    {
        try
        {
            var method = Java.Lang.Class.FromType(typeof(BluetoothGatt)).GetMethod("refresh");
            method?.Invoke(gatt);
        }
        catch (Exception ex) { Log?.Invoke($"Cache GATT non vidé : {ex.Message}"); }
    }

    // ---------- Sérialisation des opérations ----------

    private async Task<GattStatus> RunAsync(Func<bool> start, string what)
    {
        ThrowIfClosed();
        await _gate.WaitAsync();
        try
        {
            ThrowIfClosed();
            var tcs = new TaskCompletionSource<GattStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            _op = tcs;
            bool started;
            try { started = start(); }
            catch (Java.Lang.SecurityException) { throw new IOException(Strings.Err_PermConnect); }
            if (!started) throw new IOException(Loc.Fmt(Strings.Err_OpRefused, what));
            try { return await tcs.Task.WaitAsync(_timeout); }
            catch (TimeoutException) { throw new IOException(Loc.Fmt(Strings.Err_Timeout, what)); }
        }
        finally
        {
            _op = null;
            _gate.Release();
        }
    }

    private BluetoothGattCharacteristic Find(Guid service, Guid characteristic)
    {
        ThrowIfClosed();
        var ch = _gatt!.GetService(AndroidBle.ToUuid(service))?.GetCharacteristic(AndroidBle.ToUuid(characteristic));
        return ch ?? throw new IOException(Loc.Fmt(Strings.Err_UnknownCharacteristic, characteristic));
    }

    private void ThrowIfClosed()
    {
        if (_disposed || _gatt is null) throw new IOException(Strings.Err_LinkClosed);
    }

    // ---------- Callbacks du système ----------

    public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
    {
        if (newState == ProfileState.Connected)
        {
            _connected = true;
            _connect?.TrySetResult(true);
            return;
        }
        if (newState != ProfileState.Disconnected) return;

        bool wasConnected = _connected;
        _connected = false;
        _connect?.TrySetException(new BleDeviceNotFoundException(Loc.Fmt(Strings.Err_ConnectStatus, (int)status)));
        _op?.TrySetException(new IOException(Strings.Err_LinkLost));
        if (wasConnected && !_disposed && Interlocked.Exchange(ref _disconnectRaised, 1) == 0) Disconnected?.Invoke();
    }

    public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status) => _op?.TrySetResult(status);
    public override void OnMtuChanged(BluetoothGatt? gatt, int mtu, GattStatus status) => _op?.TrySetResult(status);
    public override void OnCharacteristicWrite(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status) => _op?.TrySetResult(status);
    public override void OnDescriptorWrite(BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, GattStatus status) => _op?.TrySetResult(status);

    [SupportedOSPlatform("android33.0")]
    public override void OnCharacteristicRead(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value, GattStatus status)
    {
        _readValue = value;
        _op?.TrySetResult(status);
    }

#pragma warning disable CA1422 // surcharge pour Android < 13 : le système n'appelle que l'une des deux
    public override void OnCharacteristicRead(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
    {
        _readValue = characteristic?.GetValue();
        _op?.TrySetResult(status);
    }

    public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
    {
        if (characteristic?.GetValue() is { } value) Dispatch(characteristic, value);
    }
#pragma warning restore CA1422

    [SupportedOSPlatform("android33.0")]
    public override void OnCharacteristicChanged(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value) =>
        Dispatch(characteristic, value);

    private void Dispatch(BluetoothGattCharacteristic characteristic, byte[] value)
    {
        if (_disposed || characteristic.Service?.Uuid is not { } svc || characteristic.Uuid is not { } ch) return;
        ValueChanged?.Invoke(AndroidBle.ToGuid(svc), AndroidBle.ToGuid(ch), value);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _connect?.TrySetCanceled();
        _op?.TrySetException(new IOException(Strings.Err_LinkClosed));
        var gatt = _gatt;
        _gatt = null;
        // Disconnect() puis Close() : sans Close(), l'emplacement GATT fuit et les connexions suivantes échouent (statut 133).
        try { gatt?.Disconnect(); } catch { }
        try { gatt?.Close(); } catch { }
        _infos.Clear();
        return ValueTask.CompletedTask;
    }
}
