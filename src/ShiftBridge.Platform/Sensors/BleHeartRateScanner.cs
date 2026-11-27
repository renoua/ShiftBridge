using ShiftBridge.Core.Localization;
using ShiftBridge.Core.Trainer;
using ShiftBridge.Platform.Ble;

namespace ShiftBridge.Platform.Sensors;

public sealed record DiscoveredHeartRateSensor(BleDeviceId Address, string Name, short Rssi)
{
    public string AddressText => Address.ToString();

    /// <summary>Capteur mémorisé (proposé au lancement, sans annonce reçue) : signal inconnu.</summary>
    public bool Remembered { get; init; }

    /// <summary>Signal reçu ; « Dernier utilisé » pour un capteur mémorisé (0 dBm n'est pas une mesure).</summary>
    public string DetailText => Remembered ? Strings.Device_Remembered : Rssi == 0 ? "" : $"{Rssi} dBm";

    public override string ToString() => DetailText.Length == 0 ? Name : $"{Name}  ·  {DetailText}";
}

/// <summary>Recherche des capteurs cardio Bluetooth : annonces du service Heart Rate (0x180D). Exclut nos propres appareils.</summary>
public sealed class BleHeartRateScanner(BleAdvertisementHub hub) : IDisposable
{
    private sealed class Seen { public string Name = ""; public bool IsHr; public short Rssi; }

    private readonly Dictionary<BleDeviceId, Seen> _seen = [];
    private IDisposable? _subscription;

    public bool IsScanning => _subscription is not null;
    public event Action<DiscoveredHeartRateSensor>? SensorSeen;

    public void Start()
    {
        if (_subscription is not null) return;
        lock (_seen) _seen.Clear();
        _subscription = hub.Subscribe(OnReceived);
    }

    public void Stop()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private void OnReceived(BleAdvertisement adv)
    {
        string localName = adv.LocalName ?? "";
        if (localName.StartsWith("ShiftBridge", StringComparison.OrdinalIgnoreCase)) return;

        DiscoveredHeartRateSensor report;
        lock (_seen)
        {
            if (!_seen.TryGetValue(adv.Address, out var s)) _seen[adv.Address] = s = new Seen();
            // L'annonce et la réponse de scan arrivent séparément : on cumule ce qu'on apprend.
            if (!string.IsNullOrWhiteSpace(localName)) s.Name = localName;
            s.IsHr |= HeartRateCodec.IsHeartRateSensor(adv.ServiceUuids);
            s.Rssi = adv.Rssi;
            if (!s.IsHr) return;
            report = new DiscoveredHeartRateSensor(adv.Address, string.IsNullOrWhiteSpace(s.Name) ? "Heart rate" : s.Name, s.Rssi);
        }
        SensorSeen?.Invoke(report);
    }

    public void Dispose() => Stop();
}
