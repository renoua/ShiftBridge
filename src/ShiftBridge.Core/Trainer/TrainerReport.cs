using System.Text;

namespace ShiftBridge.Core.Trainer;

/// <summary>
/// Rapport de compatibilité d'un trainer, écrit au journal à la connexion : de quoi étendre la prise en charge à d'autres
/// modèles à partir des retours d'utilisateurs (services, caractéristiques, propriétés, fonctions FTMS, premier échantillon).
/// </summary>
/// <summary>Comment le proxy peut piloter la résistance du trainer (et donc appliquer le virtual shifting).</summary>
public enum TrainerControl
{
    /// <summary>Fitness Machine Service (0x1826) : commande de simulation réécrite.</summary>
    Ftms,
    /// <summary>ANT+ FE-C sur Bluetooth (Tacx sans FTMS) : page 0x33 réécrite.</summary>
    FeC,
    /// <summary>Ni l'un ni l'autre : le trainer ne fournit que des mesures, le virtual shifting est impossible.</summary>
    None,
}

public static class TrainerReport
{
    /// <summary>Protocole de pilotage disponible dans l'arbre relayé : FTMS en priorité, sinon FE-C.</summary>
    public static TrainerControl DetectControl(IReadOnlyDictionary<Guid, List<(Guid Uuid, byte Props)>> tree)
    {
        bool Has(Guid ch) => tree.Values.Any(c => c.Any(x => x.Uuid == ch));
        if (Has(FtmsCodec.ControlPoint)) return TrainerControl.Ftms;
        if (Has(FecCodec.Tx) && Has(FecCodec.Rx)) return TrainerControl.FeC;
        return TrainerControl.None;
    }

    public static string Build(string trainerName, string transport,
        IReadOnlyDictionary<Guid, List<(Guid Uuid, byte Props)>> tree, FtmsFeatures? features,
        IEnumerable<Guid>? skippedServices = null, TrainerControl? control = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Rapport de compatibilité trainer : {trainerName} ({transport}) ===");
        foreach (var (svc, chars) in tree)
        {
            sb.AppendLine($"  service {Short(svc)}");
            foreach (var (uuid, props) in chars)
                sb.AppendLine($"    {Short(uuid)}  {Props(props)}");
        }
        if (skippedServices?.ToList() is { Count: > 0 } skipped)
            sb.AppendLine($"  non relayé(s) à l'appli : {string.Join(", ", skipped.Select(Short))}");
        sb.Append(features is null
            ? "  Fonctions FTMS : non lues (pas de caractéristique 0x2ACC)"
            : $"  Fonctions FTMS : {features.Describe()}");
        if (control is { } c)
            sb.AppendLine().Append(c switch
            {
                TrainerControl.Ftms => "  Pilotage : FTMS",
                TrainerControl.FeC => "  Pilotage : FE-C sur Bluetooth (pas de FTMS) : la pente des pages 0x33 est réécrite",
                _ => "  Pilotage : AUCUN (ni FTMS ni FE-C) : le trainer ne fournit que des mesures, virtual shifting impossible",
            });
        return sb.ToString();
    }

    /// <summary>Premier échantillon d'Indoor Bike Data : flags + octets bruts + présence de la vitesse instantanée.</summary>
    public static string DescribeBikeData(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return $"Indoor Bike Data trop court : {Convert.ToHexString(data)}";
        ushort flags = (ushort)(data[0] | (data[1] << 8));
        bool speed = FtmsCodec.TryGetSpeedMs(data, out var ms);
        return $"Premier échantillon Indoor Bike Data : {Convert.ToHexString(data)} (flags=0x{flags:X4}, vitesse instantanée " +
               (speed ? $"présente : {ms * 3.6:0.0} km/h)" : "absente)");
    }

    private static string Short(Guid g)
    {
        var s = g.ToString();
        return s.StartsWith("0000", StringComparison.Ordinal) && s.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.Ordinal)
            ? "0x" + s.Substring(4, 4).ToUpperInvariant() : s;
    }

    private static string Props(byte p)
    {
        var parts = new List<string>();
        if ((p & 0x01) != 0) parts.Add("lecture");
        if ((p & 0x02) != 0) parts.Add("écriture");
        if ((p & 0x04) != 0) parts.Add("notification");
        if ((p & 0x08) != 0) parts.Add("indication");
        return parts.Count == 0 ? "(aucune)" : string.Join(" + ", parts);
    }
}
