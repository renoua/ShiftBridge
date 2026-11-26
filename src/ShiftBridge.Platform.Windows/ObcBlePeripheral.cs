using ShiftBridge.Core.Localization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using ShiftBridge.Core.Obc;
using ShiftBridge.Platform.Obc;

namespace ShiftBridge.Platform.Windows;

/// <summary>
/// OBC en BLE : le PC annonce le service d273f680-… (Button State, Haptic, App Info) et la batterie (180F).
/// Nécessite un adaptateur supportant le rôle périphérique : voir <see cref="IsSupportedAsync"/>.
/// </summary>
public sealed class ObcBlePeripheral : IObcTransport
{
    private GattServiceProvider? _obc;
    private GattServiceProvider? _battery;
    private GattLocalCharacteristic? _buttonState;
    private GattLocalCharacteristic? _batteryLevel;
    private byte[] _lastButtonState = [ObcMessages.TypeButtonState];
    private byte _batteryValue = 100;
    private readonly Dictionary<string, ObcStreamParser> _parsers = [];

    public string Name => "OBC Bluetooth";
    public bool HasClient => _buttonState?.SubscribedClients.Count > 0;
    public ObcAppInfo? CurrentAppInfo { get; private set; }
    public string? AdvertisedName { get; private set; }

    private static string? LocalBluetoothName()
    {
        try { return System.Net.Dns.GetHostName(); }
        catch { return string.IsNullOrWhiteSpace(Environment.MachineName) ? null : Environment.MachineName; }
    }

    public event Action<ObcAppInfo>? AppInfoReceived;
    public event Action<ObcHaptic>? HapticReceived;
    public event Action? ClientsChanged;
    public event Action<string>? Log;

    /// <summary>Vrai si l'adaptateur peut être périphérique BLE (annoncer un service). Sinon, utiliser le mode réseau.</summary>
    public static async Task<bool> IsSupportedAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            return adapter is { IsPeripheralRoleSupported: true };
        }
        catch { return false; }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!await IsSupportedAsync())
            throw new NotSupportedException(Strings.Err_NoPeripheralRole);

        // --- Service OBC ---
        var r = await GattServiceProvider.CreateAsync(ObcGattUuids.Service);
        if (r.Error != BluetoothError.Success) throw new IOException(Loc.Fmt(Strings.Err_ObcServiceRefused, r.Error));
        _obc = r.ServiceProvider;

        _buttonState = await CreateAsync(_obc, ObcGattUuids.ButtonState,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify);
        var haptic = await CreateAsync(_obc, ObcGattUuids.Haptic,
            GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse);
        var appInfo = await CreateAsync(_obc, ObcGattUuids.AppInfo,
            GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse);

        _buttonState.ReadRequested += OnButtonStateRead;
        _buttonState.SubscribedClientsChanged += (_, _) => { Log?.Invoke($"OBC BLE : {_buttonState.SubscribedClients.Count} abonné(s)"); ClientsChanged?.Invoke(); };
        haptic.WriteRequested += OnHapticWrite;
        appInfo.WriteRequested += OnAppInfoWrite;

        // --- Service batterie (optionnel) ---
        var br = await GattServiceProvider.CreateAsync(GattServiceUuids.Battery);
        if (br.Error == BluetoothError.Success)
        {
            _battery = br.ServiceProvider;
            _batteryLevel = await CreateAsync(_battery, GattCharacteristicUuids.BatteryLevel,
                GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify);
            _batteryLevel.ReadRequested += OnBatteryRead;
        }

        var adv = new GattServiceProviderAdvertisingParameters { IsDiscoverable = true, IsConnectable = true };
        _obc.StartAdvertising(adv);
        _battery?.StartAdvertising(adv);
        // GattServiceProvider ne permet pas de choisir le nom annoncé : c'est le nom Bluetooth du PC, c'est-à-dire son nom d'hôte.
        AdvertisedName = LocalBluetoothName();
        Log?.Invoke($"OBC BLE : annonce du service d273f680 démarrée, sous le nom « {AdvertisedName ?? "?"} »");
    }

    private static async Task<GattLocalCharacteristic> CreateAsync(GattServiceProvider sp, Guid uuid, GattCharacteristicProperties props)
    {
        var res = await sp.Service.CreateCharacteristicAsync(uuid, new GattLocalCharacteristicParameters { CharacteristicProperties = props });
        if (res.Error != BluetoothError.Success) throw new IOException(Loc.Fmt(Strings.Err_CharRefused, uuid, res.Error));
        return res.Characteristic;
    }

    public Task StopAsync()
    {
        try { _obc?.StopAdvertising(); } catch { }
        try { _battery?.StopAdvertising(); } catch { }
        _obc = null; _battery = null; _buttonState = null; _batteryLevel = null;
        _parsers.Clear();
        CurrentAppInfo = null;
        AdvertisedName = null;
        ClientsChanged?.Invoke();
        return Task.CompletedTask;
    }

    public async Task SendButtonStateAsync(byte[] message)
    {
        _lastButtonState = message;
        if (_buttonState is null || _buttonState.SubscribedClients.Count == 0) return;
        try { await _buttonState.NotifyValueAsync(CryptographicBuffer.CreateFromByteArray(message)); }
        catch (Exception ex) { Log?.Invoke($"Notification BLE échouée : {ex.Message}"); }
    }

    public void SetBattery(int? percent)
    {
        if (percent is not (>= 0 and <= 100)) return;
        _batteryValue = (byte)percent.Value;
        if (_batteryLevel is { SubscribedClients.Count: > 0 })
            _ = _batteryLevel.NotifyValueAsync(CryptographicBuffer.CreateFromByteArray([_batteryValue]));
    }

    private async void OnButtonStateRead(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var d = args.GetDeferral();
        try
        {
            var req = await args.GetRequestAsync();
            req.RespondWithValue(CryptographicBuffer.CreateFromByteArray(_lastButtonState));
        }
        finally { d.Complete(); }
    }

    private async void OnBatteryRead(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var d = args.GetDeferral();
        try
        {
            var req = await args.GetRequestAsync();
            req.RespondWithValue(CryptographicBuffer.CreateFromByteArray([_batteryValue]));
        }
        finally { d.Complete(); }
    }

    private async void OnHapticWrite(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var d = args.GetDeferral();
        try
        {
            var req = await args.GetRequestAsync();
            CryptographicBuffer.CopyToByteArray(req.Value, out byte[] data);
            if (req.Option == GattWriteOption.WriteWithResponse) req.Respond();
            if (ObcMessages.TryDecodeHaptic(data, out var h)) HapticReceived?.Invoke(h);
        }
        finally { d.Complete(); }
    }

    private async void OnAppInfoWrite(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var d = args.GetDeferral();
        try
        {
            var req = await args.GetRequestAsync();
            CryptographicBuffer.CopyToByteArray(req.Value, out byte[] data);
            if (req.Option == GattWriteOption.WriteWithResponse) req.Respond();

            // App Info dépasse souvent la MTU par défaut : on réassemble les écritures successives par session.
            string key = args.Session.DeviceId.Id;
            if (!_parsers.TryGetValue(key, out var parser)) _parsers[key] = parser = new ObcStreamParser();
            foreach (var msg in parser.Feed(data))
            {
                if (ObcMessages.TryDecodeAppInfo(msg, out var info) && info is not null)
                {
                    CurrentAppInfo = info;
                    Log?.Invoke($"App Info : {info.AppId} {info.AppVersion}");
                    AppInfoReceived?.Invoke(info);
                }
            }
        }
        finally { d.Complete(); }
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
