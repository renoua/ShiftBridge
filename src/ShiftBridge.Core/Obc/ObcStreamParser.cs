namespace ShiftBridge.Core.Obc;

/// <summary>
/// Découpe le flux TCP entrant (appli → device) en messages. La spec v1 n'a pas de préfixe de longueur :
/// on déduit la taille du type de message (0x03 = 4 octets, 0x04 = taille variable auto-décrite).
/// Un octet de type inconnu est ignoré pour se resynchroniser. Un message resté incomplet plus de
/// <see cref="StaleAfterMs"/> (en-tête App Info corrompu, longueur aberrante) est abandonné octet par octet : sans cela,
/// il avalerait tous les messages suivants de la connexion.
/// </summary>
public sealed class ObcStreamParser
{
    /// <summary>Au-delà, un message incomplet est considéré comme corrompu.</summary>
    public const long StaleAfterMs = 2000;

    private readonly List<byte> _buffer = [];
    /// <summary>Heure à laquelle le message en tête de tampon a commencé à attendre sa suite ; -1 si le tampon est vide.</summary>
    private long _partialSinceMs = -1;

    public IEnumerable<byte[]> Feed(ReadOnlySpan<byte> data) => Feed(data, Environment.TickCount64);

    public IEnumerable<byte[]> Feed(ReadOnlySpan<byte> data, long nowMs)
    {
        var messages = new List<byte[]>();
        // Message en tête jamais complété à temps : son premier octet saute, la boucle se resynchronise sur le suivant.
        if (_buffer.Count > 0 && _partialSinceMs >= 0 && nowMs - _partialSinceMs > StaleAfterMs)
        {
            _buffer.RemoveAt(0);
            _partialSinceMs = nowMs;
        }
        _buffer.AddRange(data);
        bool consumed = false;
        while (_buffer.Count > 0)
        {
            int len = ExpectedLength();
            if (len < 0) { _buffer.RemoveAt(0); consumed = true; continue; } // type inconnu : resynchronisation
            if (len == 0 || _buffer.Count < len) break;                       // message incomplet : on attend la suite
            messages.Add(_buffer.GetRange(0, len).ToArray());
            _buffer.RemoveRange(0, len);
            consumed = true;
        }
        if (_buffer.Count == 0) _partialSinceMs = -1;
        else if (consumed || _partialSinceMs < 0) _partialSinceMs = nowMs;
        return messages;
    }

    /// <returns>Taille du message complet, 0 si pas encore déterminable, -1 si type inconnu.</returns>
    private int ExpectedLength()
    {
        switch (_buffer[0])
        {
            case ObcMessages.TypeHaptic: return 4;
            case ObcMessages.TypeAppInfo:
                // [04, ver, idLen, id..., verLen, ver..., count, ids...]
                if (_buffer.Count < 3) return 0;
                int idLen = _buffer[2];
                int verLenPos = 3 + idLen;
                if (_buffer.Count <= verLenPos) return 0;
                int countPos = verLenPos + 1 + _buffer[verLenPos];
                if (_buffer.Count <= countPos) return 0;
                return countPos + 1 + _buffer[countPos];
            default: return -1;
        }
    }
}
