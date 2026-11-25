using ShiftBridge.Core.Trainer;
using ShiftBridge.Platform.Ble;
using ShiftBridge.Platform.Trainer;

namespace ShiftBridge.Platform.Sensors;

/// <summary>
/// Ceinture ou montre cardio Bluetooth (service 0x180D), avec reconnexion automatique. Vit indépendamment du proxy trainer
/// (elle survit à ses redémarrages) : le proxy ne fait que s'abonner à <see cref="Measurement"/>.
/// </summary>
public sealed class HeartRateSource : IAsyncDisposable
{
    private readonly ReconnectingTrainerLink _link;
    private byte[] _location = [HeartRateCodec.LocationChest];
    private int _bpm;
    private int _hasBpm;

    public HeartRateSource(IBleCentral central, BleDeviceId address, string name)
        : this(ct => ConnectBleAsync(central, address, name, ct), name) { }

    /// <summary>Fabrique de liaison libre (tests).</summary>
    public HeartRateSource(Func<CancellationToken, Task<ITrainerLink>> factory, string name,
        TimeSpan? initialDelay = null, TimeSpan? maxDelay = null, TimeSpan? silenceTimeout = null)
    {
        Name = name;
        _link = new ReconnectingTrainerLink(factory, initialDelay, maxDelay, silenceTimeout);
        _link.Log += m => Log?.Invoke(m);
        _link.StateChanged += s => StateChanged?.Invoke(s);
        _link.Notification += OnNotification;
    }

    private static async Task<ITrainerLink> ConnectBleAsync(IBleCentral central, BleDeviceId address, string name, CancellationToken ct)
    {
        var l = new BleTrainerLink(central, address, name);
        try { await l.ConnectAsync(ct); }
        catch { await l.DisposeAsync(); throw; }
        return l;
    }

    public string Name { get; }
    public LinkState State => _link.State;
    public int? LastBpm => Volatile.Read(ref _hasBpm) != 0 ? Volatile.Read(ref _bpm) : null;
    /// <summary>Valeur de la position du capteur (0x2A38) à servir à l'appli.</summary>
    public byte[] SensorLocation => _location;

    /// <summary>Trame brute 0x2A37, relayée telle quelle à l'appli.</summary>
    public event Action<byte[]>? Measurement;
    public event Action<LinkState>? StateChanged;
    public event Action<string>? Log;

    /// <summary>Première connexion : doit réussir (l'erreur remonte) ; ensuite la reconnexion est automatique.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _link.ConnectAsync(ct);
        try
        {
            var services = await _link.DiscoverServicesAsync();
            if (!services.Contains(HeartRateCodec.Service))
                throw new IOException("Ce capteur n'expose pas le service de fréquence cardiaque (0x180D).");
            var chars = await _link.DiscoverCharacteristicsAsync(HeartRateCodec.Service);
            if (chars.All(c => c.Uuid != HeartRateCodec.Measurement))
                throw new IOException("Ce capteur n'expose pas la mesure de fréquence cardiaque (0x2A37).");

            if (chars.Any(c => c.Uuid == HeartRateCodec.SensorLocation))
            {
                try { var loc = await _link.ReadAsync(HeartRateCodec.SensorLocation); if (loc.Length > 0) _location = loc; }
                catch (Exception ex) { Log?.Invoke($"Position du capteur illisible : {ex.Message}"); }
            }
            await _link.EnableNotificationsAsync(HeartRateCodec.Measurement);
        }
        catch { await _link.DisposeAsync(); throw; }
    }

    private void OnNotification(Guid ch, byte[] value)
    {
        if (ch != HeartRateCodec.Measurement) return;
        if (HeartRateCodec.TryParse(value, out var m) && m.Bpm > 0)
        {
            Volatile.Write(ref _bpm, m.Bpm);
            Volatile.Write(ref _hasBpm, 1);
        }
        Measurement?.Invoke(value);
    }

    public ValueTask DisposeAsync()
    {
        _link.Notification -= OnNotification;
        return _link.DisposeAsync();
    }
}
