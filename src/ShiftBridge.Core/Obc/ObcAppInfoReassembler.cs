namespace ShiftBridge.Core.Obc;

/// <summary>
/// Réassemble les écritures App Info (souvent plus longues que la MTU) client par client, pour les périphériques OBC Bluetooth.
/// Sûr entre threads. Un reste incomplet plus vieux que <see cref="StaleAfterMs"/> est jeté avant la nouvelle écriture :
/// une App Info interrompue (client déconnecté au milieu) ne corrompt plus la suivante du même client.
/// </summary>
public sealed class ObcAppInfoReassembler
{
    public const long StaleAfterMs = 2000;

    private readonly object _lock = new();
    private readonly Dictionary<string, (ObcStreamParser Parser, long LastWriteMs)> _clients = [];

    /// <summary>Ajoute les octets reçus de <paramref name="clientKey"/> et renvoie les App Info complètes et valides.</summary>
    public List<ObcAppInfo> Feed(string clientKey, ReadOnlySpan<byte> data, long nowMs)
    {
        List<byte[]> messages;
        lock (_lock)
        {
            if (!_clients.TryGetValue(clientKey, out var entry) || nowMs - entry.LastWriteMs > StaleAfterMs)
                entry = (new ObcStreamParser(), nowMs);
            messages = [.. entry.Parser.Feed(data)];
            _clients[clientKey] = (entry.Parser, nowMs);
        }
        var result = new List<ObcAppInfo>();
        foreach (var msg in messages)
            if (ObcMessages.TryDecodeAppInfo(msg, out var info) && info is not null) result.Add(info);
        return result;
    }

    /// <summary>Oublie un client (déconnexion).</summary>
    public void Remove(string clientKey) { lock (_lock) _clients.Remove(clientKey); }

    public void Clear() { lock (_lock) _clients.Clear(); }
}
