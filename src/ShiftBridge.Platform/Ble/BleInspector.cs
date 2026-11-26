using System.Collections.Concurrent;
using ShiftBridge.Core.Localization;

namespace ShiftBridge.Platform.Ble;

/// <summary>Un appareil BLE vu par l'inspecteur (n'importe lequel, reconnu ou non).</summary>
public sealed record InspectedDevice(BleDeviceId Address, string Name, short Rssi, string Details)
{
    public string AddressText => Address.ToString();
}

/// <summary>
/// Outil de diagnostic pour les manettes encore inconnues : liste les annonces BLE proches, puis se connecte à un appareil,
/// journalise son arbre GATT et chaque notification reçue en hexadécimal. N'écrit JAMAIS dans l'appareil (lecture seule) :
/// l'utilisateur appuie sur les boutons un par un et copie le journal pour qu'on en tire le format des trames.
/// </summary>
public sealed class BleInspector(BleAdvertisementHub hub, IBleCentral central) : IDisposable
{
    private sealed class Seen
    {
        public string Name = "";
        public short Rssi;
        public readonly HashSet<Guid> Services = [];
        public readonly Dictionary<ushort, byte[]> Manufacturer = [];
        public readonly Dictionary<Guid, byte[]> ServiceData = [];
    }

    private readonly ConcurrentDictionary<BleDeviceId, Seen> _seen = new();
    private readonly object _lock = new();
    private IDisposable? _subscription;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;

    public bool IsScanning => _subscription is not null;
    public bool IsCapturing => _captureTask is { IsCompleted: false };
    public BleDeviceId CapturingAddress { get; private set; }

    public event Action? DevicesChanged;
    public event Action<string>? Log;
    /// <summary>Levé quand la capture démarre ou se termine.</summary>
    public event Action? CaptureChanged;

    public void Start()
    {
        if (_subscription is not null) return;
        _seen.Clear();
        _subscription = hub.Subscribe(OnReceived);
        DevicesChanged?.Invoke();
    }

    public void Stop()
    {
        _subscription?.Dispose();
        _subscription = null;
        DevicesChanged?.Invoke();
    }

    /// <summary>Appareils vus, du signal le plus fort au plus faible.</summary>
    public IReadOnlyList<InspectedDevice> Devices()
    {
        List<InspectedDevice> list = [];
        foreach (var (address, s) in _seen)
            lock (s) list.Add(new InspectedDevice(address, s.Name.Length > 0 ? s.Name : Strings.Inspect_NoName, s.Rssi, Describe(s)));
        return [.. list.OrderByDescending(d => d.Rssi).ThenBy(d => d.Address)];
    }

    private void OnReceived(BleAdvertisement adv)
    {
        bool isNew = !_seen.ContainsKey(adv.Address);
        var s = _seen.GetOrAdd(adv.Address, _ => new Seen());
        bool changed;
        string? trace = null;
        lock (s)
        {
            string beforeData = Describe(s), beforeName = s.Name;
            if (!string.IsNullOrWhiteSpace(adv.LocalName)) s.Name = adv.LocalName!.Trim();
            s.Rssi = adv.Rssi;
            foreach (var u in adv.ServiceUuids) s.Services.Add(u);
            foreach (var (company, data) in adv.ManufacturerData) s.Manufacturer[company] = data;
            foreach (var (u, data) in adv.ServiceData) s.ServiceData[u] = data;
            string afterData = Describe(s);
            changed = beforeData != afterData || beforeName != s.Name;
            if (beforeData != afterData && ShouldTraceAdvertisements(adv.Address, s.Name))
                trace = $"{DateTime.Now:HH:mm:ss.fff} {(s.Name.Length > 0 ? s.Name : adv.Address.ToString())} annonce : {afterData}";
        }
        if (trace is not null) Log?.Invoke(trace);
        // Les annonces arrivent plusieurs fois par seconde : l'interface n'est prévenue que d'un appareil nouveau ou plus informatif.
        if (isNew || changed) DevicesChanged?.Invoke();
    }

    /// <summary>
    /// Annonces dont chaque changement est journalisé avec l'heure : celles des composants SRAM (on cherche si un levier signale
    /// ses appuis dans son annonce) et celle de l'appareil en cours de capture. Les autres rempliraient le journal pour rien.
    /// </summary>
    private bool ShouldTraceAdvertisements(BleDeviceId address, string name) =>
        name.StartsWith("SRAM", StringComparison.OrdinalIgnoreCase) || (IsCapturing && address.Equals(CapturingAddress));

    private static string Describe(Seen s)
    {
        List<string> parts = [];
        if (s.Services.Count > 0) parts.Add(Strings.Insp_Services + " " + string.Join(", ", s.Services.Select(ShortUuid).OrderBy(x => x, StringComparer.Ordinal)));
        foreach (var (company, data) in s.Manufacturer.OrderBy(k => k.Key))
            parts.Add($"{Strings.Insp_Manufacturer} {company:X4}:{Convert.ToHexString(data)}");
        foreach (var (u, data) in s.ServiceData.OrderBy(k => k.Key))
            parts.Add($"{Strings.Insp_ServiceData} {ShortUuid(u)}:{Convert.ToHexString(data)}");
        return string.Join(" · ", parts);
    }

    /// <summary>« 180F » pour un UUID 16 bits de la base Bluetooth, l'UUID complet sinon.</summary>
    public static string ShortUuid(Guid uuid)
    {
        string s = uuid.ToString();
        return s.StartsWith("0000", StringComparison.Ordinal) && s.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.Ordinal)
            ? s[4..8].ToUpperInvariant() : s;
    }

    /// <summary>Se connecte, journalise l'arbre GATT et chaque notification jusqu'à <see cref="StopCaptureAsync"/> ou la déconnexion.</summary>
    public async Task StartCaptureAsync(BleDeviceId address)
    {
        await StopCaptureAsync();
        string name = _seen.TryGetValue(address, out var s) && s.Name.Length > 0 ? s.Name : address.ToString();
        _captureCts = new CancellationTokenSource();
        CapturingAddress = address;
        _captureTask = CaptureAsync(address, name, _captureCts.Token);
        CaptureChanged?.Invoke();
    }

    public async Task StopCaptureAsync()
    {
        var cts = _captureCts;
        var task = _captureTask;
        if (cts is null || task is null) return;
        cts.Cancel();
        try { await task; } catch { /* annulé */ }
        _captureCts = null;
    }

    private async Task CaptureAsync(BleDeviceId address, string name, CancellationToken ct)
    {
        IBleConnection? connection = null;
        try
        {
            Log?.Invoke(Loc.Fmt(Strings.Inspect_Connecting, name));
            connection = await central.ConnectAsync(address, new BleConnectOptions(TimeSpan.FromSeconds(15)), ct);
            var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Disconnected += () => gone.TrySetResult();
            connection.ValueChanged += (svc, chr, value) =>
                Log?.Invoke($"{name} < {ShortUuid(svc)}/{ShortUuid(chr)} {Convert.ToHexString(value)}");
            connection.Log += m => Log?.Invoke(m);

            var services = await connection.DiscoverAsync(null, strict: false, ct: ct);
            Log?.Invoke(Loc.Fmt(Strings.Inspect_Tree, name, services.Count));
            foreach (var svc in services)
            {
                Log?.Invoke($"  service {ShortUuid(svc.Uuid)}");
                foreach (var c in svc.Characteristics)
                    Log?.Invoke($"    {ShortUuid(c.Uuid)} [{Props(c)}]");
            }

            int subscribed = 0;
            foreach (var svc in services)
                foreach (var c in svc.Characteristics)
                {
                    var mode = c.CanNotify ? BleNotifyMode.Notify : c.CanIndicate ? BleNotifyMode.Indicate : BleNotifyMode.None;
                    if (mode == BleNotifyMode.None) continue;
                    try { await connection.SetNotifyAsync(svc.Uuid, c.Uuid, mode); subscribed++; }
                    catch (Exception ex) { Log?.Invoke($"  {ShortUuid(svc.Uuid)}/{ShortUuid(c.Uuid)} : {ex.Message}"); }
                }
            Log?.Invoke(Loc.Fmt(Strings.Inspect_Listening, subscribed));

            await gone.Task.WaitAsync(ct);
            Log?.Invoke(Loc.Fmt(Strings.Inspect_Lost, name));
        }
        catch (OperationCanceledException) { Log?.Invoke(Loc.Fmt(Strings.Inspect_Stopped, name)); }
        catch (Exception ex) { Log?.Invoke(Loc.Fmt(Strings.Inspect_Failed, name, ex.Message)); }
        finally
        {
            if (connection is not null) { try { await connection.DisposeAsync(); } catch { /* déjà fermée */ } }
            CaptureChanged?.Invoke();
        }
    }

    private static string Props(BleCharacteristicInfo c)
    {
        List<string> p = [];
        if ((c.Properties & 0x02) != 0) p.Add("read");
        if ((c.Properties & 0x04) != 0) p.Add("write-no-resp");
        if (c.CanWrite) p.Add("write");
        if (c.CanNotify) p.Add("notify");
        if (c.CanIndicate) p.Add("indicate");
        return string.Join(",", p);
    }

    public void Dispose()
    {
        Stop();
        _captureCts?.Cancel();
    }
}
