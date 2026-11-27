namespace ShiftBridge.Core.Trainer;

/// <summary>Lecture/modification des trames FTMS (Fitness Machine Service, 0x1826) utiles au proxy.</summary>
public static class FtmsCodec
{
    public static readonly Guid FitnessMachineService = BaseUuid(0x1826);
    public static readonly Guid IndoorBikeData = BaseUuid(0x2AD2);
    public static readonly Guid ControlPoint = BaseUuid(0x2AD9);

    public static readonly Guid FitnessMachineFeature = BaseUuid(0x2ACC);
    public static readonly Guid FitnessMachineStatus = BaseUuid(0x2ADA);

    // Opcodes du Control Point (FTMS)
    public const byte OpRequestControl = 0x00;
    public const byte OpReset = 0x01;
    public const byte OpSetTargetSpeed = 0x02;
    public const byte OpSetTargetInclination = 0x03;
    public const byte OpSetTargetResistance = 0x04;
    public const byte OpSetTargetPower = 0x05;
    public const byte OpSetTargetHeartRate = 0x06;
    public const byte OpStart = 0x07;
    public const byte OpStop = 0x08;
    public const byte OpSetSimulation = 0x11;
    public const byte OpResponse = 0x80;

    /// <summary>Statut « paramètres de simulation modifiés » (Fitness Machine Status, opcode 0x12) : [0x12, vent, pente, crr, cw].</summary>
    public const byte StatusSimulationChanged = 0x12;

    /// <summary>Vrai pour les commandes qui fixent une cible (et font donc sortir d'un autre mode : ERG, simulation…).</summary>
    public static bool IsTargetCommand(byte opcode) =>
        opcode is OpSetTargetSpeed or OpSetTargetInclination or OpSetTargetResistance or OpSetTargetPower
            or OpSetTargetHeartRate or OpSetSimulation;

    /// <summary>
    /// Remplace tous les paramètres du statut 0x12 (vent, pente, roulement, traînée) par ceux que l'APPLI a demandés : en mise
    /// à l'échelle complète, le proxy réécrit aussi le roulement et la traînée, que l'appli ne doit pas voir revenir modifiés.
    /// </summary>
    public static byte[] RewriteStatusSimulation(ReadOnlySpan<byte> status, SimulationParams app)
    {
        var copy = status.ToArray();
        if (copy.Length >= 7 && copy[0] == StatusSimulationChanged)
        {
            short wind = app.WindRaw;
            copy[1] = (byte)(wind & 0xFF);
            copy[2] = (byte)((wind >> 8) & 0xFF);
            copy[3] = (byte)(app.GradeRaw & 0xFF);
            copy[4] = (byte)((app.GradeRaw >> 8) & 0xFF);
            copy[5] = app.CrrRaw;
            copy[6] = app.CwRaw;
        }
        return copy;
    }

    /// <summary>Remplace la pente du statut 0x12 par celle que l'APPLI a demandée (elle ne doit pas voir la pente réécrite).</summary>
    public static byte[] RewriteStatusGrade(ReadOnlySpan<byte> status, short appGradeRaw)
    {
        var copy = status.ToArray();
        if (copy.Length >= 7 && copy[0] == StatusSimulationChanged)
        {
            copy[3] = (byte)(appGradeRaw & 0xFF);
            copy[4] = (byte)((appGradeRaw >> 8) & 0xFF);
        }
        return copy;
    }

    public static Guid BaseUuid(ushort shortUuid) => new($"0000{shortUuid:x4}-0000-1000-8000-00805f9b34fb");

    /// <summary>Set Indoor Bike Simulation Parameters : [0x11, vent(s16, 0,001 m/s), pente(s16, 0,01 %), crr(u8), cw(u8)].</summary>
    public static bool TryParseSimulation(ReadOnlySpan<byte> cp, out SimulationParams p)
    {
        p = default;
        if (cp.Length < 7 || cp[0] != OpSetSimulation) return false;
        short wind = (short)(cp[1] | (cp[2] << 8));
        short grade = (short)(cp[3] | (cp[4] << 8));
        p = new SimulationParams(wind * 0.001, grade, cp[5], cp[6]);
        return true;
    }

    /// <summary>Réécrit tous les champs d'une commande de simulation (vent, pente, roulement, traînée).</summary>
    public static byte[] WithSimulation(ReadOnlySpan<byte> cp, SimulationParams p)
    {
        var copy = cp.ToArray();
        short wind = p.WindRaw;
        copy[1] = (byte)(wind & 0xFF);
        copy[2] = (byte)((wind >> 8) & 0xFF);
        copy[3] = (byte)(p.GradeRaw & 0xFF);
        copy[4] = (byte)((p.GradeRaw >> 8) & 0xFF);
        copy[5] = p.CrrRaw;
        copy[6] = p.CwRaw;
        return copy;
    }

    public static byte[] WithGrade(ReadOnlySpan<byte> cp, short gradeRaw)
    {
        var copy = cp.ToArray();
        copy[3] = (byte)(gradeRaw & 0xFF);
        copy[4] = (byte)((gradeRaw >> 8) & 0xFF);
        return copy;
    }

    /// <summary>Indoor Bike Data : flags (u16), puis vitesse instantanée (u16, 0,01 km/h) si le bit 0 des flags vaut 0.</summary>
    public static bool TryGetSpeedMs(ReadOnlySpan<byte> data, out double speedMs)
    {
        speedMs = 0;
        if (data.Length < 4 || (data[0] & 0x01) != 0) return false;
        int raw = data[2] | (data[3] << 8);
        speedMs = raw / 100.0 / 3.6;
        return true;
    }

    /// <summary>
    /// Cadence instantanée (tr/min) de l'Indoor Bike Data. Les champs présents dépendent des flags : la vitesse instantanée est
    /// là si le bit 0 vaut 0, la vitesse moyenne si le bit 1 vaut 1 ; la cadence instantanée (u16, 0,5 tr/min) si le bit 2 vaut 1.
    /// </summary>
    public static bool TryGetCadenceRpm(ReadOnlySpan<byte> data, out double rpm)
    {
        rpm = 0;
        if (data.Length < 2) return false;
        ushort flags = (ushort)(data[0] | (data[1] << 8));
        if ((flags & 0x04) == 0) return false;

        int offset = 2;
        if ((flags & 0x01) == 0) offset += 2; // vitesse instantanée
        if ((flags & 0x02) != 0) offset += 2; // vitesse moyenne
        if (data.Length < offset + 2) return false;
        rpm = (data[offset] | (data[offset + 1] << 8)) * 0.5;
        return true;
    }

    /// <summary>Puissance instantanée (W) d'un Indoor Bike Data, si le champ est présent (bit 6 des fanions).</summary>
    public static bool TryGetPowerW(ReadOnlySpan<byte> data, out int watts)
    {
        watts = 0;
        if (data.Length < 2) return false;
        ushort flags = (ushort)(data[0] | (data[1] << 8));
        if ((flags & 0x40) == 0) return false;

        int offset = 2;
        if ((flags & 0x01) == 0) offset += 2; // vitesse instantanée
        if ((flags & 0x02) != 0) offset += 2; // vitesse moyenne
        if ((flags & 0x04) != 0) offset += 2; // cadence instantanée
        if ((flags & 0x08) != 0) offset += 2; // cadence moyenne
        if ((flags & 0x10) != 0) offset += 3; // distance totale
        if ((flags & 0x20) != 0) offset += 2; // niveau de résistance
        if (data.Length < offset + 2) return false;
        watts = (short)(data[offset] | (data[offset + 1] << 8));
        return true;
    }

    /// <summary>Retourne une copie dont la vitesse est multipliée par <paramref name="factor"/> (vitesse virtuelle).</summary>
    public static byte[] ScaleSpeed(ReadOnlySpan<byte> data, double factor)
    {
        var copy = data.ToArray();
        if (copy.Length < 4 || (copy[0] & 0x01) != 0) return copy;
        int raw = copy[2] | (copy[3] << 8);
        int scaled = (int)Math.Clamp(Math.Round(raw * factor), 0, ushort.MaxValue);
        copy[2] = (byte)(scaled & 0xFF);
        copy[3] = (byte)((scaled >> 8) & 0xFF);
        return copy;
    }

    /// <summary>Réponse du point de contrôle : [0x80, opcode demandé, résultat].</summary>
    public static bool IsResponseTo(ReadOnlySpan<byte> data, byte opcode) =>
        data.Length >= 3 && data[0] == OpResponse && data[1] == opcode;
}
