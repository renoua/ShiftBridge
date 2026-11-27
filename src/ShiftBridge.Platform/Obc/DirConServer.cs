using ShiftBridge.Core.Localization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ShiftBridge.Core.DirCon;
using ShiftBridge.Core.Obc;

namespace ShiftBridge.Platform.Obc;

/// <summary>
/// OBC via Wahoo Direct Connect (DirCon) : le service OBC est présenté comme un « faux périphérique BLE » sur TCP.
/// Button State = notification, App Info et Haptic = écritures. Utilisé par TrainingPeaks Virtual en réseau.
/// </summary>
public sealed class DirConServer : IObcTransport
{
    public const int PreferredPort = 36866;

    private static readonly Guid Service = ObcGattUuids.Service;
    private static readonly Guid ButtonState = ObcGattUuids.ButtonState;
    private static readonly Guid Haptic = ObcGattUuids.Haptic;
    private static readonly Guid AppInfo = ObcGattUuids.AppInfo;

    // Device Information (0x180A) : un appareil DirCon qui n'annonce qu'un UUID propriétaire déroute certaines applis de trainer.
    private static readonly Guid DeviceInfo = Guid.Parse("0000180a-0000-1000-8000-00805f9b34fb");
    private static readonly Guid ManufacturerName = Guid.Parse("00002a29-0000-1000-8000-00805f9b34fb");
    private static readonly Guid ModelNumber = Guid.Parse("00002a24-0000-1000-8000-00805f9b34fb");

    private readonly IMdnsAdvertiser _mdns;
    private readonly List<Client> _clients = [];
    private readonly object _lock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private byte[] _lastButtonState = [ObcMessages.TypeButtonState];

    public DirConServer(IMdnsAdvertiser? mdns = null) => _mdns = mdns ?? new MdnsAdvertiser();

    public string Name => "OBC DirCon";
    public int Port { get; private set; }
    public bool HasClient { get { lock (_lock) return _clients.Count > 0; } }
    public ObcAppInfo? CurrentAppInfo { get; private set; }

    /// <summary>Journalise chaque paquet DirCon en hexadécimal (diagnostic de compatibilité avec les applis).</summary>
    public bool LogWire { get; set; }

    public event Action<ObcAppInfo>? AppInfoReceived;
    public event Action<ObcHaptic>? HapticReceived;
    public event Action? ClientsChanged;
    public event Action<string>? Log;

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        for (int port = PreferredPort; port < PreferredPort + 20; port++)
        {
            try { _listener = new TcpListener(IPAddress.Any, port); _listener.Start(); Port = port; break; }
            catch (SocketException) { _listener = null; }
        }
        if (_listener is null) throw new IOException(Strings.Err_NoFreePortDirCon);

        var (mac, serial) = NicIdentity();
        try
        {
            _mdns.Advertise($"ShiftBridge {serial[^4..]}", "_wahoo-fitness-tnp._tcp", (ushort)Port, new Dictionary<string, string>
            {
                ["mac-address"] = mac,
                ["serial-number"] = serial,
                ["ble-service-uuids"] = Service.ToString(),
            }, m => Log?.Invoke(m));
        }
        catch (Exception ex) { Log?.Invoke($"mDNS indisponible : {ex.Message}"); }

        Log?.Invoke($"DirCon en écoute sur le port {Port}");
        _ = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _mdns.Stop();
        _listener?.Stop();
        _listener = null;
        lock (_lock) { foreach (var c in _clients) c.Dispose(); _clients.Clear(); }
        CurrentAppInfo = null;
        ClientsChanged?.Invoke();
        return Task.CompletedTask;
    }

    public async Task SendButtonStateAsync(byte[] message)
    {
        _lastButtonState = message;
        var packet = DirConPacket.NotificationPacket(ButtonState, message).Encode();
        if (LogWire) Log?.Invoke($"DirCon > {Convert.ToHexString(packet)}");
        List<Client> targets;
        lock (_lock) targets = _clients.Where(c => c.IsSubscribed(ButtonState)).ToList();
        await Task.WhenAll(targets.Select(c => c.SendAsync(packet)));
    }

    public void SetBattery(int? percent) { /* DirCon n'expose pas de service batterie ici */ }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        while (!ct.IsCancellationRequested && listener is not null)
        {
            if (await TcpAccept.NextAsync(listener, ct, m => Log?.Invoke(m)) is not { } tcp) break;
            tcp.NoDelay = true;
            var client = new Client(tcp);
            lock (_lock) _clients.Add(client);
            Log?.Invoke($"Client DirCon connecté : {tcp.Client.RemoteEndPoint}");
            ClientsChanged?.Invoke();
            _ = ReadLoopAsync(client, ct);
        }
    }

    private async Task ReadLoopAsync(Client client, CancellationToken ct)
    {
        var parser = new DirConStreamParser();
        var appInfoParser = new ObcStreamParser(); // App Info peut arriver en plusieurs écritures
        var buf = new byte[1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await client.Stream.ReadAsync(buf, ct);
                if (n == 0) break;
                foreach (var p in parser.Feed(buf.AsSpan(0, n)))
                {
                    if (LogWire) Log?.Invoke($"DirCon < {Convert.ToHexString(p.Encode())}");
                    var response = Handle(client, p, appInfoParser);
                    if (response is not null)
                    {
                        var bytes = response.Encode();
                        if (LogWire) Log?.Invoke($"DirCon > {Convert.ToHexString(bytes)}");
                        await client.SendAsync(bytes);
                    }

                    // Après App Info, TPV attend une première notification de l'état des boutons pour considérer
                    // la manette « connectée » (sinon il reste sur le rond qui tourne). BikeControl fait de même.
                    if (client.SendInitialState && client.IsSubscribed(ButtonState))
                    {
                        client.SendInitialState = false;
                        var initial = DirConPacket.NotificationPacket(ButtonState, [ObcMessages.TypeButtonState]).Encode();
                        if (LogWire) Log?.Invoke($"DirCon > {Convert.ToHexString(initial)}");
                        await client.SendAsync(initial);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (_lock) _clients.Remove(client);
            client.Dispose();
            if (!HasClient) CurrentAppInfo = null;
            Log?.Invoke("Client DirCon déconnecté");
            ClientsChanged?.Invoke();
        }
    }

    private DirConPacket? Handle(Client client, DirConPacket p, ObcStreamParser appInfoParser)
    {
        if (p.ResponseCode != DirConResponse.Success) return null; // ce n'est pas une requête
        if (LogWire) Log?.Invoke($"DirCon < {(DirConId)p.Id} (seq {p.Sequence}, {p.Payload.Length} octets)");

        switch ((DirConId)p.Id)
        {
            case DirConId.DiscoverServices:
                return DirConPacket.ServicesResponse(p.Sequence, [Service, DeviceInfo]);

            case DirConId.DiscoverCharacteristics:
                if (p.Payload.Length < 16) return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.UnexpectedError);
                if (DirConPacket.ReadUuid(p.Payload) == DeviceInfo)
                    return DirConPacket.CharacteristicsResponse(p.Sequence, DeviceInfo,
                        [(ManufacturerName, DirConProps.Read), (ModelNumber, DirConProps.Read)]);
                if (DirConPacket.ReadUuid(p.Payload) != Service)
                    return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.ServiceNotFound);
                return DirConPacket.CharacteristicsResponse(p.Sequence, Service,
                [
                    (ButtonState, DirConProps.Read | DirConProps.Notify),
                    (Haptic, DirConProps.Write),
                    (AppInfo, DirConProps.Write),
                ]);

            case DirConId.ReadCharacteristic:
                if (p.Payload.Length < 16) return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.UnexpectedError);
            {
                var ch = DirConPacket.ReadUuid(p.Payload);
                if (ch == ButtonState) return DirConPacket.ReadResponse(p.Sequence, ButtonState, _lastButtonState);
                if (ch == ManufacturerName) return DirConPacket.ReadResponse(p.Sequence, ch, System.Text.Encoding.UTF8.GetBytes("ShiftBridge"));
                if (ch == ModelNumber) return DirConPacket.ReadResponse(p.Sequence, ch, System.Text.Encoding.UTF8.GetBytes("OBC"));
                return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.CharacteristicNotFound);
            }

            case DirConId.WriteCharacteristic:
            {
                if (p.Payload.Length <= 16) return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.UnexpectedError);
                var ch = DirConPacket.ReadUuid(p.Payload);
                var value = p.Payload.AsSpan(16).ToArray();
                if (ch == Haptic && ObcMessages.TryDecodeHaptic(value, out var h)) HapticReceived?.Invoke(h);
                else if (ch == AppInfo)
                {
                    foreach (var msg in appInfoParser.Feed(value))
                        if (ObcMessages.TryDecodeAppInfo(msg, out var info) && info is not null)
                        {
                            CurrentAppInfo = info;
                            client.SendInitialState = true;
                            Log?.Invoke($"App Info : {info.AppId} {info.AppVersion}");
                            AppInfoReceived?.Invoke(info);
                        }
                }
                else if (ch != Haptic) return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.CharacteristicNotFound);
                return DirConPacket.AckResponse(DirConId.WriteCharacteristic, p.Sequence, ch);
            }

            case DirConId.EnableNotifications:
            {
                if (p.Payload.Length < 16) return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.UnexpectedError);
                var ch = DirConPacket.ReadUuid(p.Payload);
                if (ch != ButtonState) return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.CharacteristicNotFound);
                // La spec prévoit un octet « activer » après l'UUID, mais TrainingPeaks Virtual envoie l'UUID seul
                // (capture réelle : payload de 16 octets). Sans octet, la requête signifie « activer ».
                bool enable = p.Payload.Length < 17 || p.Payload[16] != 0;
                client.SetSubscribed(ch, enable);
                Log?.Invoke($"Notifications boutons {(enable ? "activées" : "désactivées")} par l'appli");
                ClientsChanged?.Invoke();
                return DirConPacket.AckResponse(DirConId.EnableNotifications, p.Sequence, ch);
            }

            default:
                return DirConPacket.Error(p.Id, p.Sequence, DirConResponse.UnknownMessageType);
        }
    }

    /// <summary>
    /// Identité (MAC, numéro de série) annoncée en mDNS. <paramref name="variant"/> > 0 dérive une identité distincte :
    /// le serveur OBC et le proxy trainer sont deux appareils DirCon, une appli ne doit pas les confondre.
    /// </summary>
    internal static (string Mac, string Serial) NicIdentity(int variant = 0)
    {
        var nic = MdnsAdvertiser.PrimaryNic();
        var b = (nic?.GetPhysicalAddress().GetAddressBytes() ?? [0x02, 0x00, 0x53, 0x42, 0x00, 0x01]).ToArray();
        b[^1] ^= (byte)variant;
        return (string.Join("-", b.Select(x => x.ToString("X2"))), Convert.ToHexString(b));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _mdns.Dispose();
    }

    private sealed class Client(TcpClient tcp) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        public NetworkStream Stream { get; } = tcp.GetStream();
        private readonly HashSet<Guid> _subscribed = [];
        public bool SendInitialState { get; set; }

        // Lu par la boucle d'envoi des boutons, modifié par la boucle de lecture TCP : accès sous verrou.
        public bool IsSubscribed(Guid ch) { lock (_subscribed) return _subscribed.Contains(ch); }
        public void SetSubscribed(Guid ch, bool on) { lock (_subscribed) { if (on) _subscribed.Add(ch); else _subscribed.Remove(ch); } }

        public async Task SendAsync(byte[] data)
        {
            await _writeLock.WaitAsync();
            try { await TcpSend.WriteAsync(Stream, data, Dispose); } // client bloqué : coupé, la boucle de lecture nettoie
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
            finally { _writeLock.Release(); }
        }

        public void Dispose() { try { tcp.Dispose(); } catch { } }
    }
}
