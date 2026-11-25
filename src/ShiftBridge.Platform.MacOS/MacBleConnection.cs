using CoreBluetooth;
using Foundation;
using ShiftBridge.Core.Localization;
using ShiftBridge.Platform.Ble;

namespace ShiftBridge.Platform.MacOS;

internal sealed class MacBleConnection(MacBlePlatform owner, CBPeripheral peripheral, TimeSpan timeout)
    : CBPeripheralDelegate, IBleConnection
{
    private enum Operation { Services, Characteristics, Read, Write, Subscribe, WriteWithoutResponse }

    private sealed record PendingOperation(Operation Kind, NSObject? Target)
    {
        public TaskCompletionSource<object?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Native objects and callback state are owned by the central manager's serial queue.
    private readonly Dictionary<(Guid Service, Guid Characteristic), CBCharacteristic> _characteristics = [];
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _operations = new(1);
    private PendingOperation? _pending;
    private Action? _writeWhenReady;
    private volatile bool _isConnected;
    private bool _ended;

    public Task Connected => _connected.Task;
    public bool IsConnected => _isConnected;
    public event Action? Disconnected;
    public event Action<Guid, Guid, byte[]>? ValueChanged;
    public event Action<string>? Log;

    public void Open()
    {
        if (_ended) return;
        peripheral.Delegate = this;
        _isConnected = true;
        _connected.TrySetResult();
    }

    public void End(IOException error, bool voluntary = false)
    {
        if (_ended) return;
        _ended = true;
        bool notify = _isConnected && !voluntary;
        _isConnected = false;
        peripheral.WeakDelegate = null;
        _characteristics.Clear();
        _writeWhenReady = null;
        _pending?.Completion.TrySetException(error);
        _pending = null;
        _connected.TrySetException(error);
        if (notify) Disconnected?.Invoke();
    }

    public void Close(bool voluntary = true)
    {
        if (_ended) return;
        End(new IOException(Strings.Err_BleDeviceNotFound), voluntary);
        owner.Cancel(peripheral);
    }

    public async ValueTask DisposeAsync() => await owner.InvokeAsync(() =>
    {
        Close();
        return true;
    });

    public async Task<IReadOnlyList<BleServiceInfo>> DiscoverAsync(Func<Guid, bool>? serviceFilter = null,
        bool strict = false, CancellationToken ct = default)
    {
        await _operations.WaitAsync(ct);
        try
        {
            var services = (CBService[])(await RunAsync(Operation.Services, () => null,
                _ => peripheral.DiscoverServices(), ct))!;

            var result = new List<BleServiceInfo>();
            foreach (var service in services)
            {
                Guid id = MacBlePlatform.GattUuid(service.UUID);
                if (serviceFilter is not null && !serviceFilter(id)) continue;

                try
                {
                    var info = (BleServiceInfo)(await RunAsync(Operation.Characteristics, () => service,
                        _ => peripheral.DiscoverCharacteristics(service), ct))!;
                    result.Add(info);
                }
                catch (IOException ex) when (!strict && IsConnected)
                {
                    Log?.Invoke($"{id}: {ex.Message}");
                }
            }
            return result;
        }
        finally { _operations.Release(); }
    }

    public async Task<byte[]> ReadAsync(Guid service, Guid characteristic) =>
        (byte[])(await CharacteristicAsync(Operation.Read, service, characteristic, peripheral.ReadValue))!;

    public Task WriteAsync(Guid service, Guid characteristic, byte[] value, bool withResponse) =>
        CharacteristicAsync(withResponse ? Operation.Write : Operation.WriteWithoutResponse, service, characteristic, target =>
        {
            var type = withResponse ? CBCharacteristicWriteType.WithResponse : CBCharacteristicWriteType.WithoutResponse;
            void Send()
            {
                using var data = NSData.FromArray(value);
                peripheral.WriteValue(data, target, type);
                if (!withResponse) Complete(Operation.WriteWithoutResponse, target, null);
            }

            if (withResponse || peripheral.CanSendWriteWithoutResponse) Send();
            else _writeWhenReady = Send;
        });

    public Task SetNotifyAsync(Guid service, Guid characteristic, BleNotifyMode mode) =>
        CharacteristicAsync(Operation.Subscribe, service, characteristic, target =>
        {
            // Core Bluetooth selects notification/indication from the characteristic's properties.
            bool enabled = mode != BleNotifyMode.None;
            if (target.IsNotifying == enabled) Complete(Operation.Subscribe, target, null);
            else peripheral.SetNotifyValue(enabled, target);
        });

    private async Task<object?> CharacteristicAsync(Operation operation, Guid service, Guid characteristic,
        Action<CBCharacteristic> start)
    {
        await _operations.WaitAsync();
        try
        {
            return await RunAsync(operation,
                () => _characteristics.GetValueOrDefault((service, characteristic))
                    ?? throw new IOException(Loc.Fmt(Strings.Mac_BleCharacteristicMissing, characteristic)),
                target => start((CBCharacteristic)target!), CancellationToken.None);
        }
        finally { _operations.Release(); }
    }

    private async Task<object?> RunAsync(Operation operation, Func<NSObject?> findTarget, Action<NSObject?> start,
        CancellationToken ct)
    {
        var pending = await owner.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!_isConnected) throw new IOException(Strings.Err_BleDeviceNotFound);
            var request = new PendingOperation(operation, findTarget());
            _pending = request;
            try { start(request.Target); }
            catch
            {
                _pending = null;
                throw;
            }
            return request;
        });

        try { return await pending.Completion.Task.WaitAsync(timeout, ct); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Core Bluetooth cannot cancel individual GATT requests. A late callback from an abandoned
            // request could otherwise complete a subsequent request for the same attribute.
            await owner.InvokeAsync(() =>
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                    Close(voluntary: false);
                }
                return true;
            });
            if (ex is TimeoutException) throw new IOException(Strings.Mac_BleTimeout, ex);
            throw;
        }
    }

    private bool Complete(Operation operation, NSObject? target, NSError? error, object? result = null)
    {
        if (_pending is not { } pending || pending.Kind != operation || !Equals(pending.Target, target)) return false;
        _pending = null;
        if (error is null) pending.Completion.TrySetResult(result);
        else pending.Completion.TrySetException(new IOException(error.LocalizedDescription));
        return true;
    }

    public override void DiscoveredService(CBPeripheral remote, NSError? error) =>
        Complete(Operation.Services, null, error, remote.Services ?? Array.Empty<CBService>());

    public override void DiscoveredCharacteristics(CBPeripheral remote, CBService service, NSError? error)
    {
        if (_ended) return;
        Guid id = MacBlePlatform.GattUuid(service.UUID);
        var info = new List<BleCharacteristicInfo>();
        if (error is null)
        {
            RemoveCharacteristics(id);
            foreach (var ch in service.Characteristics ?? [])
            {
                var uuid = MacBlePlatform.GattUuid(ch.UUID);
                _characteristics[(id, uuid)] = ch;
                info.Add(new BleCharacteristicInfo(uuid, (uint)ch.Properties));
            }
        }
        Complete(Operation.Characteristics, service, error, new BleServiceInfo(id, info));
    }

    public override void ModifiedServices(CBPeripheral remote, CBService[] services)
    {
        foreach (var service in services) RemoveCharacteristics(MacBlePlatform.GattUuid(service.UUID));
    }

    private void RemoveCharacteristics(Guid service)
    {
        foreach (var key in _characteristics.Keys.Where(key => key.Service == service).ToArray())
            _characteristics.Remove(key);
    }

    public override void UpdatedCharacterteristicValue(CBPeripheral remote, CBCharacteristic characteristic, NSError? error)
    {
        if (_ended) return;
        byte[] value = error is null ? characteristic.Value?.ToArray() ?? [] : [];
        bool wasRead = Complete(Operation.Read, characteristic, error, value);
        // Reads and notifications use the same callback. Continue delivering values for subscribed attributes.
        if (error is null && (!wasRead || characteristic.IsNotifying) && characteristic.Service is { } service)
            ValueChanged?.Invoke(MacBlePlatform.GattUuid(service.UUID), MacBlePlatform.GattUuid(characteristic.UUID), value);
    }

    public override void WroteCharacteristicValue(CBPeripheral remote, CBCharacteristic characteristic, NSError? error) =>
        Complete(Operation.Write, characteristic, error);

    public override void UpdatedNotificationState(CBPeripheral remote, CBCharacteristic characteristic, NSError? error) =>
        Complete(Operation.Subscribe, characteristic, error);

    public override void IsReadyToSendWriteWithoutResponse(CBPeripheral remote)
    {
        var write = _writeWhenReady;
        _writeWhenReady = null;
        try { write?.Invoke(); }
        catch (Exception ex)
        {
            _pending?.Completion.TrySetException(ex);
            _pending = null;
        }
    }
}
