using ShiftBridge.Core.Localization;
using System.Net.Sockets;
using ShiftBridge.Core.DirCon;

namespace ShiftBridge.Platform.Trainer;

public sealed class DirConException(string message, byte code) : IOException(message)
{
    public byte Code { get; } = code;
}

/// <summary>
/// Client DirCon (Wahoo Direct Connect) : se connecte au trainer (ex. KICKR en Wi-Fi) comme le ferait une appli
/// d'entraînement. Requêtes numérotées (séquence 1..255) avec attente de la réponse correspondante.
/// </summary>
public sealed class DirConClient : ITrainerLink
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly TcpClient _tcp = new() { NoDelay = true };
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<(byte Id, byte Seq), TaskCompletionSource<DirConPacket>> _pending = [];
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private NetworkStream? _stream;
    private byte _seq;

    public bool IsConnected => _tcp.Connected;

    /// <summary>Notification (ou indication) reçue du trainer : caractéristique + valeur.</summary>
    public event Action<Guid, byte[]>? Notification;
    public event Action? Disconnected;
    public event Action<string>? Log;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        await _tcp.ConnectAsync(host, port, ct);
        _stream = _tcp.GetStream();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ReadLoopAsync(_cts.Token);
    }

    public async Task<List<Guid>> DiscoverServicesAsync()
    {
        var r = await RequestAsync(DirConId.DiscoverServices, []);
        var list = new List<Guid>();
        for (int i = 0; i + 16 <= r.Payload.Length; i += 16) list.Add(DirConPacket.ReadUuid(r.Payload.AsSpan(i, 16)));
        return list;
    }

    public async Task<List<(Guid Uuid, byte Props)>> DiscoverCharacteristicsAsync(Guid service)
    {
        var r = await RequestAsync(DirConId.DiscoverCharacteristics, DirConPacket.UuidBytes(service));
        var list = new List<(Guid, byte)>();
        for (int i = 16; i + 17 <= r.Payload.Length; i += 17)
            list.Add((DirConPacket.ReadUuid(r.Payload.AsSpan(i, 16)), r.Payload[i + 16]));
        return list;
    }

    public async Task<byte[]> ReadAsync(Guid ch)
    {
        var r = await RequestAsync(DirConId.ReadCharacteristic, DirConPacket.UuidBytes(ch));
        return r.Payload.Length > 16 ? r.Payload[16..] : [];
    }

    public Task WriteAsync(Guid ch, byte[] value) =>
        RequestAsync(DirConId.WriteCharacteristic, [.. DirConPacket.UuidBytes(ch), .. value]);

    /// <summary>
    /// Active les notifications. La spec prévoit un octet « activer » après l'UUID ; certains clients (TPV) l'omettent.
    /// On essaie d'abord la forme de la spec, puis l'UUID seul si le trainer la refuse.
    /// </summary>
    public async Task EnableNotificationsAsync(Guid ch, bool enable = true)
    {
        var uuid = DirConPacket.UuidBytes(ch);
        try { await RequestAsync(DirConId.EnableNotifications, [.. uuid, enable ? (byte)1 : (byte)0]); }
        catch (DirConException)
        {
            await RequestAsync(DirConId.EnableNotifications, uuid);
        }
    }

    private async Task<DirConPacket> RequestAsync(DirConId id, byte[] payload)
    {
        if (_stream is null) throw new IOException(Strings.Err_DirConNotConnected);
        byte seq;
        var tcs = new TaskCompletionSource<DirConPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            seq = ++_seq;
            if (seq == 0) seq = ++_seq; // 0 est réservé aux notifications
            _pending[((byte)id, seq)] = tcs;
        }

        // Un seul délai pour toute la requête (attente du tour d'écriture, écriture, réponse) ; l'entrée en attente est
        // retirée quoi qu'il arrive, même si l'écriture échoue.
        using var timeout = new CancellationTokenSource(RequestTimeout);
        try
        {
            await _writeLock.WaitAsync(timeout.Token);
            try { await _stream.WriteAsync(new DirConPacket((byte)id, seq, 0, payload).Encode(), timeout.Token); }
            catch (OperationCanceledException)
            {
                // Écriture interrompue au milieu d'un paquet : le flux n'est plus fiable, la liaison est coupée (reconnexion).
                try { _tcp.Dispose(); } catch { }
                throw;
            }
            finally { _writeLock.Release(); }

            var response = await tcs.Task.WaitAsync(timeout.Token);
            if (response.ResponseCode != DirConResponse.Success)
                throw new DirConException(Loc.Fmt(Strings.Err_DirConRefused, id, response.ResponseCode), response.ResponseCode);
            return response;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(Loc.Fmt(Strings.Err_DirConTimeout, id, RequestTimeout.TotalSeconds));
        }
        finally { lock (_lock) _pending.Remove(((byte)id, seq)); }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var parser = new DirConStreamParser();
        var buf = new byte[2048];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream!.ReadAsync(buf, ct);
                if (n == 0) break;
                foreach (var p in parser.Feed(buf.AsSpan(0, n))) Dispatch(p);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (_lock)
                foreach (var t in _pending.Values) t.TrySetException(new IOException(Strings.Err_DirConLost));
            Disconnected?.Invoke();
        }
    }

    private void Dispatch(DirConPacket p)
    {
        if (p.Id == (byte)DirConId.Notification && p.Payload.Length >= 16)
        {
            // Une erreur d'un abonné ne doit pas couper la liaison au trainer (elle terminait la boucle de lecture).
            try { Notification?.Invoke(DirConPacket.ReadUuid(p.Payload), p.Payload[16..]); }
            catch (Exception ex) { Log?.Invoke($"DirCon : traitement d'une notification en échec ({ex.Message})"); }
            return;
        }
        TaskCompletionSource<DirConPacket>? tcs;
        lock (_lock) _pending.TryGetValue((p.Id, p.Sequence), out tcs);
        if (tcs is null) { Log?.Invoke($"DirCon : réponse inattendue id={p.Id} seq={p.Sequence}"); return; }
        tcs.TrySetResult(p);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _tcp.Dispose(); } catch { }
        await Task.CompletedTask;
    }
}
