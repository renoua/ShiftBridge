using ShiftBridge.Core.Localization;
using ShiftBridge.Platform.Obc;

namespace ShiftBridge.Platform.Ble;

/// <summary>Annonce BLE neutre (indépendante de WinRT et d'Android).</summary>
/// <param name="ManufacturerData">Données constructeur : company id + octets.</param>
/// <param name="ServiceUuids">UUID de services annoncés (16/32 bits déjà étendus sur la base Bluetooth).</param>
/// <param name="ServiceData">Données de service (type AD 0x16…), indexées par UUID étendu ; la valeur exclut l'UUID lui-même.</param>
public sealed record BleAdvertisement(
    BleDeviceId Address,
    string? LocalName,
    short Rssi,
    IReadOnlyList<(ushort CompanyId, byte[] Data)> ManufacturerData,
    IReadOnlyList<Guid> ServiceUuids,
    IReadOnlyDictionary<Guid, byte[]> ServiceData);

/// <summary>Scan BLE actif de la plateforme. Une seule instance est partagée par <see cref="BleAdvertisementHub"/>.</summary>
public interface IBleScanner : IDisposable
{
    bool IsRunning { get; }
    event Action<BleAdvertisement>? Received;
    /// <summary>Levé quand le scan s'arrête de lui-même (erreur, Bluetooth coupé…), avec la raison.</summary>
    event Action<string>? Stopped;
    void Start();
    void Stop();
}

/// <summary>Mode d'abonnement d'une caractéristique (valeur du descripteur de configuration 0x2902).</summary>
public enum BleNotifyMode { None, Notify, Indicate }

/// <summary>Une caractéristique GATT distante ; <see cref="Properties"/> utilise les bits GATT standard (Read 0x02, WriteNoResp 0x04, Write 0x08, Notify 0x10, Indicate 0x20).</summary>
public sealed record BleCharacteristicInfo(Guid Uuid, uint Properties)
{
    public bool CanWrite => (Properties & 0x08) != 0;
    public bool CanNotify => (Properties & 0x10) != 0;
    public bool CanIndicate => (Properties & 0x20) != 0;
}

public sealed record BleServiceInfo(Guid Uuid, IReadOnlyList<BleCharacteristicInfo> Characteristics);

/// <summary>Appareil introuvable (éteint, hors de portée ou déjà connecté ailleurs). Chaque appelant reformule le message.</summary>
public sealed class BleDeviceNotFoundException(string message) : IOException(message);

/// <param name="OperationTimeout">Délai maximal de chaque opération GATT (null = délai par défaut de la plateforme : 15 s sous Windows et macOS, 10 s sous Android).</param>
/// <param name="HighPriority">Groupe qui coupe une liaison peu active (sous Android, toutes les liaisons sont déjà en priorité haute).</param>
public sealed record BleConnectOptions(TimeSpan? OperationTimeout = null, bool HighPriority = false);

/// <summary>
/// Une liaison GATT sortante (rôle central). L'implémentation garde en vie tout ce que sa plateforme exige (sous Windows, les
/// objets GATT doivent rester référencés sinon les notifications cessent sans erreur) et maintient la liaison active.
/// Les opérations échouées lèvent <see cref="IOException"/> avec un message exploitable par l'utilisateur.
/// </summary>
public interface IBleConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Levé une seule fois, quand la liaison est perdue (pas à l'arrêt volontaire).</summary>
    event Action? Disconnected;

    /// <summary>Notification/indication reçue : service, caractéristique, valeur.</summary>
    event Action<Guid, Guid, byte[]>? ValueChanged;

    /// <summary>Messages de diagnostic de la plateforme (service ignoré, etc.).</summary>
    event Action<string>? Log;

    /// <summary>
    /// Découvre (sans cache) les services dont l'UUID satisfait <paramref name="serviceFilter"/> (tous si null), avec leurs
    /// caractéristiques. Cumulatif : un second appel ajoute des services aux premiers. Un service dont les caractéristiques
    /// sont illisibles est ignoré (et journalisé), sauf si <paramref name="strict"/> : l'échec est alors levé en <see cref="IOException"/>.
    /// </summary>
    Task<IReadOnlyList<BleServiceInfo>> DiscoverAsync(Func<Guid, bool>? serviceFilter = null, bool strict = false, CancellationToken ct = default);

    Task<byte[]> ReadAsync(Guid service, Guid characteristic);
    Task WriteAsync(Guid service, Guid characteristic, byte[] value, bool withResponse);

    /// <summary>Le gestionnaire <see cref="ValueChanged"/> peut être abonné avant l'appel : aucune trame n'est perdue.</summary>
    Task SetNotifyAsync(Guid service, Guid characteristic, BleNotifyMode mode);
}

public interface IBleCentral
{
    /// <exception cref="BleDeviceNotFoundException">Appareil introuvable.</exception>
    Task<IBleConnection> ConnectAsync(BleDeviceId address, BleConnectOptions options, CancellationToken ct = default);
}

/// <summary>Point d'entrée unique de la plateforme (Windows, Android…), injecté dans <see cref="BridgeService"/>.</summary>
public interface IBlePlatform
{
    IBleScanner CreateScanner();
    IBleCentral Central { get; }

    /// <summary>Vrai si l'appareil peut jouer le rôle périphérique BLE (annoncer le service OBC).</summary>
    Task<bool> IsPeripheralSupportedAsync();

    /// <summary>Sortie OBC en BLE (périphérique). Appeler seulement si <see cref="IsPeripheralSupportedAsync"/> est vrai.</summary>
    IObcTransport CreateObcPeripheral();
}

/// <summary>Plateforme sans Bluetooth (tests, environnements sans radio) : aucun scan, aucune connexion.</summary>
public sealed class NullBlePlatform : IBlePlatform
{
    public IBleCentral Central { get; } = new NullCentral();
    public IBleScanner CreateScanner() => new NullScanner();
    public Task<bool> IsPeripheralSupportedAsync() => Task.FromResult(false);
    public IObcTransport CreateObcPeripheral() => throw new NotSupportedException(Strings.Err_BleUnavailablePlatform);

    private sealed class NullCentral : IBleCentral
    {
        public Task<IBleConnection> ConnectAsync(BleDeviceId address, BleConnectOptions options, CancellationToken ct = default) =>
            throw new BleDeviceNotFoundException(Strings.Err_BleUnavailablePlatform);
    }

    private sealed class NullScanner : IBleScanner
    {
        public bool IsRunning { get; private set; }
        public event Action<BleAdvertisement>? Received { add { } remove { } }
        public event Action<string>? Stopped { add { } remove { } }
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Dispose() => IsRunning = false;
    }
}

/// <summary>Adresse BLE sous forme d'entier (48 bits, octet de poids fort en premier) ↔ texte « AA:BB:CC:DD:EE:FF ».</summary>
public static class BleAddress
{
    public static string ToText(ulong address) =>
        string.Join(":", BitConverter.GetBytes(address).Take(6).Reverse().Select(b => b.ToString("X2")));

    public static bool TryParse(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(':');
        if (parts.Length != 6) return false;
        ulong v = 0;
        foreach (var p in parts)
        {
            if (!byte.TryParse(p, System.Globalization.NumberStyles.HexNumber, null, out byte b)) return false;
            v = (v << 8) | b;
        }
        address = v;
        return true;
    }
}
