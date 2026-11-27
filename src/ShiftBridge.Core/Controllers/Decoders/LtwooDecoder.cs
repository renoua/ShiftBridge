namespace ShiftBridge.Core.Controllers.Decoders;

/// <summary>
/// L-TWOO eRX / eR9 (Nordic UART). Le groupe ne signale pas les appuis : on l'interroge (position du pignon arrière toutes les
/// 250 ms) et un changement de position est traduit en appuis (jusqu'à 3 par changement). Protocole piloté par l'hôte.
/// Requête : <c>A5 | code PIN (3 octets ASCII) | 46 46 46 | opcode | XOR de tout ce qui précède</c>.
/// Réponse : <c>5A | 3 octets | 46 46 46 | opcode (2 octets) | charge utile | XOR</c> (le XOR de la trame entière vaut 0).
/// Opcodes : 0x20 hello (charge utile : nombre de vitesses), 0x09 pignon arrière (brut), 0x11 plateau avant (brut), 0x0A batterie (%).
/// Un pignon brut qui DIMINUE = vitesse + ; qui augmente = vitesse −. Une réponse portant « EE EE EE » = mauvais code PIN.
/// </summary>
public sealed class LtwooDecoder : IButtonDecoder, IPollingDecoder
{
    private const byte Header = 0xA5, ReplyHeader = 0x5A, Filler = 0x46;
    private int? _rear, _front;

    public LtwooDecoder(string? pin = null) => Pin = NormalizePin(pin);

    /// <summary>Trois caractères ASCII : complété par des « 0 » si trop court, tronqué si trop long.</summary>
    public static string NormalizePin(string? pin)
    {
        var p = new string((pin ?? "").Where(c => c is >= ' ' and <= '~').ToArray());
        return p.Length >= 3 ? p[..3] : p.PadRight(3, '0');
    }

    public string Pin { get; }

    public static byte[] Request(string pin, params byte[] opcode)
    {
        var frame = new List<byte> { Header };
        frame.AddRange(System.Text.Encoding.ASCII.GetBytes(NormalizePin(pin)));
        frame.AddRange([Filler, Filler, Filler]);
        frame.AddRange(opcode);
        byte xor = 0;
        foreach (var b in frame) xor ^= b;
        frame.Add(xor);
        return [.. frame];
    }

    public IReadOnlyList<byte[]> InitFrames => [Request(Pin, 0x20, 0x01, 0x00), Request(Pin, 0x0A, 0x00)];
    public IReadOnlyList<byte[]> PollFrames => [Request(Pin, 0x09, 0x00), Request(Pin, 0x11, 0x00)];
    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(250);

    public DecodedFrame? Decode(Guid characteristic, byte[] data)
    {
        if (data.Length < 11 || data[0] != ReplyHeader) return null;
        // Le groupe renvoie EE EE EE à la place du code PIN (octets 1 à 3) quand il le refuse. Chercher ce motif ailleurs
        // (données, somme) pouvait signaler à tort un mauvais code.
        if (data[1] == 0xEE && data[2] == 0xEE && data[3] == 0xEE)
            throw new InvalidOperationException("code PIN refusé par le groupe");
        byte xor = 0;
        foreach (var b in data) xor ^= b;
        if (xor != 0) return null;

        byte opcode = data[7];
        int value = data[9];
        switch (opcode)
        {
            case 0x0A: return new DecodedFrame(null, DecodedFrame.NoTaps, value);
            case 0x09:
                {
                    var taps = new List<string>();
                    if (_rear is { } last && value != last)
                        for (int i = 0; i < Math.Min(Math.Abs(value - last), 3); i++) taps.Add(value < last ? "Up" : "Down");
                    _rear = value;
                    return new DecodedFrame(null, taps);
                }
            case 0x11:
                {
                    var taps = _front is { } last && value != last ? new List<string> { "Front" } : [];
                    _front = value;
                    return new DecodedFrame(null, taps);
                }
            default: return DecodedFrame.Nothing; // hello (0x20) et autres
        }
    }
}
