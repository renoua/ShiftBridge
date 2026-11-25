using ShiftBridge.Platform.Ble;

namespace ShiftBridge.Core.Tests;

/// <summary>Scanner BLE de test : les annonces sont poussées à la main.</summary>
internal sealed class FakeBleScanner : IBleScanner
{
    public bool IsRunning { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public event Action<BleAdvertisement>? Received;
    public event Action<string>? Stopped;

    public void Start() { IsRunning = true; StartCount++; }
    public void Stop() { IsRunning = false; StopCount++; }
    public void Dispose() => IsRunning = false;

    public void Emit(BleAdvertisement adv) => Received?.Invoke(adv);
    public void StopByItself(string reason) { IsRunning = false; Stopped?.Invoke(reason); }

    public static BleAdvertisement Adv(BleDeviceId address, string? name = null, short rssi = -60,
        (ushort CompanyId, byte[] Data)[]? manufacturer = null, Guid[]? services = null,
        Dictionary<Guid, byte[]>? serviceData = null) =>
        new(address, name, rssi, manufacturer ?? [], services ?? [], serviceData ?? []);
}

/// <summary>Liaison GATT de test : expose des services figés, enregistre les écritures et les abonnements.</summary>
internal sealed class FakeBleConnection : IBleConnection
{
    private readonly List<BleServiceInfo> _services;

    public FakeBleConnection(params BleServiceInfo[] services) => _services = [.. services];

    public bool Connected { get; set; } = true;
    public bool IsConnected => Connected;
    public bool Disposed { get; private set; }
    public List<(Guid Service, Guid Char, byte[] Value, bool WithResponse)> Writes { get; } = [];
    public Dictionary<(Guid Service, Guid Char), BleNotifyMode> Notify { get; } = [];
    public Dictionary<(Guid Service, Guid Char), byte[]> ReadValues { get; } = [];
    /// <summary>Lectures successives d'une caractéristique (une valeur par lecture) ; prioritaires sur <see cref="ReadValues"/>.</summary>
    public Dictionary<(Guid Service, Guid Char), Queue<byte[]>> ReadQueues { get; } = [];
    /// <summary>Appelé à chaque écriture : permet à un appareil simulé de réagir (p. ex. répondre à un appairage).</summary>
    public Action<Guid, Guid, byte[]>? OnWrite { get; set; }
    public bool? FirstDiscoverStrict { get; private set; }

    public event Action? Disconnected;
    public event Action<Guid, Guid, byte[]>? ValueChanged;
    public event Action<string>? Log { add { } remove { } }

    public Task<IReadOnlyList<BleServiceInfo>> DiscoverAsync(Func<Guid, bool>? serviceFilter = null, bool strict = false, CancellationToken ct = default)
    {
        FirstDiscoverStrict ??= strict;
        IReadOnlyList<BleServiceInfo> r = [.. _services.Where(s => serviceFilter is null || serviceFilter(s.Uuid))];
        return Task.FromResult(r);
    }

    public Task<byte[]> ReadAsync(Guid service, Guid characteristic) =>
        ReadQueues.TryGetValue((service, characteristic), out var q) && q.Count > 0 ? Task.FromResult(q.Dequeue()) :
        ReadValues.TryGetValue((service, characteristic), out var v) ? Task.FromResult(v) : throw new IOException("lecture impossible");

    public Task WriteAsync(Guid service, Guid characteristic, byte[] value, bool withResponse)
    {
        Writes.Add((service, characteristic, value, withResponse));
        OnWrite?.Invoke(service, characteristic, value);
        return Task.CompletedTask;
    }

    public Task SetNotifyAsync(Guid service, Guid characteristic, BleNotifyMode mode)
    {
        Notify[(service, characteristic)] = mode;
        return Task.CompletedTask;
    }

    public void Emit(Guid service, Guid characteristic, byte[] value) => ValueChanged?.Invoke(service, characteristic, value);
    public void Drop() { Connected = false; Disconnected?.Invoke(); }

    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

/// <summary>Plateforme BLE de test complète (scanner et central factices) pour piloter un <c>BridgeService</c>.</summary>
internal sealed class FakeBlePlatform(FakeBleScanner scanner, FakeBleCentral central) : IBlePlatform
{
    public IBleCentral Central => central;
    public IBleScanner CreateScanner() => scanner;
    public Task<bool> IsPeripheralSupportedAsync() => Task.FromResult(false);
    public ShiftBridge.Platform.Obc.IObcTransport CreateObcPeripheral() => throw new NotSupportedException();
}

internal sealed class FakeBleCentral : IBleCentral
{
    public FakeBleConnection? Connection { get; set; }
    public List<(BleDeviceId Address, BleConnectOptions Options)> Requests { get; } = [];

    public Task<IBleConnection> ConnectAsync(BleDeviceId address, BleConnectOptions options, CancellationToken ct = default)
    {
        Requests.Add((address, options));
        if (Connection is null) throw new BleDeviceNotFoundException("introuvable");
        return Task.FromResult<IBleConnection>(Connection);
    }
}
