using ShiftBridge.Platform.Ble;
namespace ShiftBridge.Platform.Trainer;

/// <summary>Comment joindre le vrai trainer : en Wi-Fi/Ethernet (DirCon) ou en Bluetooth.</summary>
public abstract record TrainerEndpoint
{
    /// <summary>Trainer réseau (KICKR v6/Core 2…, Elite Justo 2…). Les attributs mDNS du trainer servent de modèle pour le proxy.</summary>
    public sealed record DirCon(string Host, int Port, IReadOnlyDictionary<string, string>? Txt = null) : TrainerEndpoint;

    /// <summary>Trainer Bluetooth (FTMS, ou FE-C expérimental).</summary>
    public sealed record Ble(BleDeviceId Address, string Name) : TrainerEndpoint;
}
