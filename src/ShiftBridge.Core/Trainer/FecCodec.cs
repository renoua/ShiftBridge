namespace ShiftBridge.Core.Trainer;

/// <summary>
/// ANT+ FE-C (Fitness Equipment Control) transporté sur Bluetooth, tel que les trainers Tacx l'exposent quand ils n'ont pas
/// FTMS (NEO Smart T2800 par exemple) : service 6E40FEC1, notifications du trainer sur FEC2, commandes de l'appli sur FEC3.
/// Chaque message est un message ANT complet : [A4, 09, type (4E diffusion / 4F acquitté), canal, page (8 octets), somme],
/// la somme étant le OU exclusif de tous les octets qui la précèdent. Une valeur peut en contenir plusieurs à la suite.
/// </summary>
public static class FecCodec
{
    public static readonly Guid Service = new("6e40fec1-b5a3-f393-e0a9-e50e24dcca9e");
    /// <summary>Trainer → appli (notifications).</summary>
    public static readonly Guid Rx = new("6e40fec2-b5a3-f393-e0a9-e50e24dcca9e");
    /// <summary>Appli → trainer (écriture).</summary>
    public static readonly Guid Tx = new("6e40fec3-b5a3-f393-e0a9-e50e24dcca9e");

    public const byte Sync = 0xA4;
    public const int MessageLength = 13;

    // Pages de données utiles au proxy.
    public const byte PageGeneral = 0x10;          // vitesse (0,001 m/s), état de l'appareil
    public const byte PageTrainerData = 0x19;      // cadence, puissance instantanée
    public const byte PageBasicResistance = 0x30;  // résistance en %
    public const byte PageTargetPower = 0x31;      // ERG
    public const byte PageWindResistance = 0x32;   // coefficient de traînée, vent, aspiration
    public const byte PageTrackResistance = 0x33;  // pente, roulement : la simulation
    public const byte PageUserConfiguration = 0x37;

    /// <summary>Roulement par défaut de la norme quand l'appli ne le donne pas (0xFF).</summary>
    public const double DefaultCrr = 0.004;
    /// <summary>Coefficient de traînée par défaut de la norme (kg/m) quand l'appli ne l'a pas donné.</summary>
    public const double DefaultCw = 0.51;

    /// <summary>Vrai pour une page qui fixe le mode de résistance (fait sortir des autres modes).</summary>
    public static bool IsTargetPage(byte page) => page is PageBasicResistance or PageTargetPower or PageTrackResistance;

    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        byte x = 0;
        foreach (var b in bytes) x ^= b;
        return x;
    }

    /// <summary>
    /// Découpe une valeur en messages FE-C (13 octets, somme valide). Faux si la valeur n'est pas une suite de messages
    /// valides : le proxy la relaie alors telle quelle sans rien y toucher.
    /// </summary>
    public static bool TrySplit(ReadOnlySpan<byte> value, out List<byte[]> messages)
    {
        messages = [];
        if (value.Length == 0 || value.Length % MessageLength != 0) return false;
        for (int i = 0; i < value.Length; i += MessageLength)
        {
            var m = value.Slice(i, MessageLength);
            if (m[0] != Sync || m[1] != 0x09 || Checksum(m[..^1]) != m[^1]) { messages = []; return false; }
            messages.Add(m.ToArray());
        }
        return true;
    }

    /// <summary>Numéro de page d'un message (octet 4).</summary>
    public static byte Page(ReadOnlySpan<byte> message) => message[4];

    /// <summary>Recalcule la somme après modification d'une page.</summary>
    public static void Seal(byte[] message) => message[^1] = Checksum(message.AsSpan(0, MessageLength - 1));

    // ---------- Page 0x33 : simulation ----------

    /// <summary>Pente brute de la page 0x33 (0,01 %, décalage −200 %) en 0,01 % signés, comme FTMS.</summary>
    public static bool TryParseTrack(ReadOnlySpan<byte> message, out short gradeRaw, out double crr)
    {
        gradeRaw = 0; crr = DefaultCrr;
        if (message.Length < MessageLength || message[4] != PageTrackResistance) return false;
        int raw = message[9] | (message[10] << 8);
        if (raw == 0xFFFF) return false; // pente invalide : rien à ajuster
        gradeRaw = (short)Math.Clamp(raw - 20000, short.MinValue, short.MaxValue);
        if (message[11] != 0xFF) crr = message[11] * 0.00005;
        return true;
    }

    /// <summary>Copie de la page 0x33 avec une autre pente (0,01 %), somme recalculée.</summary>
    public static byte[] WithGrade(ReadOnlySpan<byte> message, short gradeRaw)
    {
        var copy = message.ToArray();
        int raw = Math.Clamp(gradeRaw + 20000, 0, 40000); // ±200 % : plage de la norme
        copy[9] = (byte)(raw & 0xFF);
        copy[10] = (byte)(raw >> 8);
        Seal(copy);
        return copy;
    }

    /// <summary>Coefficient de traînée (kg/m) de la page 0x32, s'il est donné.</summary>
    public static bool TryParseWindCw(ReadOnlySpan<byte> message, out double cw)
    {
        cw = DefaultCw;
        if (message.Length < MessageLength || message[4] != PageWindResistance || message[9] == 0xFF) return false;
        cw = message[9] * 0.01;
        return true;
    }

    /// <summary>
    /// Page 0x37 (configuration du cycliste) : poids du cycliste (0,01 kg, octets 1-2 de la page) et du vélo (12 bits en
    /// 0,05 kg : quartet haut de l'octet 4 et octet 5). Les valeurs « invalides » de la norme donnent null.
    /// </summary>
    public static bool TryParseUserConfig(ReadOnlySpan<byte> message, out double? riderKg, out double? bikeKg)
    {
        riderKg = null; bikeKg = null;
        if (message.Length < MessageLength || message[4] != PageUserConfiguration) return false;
        int rider = message[5] | (message[6] << 8);
        if (rider != 0xFFFF && rider > 0) riderKg = rider * 0.01;
        int bike = (message[8] >> 4) | (message[9] << 4);
        if (bike != 0xFFF && bike > 0) bikeKg = bike * 0.05;
        return true;
    }

    // ---------- Pages 0x10 et 0x19 : mesures ----------

    /// <summary>Vitesse de la page 0x10 (0,001 m/s ; 0xFFFF = invalide).</summary>
    public static bool TryGetSpeedMs(ReadOnlySpan<byte> message, out double speedMs)
    {
        speedMs = 0;
        if (message.Length < MessageLength || message[4] != PageGeneral) return false;
        int raw = message[8] | (message[9] << 8);
        if (raw == 0xFFFF) return false;
        speedMs = raw * 0.001;
        return true;
    }

    /// <summary>Copie de la page 0x10 dont la vitesse est multipliée par <paramref name="factor"/> (vitesse virtuelle).</summary>
    public static byte[] ScaleSpeed(ReadOnlySpan<byte> message, double factor)
    {
        var copy = message.ToArray();
        if (!TryGetSpeedMs(message, out _)) return copy;
        int raw = copy[8] | (copy[9] << 8);
        int scaled = (int)Math.Clamp(Math.Round(raw * factor), 0, 0xFFFE);
        copy[8] = (byte)(scaled & 0xFF);
        copy[9] = (byte)(scaled >> 8);
        Seal(copy);
        return copy;
    }

    /// <summary>Cadence (tr/min, 0xFF = invalide) et puissance instantanée (W, 12 bits, 0xFFF = invalide) de la page 0x19.</summary>
    public static bool TryGetTrainerData(ReadOnlySpan<byte> message, out int? cadenceRpm, out int? powerW)
    {
        cadenceRpm = null; powerW = null;
        if (message.Length < MessageLength || message[4] != PageTrainerData) return false;
        if (message[6] != 0xFF) cadenceRpm = message[6];
        int power = message[9] | ((message[10] & 0x0F) << 8);
        if (power != 0xFFF) powerW = power;
        return true;
    }

    /// <summary>Résumé lisible d'un message (journal) : page et champs principaux.</summary>
    public static string Describe(ReadOnlySpan<byte> message)
    {
        if (message.Length < MessageLength) return Convert.ToHexString(message);
        byte page = message[4];
        return page switch
        {
            PageTrackResistance when TryParseTrack(message, out var g, out var crr) => $"FE-C 0x33 pente {g * 0.01:0.00} % roulement {crr:0.0000}",
            PageTargetPower => $"FE-C 0x31 puissance cible {(message[10] | (message[11] << 8)) * 0.25:0} W",
            PageBasicResistance => $"FE-C 0x30 résistance {message[11] * 0.5:0.0} %",
            PageWindResistance when TryParseWindCw(message, out var cw) => $"FE-C 0x32 traînée {cw:0.00} kg/m",
            PageUserConfiguration => $"FE-C 0x37 configuration ({(message[5] | (message[6] << 8)) * 0.01:0.0} kg)",
            _ => $"FE-C page 0x{page:X2}",
        };
    }
}
