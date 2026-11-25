using System.Text.RegularExpressions;
using ShiftBridge.Core.Controllers;
using ShiftBridge.Core.Controllers.Decoders;
using ShiftBridge.Core.Trainer;
using ShiftBridge.Platform.Ble;

namespace ShiftBridge.Platform.Controllers;

/// <summary>
/// Reconnaît les manettes BLE non Zwift d'après leur annonce. On se fonde sur le NOM : les services qu'elles utilisent
/// (FEA0, Nordic UART, UUID Onelap partagé avec des vélos Kettler…) sont communs à d'autres appareils.
/// Exceptions : Shimano (services D-Fly propres à Shimano) et Wheeltop (aucun nom annoncé, préfixe constant des données constructeur).
/// </summary>
public static partial class ControllerCatalog
{
    [GeneratedRegex(@"^SQUARE(\s+[0-9A-F]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SquareName();

    // Capteurs de puissance Shimano (« FC-R9200-P ») : ils annoncent aussi le service 18FF, ce ne sont pas des commandes.
    [GeneratedRegex(@"^FC-?[A-Z]\d{4}-?P", RegexOptions.IgnoreCase)]
    private static partial Regex ShimanoPowerMeter();

    private static readonly Guid SramService = FtmsCodec.BaseUuid(0xFE51);

    public static ControllerKind? Identify(BleAdvertisement adv) => Match(adv)?.Kind;

    public static DiscoveredController? Detect(BleAdvertisement adv) =>
        Match(adv) is { } m ? new DiscoveredController(adv.Address, m.Name, m.Kind, adv.Rssi) : null;

    private static (ControllerKind Kind, string Name)? Match(BleAdvertisement adv)
    {
        string name = (adv.LocalName ?? "").Trim();
        string upper = name.ToUpperInvariant();
        string suffix = adv.Address.NameSuffix;

        if (WheeltopType(adv) is { } wheeltopType)
            return (ControllerKind.WheeltopEds, $"Wheeltop EDS {wheeltopType} {suffix}");

        if (name.Length > 0)
        {
            // « Think VS01-0001725 » : jamais « THINK » seul, qui est aussi le préfixe des home-trainers Thinkrider (THINK X, THINK-…).
            if (upper.StartsWith("THINK VS", StringComparison.Ordinal)) return (ControllerKind.ThinkriderVs200, name);
            // « CYCPLUS BC2 57344 ». Le service Nordic UART seul ne suffit pas (Wheeltop, L-TWOO l'utilisent aussi).
            if (upper.StartsWith("CYCPLUS", StringComparison.Ordinal) && upper.Contains("BC2", StringComparison.Ordinal)) return (ControllerKind.CycplusBc2, name);
            // « T7H-L… » / « T7H-R… » : un appareil par levier.
            if (upper.StartsWith("T7H-L", StringComparison.Ordinal) || upper.StartsWith("T7H-R", StringComparison.Ordinal)) return (ControllerKind.CycplusT7h, name);
            // « Onelap Key_12224 ».
            if (upper == "ONELAP KEY" || upper.StartsWith("ONELAP KEY_", StringComparison.Ordinal)) return (ControllerKind.OnelapKey, name);
            if (upper.StartsWith("KICKR BIKE", StringComparison.Ordinal)) return (ControllerKind.WahooKickrBike, name);
            if (SquareName().IsMatch(name)) return (ControllerKind.EliteSquare, name);
            if (upper.StartsWith("STAGES BIKE", StringComparison.Ordinal)) return (ControllerKind.StagesSb20, name);
            // L-TWOO eRX / eR9 : « LTOED2501… » ; « LTOED00… » est l'ancienne génération, non prise en charge.
            if (upper.StartsWith("LTOED", StringComparison.Ordinal) && !upper.StartsWith("LTOED00", StringComparison.Ordinal)) return (ControllerKind.Ltwoo, name);
        }

        if (IsShimano(adv, upper)) return (ControllerKind.ShimanoDi2, name.Length > 0 ? name : $"Shimano Di2 {suffix}");
        if (IsSramRearDerailleur(adv, upper)) return (ControllerKind.SramAxs, name);
        return null;
    }

    private static bool IsShimano(BleAdvertisement adv, string upper)
    {
        if (ShimanoPowerMeter().IsMatch(upper)) return false;
        return upper.StartsWith("RDR", StringComparison.Ordinal) || upper.StartsWith("EWWU", StringComparison.Ordinal)
            || adv.ServiceUuids.Contains(ShimanoDi2Decoder.Service) || adv.ServiceUuids.Contains(ShimanoDi2Decoder.ConfigService);
    }

    /// <summary>
    /// « SRAM &lt;n° de série&gt; ». Seul le dérailleur arrière nous intéresse (leviers et Blips annoncent aussi FE51) :
    /// quand les données de service FE51 sont lisibles, leur dernier octet (classe) doit être 0x81.
    /// </summary>
    private static bool IsSramRearDerailleur(BleAdvertisement adv, string upper)
    {
        if (!upper.StartsWith("SRAM", StringComparison.Ordinal)) return false;
        if (adv.ServiceData.TryGetValue(SramService, out var data) && data.Length >= 9 && data[8] != 0x81) return false;
        return true;
    }

    /// <summary>Type d'unité Wheeltop (octet 7 des données constructeur), si l'annonce porte le préfixe constant.</summary>
    private static string? WheeltopType(BleAdvertisement adv)
    {
        foreach (var (company, data) in adv.ManufacturerData)
        {
            // Le préfixe couvre le company id (petit-boutiste) puis la charge utile : on essaie les deux lectures.
            byte[] full = [(byte)(company & 0xFF), (byte)(company >> 8), .. data];
            byte[][] candidates = [full, data];
            foreach (var c in candidates)
            {
                if (c.Length <= 7 || !c.AsSpan(0, 7).SequenceEqual(WheeltopEdsDecoder.AdvertPrefix)) continue;
                return c[7] switch { 0x37 => "OX", 0x39 => "TX Front", 0x36 => "TX Left", 0x38 => "TX Right", _ => "" };
            }
        }
        return null;
    }
}
