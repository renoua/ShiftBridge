using ShiftBridge.Core.Localization;
using System.Net;
using System.Net.Sockets;
using ShiftBridge.Core.Obc;

namespace ShiftBridge.Platform.Obc;

/// <summary>
/// OBC sur TCP + mDNS. Le port 36867 est essayé en premier, puis les suivants ; le port réellement lié est annoncé.
/// Plusieurs applis peuvent se connecter en même temps : les états de boutons sont diffusés à toutes.
/// </summary>
public sealed class ObcTcpServer : IObcTransport
{
    public const int PreferredPort = 36867;

    private readonly string _deviceName;
    private readonly IMdnsAdvertiser _mdns;
    private readonly List<ClientConn> _clients = [];
    private readonly object _lock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int? _battery;

    public ObcTcpServer(string deviceName = "ShiftBridge", IMdnsAdvertiser? mdns = null)
    {
        _deviceName = deviceName;
        _mdns = mdns ?? new MdnsAdvertiser();
    }

    public string Name => "OBC réseau";
    public int Port { get; private set; }
    public bool HasClient { get { lock (_lock) return _clients.Count > 0; } }
    public ObcAppInfo? CurrentAppInfo { get; private set; }

    public event Action<ObcAppInfo>? AppInfoReceived;
    public event Action<ObcHaptic>? HapticReceived;
    public event Action? ClientsChanged;
    public event Action<string>? Log;

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        for (int port = PreferredPort; port < PreferredPort + 20; port++)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                Port = port;
                break;
            }
            catch (SocketException) { _listener = null; }
        }
        if (_listener is null) throw new IOException(Strings.Err_NoFreePortObc);

        try
        {
            _mdns.Advertise(_deviceName, "_openbikecontrol._tcp", (ushort)Port, new Dictionary<string, string>
            {
                ["version"] = "1",
                ["id"] = Environment.MachineName,
                ["name"] = _deviceName,
                ["service-uuids"] = "d273f680-d548-419d-b9d1-fa0472345229",
                ["manufacturer"] = "ShiftBridge",
                ["model"] = "ShiftBridge",
            }, m => Log?.Invoke(m));
        }
        catch (Exception ex)
        {
            // Le serveur reste utilisable en saisissant l'IP à la main.
            Log?.Invoke($"mDNS indisponible : {ex.Message}");
        }

        Log?.Invoke($"OBC réseau en écoute sur le port {Port}");
        _ = AcceptLoopAsync(_cts.Token);
        _ = StatusLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _mdns.Stop();
        _listener?.Stop();
        _listener = null;
        lock (_lock)
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
        CurrentAppInfo = null;
        ClientsChanged?.Invoke();
        return Task.CompletedTask;
    }

    public async Task SendButtonStateAsync(byte[] message)
    {
        List<ClientConn> snapshot;
        lock (_lock) snapshot = [.. _clients];
        await Task.WhenAll(snapshot.Select(c => c.SendAsync(message)));
    }

    public void SetBattery(int? percent) => _battery = percent;

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        while (!ct.IsCancellationRequested && listener is not null)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }

            tcp.NoDelay = true; // les appuis de boutons doivent partir immédiatement
            var conn = new ClientConn(tcp);
            lock (_lock) _clients.Add(conn);
            Log?.Invoke($"Client OBC connecté : {tcp.Client.RemoteEndPoint}");
            ClientsChanged?.Invoke();
            _ = conn.SendAsync(ObcMessages.EncodeDeviceStatus(_battery, true));
            _ = ReadLoopAsync(conn, ct);
        }
    }

    private async Task ReadLoopAsync(ClientConn conn, CancellationToken ct)
    {
        var parser = new ObcStreamParser();
        var buf = new byte[256];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await conn.Stream.ReadAsync(buf, ct);
                if (n == 0) break;
                foreach (var msg in parser.Feed(buf.AsSpan(0, n)))
                {
                    if (ObcMessages.TryDecodeAppInfo(msg, out var info) && info is not null)
                    {
                        CurrentAppInfo = info;
                        Log?.Invoke($"App Info : {info.AppId} {info.AppVersion}");
                        AppInfoReceived?.Invoke(info);
                    }
                    else if (ObcMessages.TryDecodeHaptic(msg, out var h))
                    {
                        HapticReceived?.Invoke(h);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (_lock) _clients.Remove(conn);
            conn.Dispose();
            if (!HasClient) CurrentAppInfo = null;
            Log?.Invoke("Client OBC déconnecté");
            ClientsChanged?.Invoke();
        }
    }

    /// <summary>Statut batterie périodique (30 s), comme le recommande la spec pour TCP.</summary>
    private async Task StatusLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(ct))
                await SendButtonStateAsync(ObcMessages.EncodeDeviceStatus(_battery, true));
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _mdns.Dispose();
    }

    private sealed class ClientConn(TcpClient tcp) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        public NetworkStream Stream { get; } = tcp.GetStream();

        public async Task SendAsync(byte[] data)
        {
            await _writeLock.WaitAsync();
            try { await TcpSend.WriteAsync(Stream, data, Dispose); } // client bloqué : coupé, la boucle de lecture nettoie
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { /* la boucle de lecture nettoie */ }
            finally { _writeLock.Release(); }
        }

        public void Dispose() { try { tcp.Dispose(); } catch { } }
    }
}
