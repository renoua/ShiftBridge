using System.Numerics;
using System.Security.Cryptography;

namespace ShiftBridge.Core.Controllers.Sram;

/// <summary>
/// Cryptographie de l'appairage « SRAMBond » v1 des composants SRAM AXS, d'après la rétro-ingénierie publique
/// (GaborWnuk/sram-axs, PROTOCOL.md) : échange Diffie-Hellman sur 128 bits (g = 5, p = 2^128 − 713), puis AES-128-EAX.
/// Tout est hors ligne et sans état. Non officiel : SRAM peut changer son protocole sans préavis.
/// </summary>
public static class SramCrypto
{
    /// <summary>p = 2^128 − 713 = 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFD37.</summary>
    public static readonly BigInteger Modulus = (BigInteger.One << 128) - 713;
    public const int Generator = 5;
    public const int KeyLength = 16;

    private static BigInteger FromBigEndian(byte[] b) => new(b, isUnsigned: true, isBigEndian: true);
    private static BigInteger FromLittleEndian(byte[] b) => new(b, isUnsigned: true, isBigEndian: false);

    private static byte[] ToBigEndian16(BigInteger v)
    {
        var raw = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length > KeyLength) throw new CryptographicException("valeur trop grande");
        var b = new byte[KeyLength];
        raw.CopyTo(b, KeyLength - raw.Length);
        return b;
    }

    /// <summary>
    /// Clé publique = 5^privée mod p, sur 16 octets gros-boutiste. Les 16 octets de la clé PRIVÉE forment l'exposant en
    /// PETIT-boutiste (c'est ce que donne le vecteur de test publié ; voir les tests).
    /// </summary>
    public static byte[] PublicKey(byte[] privateKey)
    {
        CheckLength(privateKey, nameof(privateKey));
        return ToBigEndian16(BigInteger.ModPow(Generator, FromLittleEndian(privateKey), Modulus));
    }

    /// <summary>Secret partagé = clé publique de l'appareil ^ privée mod p (16 octets gros-boutiste).</summary>
    public static byte[] SharedSecret(byte[] devicePublicKey, byte[] privateKey)
    {
        CheckLength(devicePublicKey, nameof(devicePublicKey));
        CheckLength(privateKey, nameof(privateKey));
        return ToBigEndian16(BigInteger.ModPow(FromBigEndian(devicePublicKey), FromLittleEndian(privateKey), Modulus));
    }

    /// <summary>Clé privée aléatoire de 16 octets (jamais nulle).</summary>
    public static byte[] NewPrivateKey()
    {
        byte[] k;
        do k = RandomNumberGenerator.GetBytes(KeyLength); while (k.All(b => b == 0));
        return k;
    }

    /// <summary>
    /// Déchiffre le « blob » de 48 octets de l'appairage : nonce (16) ‖ texte chiffré (16) ‖ étiquette (16), AES-EAX avec le
    /// secret partagé. Retourne la clé de l'appareil (16 octets), ou null si l'étiquette est invalide.
    /// </summary>
    public static byte[]? DecryptBondBlob(byte[] sharedSecret, byte[] blob)
    {
        if (blob.Length != 48) return null;
        return EaxDecrypt(sharedSecret, blob[..16], blob[16..32], blob[32..48]);
    }

    /// <summary>
    /// Déchiffre une trame d'état : nonce (16) ‖ texte chiffré (n) ‖ étiquette (16). Null si l'étiquette est invalide
    /// (clé périmée, p. ex. après un nouvel appairage par l'appli SRAM).
    /// </summary>
    public static byte[]? DecryptFrame(byte[] deviceKey, byte[] frame)
    {
        if (frame.Length < 33) return null;
        return EaxDecrypt(deviceKey, frame[..16], frame[16..^16], frame[^16..]);
    }

    private static void CheckLength(byte[] b, string name)
    {
        if (b.Length != KeyLength) throw new ArgumentException($"{name} : {KeyLength} octets attendus", name);
    }

    // ---------- AES-128-EAX (en-tête vide) ----------

    public static byte[]? EaxDecrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag)
    {
        if (key.Length != 16 || nonce.Length != 16 || tag.Length != 16) return null;
        using var aes = Aes.Create();
        aes.Key = key;
        var n = Omac(aes, 0, nonce);
        var h = Omac(aes, 1, []);
        var c = Omac(aes, 2, ciphertext);
        var expected = new byte[16];
        for (int i = 0; i < 16; i++) expected[i] = (byte)(n[i] ^ h[i] ^ c[i]);
        if (!CryptographicOperations.FixedTimeEquals(expected, tag)) return null;
        return Ctr(aes, n, ciphertext);
    }

    /// <summary>Chiffrement EAX : sert aux tests (fabriquer des trames) ; l'appareil, lui, ne fait que chiffrer.</summary>
    public static (byte[] Ciphertext, byte[] Tag) EaxEncrypt(byte[] key, byte[] nonce, byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var n = Omac(aes, 0, nonce);
        var h = Omac(aes, 1, []);
        var ct = Ctr(aes, n, plaintext);
        var c = Omac(aes, 2, ct);
        var tag = new byte[16];
        for (int i = 0; i < 16; i++) tag[i] = (byte)(n[i] ^ h[i] ^ c[i]);
        return (ct, tag);
    }

    /// <summary>OMAC^t(X) = CMAC([0 × 15 ‖ t] ‖ X).</summary>
    private static byte[] Omac(Aes aes, byte t, byte[] data)
    {
        var msg = new byte[16 + data.Length];
        msg[15] = t;
        data.CopyTo(msg, 16);
        return Cmac(aes, msg);
    }

    private static byte[] Cmac(Aes aes, byte[] msg)
    {
        var l = aes.EncryptEcb(new byte[16], PaddingMode.None);
        var k1 = Double(l);
        var k2 = Double(k1);
        int blocks = Math.Max(1, (msg.Length + 15) / 16);
        bool complete = msg.Length > 0 && msg.Length % 16 == 0;
        var last = new byte[16];
        int lastStart = (blocks - 1) * 16;
        int lastLen = msg.Length - lastStart;
        Array.Copy(msg, lastStart, last, 0, lastLen);
        if (!complete) last[lastLen] = 0x80;
        var k = complete ? k1 : k2;
        for (int i = 0; i < 16; i++) last[i] ^= k[i];

        var x = new byte[16];
        for (int b = 0; b < blocks - 1; b++)
        {
            for (int i = 0; i < 16; i++) x[i] ^= msg[b * 16 + i];
            x = aes.EncryptEcb(x, PaddingMode.None);
        }
        for (int i = 0; i < 16; i++) x[i] ^= last[i];
        return aes.EncryptEcb(x, PaddingMode.None);
    }

    private static byte[] Double(byte[] b)
    {
        var r = new byte[16];
        int carry = 0;
        for (int i = 15; i >= 0; i--)
        {
            int v = (b[i] << 1) | carry;
            r[i] = (byte)v;
            carry = (b[i] >> 7) & 1;
        }
        if ((b[0] & 0x80) != 0) r[15] ^= 0x87;
        return r;
    }

    private static byte[] Ctr(Aes aes, byte[] counter0, byte[] data)
    {
        var counter = (byte[])counter0.Clone();
        var output = new byte[data.Length];
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            var stream = aes.EncryptEcb(counter, PaddingMode.None);
            int n = Math.Min(16, data.Length - offset);
            for (int i = 0; i < n; i++) output[offset + i] = (byte)(data[offset + i] ^ stream[i]);
            for (int i = 15; i >= 0; i--) if (++counter[i] != 0) break; // incrément gros-boutiste
        }
        return output;
    }
}
