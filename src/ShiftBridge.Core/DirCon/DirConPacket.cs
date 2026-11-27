namespace ShiftBridge.Core.DirCon;

public enum DirConId : byte
{
    DiscoverServices = 0x01,
    DiscoverCharacteristics = 0x02,
    ReadCharacteristic = 0x03,
    WriteCharacteristic = 0x04,
    EnableNotifications = 0x05,
    Notification = 0x06,
}

public static class DirConResponse
{
    public const byte Success = 0x00;
    public const byte UnknownMessageType = 0x01;
    public const byte UnexpectedError = 0x02;
    public const byte ServiceNotFound = 0x03;
    public const byte CharacteristicNotFound = 0x04;
    public const byte OperationNotSupported = 0x05;
    public const byte WriteFailed = 0x06;
}

/// <summary>Bits de propriétés d'une caractéristique dans DISCOVER_CHARACTERISTICS.</summary>
public static class DirConProps
{
    public const byte Read = 0x01, Write = 0x02, Notify = 0x04, Indicate = 0x08;
}

/// <summary>
/// Paquet Wahoo Direct Connect (DirCon / WFTNP) : en-tête de 6 octets
/// [version=1, id, séquence, code réponse, longueur (uint16 big-endian)] suivi de la charge utile.
/// Format d'après Berg0162/DirCon et qdomyos-zwift (GPL-3.0), non documenté officiellement.
/// </summary>
public sealed record DirConPacket(byte Id, byte Sequence, byte ResponseCode, byte[] Payload)
{
    public const int HeaderSize = 6;

    public byte[] Encode()
    {
        var data = new byte[HeaderSize + Payload.Length];
        data[0] = 1;
        data[1] = Id;
        data[2] = Sequence;
        data[3] = ResponseCode;
        data[4] = (byte)(Payload.Length >> 8);
        data[5] = (byte)Payload.Length;
        Payload.CopyTo(data, HeaderSize);
        return data;
    }

    /// <summary>UUID de 16 octets en big-endian (ordre du texte), sans forme courte 16 bits.</summary>
    public static byte[] UuidBytes(Guid uuid)
    {
        var b = new byte[16];
        uuid.TryWriteBytes(b, bigEndian: true, out _);
        return b;
    }

    public static Guid ReadUuid(ReadOnlySpan<byte> data) => new(data[..16], bigEndian: true);

    // ----- Constructeurs de réponses -----

    public static DirConPacket Error(byte id, byte seq, byte code) => new(id, seq, code, []);

    public static DirConPacket ServicesResponse(byte seq, IEnumerable<Guid> services) =>
        new((byte)DirConId.DiscoverServices, seq, DirConResponse.Success, [.. services.SelectMany(UuidBytes)]);

    public static DirConPacket CharacteristicsResponse(byte seq, Guid service, IEnumerable<(Guid Uuid, byte Props)> chars) =>
        new((byte)DirConId.DiscoverCharacteristics, seq, DirConResponse.Success,
            [.. UuidBytes(service), .. chars.SelectMany(c => UuidBytes(c.Uuid).Append(c.Props))]);

    public static DirConPacket ReadResponse(byte seq, Guid ch, byte[] value) =>
        new((byte)DirConId.ReadCharacteristic, seq, DirConResponse.Success, [.. UuidBytes(ch), .. value]);

    public static DirConPacket AckResponse(DirConId id, byte seq, Guid ch) =>
        new((byte)id, seq, DirConResponse.Success, UuidBytes(ch));

    /// <summary>Notification non sollicitée : séquence toujours 0.</summary>
    public static DirConPacket NotificationPacket(Guid ch, byte[] value) =>
        new((byte)DirConId.Notification, 0, DirConResponse.Success, [.. UuidBytes(ch), .. value]);
}

/// <summary>
/// Découpe le flux TCP DirCon en paquets complets (l'en-tête donne la longueur). Tampon unique réutilisé (plus de copie de
/// tout le reste à chaque paquet) ; une longueur aberrante (flux corrompu) n'est plus attendue indéfiniment : elle déclenche
/// une resynchronisation, comme une version inconnue.
/// </summary>
public sealed class DirConStreamParser
{
    /// <summary>Charge utile maximale acceptée (la plus longue réponse réelle, la liste des caractéristiques, fait quelques centaines d'octets).</summary>
    public const int MaxPayload = 4096;

    private byte[] _buffer = new byte[1024];
    private int _start, _count;

    public IEnumerable<DirConPacket> Feed(ReadOnlySpan<byte> data)
    {
        Append(data);
        var packets = new List<DirConPacket>();
        while (_count >= DirConPacket.HeaderSize)
        {
            var b = _buffer.AsSpan(_start, _count);
            int len = (b[4] << 8) | b[5];
            if (b[0] != 1 || len > MaxPayload)
            {
                // Version inconnue ou longueur impossible : on avance jusqu'au prochain octet de version plausible.
                int next = b[1..].IndexOf((byte)1);
                int skip = next < 0 ? _count : next + 1;
                _start += skip; _count -= skip;
                continue;
            }
            if (_count < DirConPacket.HeaderSize + len) break;
            packets.Add(new DirConPacket(b[1], b[2], b[3], b.Slice(DirConPacket.HeaderSize, len).ToArray()));
            _start += DirConPacket.HeaderSize + len;
            _count -= DirConPacket.HeaderSize + len;
        }
        if (_count == 0) _start = 0;
        return packets;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_start + _count + data.Length > _buffer.Length)
        {
            // Compacte d'abord, n'agrandit que si nécessaire.
            if (_count + data.Length > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _count + data.Length));
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _count);
            _start = 0;
        }
        data.CopyTo(_buffer.AsSpan(_start + _count));
        _count += data.Length;
    }
}
