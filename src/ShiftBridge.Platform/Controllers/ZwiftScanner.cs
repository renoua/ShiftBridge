using ShiftBridge.Core.Controllers;
using ShiftBridge.Core.Zwift;
using ShiftBridge.Platform.Ble;

namespace ShiftBridge.Platform.Controllers;

/// <summary>Une manette BLE vue dans le scan, quel que soit son modèle.</summary>
public record DiscoveredController(BleDeviceId Address, string Name, ControllerKind Kind, short Rssi)
{
    public string AddressText => Address.ToString();
}

public sealed record DiscoveredZwiftDevice(BleDeviceId Address, string Name, ZwiftDeviceType Type, short Rssi, byte[] ManufacturerData)
    : DiscoveredController(Address, Name, ControllerKind.Zwift, Rssi);

/// <summary>Scan BLE filtré sur le company id Zwift (0x094A), via le hub d'annonces partagé.</summary>
public sealed class ZwiftScanner(BleAdvertisementHub hub) : IDisposable
{
    private IDisposable? _subscription;

    public bool IsScanning => _subscription is not null;

    /// <summary>Levé à chaque annonce reçue (la même manette plusieurs fois : l'appelant déduplique par adresse).</summary>
    public event Action<DiscoveredZwiftDevice>? DeviceSeen;
    public event Action<string>? Log;

    public void Start()
    {
        if (_subscription is not null) return;
        _subscription = hub.Subscribe(OnReceived);
        Log?.Invoke("Recherche des manettes Zwift démarrée");
    }

    public void Stop()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private void OnReceived(BleAdvertisement adv)
    {
        foreach (var d in Detect(adv)) DeviceSeen?.Invoke(d);
    }

    /// <summary>Les manettes Zwift annoncées (company id 0x094A) ; vide si l'annonce n'en est pas une.</summary>
    public static IEnumerable<DiscoveredZwiftDevice> Detect(BleAdvertisement adv)
    {
        foreach (var (companyId, data) in adv.ManufacturerData)
        {
            if (companyId != ZwiftConstants.ManufacturerId) continue;
            var type = ZwiftDeviceType.FromManufacturerData(data);
            string baseName = string.IsNullOrWhiteSpace(adv.LocalName) ? $"Zwift {type.Model}" : adv.LocalName;
            // Deux manettes du même modèle (gauche/droite) doivent être distinguables dans le journal.
            string side = type.Side switch { ZwiftSide.Left => "G", ZwiftSide.Right => "D", _ => "" };
            string name = $"{baseName} {side}{adv.Address.NameSuffix}".Replace("  ", " ");
            yield return new DiscoveredZwiftDevice(adv.Address, name, type, adv.Rssi, data);
        }
    }

    public void Dispose() => Stop();
}
