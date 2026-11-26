namespace ShiftBridge.Core.Controllers.Sram;

/// <summary>
/// Texte clair (protobuf) des trames déchiffrées d'un dérailleur AXS. Champs entiers (varint) utilisés :
/// état en direct (d905000b) : 20 = plateau avant (absent en 1x), 21 = pignon arrière actuel (1..N), 22 = compensation ;
/// configuration (d9050025) : 24 = nombre de pignons arrière (observé par qdomyos-zwift ; non documenté ailleurs).
/// </summary>
public static class SramAxsFrames
{
    public const int FrontPositionField = 20, RearPositionField = 21, TrimField = 22, RearGearCountField = 24;
    public const int DefaultGearCount = 12;

    public static bool TryParseDrivetrain(byte[] plaintext, out int rear, out int? front)
    {
        rear = 0; front = null;
        int? r = null;
        foreach (var (field, value) in Varints(plaintext))
        {
            if (field == RearPositionField) r = (int)Math.Min(value, int.MaxValue);
            else if (field == FrontPositionField) front = (int)Math.Min(value, int.MaxValue);
        }
        if (r is not { } v || v < 1 || v > 24) return false; // 24 : plafond de plausibilité (le RD-GX en a 12)
        rear = v;
        return true;
    }

    public static int? TryParseGearCount(byte[] configPlaintext)
    {
        foreach (var (field, value) in Varints(configPlaintext))
            if (field == RearGearCountField && value is >= 1 and <= 24) return (int)value;
        return null;
    }

    /// <summary>
    /// Tous les champs entiers d'une trame déchiffrée (« 20=1 21=7 22=0 »), pour le diagnostic : sert à repérer un champ qui
    /// distinguerait le levier gauche du droit. Vide si la trame n'en contient aucun.
    /// </summary>
    public static string DescribeFields(byte[] plaintext) =>
        string.Join(" ", Varints(plaintext).Select(f => $"{f.Field}={f.Value}"));

    /// <summary>Champs varint (type de fil 0) d'un message protobuf ; les autres types sont sautés, un message tronqué s'arrête.</summary>
    private static IEnumerable<(int Field, ulong Value)> Varints(byte[] data)
    {
        int i = 0;
        while (i < data.Length)
        {
            if (!ReadVarint(data, ref i, out ulong tag)) yield break;
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            switch (wire)
            {
                case 0:
                    if (!ReadVarint(data, ref i, out ulong v)) yield break;
                    yield return (field, v);
                    break;
                case 1: i += 8; break;
                case 5: i += 4; break;
                case 2:
                    if (!ReadVarint(data, ref i, out ulong len) || len > (ulong)(data.Length - i)) yield break;
                    i += (int)len;
                    break;
                default: yield break;
            }
        }
    }

    private static bool ReadVarint(byte[] data, ref int i, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && i < data.Length; shift += 7)
        {
            byte b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }
}

/// <summary>
/// Transforme les changements de pignon arrière en appuis : le levier utilisé n'est PAS connu, seul le sens l'est.
/// Position qui diminue = pignon plus petit = « vitesse + » (même convention que qdomyos-zwift) ; qui augmente = « vitesse − ».
/// Un passage en butée (1 ou N) ne change rien : il est invisible. Plusieurs crans d'un coup donnent plusieurs appuis.
/// </summary>
public sealed class SramAxsShiftTracker
{
    public const int MaxTapsPerChange = 4;
    private int? _last;

    public int GearCount { get; set; } = SramAxsFrames.DefaultGearCount;

    public void Reset() => _last = null;

    public IReadOnlyList<string> Update(int rear)
    {
        if (_last is not { } last) { _last = rear; return []; } // la première lecture sert de référence
        int delta = rear - last;
        if (delta == 0) return [];
        if (Math.Abs(delta) > GearCount) return []; // valeur aberrante : on ne met pas la référence à jour
        _last = rear;
        string name = delta < 0 ? "Up" : "Down";
        return [.. Enumerable.Repeat(name, Math.Min(Math.Abs(delta), MaxTapsPerChange))];
    }
}
