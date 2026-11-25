namespace ShiftBridge.Core.Zwift;

public enum ZwiftModel { Unknown, Play, Click, ClickV2, Ride }
public enum ZwiftSide { None, Left, Right }

/// <summary>Type d'appareil lu dans les données constructeur (company id 0x094A), premier octet.</summary>
public readonly record struct ZwiftDeviceType(ZwiftModel Model, ZwiftSide Side, byte RawCode)
{
    /// <summary>Les codes 0x0A/0x0B du Click v2 sont marqués « non confirmés » dans les sources publiques.</summary>
    public static ZwiftDeviceType FromManufacturerData(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return new(ZwiftModel.Unknown, ZwiftSide.None, 0);
        byte code = data[0];
        return code switch
        {
            0x02 => new(ZwiftModel.Play, ZwiftSide.Right, code),
            0x03 => new(ZwiftModel.Play, ZwiftSide.Left, code),
            // Play firmware récent : le code ne dit pas le côté (les deux manettes peuvent l'annoncer). Côté inconnu :
            // l'analyse de la trame lit le champ 1 (0 = droite), sinon les deux manettes passeraient pour la gauche.
            0x0E => new(ZwiftModel.Play, ZwiftSide.None, code),
            0x07 => new(ZwiftModel.Ride, ZwiftSide.Right, code),
            0x08 => new(ZwiftModel.Ride, ZwiftSide.Left, code),
            0x09 => new(ZwiftModel.Click, ZwiftSide.None, code),
            0x0A => new(ZwiftModel.ClickV2, ZwiftSide.Right, code),
            0x0B => new(ZwiftModel.ClickV2, ZwiftSide.Left, code),
            _ => new(ZwiftModel.Unknown, ZwiftSide.None, code),
        };
    }

    public bool UsesRideProtocol => Model is ZwiftModel.Ride or ZwiftModel.ClickV2;
}
