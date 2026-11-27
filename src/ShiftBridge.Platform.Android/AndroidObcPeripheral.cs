using ShiftBridge.Core.Localization;
using System.Runtime.Versioning;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using ShiftBridge.Core.Obc;
using ShiftBridge.Platform.Obc;

namespace ShiftBridge.Platform.Android;

/// <summary>
/// OBC en BLE sous Android : le téléphone annonce le service d273f680-… (Button State, Haptic, App Info) et la batterie (180F)
/// via un BluetoothGattServer. Nécessite le multi-advertising (voir <see cref="IsSupported"/>) et, depuis Android 12, les
/// permissions BLUETOOTH_CONNECT et BLUETOOTH_ADVERTISE. Inutile si l'appli d'entraînement tourne sur ce même appareil :
/// utiliser alors OBC réseau (localhost).
/// </summary>
internal sealed class AndroidObcPeripheral(Context context) : IObcTransport
{
    private static readonly Guid BatteryService = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevel = new("00002a19-0000-1000-8000-00805f9b34fb");

    private readonly object _lock = new();
    private readonly Dictionary<string, BluetoothDevice> _buttonSubscribers = [];
    private readonly Dictionary<string, BluetoothDevice> _batterySubscribers = [];
    private readonly ObcAppInfoReassembler _appInfo = new();
    /// <summary>Écritures longues (« prepared writes ») en cours, par client : fragments placés à leur offset, appliqués à l'Execute Write.</summary>
    private readonly Dictionary<string, (Guid Characteristic, byte[] Data)> _preparedWrites = [];
    /// <summary>Notifications restées sans accusé, par client : au-delà de <see cref="MaxMissedNotifications"/>, le client est considéré parti.</summary>
    private readonly Dictionary<string, int> _missedNotifications = [];
    private const int MaxMissedNotifications = 5;
    private static readonly TimeSpan NotificationAckTimeout = TimeSpan.FromMilliseconds(150);
    private readonly SemaphoreSlim _notifyGate = new(1, 1);
    private ServerCallback? _callback;
    private BluetoothGattServer? _server;
    private BluetoothGattCharacteristic? _buttonState;
    private BluetoothGattCharacteristic? _batteryLevel;
    private BluetoothLeAdvertiser? _advertiser;
    private AdvertiseHandler? _advertise;
    private TaskCompletionSource<bool>? _serviceAdded;
    private TaskCompletionSource<bool>? _notificationSent;
    private byte[] _lastButtonState = [ObcMessages.TypeButtonState];
    private byte _batteryValue = 100;
    private bool _batterySet;

    public string Name => "OBC Bluetooth";
    public bool HasClient { get { lock (_lock) return _buttonSubscribers.Count > 0; } }
    public ObcAppInfo? CurrentAppInfo { get; private set; }
    /// <summary>Nom Bluetooth du téléphone, placé dans la réponse de scan : c'est lui que l'appli d'entraînement affiche.</summary>
    public string? AdvertisedName { get; private set; }

    public event Action<ObcAppInfo>? AppInfoReceived;
    public event Action<ObcHaptic>? HapticReceived;
    public event Action? ClientsChanged;
    public event Action<string>? Log;

    /// <summary>Vrai si l'appareil sait annoncer un service BLE (multi-advertising).</summary>
    public static bool IsSupported(Context context)
    {
        var adapter = AndroidBle.Adapter(context);
        return adapter is { IsEnabled: true, IsMultipleAdvertisementSupported: true } && adapter.BluetoothLeAdvertiser is not null;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!IsSupported(context))
            throw new NotSupportedException(Strings.Err_NoPeripheralRoleAndroid);

        var manager = (BluetoothManager)context.GetSystemService(Context.BluetoothService)!;
        _callback = new ServerCallback(this);
        try { _server = manager.OpenGattServer(context, _callback); }
        catch (Java.Lang.SecurityException) { throw new IOException(Strings.Err_PermConnect); }
        if (_server is null) throw new IOException(Strings.Err_GattServerRefused);

        // --- Service OBC ---
        var obc = new BluetoothGattService(AndroidBle.ToUuid(ObcGattUuids.Service), GattServiceType.Primary);
        _buttonState = NewCharacteristic(ObcGattUuids.ButtonState, GattProperty.Read | GattProperty.Notify, GattPermission.Read, withCccd: true);
        obc.AddCharacteristic(_buttonState);
        obc.AddCharacteristic(NewCharacteristic(ObcGattUuids.Haptic, GattProperty.Write | GattProperty.WriteNoResponse, GattPermission.Write));
        obc.AddCharacteristic(NewCharacteristic(ObcGattUuids.AppInfo, GattProperty.Write | GattProperty.WriteNoResponse, GattPermission.Write));
        await AddServiceAsync(obc, ct);

        // --- Service batterie (optionnel) ---
        var battery = new BluetoothGattService(AndroidBle.ToUuid(BatteryService), GattServiceType.Primary);
        _batteryLevel = NewCharacteristic(BatteryLevel, GattProperty.Read | GattProperty.Notify, GattPermission.Read, withCccd: true);
        battery.AddCharacteristic(_batteryLevel);
        try { await AddServiceAsync(battery, ct); }
        catch (IOException ex) { _batteryLevel = null; Log?.Invoke($"Service batterie ignoré : {ex.Message}"); }

        await StartAdvertisingAsync(ct);
        try { AdvertisedName = AndroidBle.Adapter(context)?.Name; }
        catch (Java.Lang.SecurityException) { AdvertisedName = null; }
        Log?.Invoke($"OBC BLE : annonce du service d273f680 démarrée, sous le nom « {AdvertisedName ?? "?"} »");
    }

    private static BluetoothGattCharacteristic NewCharacteristic(Guid uuid, GattProperty props, GattPermission perms, bool withCccd = false)
    {
        var ch = new BluetoothGattCharacteristic(AndroidBle.ToUuid(uuid), props, perms);
        if (withCccd)
            ch.AddDescriptor(new BluetoothGattDescriptor(AndroidBle.ToUuid(AndroidBle.Cccd), GattDescriptorPermission.Read | GattDescriptorPermission.Write));
        return ch;
    }

    /// <summary>Android n'accepte qu'un AddService à la fois : on attend OnServiceAdded avant d'ajouter le suivant.</summary>
    private async Task AddServiceAsync(BluetoothGattService service, CancellationToken ct)
    {
        _serviceAdded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_server!.AddService(service)) throw new IOException(Loc.Fmt(Strings.Err_ServiceCreateRefused, service.Uuid));
        using var reg = ct.Register(() => _serviceAdded.TrySetCanceled(ct));
        try { await _serviceAdded.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { throw new IOException(Loc.Fmt(Strings.Err_ServiceCreateTimeout, service.Uuid)); }
    }

    private async Task StartAdvertisingAsync(CancellationToken ct)
    {
        _advertiser = AndroidBle.Adapter(context)?.BluetoothLeAdvertiser
                      ?? throw new IOException(Strings.Err_AdvUnavailable);
        var settings = new AdvertiseSettings.Builder()!
            .SetAdvertiseMode(AdvertiseMode.LowLatency)!
            .SetTxPowerLevel(AdvertiseTx.PowerHigh)!
            .SetConnectable(true)!
            .SetTimeout(0)!
            .Build()!;
        // L'UUID 128 bits occupe presque tout le paquet d'annonce : le nom part dans la réponse de scan.
        var data = new AdvertiseData.Builder()!
            .SetIncludeDeviceName(false)!
            .AddServiceUuid(new ParcelUuid(AndroidBle.ToUuid(ObcGattUuids.Service)))!
            .Build()!;
        var scanResponse = new AdvertiseData.Builder()!.SetIncludeDeviceName(true)!.Build()!;

        var failure = await TryStartAdvertisingAsync(settings, data, scanResponse, ct);
        if (failure == AdvertiseFailure.DataTooLarge)
        {
            // Nom du téléphone trop long pour la réponse de scan (29 octets) : Android ne le tronque pas, il refuse tout.
            // On annonce alors sans nom : l'appli voit le service OBC, sous un nom générique.
            Log?.Invoke("OBC BLE : nom du téléphone trop long pour l'annonce, annonce sans nom.");
            failure = await TryStartAdvertisingAsync(settings, data, new AdvertiseData.Builder()!.Build()!, ct);
        }
        if (failure is { } f) throw new IOException(Loc.Fmt(Strings.Err_AdvRefused, f));
    }

    /// <returns>null si l'annonce a démarré, sinon le code d'échec d'Android.</returns>
    private async Task<AdvertiseFailure?> TryStartAdvertisingAsync(AdvertiseSettings settings, AdvertiseData data, AdvertiseData scanResponse, CancellationToken ct)
    {
        _advertise = new AdvertiseHandler();
        try { _advertiser!.StartAdvertising(settings, data, scanResponse, _advertise); }
        catch (Java.Lang.SecurityException) { throw new IOException(Strings.Err_PermAdvertise); }

        using var reg = ct.Register(() => _advertise.Started.TrySetCanceled(ct));
        try { return await _advertise.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { throw new IOException(Strings.Err_AdvStartTimeout); }
    }

    public Task StopAsync()
    {
        try { if (_advertise is not null) _advertiser?.StopAdvertising(_advertise); } catch { }
        try { _server?.Close(); } catch { }
        _advertise = null; _advertiser = null; _server = null; _callback = null;
        _buttonState = null; _batteryLevel = null;
        lock (_lock) { _buttonSubscribers.Clear(); _batterySubscribers.Clear(); _preparedWrites.Clear(); _missedNotifications.Clear(); }
        _appInfo.Clear();
        CurrentAppInfo = null;
        AdvertisedName = null;
        ClientsChanged?.Invoke();
        return Task.CompletedTask;
    }

    public async Task SendButtonStateAsync(byte[] message)
    {
        _lastButtonState = message;
        BluetoothDevice[] targets;
        lock (_lock) targets = [.. _buttonSubscribers.Values];
        if (_server is null || _buttonState is null || targets.Length == 0) return;

        foreach (var device in targets)
        {
            try { await NotifyAsync(device, _buttonState, message); }
            catch (Exception ex) { Log?.Invoke($"Notification BLE échouée : {ex.Message}"); }
        }
    }

    public void SetBattery(int? percent)
    {
        if (percent is not (>= 0 and <= 100)) return;
        if (_batterySet && _batteryValue == percent.Value) return; // inchangée : aucune notification Bluetooth
        _batteryValue = (byte)percent.Value;
        _batterySet = true;
        BluetoothDevice[] targets;
        lock (_lock) targets = [.. _batterySubscribers.Values];
        if (_batteryLevel is null || targets.Length == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (var d in targets)
            {
                try { await NotifyAsync(d, _batteryLevel, [_batteryValue]); } catch { /* best-effort */ }
            }
        });
    }

    /// <summary>Une notification à la fois : Android refuse la suivante tant qu'OnNotificationSent n'est pas arrivé.</summary>
    private async Task NotifyAsync(BluetoothDevice device, BluetoothGattCharacteristic ch, byte[] value)
    {
        await _notifyGate.WaitAsync();
        try
        {
            var server = _server;
            if (server is null) return;
            _notificationSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool ok;
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                ok = server.NotifyCharacteristicChanged(device, ch, false, value) == 0;
            else
            {
#pragma warning disable CA1422
                ch.SetValue(value);
                ok = server.NotifyCharacteristicChanged(device, ch, false);
#pragma warning restore CA1422
            }
            if (!ok) throw new IOException(Strings.Err_NotifyRefused);
            try
            {
                await _notificationSent.Task.WaitAsync(NotificationAckTimeout);
                lock (_lock) _missedNotifications.Remove(device.Address!);
            }
            catch (TimeoutException)
            {
                // Pas d'accusé : le client a peut-être disparu sans déconnexion signalée. Après plusieurs ratés d'affilée,
                // on cesse de l'attendre, pour qu'il ne retarde plus chaque appui destiné aux autres clients.
                bool drop;
                lock (_lock)
                {
                    int missed = _missedNotifications.GetValueOrDefault(device.Address!) + 1;
                    _missedNotifications[device.Address!] = missed;
                    drop = missed >= MaxMissedNotifications;
                }
                if (drop) DropSubscriber(device.Address!, "plus de réponse aux notifications");
            }
        }
        finally { _notifyGate.Release(); }
    }

    private void DropSubscriber(string address, string reason)
    {
        bool changed;
        int count;
        lock (_lock)
        {
            changed = _buttonSubscribers.Remove(address);
            _batterySubscribers.Remove(address);
            _missedNotifications.Remove(address);
            count = _buttonSubscribers.Count;
        }
        if (changed) { Log?.Invoke($"OBC BLE : client {address} retiré ({reason}), {count} abonné(s)"); ClientsChanged?.Invoke(); }
    }

    // ---------- Requêtes des clients ----------

    private void OnConnectionChanged(BluetoothDevice device, ProfileState state)
    {
        if (state != ProfileState.Disconnected) return;
        bool changed;
        int count;
        lock (_lock)
        {
            changed = _buttonSubscribers.Remove(device.Address!);
            _batterySubscribers.Remove(device.Address!);
            _preparedWrites.Remove(device.Address!);
            _missedNotifications.Remove(device.Address!);
            count = _buttonSubscribers.Count;
        }
        _appInfo.Remove(device.Address!);
        if (changed) { Log?.Invoke($"OBC BLE : {count} abonné(s)"); ClientsChanged?.Invoke(); }
    }

    private void OnRead(BluetoothDevice device, int requestId, int offset, BluetoothGattCharacteristic characteristic)
    {
        var uuid = AndroidBle.ToGuid(characteristic.Uuid!);
        byte[] value = uuid == BatteryLevel ? [_batteryValue] : _lastButtonState;
        byte[] slice = offset >= value.Length ? [] : value[offset..];
        _server?.SendResponse(device, requestId, GattStatus.Success, offset, slice);
    }

    private void OnWrite(BluetoothDevice device, int requestId, BluetoothGattCharacteristic characteristic, bool preparedWrite,
        bool responseNeeded, int offset, byte[] value)
    {
        if (responseNeeded) _server?.SendResponse(device, requestId, GattStatus.Success, offset, value);

        var uuid = AndroidBle.ToGuid(characteristic.Uuid!);
        if (preparedWrite)
        {
            // Écriture longue : le fragment est placé à son offset et ne sera appliqué qu'à l'Execute Write (OnExecuteWrite).
            lock (_lock)
            {
                var data = _preparedWrites.TryGetValue(device.Address!, out var pending) && pending.Characteristic == uuid ? pending.Data : [];
                if (offset < 0 || offset > 512) return; // longueur maximale d'un attribut ATT
                if (data.Length < offset + value.Length) Array.Resize(ref data, offset + value.Length);
                value.CopyTo(data, offset);
                _preparedWrites[device.Address!] = (uuid, data);
            }
            return;
        }
        ApplyWrite(device, uuid, value);
    }

    private void OnExecuteWrite(BluetoothDevice device, int requestId, bool execute)
    {
        (Guid Characteristic, byte[] Data) pending;
        bool has;
        lock (_lock)
        {
            has = _preparedWrites.Remove(device.Address!, out pending);
        }
        // Le client attend la réponse à l'Execute Write : sans elle, il coupe la liaison au bout de 30 s (délai ATT).
        _server?.SendResponse(device, requestId, GattStatus.Success, 0, []);
        if (execute && has) ApplyWrite(device, pending.Characteristic, pending.Data);
    }

    private void ApplyWrite(BluetoothDevice device, Guid uuid, byte[] value)
    {
        if (uuid == ObcGattUuids.Haptic)
        {
            if (ObcMessages.TryDecodeHaptic(value, out var h)) HapticReceived?.Invoke(h);
        }
        else if (uuid == ObcGattUuids.AppInfo)
        {
            // App Info dépasse souvent la MTU : on réassemble les écritures successives de ce client.
            foreach (var info in _appInfo.Feed(device.Address!, value, System.Environment.TickCount64))
            {
                CurrentAppInfo = info;
                Log?.Invoke($"App Info : {info.AppId} {info.AppVersion}");
                AppInfoReceived?.Invoke(info);
            }
        }
    }

    private void OnDescriptorWrite(BluetoothDevice device, int requestId, BluetoothGattDescriptor descriptor, bool responseNeeded, int offset, byte[] value)
    {
        if (responseNeeded) _server?.SendResponse(device, requestId, GattStatus.Success, offset, value);
        if (AndroidBle.ToGuid(descriptor.Uuid!) != AndroidBle.Cccd) return;

        bool enabled = value.Length > 0 && (value[0] & 0x03) != 0;
        var owner = AndroidBle.ToGuid(descriptor.Characteristic!.Uuid!);
        var map = owner == BatteryLevel ? _batterySubscribers : _buttonSubscribers;
        bool changed;
        lock (_lock) changed = enabled ? map.TryAdd(device.Address!, device) : map.Remove(device.Address!);
        if (changed && map == _buttonSubscribers)
        {
            Log?.Invoke($"OBC BLE : {_buttonSubscribers.Count} abonné(s)");
            ClientsChanged?.Invoke();
        }
    }

    private void OnDescriptorRead(BluetoothDevice device, int requestId, int offset, BluetoothGattDescriptor descriptor)
    {
        var owner = AndroidBle.ToGuid(descriptor.Characteristic!.Uuid!);
        var map = owner == BatteryLevel ? _batterySubscribers : _buttonSubscribers;
        bool on;
        lock (_lock) on = map.ContainsKey(device.Address!);
        byte[] value = on ? [0x01, 0x00] : [0x00, 0x00];
        _server?.SendResponse(device, requestId, GattStatus.Success, offset, offset >= value.Length ? [] : value[offset..]);
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private sealed class ServerCallback(AndroidObcPeripheral owner) : BluetoothGattServerCallback
    {
        public override void OnServiceAdded(GattStatus status, BluetoothGattService? service)
        {
            if (status == GattStatus.Success) owner._serviceAdded?.TrySetResult(true);
            else owner._serviceAdded?.TrySetException(new IOException(Loc.Fmt(Strings.Err_ServiceRefusedStatus, (int)status)));
        }

        public override void OnConnectionStateChange(BluetoothDevice? device, ProfileState status, ProfileState newState)
        {
            if (device is not null) owner.OnConnectionChanged(device, newState);
        }

        public override void OnCharacteristicReadRequest(BluetoothDevice? device, int requestId, int offset, BluetoothGattCharacteristic? characteristic)
        {
            if (device is not null && characteristic is not null) owner.OnRead(device, requestId, offset, characteristic);
        }

        public override void OnCharacteristicWriteRequest(BluetoothDevice? device, int requestId, BluetoothGattCharacteristic? characteristic,
            bool preparedWrite, bool responseNeeded, int offset, byte[]? value)
        {
            if (device is not null && characteristic is not null && value is not null)
                owner.OnWrite(device, requestId, characteristic, preparedWrite, responseNeeded, offset, value);
        }

        public override void OnExecuteWrite(BluetoothDevice? device, int requestId, bool execute)
        {
            if (device is not null) owner.OnExecuteWrite(device, requestId, execute);
        }

        public override void OnDescriptorWriteRequest(BluetoothDevice? device, int requestId, BluetoothGattDescriptor? descriptor,
            bool preparedWrite, bool responseNeeded, int offset, byte[]? value)
        {
            if (device is not null && descriptor is not null && value is not null)
                owner.OnDescriptorWrite(device, requestId, descriptor, responseNeeded, offset, value);
        }

        public override void OnDescriptorReadRequest(BluetoothDevice? device, int requestId, int offset, BluetoothGattDescriptor? descriptor)
        {
            if (device is not null && descriptor is not null) owner.OnDescriptorRead(device, requestId, offset, descriptor);
        }

        public override void OnNotificationSent(BluetoothDevice? device, GattStatus status) =>
            owner._notificationSent?.TrySetResult(true);
    }

    private sealed class AdvertiseHandler : AdvertiseCallback
    {
        /// <summary>null quand l'annonce a démarré, sinon le code d'échec.</summary>
        public TaskCompletionSource<AdvertiseFailure?> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void OnStartSuccess(AdvertiseSettings? settingsInEffect) => Started.TrySetResult(null);

        public override void OnStartFailure(AdvertiseFailure errorCode) => Started.TrySetResult(errorCode);
    }
}
