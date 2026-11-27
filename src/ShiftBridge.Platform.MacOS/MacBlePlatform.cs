using System.Buffers.Binary;
using CoreBluetooth;
using CoreFoundation;
using Foundation;
using ShiftBridge.Core.Localization;
using ShiftBridge.Platform.Ble;
using ShiftBridge.Platform.Obc;

namespace ShiftBridge.Platform.MacOS;

public enum MacBluetoothState { Starting, Available, Off, Unauthorized, Unsupported }

public sealed class MacBlePlatform : IBlePlatform, IBleCentral, IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly DispatchQueue _queue = new("ShiftBridge.CoreBluetooth");
    private CBCentralManager _manager = null!;
    private readonly CentralDelegate _delegate;
    private readonly Scanner _scanner;
    private readonly Dictionary<Guid, MacBleConnection> _connections = [];
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile MacBluetoothState _state;
    private bool _disposed;

    public MacBlePlatform()
    {
        _scanner = new Scanner(this);
        _delegate = new CentralDelegate(this);
        // Assign the manager on its callback queue before its first state callback can run.
        _queue.DispatchSync(() => _manager = new CBCentralManager(_delegate, _queue,
            new CBCentralInitOptions { ShowPowerAlert = false }));
    }

    public MacBluetoothState State => _state;
    public event Action? StateChanged;
    public IBleCentral Central => this;
    public IBleScanner CreateScanner() => _scanner;
    public Task<bool> IsPeripheralSupportedAsync() => Task.FromResult(false);
    public IObcTransport CreateObcPeripheral() => throw new NotSupportedException(Strings.Err_BleUnavailablePlatform);

    public async Task<IBleConnection> ConnectAsync(BleDeviceId address, BleConnectOptions options, CancellationToken ct = default)
    {
        if (address.PeerUuid is not { } id) throw new BleDeviceNotFoundException(Strings.Err_BtBadAddress);

        var ready = await InvokeAsync(() => _ready.Task);
        try { await ready.WaitAsync(ConnectTimeout, ct); }
        catch (TimeoutException ex) { throw new IOException(Strings.Err_BtOff, ex); }

        var connection = await InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureAvailable();
            if (_connections.ContainsKey(id))
                throw new IOException(Loc.Fmt(Strings.Mac_BleConnectionBusy, address));

            using var identifier = new NSUuid(id.ToString("D"));
            var peripheral = _manager.RetrievePeripheralsWithIdentifiers([identifier]).FirstOrDefault()
                ?? throw new BleDeviceNotFoundException(Strings.Err_BleDeviceNotFound);
            var link = new MacBleConnection(this, peripheral, options.OperationTimeout ?? ConnectTimeout);
            _connections.Add(id, link);
            try { _manager.ConnectPeripheral(peripheral); }
            catch
            {
                _connections.Remove(id);
                throw;
            }
            return link;
        });

        try
        {
            await connection.Connected.WaitAsync(options.OperationTimeout ?? ConnectTimeout, ct);
            return connection;
        }
        catch (TimeoutException)
        {
            await connection.DisposeAsync();
            throw new BleDeviceNotFoundException(Strings.Err_BleDeviceNotFound);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal Task<T> InvokeAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.DispatchAsync(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }

    /// <summary>
    /// Ferme la liaison et oublie aussitôt l'appareil : CoreBluetooth n'envoie pas toujours de « déconnecté » pour une connexion
    /// annulée avant d'aboutir, et l'entrée restée dans la table rendait l'appareil « occupé » jusqu'au redémarrage.
    /// </summary>
    internal void Cancel(CBPeripheral peripheral)
    {
        _connections.Remove(PeerId(peripheral));
        _manager.CancelPeripheralConnection(peripheral);
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != MacBluetoothState.Available)
            throw new IOException(_state switch
            {
                MacBluetoothState.Unauthorized => Strings.Mac_BluetoothDenied,
                MacBluetoothState.Unsupported => Strings.Err_BleUnavailablePlatform,
                _ => Strings.Err_BtOff
            });
    }

    private void UpdateState(CBCentralManager central)
    {
        if (_disposed) return;

        var state = central.State switch
        {
            CBManagerState.PoweredOn => MacBluetoothState.Available,
            CBManagerState.PoweredOff => MacBluetoothState.Off,
            CBManagerState.Unauthorized => MacBluetoothState.Unauthorized,
            CBManagerState.Unsupported => MacBluetoothState.Unsupported,
            _ => MacBluetoothState.Starting
        };
        if (state == MacBluetoothState.Starting)
        {
            if (_ready.Task.IsCompleted) _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        else _ready.TrySetResult();

        bool changed = state != _state;
        _state = state;
        _scanner.Update(central);
        if (state != MacBluetoothState.Available)
        {
            var interrupted = _connections.Values.ToArray();
            _connections.Clear();
            foreach (var link in interrupted) link.End(new IOException(Strings.Err_BtOff));
        }
        if (changed) StateChanged?.Invoke();
    }

    private void Finish(CBPeripheral peripheral, NSError? error, bool failedToConnect)
    {
        if (_connections.Remove(PeerId(peripheral), out var link))
            link.End(failedToConnect
                ? new BleDeviceNotFoundException(error?.LocalizedDescription ?? Strings.Err_BleDeviceNotFound)
                : new IOException(error?.LocalizedDescription ?? Strings.Err_BleDeviceNotFound));
    }

    public async ValueTask DisposeAsync()
    {
        await InvokeAsync(() =>
        {
            if (_disposed) return false;
            _disposed = true;
            _ready.TrySetResult();
            if (_manager.IsScanning) _manager.StopScan();
            foreach (var link in _connections.Values.ToArray()) link.Close();
            _connections.Clear();
            _manager.WeakDelegate = null;
            _manager.Dispose();
            _delegate.Dispose();
            return true;
        });
    }

    private static Guid PeerId(CBPeripheral peripheral) => Guid.Parse(peripheral.Identifier.AsString());

    internal static Guid GattUuid(CBUUID uuid)
    {
        string value = uuid.Uuid;
        return value.Length is 4 or 8
            ? Guid.Parse($"{value.PadLeft(8, '0')}-0000-1000-8000-00805f9b34fb")
            : Guid.Parse(value);
    }

    private sealed class CentralDelegate(MacBlePlatform owner) : CBCentralManagerDelegate
    {
        public override void UpdatedState(CBCentralManager central) => owner.UpdateState(central);

        public override void DiscoveredPeripheral(CBCentralManager central, CBPeripheral peripheral,
            NSDictionary advertisementData, NSNumber RSSI) => owner._scanner.Publish(peripheral, advertisementData, RSSI);

        public override void ConnectedPeripheral(CBCentralManager central, CBPeripheral peripheral)
        {
            if (owner._connections.TryGetValue(PeerId(peripheral), out var link)) link.Open();
        }

        public override void FailedToConnectPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error) =>
            owner.Finish(peripheral, error, failedToConnect: true);

        public override void DisconnectedPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error) =>
            owner.Finish(peripheral, error, failedToConnect: false);
    }

    private sealed class Scanner(MacBlePlatform owner) : IBleScanner
    {
        // Keep the requested scan across radio and permission changes while the shared hub has subscribers.
        private bool _requested;
        private bool _disposed;
        private volatile bool _running;
        public bool IsRunning => _running;
        public event Action<BleAdvertisement>? Received;
        public event Action<string>? Stopped { add { } remove { } }

        public void Start() => SetRequested(true);
        public void Stop() => SetRequested(false);

        private void SetRequested(bool requested) => owner._queue.DispatchAsync(() =>
        {
            if (_disposed || owner._disposed) return;
            _requested = requested;
            Update(owner._manager);
        });

        public void Dispose() => owner._queue.DispatchAsync(() =>
        {
            _disposed = true;
            _requested = false;
            if (!owner._disposed) Update(owner._manager);
        });

        public void Update(CBCentralManager central)
        {
            bool shouldScan = _requested && owner.State == MacBluetoothState.Available;
            if (shouldScan && !central.IsScanning)
                central.ScanForPeripherals((CBUUID[]?)null, new PeripheralScanningOptions { AllowDuplicatesKey = true });
            else if (!shouldScan && central.IsScanning)
                central.StopScan();
            _running = shouldScan;
        }

        public void Publish(CBPeripheral peripheral, NSDictionary values, NSNumber rssi)
        {
            if (!_running || rssi.Int16Value == 127) return; // the controller could not measure RSSI
            byte[]? bytes = (values[CBAdvertisement.DataManufacturerDataKey] as NSData)?.ToArray();
            var manufacturer = bytes is { Length: >= 2 }
                ? new[] { (BinaryPrimitives.ReadUInt16LittleEndian(bytes), bytes[2..]) }
                : [];
            var serviceData = new Dictionary<Guid, byte[]>();
            if (values[CBAdvertisement.DataServiceDataKey] is NSDictionary data)
                foreach (var key in data.Keys)
                    if (key is CBUUID uuid && data[key] is NSData value)
                        serviceData[GattUuid(uuid)] = value.ToArray();

            var uuids = new List<Guid>();
            if (values[CBAdvertisement.DataServiceUUIDsKey] is NSArray services)
                for (nuint i = 0; i < services.Count; i++)
                    uuids.Add(GattUuid(services.GetItem<CBUUID>(i)));
            string? name = (values[CBAdvertisement.DataLocalNameKey] as NSString)?.ToString() ?? peripheral.Name;
            Received?.Invoke(new BleAdvertisement(new BleDeviceId(PeerId(peripheral)),
                name, rssi.Int16Value, manufacturer, uuids, serviceData));
        }
    }
}
