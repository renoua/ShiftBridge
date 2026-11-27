using ShiftBridge.Core.Localization;
using ShiftBridge.Core.Trainer;
using ShiftBridge.Core.Zwift;
using ShiftBridge.Platform.Ble;

namespace ShiftBridge.Platform.Trainer;

public sealed record DiscoveredBleTrainer(BleDeviceId Address, string Name, short Rssi, TrainerProtocols Protocols)
{
    public string AddressText => Address.ToString();

    /// <summary>Trainer mémorisé (proposé au lancement, sans annonce reçue) : ni protocoles ni signal connus.</summary>
    public bool Remembered { get; init; }

    public string ProtocolText =>
        Remembered ? Strings.Device_Remembered
        : Protocols == TrainerProtocols.None ? Strings.Proto_ToCheck
        : string.Join(" + ", new[]
        {
            Protocols.HasFlag(TrainerProtocols.Ftms) ? "FTMS" : null,
            Protocols.HasFlag(TrainerProtocols.FecOverBle) ? Strings.Proto_FecExperimental : null,
            Protocols == TrainerProtocols.CyclingPower ? Strings.Proto_PowerOnly : null,
        }.Where(s => s is not null));

    /// <summary>Signal reçu, vide tant qu'aucune annonce n'a été vue (0 dBm n'est pas une mesure).</summary>
    public string RssiText => Rssi == 0 ? "" : $"{Rssi} dBm";

    public override string ToString() => Rssi == 0 ? $"{Name}  ·  {ProtocolText}" : $"{Name}  ·  {ProtocolText}  ·  {RssiText}";
}

/// <summary>
/// Recherche des trainers Bluetooth : annonces FTMS ou FE-C (et noms courants en secours, car certains trainers placent
/// leurs UUID dans la réponse de scan). Exclut les manettes Zwift connues, nos propres appareils et les machines FTMS qui ne
/// sont pas des vélos d'intérieur.
/// </summary>
public sealed class BleTrainerScanner(BleAdvertisementHub hub) : IDisposable
{
    private sealed class Seen { public string Name = ""; public TrainerProtocols Protocols; public bool Excluded; public short Rssi; }

    private readonly Dictionary<BleDeviceId, Seen> _seen = [];
    private IDisposable? _subscription;

    public bool IsScanning => _subscription is not null;
    public event Action<DiscoveredBleTrainer>? TrainerSeen;

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
        bool excluded = false;
        string localName = adv.LocalName ?? "";

        // Manettes Zwift connues (Play, Click, Ride…). On n'exclut PAS tout le company id : un Zwift Hub pourrait l'utiliser.
        foreach (var (companyId, data) in adv.ManufacturerData)
        {
            if (companyId != ZwiftConstants.ManufacturerId) continue;
            if (ZwiftDeviceType.FromManufacturerData(data).Model != ZwiftModel.Unknown) excluded = true;
        }
        if (localName.StartsWith("ShiftBridge", StringComparison.OrdinalIgnoreCase)) excluded = true;
        // Télécommandes de guidon (Thinkrider VS200, CYCPLUS BC2, Onelap Key…) : leurs noms ressemblent à ceux de home-trainers de la même marque.
        if (ShiftBridge.Platform.Controllers.ControllerCatalog.Identify(adv) is { } remote && ShiftBridge.Core.Controllers.ControllerKinds.IsPureRemote(remote)) excluded = true;

        var protocols = TrainerAdvertisementRules.FromServiceUuids(adv.ServiceUuids);

        // Données de service FTMS (UUID 0x1826) : on écarte les tapis, rameurs, etc.
        if (adv.ServiceData.TryGetValue(FtmsCodec.BaseUuid(0x1826), out byte[]? bytes))
        {
            protocols |= TrainerProtocols.Ftms;
            if (FtmsAdvertisement.TryParse(bytes, out var ftms) && !ftms.IsIndoorBike) excluded = true;
        }

        DiscoveredBleTrainer? report = null;
        lock (_seen)
        {
            if (!_seen.TryGetValue(adv.Address, out var s)) _seen[adv.Address] = s = new Seen();
            // L'annonce et la réponse de scan arrivent séparément : on cumule ce qu'on apprend.
            if (!string.IsNullOrWhiteSpace(localName)) s.Name = localName;
            s.Protocols |= protocols;
            s.Excluded |= excluded;
            s.Rssi = adv.Rssi;
            if (s.Excluded) return;

            bool isTrainer = (s.Protocols & (TrainerProtocols.Ftms | TrainerProtocols.FecOverBle)) != 0
                             || TrainerAdvertisementRules.LooksLikeTrainerName(s.Name);
            if (!isTrainer) return;

            string name = string.IsNullOrWhiteSpace(s.Name) ? "Trainer" : s.Name;
            report = new DiscoveredBleTrainer(adv.Address, name, s.Rssi, s.Protocols);
        }
        TrainerSeen?.Invoke(report);
    }

    public void Dispose() => Stop();
}
