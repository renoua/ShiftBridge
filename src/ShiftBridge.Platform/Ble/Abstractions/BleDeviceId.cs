using System.Globalization;

namespace ShiftBridge.Platform.Ble;

/// <summary>A BLE MAC address or an opaque Core Bluetooth peer identifier.</summary>
public readonly record struct BleDeviceId : IComparable<BleDeviceId>
{
    public BleDeviceId(ulong macAddress) => MacAddress = macAddress;
    public BleDeviceId(Guid peerUuid) => PeerUuid = peerUuid;

    public ulong? MacAddress { get; }
    public Guid? PeerUuid { get; }

    public static implicit operator BleDeviceId(ulong address) => new(address);

    // Controller keys and display text have always used colon-delimited MAC addresses.
    public override string ToString() => MacAddress is { } mac
        ? BleAddress.ToText(mac)
        : PeerUuid?.ToString("D") ?? "";

    internal string NameSuffix => MacAddress is { } mac
        ? (mac & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture)
        : PeerUuid?.ToString("N")[^4..].ToUpperInvariant() ?? "";

    // Trainer and heart-rate settings have always used decimal MAC addresses.
    public string ToSavedAddress() => MacAddress?.ToString(CultureInfo.InvariantCulture) ?? ToString();

    public static bool TryParseSavedAddress(string? text, out BleDeviceId id)
    {
        if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mac) && mac != 0)
        {
            id = new(mac);
            return true;
        }
        if (Guid.TryParseExact(text, "D", out var uuid) && uuid != Guid.Empty)
        {
            id = new(uuid);
            return true;
        }
        id = default;
        return false;
    }

    public int CompareTo(BleDeviceId other)
    {
        int macOrder = Nullable.Compare(MacAddress, other.MacAddress);
        return macOrder != 0 ? macOrder : Nullable.Compare(PeerUuid, other.PeerUuid);
    }
}
