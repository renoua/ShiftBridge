using ShiftBridge.Core.Obc;

namespace ShiftBridge.Platform.Obc;

/// <summary>Une sortie OpenBikeControl (BLE, TCP, DirCon). Notre appli y est le « device » (serveur).</summary>
public interface IObcTransport : IAsyncDisposable
{
    string Name { get; }

    /// <summary>Vrai si au moins une appli d'entraînement est connectée.</summary>
    bool HasClient { get; }

    /// <summary>Dernier App Info reçu (null si aucune appli ne l'a envoyé).</summary>
    ObcAppInfo? CurrentAppInfo { get; }

    /// <summary>
    /// Nom sous lequel l'appli d'entraînement voit ce transport, quand ce n'est pas « ShiftBridge » (null sinon). En Bluetooth,
    /// Windows et Android annoncent le nom Bluetooth de l'appareil (« DESKTOP-AB12 », « Galaxy S21 »…) : l'utilisateur doit le savoir.
    /// </summary>
    string? AdvertisedName => null;

    event Action<ObcAppInfo>? AppInfoReceived;
    event Action<ObcHaptic>? HapticReceived;
    event Action? ClientsChanged;
    event Action<string>? Log;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();

    /// <summary>Envoie un message 0x01 déjà encodé.</summary>
    Task SendButtonStateAsync(byte[] message);

    /// <summary>Niveau de batterie de la manette (0..100), null si inconnu.</summary>
    void SetBattery(int? percent);
}
