using ShiftBridge.Core.Input;
using ShiftBridge.Core.Trainer;
using ShiftBridge.Core.Zwift;
using ShiftBridge.Platform.Ble;
using ShiftBridge.Platform.Controllers;
using ShiftBridge.Platform.Trainer;

namespace ShiftBridge.Core.Tests;

public class BleAddressTests
{
    [Fact]
    public void Text_is_most_significant_byte_first()
    {
        Assert.Equal("AA:BB:CC:DD:EE:FF", BleAddress.ToText(0xAABBCCDDEEFF));
        Assert.Equal("00:00:00:00:00:01", BleAddress.ToText(1));
    }

    [Theory]
    [InlineData(0xAABBCCDDEEFFUL)]
    [InlineData(0x0102030405FFUL)]
    [InlineData(0UL)]
    public void Text_round_trips(ulong address)
    {
        Assert.True(BleAddress.TryParse(BleAddress.ToText(address), out var back));
        Assert.Equal(address, back);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AA:BB:CC")]
    [InlineData("AA:BB:CC:DD:EE:GG")]
    [InlineData("AA:BB:CC:DD:EE:FF:00")]
    public void Invalid_text_is_rejected(string? text) => Assert.False(BleAddress.TryParse(text, out _));
}

public class AdvertisementHubTests
{
    [Fact]
    public void Scan_runs_only_while_someone_is_subscribed()
    {
        var scanner = new FakeBleScanner();
        using var hub = new BleAdvertisementHub(scanner);

        var a = hub.Subscribe(_ => { });
        var b = hub.Subscribe(_ => { });
        Assert.Equal(1, scanner.StartCount);
        Assert.True(hub.IsScanning);

        a.Dispose();
        Assert.True(scanner.IsRunning);   // b est encore abonné
        b.Dispose();
        Assert.False(scanner.IsRunning);
        Assert.Equal(1, scanner.StopCount);

        hub.Subscribe(_ => { });          // un nouvel abonné relance le scan
        Assert.Equal(2, scanner.StartCount);
    }

    [Fact]
    public void A_failing_consumer_does_not_starve_the_others()
    {
        var scanner = new FakeBleScanner();
        using var hub = new BleAdvertisementHub(scanner);
        int received = 0;
        hub.Subscribe(_ => throw new InvalidOperationException("boom"));
        hub.Subscribe(_ => received++);

        scanner.Emit(FakeBleScanner.Adv(1));
        Assert.Equal(1, received);
    }

    [Fact]
    public void Scanner_stopping_by_itself_is_logged_and_allows_restart()
    {
        var scanner = new FakeBleScanner();
        using var hub = new BleAdvertisementHub(scanner);
        var logs = new List<string>();
        hub.Log += logs.Add;

        hub.Subscribe(_ => { });
        scanner.StopByItself("Bluetooth coupé");
        Assert.Contains(logs, l => l.Contains("Bluetooth coupé"));

        hub.Subscribe(_ => { });
        Assert.Equal(2, scanner.StartCount);
    }
}

public class ScannerFilteringTests
{
    private static (ushort, byte[])[] Zwift(byte code) => [(ZwiftConstants.ManufacturerId, [code, 0x01])];

    [Fact]
    public void ZwiftScanner_reports_only_zwift_devices_with_side_in_name()
    {
        var scanner = new FakeBleScanner();
        using var hub = new BleAdvertisementHub(scanner);
        using var zs = new ZwiftScanner(hub);
        var seen = new List<DiscoveredZwiftDevice>();
        zs.DeviceSeen += seen.Add;
        zs.Start();

        scanner.Emit(FakeBleScanner.Adv(0x112233445566, "Zwift Play", manufacturer: Zwift(0x03)));
        scanner.Emit(FakeBleScanner.Adv(0x1, "Casque", manufacturer: [(0x004C, [0x03])]));
        scanner.Emit(FakeBleScanner.Adv(0x2));

        var d = Assert.Single(seen);
        Assert.Equal(ZwiftModel.Play, d.Type.Model);
        Assert.Equal(ZwiftSide.Left, d.Type.Side);
        Assert.Equal("Zwift Play G5566", d.Name);
        Assert.Equal("11:22:33:44:55:66", d.AddressText);
    }

    [Fact]
    public void ZwiftScanner_falls_back_to_model_name_without_local_name()
    {
        var scanner = new FakeBleScanner();
        using var hub = new BleAdvertisementHub(scanner);
        using var zs = new ZwiftScanner(hub);
        DiscoveredZwiftDevice? seen = null;
        zs.DeviceSeen += d => seen = d;
        zs.Start();

        scanner.Emit(FakeBleScanner.Adv(0xAB, null, manufacturer: Zwift(0x09)));
        Assert.Equal("Zwift Click 00AB", seen!.Name);
    }

    private static List<DiscoveredBleTrainer> Collect(FakeBleScanner scanner, out BleTrainerScanner ts)
    {
        var hub = new BleAdvertisementHub(scanner);
        ts = new BleTrainerScanner(hub);
        var list = new List<DiscoveredBleTrainer>();
        ts.TrainerSeen += list.Add;
        ts.Start();
        return list;
    }

    [Fact]
    public void TrainerScanner_detects_ftms_by_service_uuid()
    {
        var scanner = new FakeBleScanner();
        var seen = Collect(scanner, out var ts);
        using var _ = ts;

        scanner.Emit(FakeBleScanner.Adv(1, "Mon trainer", services: [FtmsCodec.FitnessMachineService]));
        var t = Assert.Single(seen);
        Assert.Equal("Mon trainer", t.Name);
        Assert.True(t.Protocols.HasFlag(TrainerProtocols.Ftms));
    }

    [Fact]
    public void TrainerScanner_merges_advertisement_and_scan_response()
    {
        var scanner = new FakeBleScanner();
        var seen = Collect(scanner, out var ts);
        using var _ = ts;

        scanner.Emit(FakeBleScanner.Adv(1, "KICKR CORE 1234"));                                   // le nom suffit
        scanner.Emit(FakeBleScanner.Adv(1, null, services: [FtmsCodec.FitnessMachineService]));   // les UUID arrivent après

        Assert.Equal(2, seen.Count);
        Assert.Equal("KICKR CORE 1234", seen[1].Name);
        Assert.True(seen[1].Protocols.HasFlag(TrainerProtocols.Ftms));
    }

    [Theory]
    [InlineData(1 << 5, true)]   // vélo d'intérieur
    [InlineData(1 << 0, false)]  // tapis
    [InlineData(1 << 4, false)]  // rameur
    public void TrainerScanner_uses_ftms_service_data_to_drop_other_machines(int machineType, bool expected)
    {
        var scanner = new FakeBleScanner();
        var seen = Collect(scanner, out var ts);
        using var _ = ts;

        byte[] data = [0x01, (byte)machineType, (byte)(machineType >> 8)];
        scanner.Emit(FakeBleScanner.Adv(1, "Machine", serviceData: new() { [FtmsCodec.FitnessMachineService] = data }));
        Assert.Equal(expected, seen.Count == 1);
    }

    [Fact]
    public void TrainerScanner_excludes_zwift_controllers_and_own_devices_for_good()
    {
        var scanner = new FakeBleScanner();
        var seen = Collect(scanner, out var ts);
        using var _ = ts;

        // Une manette Zwift qui annoncerait FTMS ne doit pas être proposée comme trainer.
        scanner.Emit(FakeBleScanner.Adv(1, "Zwift Click", manufacturer: [(ZwiftConstants.ManufacturerId, [0x09])],
            services: [FtmsCodec.FitnessMachineService]));
        // Notre propre proxy.
        scanner.Emit(FakeBleScanner.Adv(2, "ShiftBridge Trainer", services: [FtmsCodec.FitnessMachineService]));
        // L'exclusion est définitive même si une réponse de scan suivante omet le company id.
        scanner.Emit(FakeBleScanner.Adv(1, "Zwift Click", services: [FtmsCodec.FitnessMachineService]));

        Assert.Empty(seen);
    }

    [Fact]
    public void TrainerScanner_keeps_unknown_zwift_company_id_devices_eligible()
    {
        var scanner = new FakeBleScanner();
        var seen = Collect(scanner, out var ts);
        using var _ = ts;

        // Company id Zwift mais type inconnu (p. ex. Zwift Hub) : ce n'est pas une manette.
        scanner.Emit(FakeBleScanner.Adv(1, "Zwift Hub", manufacturer: [(ZwiftConstants.ManufacturerId, [0x7F])],
            services: [FtmsCodec.FitnessMachineService]));
        Assert.Single(seen);
    }
}

public class ZwiftControllerTests
{
    private static readonly Guid Svc = ZwiftConstants.CustomService;

    private static BleServiceInfo ZwiftService(Guid? uuid = null, bool withSyncRx = true)
    {
        var chars = new List<BleCharacteristicInfo>
        {
            new(ZwiftConstants.AsyncCharacteristic, 0x10),
            new(ZwiftConstants.SyncTxCharacteristic, 0x20),
        };
        if (withSyncRx) chars.Add(new(ZwiftConstants.SyncRxCharacteristic, 0x04));
        return new BleServiceInfo(uuid ?? Svc, chars);
    }

    private static DiscoveredZwiftDevice Click() =>
        new(0xAABBCCDDEEFF, "Zwift Click", new ZwiftDeviceType(ZwiftModel.Click, ZwiftSide.None, 0x09), -50, [0x09]);

    private static DiscoveredZwiftDevice Play() =>
        new(0xAABBCCDDEE01, "Zwift Play", new ZwiftDeviceType(ZwiftModel.Play, ZwiftSide.Left, 0x03), -50, [0x03]);

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > until) throw new TimeoutException("condition non atteinte");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Connect_subscribes_then_sends_rideon_without_response()
    {
        var conn = new FakeBleConnection(ZwiftService());
        var central = new FakeBleCentral { Connection = conn };
        await using var c = new ZwiftController(central, Click(), autoReconnect: false);

        c.Start();
        await WaitFor(() => c.State == ControllerState.Connected);

        Assert.Equal(new BleDeviceId(0xAABBCCDDEEFFUL), central.Requests[0].Address);
        Assert.True(conn.FirstDiscoverStrict); // le service Zwift est requis : un échec n'est pas un « verrouillage »
        Assert.Equal(BleNotifyMode.Notify, conn.Notify[(Svc, ZwiftConstants.AsyncCharacteristic)]);
        Assert.Equal(BleNotifyMode.Indicate, conn.Notify[(Svc, ZwiftConstants.SyncTxCharacteristic)]);
        var w = Assert.Single(conn.Writes);
        Assert.Equal(ZwiftConstants.SyncRxCharacteristic, w.Char);
        Assert.Equal(ZwiftConstants.RideOn, w.Value);
        Assert.False(w.WithResponse);
    }

    [Fact]
    public async Task Prefers_the_ride_service_when_both_are_exposed()
    {
        var conn = new FakeBleConnection(ZwiftService(), ZwiftService(ZwiftConstants.RideCustomService));
        await using var c = new ZwiftController(new FakeBleCentral { Connection = conn }, Click(), autoReconnect: false);

        c.Start();
        await WaitFor(() => c.State == ControllerState.Connected);
        Assert.Equal(ZwiftConstants.RideCustomService, conn.Writes[0].Service);
    }

    [Fact]
    public async Task Button_frames_become_press_and_release_events()
    {
        var conn = new FakeBleConnection(ZwiftService());
        await using var c = new ZwiftController(new FakeBleCentral { Connection = conn }, Click(), autoReconnect: false);
        var events = new List<ButtonEvent>();
        c.ButtonEvents += events.AddRange;
        c.Start();
        await WaitFor(() => c.State == ControllerState.Connected);

        conn.Emit(Svc, ZwiftConstants.AsyncCharacteristic, [0x37, 0x08, 0x00, 0x10, 0x01]);
        Assert.Contains(new ButtonEvent(PhysicalButton.ClickPlus, true), events);
        conn.Emit(Svc, ZwiftConstants.AsyncCharacteristic, [0x37, 0x08, 0xFF, 0x10, 0x01]);
        Assert.Contains(new ButtonEvent(PhysicalButton.ClickPlus, false), events);
    }

    [Fact]
    public async Task Missing_zwift_service_means_locked()
    {
        var conn = new FakeBleConnection(new BleServiceInfo(FtmsCodec.BaseUuid(0x180F), []));
        await using var c = new ZwiftController(new FakeBleCentral { Connection = conn }, Click(), autoReconnect: false);
        c.Start();
        await WaitFor(() => c.State == ControllerState.Locked);
        Assert.Empty(conn.Writes);
    }

    [Fact]
    public async Task Missing_characteristic_is_an_error_not_a_lock()
    {
        var conn = new FakeBleConnection(ZwiftService(withSyncRx: false));
        await using var c = new ZwiftController(new FakeBleCentral { Connection = conn }, Click(), autoReconnect: false);
        var logs = new List<string>();
        c.Log += logs.Add;
        c.Start();
        await WaitFor(() => c.State == ControllerState.Error || c.State == ControllerState.Disconnected && logs.Count > 0);
        Assert.Contains(logs, l => l.Contains("Caractéristiques Zwift manquantes"));
        Assert.NotEqual(ControllerState.Locked, c.State);
    }

    [Fact]
    public async Task Unreachable_device_reports_a_friendly_error()
    {
        var central = new FakeBleCentral(); // aucune connexion : introuvable
        await using var c = new ZwiftController(central, Click(), autoReconnect: false);
        var logs = new List<string>();
        c.Log += logs.Add;
        c.Start();
        await WaitFor(() => logs.Any(l => l.Contains("Manette introuvable")));
    }

    [Fact]
    public async Task Click_v2_status_frame_does_not_lock_the_controller()
    {
        var conn = new FakeBleConnection(ZwiftService());
        await using var c = new ZwiftController(new FakeBleCentral { Connection = conn }, Click(), autoReconnect: false)
        {
            ReconnectOnLock = false,
        };
        c.Start();
        await WaitFor(() => c.State == ControllerState.Connected);

        // Trame réelle du journal (réponse au handshake, suivie du numéro de série) : aucun verrouillage.
        conn.Emit(Svc, ZwiftConstants.SyncTxCharacteristic, [0xFF, 0x05, 0x00, 0xFA, 0x05, 0x18, 0x0A, 0x0C]);
        Assert.Equal(ControllerState.Connected, c.State);
    }

    [Fact]
    public async Task Battery_is_read_from_the_battery_service()
    {
        var batteryService = FtmsCodec.BaseUuid(0x180F);
        var conn = new FakeBleConnection(ZwiftService(), new BleServiceInfo(batteryService, [new(FtmsCodec.BaseUuid(0x2A19), 0x02)]));
        conn.ReadValues[(batteryService, FtmsCodec.BaseUuid(0x2A19))] = [77];
        await using var c = new ZwiftController(new FakeBleCentral { Connection = conn }, Click(), autoReconnect: false);
        c.Start();
        await WaitFor(() => c.BatteryPercent == 77);
    }

    [Fact]
    public async Task Vibration_is_written_without_response_and_only_on_play_and_ride()
    {
        var conn = new FakeBleConnection(ZwiftService());
        await using var play = new ZwiftController(new FakeBleCentral { Connection = conn }, Play(), autoReconnect: false);
        play.Start();
        await WaitFor(() => play.State == ControllerState.Connected);

        await play.VibrateAsync();
        var last = conn.Writes[^1];
        Assert.Equal(ZwiftConstants.VibrationCommand(0x20), last.Value);
        Assert.False(last.WithResponse);

        var conn2 = new FakeBleConnection(ZwiftService());
        await using var click = new ZwiftController(new FakeBleCentral { Connection = conn2 }, Click(), autoReconnect: false);
        click.Start();
        await WaitFor(() => click.State == ControllerState.Connected);
        await click.VibrateAsync();
        Assert.Single(conn2.Writes); // seulement RideOn
    }

    [Fact]
    public async Task Disconnect_releases_held_buttons_and_closes_the_connection()
    {
        var conn = new FakeBleConnection(ZwiftService());
        await using var c = new ZwiftController(new FakeBleCentral { Connection = conn }, Click(), autoReconnect: false);
        var events = new List<ButtonEvent>();
        c.ButtonEvents += events.AddRange;
        c.Start();
        await WaitFor(() => c.State == ControllerState.Connected);

        conn.Emit(Svc, ZwiftConstants.AsyncCharacteristic, [0x37, 0x08, 0x00, 0x10, 0x01]);
        conn.Drop();

        await WaitFor(() => events.Contains(new ButtonEvent(PhysicalButton.ClickPlus, false)));
        await WaitFor(() => conn.Disposed);
    }

    [Fact]
    public async Task Reconnects_after_a_drop_when_auto_reconnect_is_on()
    {
        var first = new FakeBleConnection(ZwiftService());
        var central = new FakeBleCentral { Connection = first };
        await using var c = new ZwiftController(central, Click(), autoReconnect: true);
        c.Start();
        await WaitFor(() => c.State == ControllerState.Connected);

        var second = new FakeBleConnection(ZwiftService());
        central.Connection = second;
        first.Drop();

        await WaitFor(() => central.Requests.Count >= 2 && second.Writes.Count == 1, timeoutMs: 6000);
        Assert.Equal(ZwiftConstants.RideOn, second.Writes[0].Value);
    }
}

public class BleTrainerLinkTests
{
    private static readonly Guid Ftms = FtmsCodec.FitnessMachineService;
    private static readonly Guid Generic = FtmsCodec.BaseUuid(0x1800);

    private static FakeBleConnection Trainer() => new(
        new BleServiceInfo(Generic, [new(FtmsCodec.BaseUuid(0x2A00), 0x02)]),
        new BleServiceInfo(Ftms,
        [
            new(FtmsCodec.IndoorBikeData, 0x10),                 // notify
            new(FtmsCodec.ControlPoint, 0x08 | 0x20),            // write + indicate
            new(FtmsCodec.BaseUuid(0x2ACC), 0x02),               // read seul (feature)
            new(FtmsCodec.BaseUuid(0x2AD3), 0x04),               // write sans réponse
        ]));

    private static async Task<(BleTrainerLink Link, FakeBleConnection Conn, FakeBleCentral Central)> Open()
    {
        var conn = Trainer();
        var central = new FakeBleCentral { Connection = conn };
        var link = new BleTrainerLink(central, 0x1234, "Trainer");
        await link.ConnectAsync();
        return (link, conn, central);
    }

    [Fact]
    public async Task Connect_uses_a_bounded_operation_timeout_and_skips_generic_services()
    {
        var (link, _, central) = await Open();
        await using var _ = link;

        Assert.NotNull(central.Requests[0].Options.OperationTimeout);
        var services = await link.DiscoverServicesAsync();
        Assert.Equal([Ftms], services);
    }

    [Fact]
    public async Task Missing_device_gives_a_trainer_specific_message()
    {
        var link = new BleTrainerLink(new FakeBleCentral(), 1, "Trainer");
        var ex = await Assert.ThrowsAsync<IOException>(() => link.ConnectAsync());
        Assert.Contains("Trainer introuvable", ex.Message);
    }

    [Fact]
    public async Task Characteristic_properties_are_converted_to_dircon_bits()
    {
        var (link, _, _) = await Open();
        await using var _ = link;

        var chars = (await link.DiscoverCharacteristicsAsync(Ftms)).ToDictionary(c => c.Uuid, c => c.Props);
        Assert.Equal(0x04, chars[FtmsCodec.IndoorBikeData]);                        // Notify
        Assert.Equal(0x02 | 0x08, chars[FtmsCodec.ControlPoint]);                   // Write + Indicate
        Assert.Equal(0x01, chars[FtmsCodec.BaseUuid(0x2ACC)]);                      // Read
        Assert.Equal(0x02, chars[FtmsCodec.BaseUuid(0x2AD3)]);                      // Write sans réponse
        Assert.Empty(await link.DiscoverCharacteristicsAsync(Generic));
    }

    [Fact]
    public async Task Writes_use_a_response_only_when_the_characteristic_supports_it()
    {
        var (link, conn, _) = await Open();
        await using var _ = link;

        await link.WriteAsync(FtmsCodec.ControlPoint, [0x00]);
        await link.WriteAsync(FtmsCodec.BaseUuid(0x2AD3), [0x01]);

        Assert.True(conn.Writes[0].WithResponse);
        Assert.Equal(Ftms, conn.Writes[0].Service);
        Assert.False(conn.Writes[1].WithResponse);
    }

    [Fact]
    public async Task Control_point_is_subscribed_by_indication_and_data_by_notification()
    {
        var (link, conn, _) = await Open();
        await using var _ = link;

        await link.EnableNotificationsAsync(FtmsCodec.ControlPoint);
        await link.EnableNotificationsAsync(FtmsCodec.IndoorBikeData);
        Assert.Equal(BleNotifyMode.Indicate, conn.Notify[(Ftms, FtmsCodec.ControlPoint)]);
        Assert.Equal(BleNotifyMode.Notify, conn.Notify[(Ftms, FtmsCodec.IndoorBikeData)]);

        await link.EnableNotificationsAsync(FtmsCodec.IndoorBikeData, enable: false);
        Assert.Equal(BleNotifyMode.None, conn.Notify[(Ftms, FtmsCodec.IndoorBikeData)]);
    }

    [Fact]
    public async Task Subscribing_to_a_silent_characteristic_is_refused()
    {
        var (link, _, _) = await Open();
        await using var _ = link;
        await Assert.ThrowsAsync<IOException>(() => link.EnableNotificationsAsync(FtmsCodec.BaseUuid(0x2ACC)));
    }

    [Fact]
    public async Task Unknown_characteristic_is_an_io_error()
    {
        var (link, _, _) = await Open();
        await using var _ = link;
        await Assert.ThrowsAsync<IOException>(() => link.ReadAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Notifications_are_forwarded_with_the_characteristic_uuid()
    {
        var (link, conn, _) = await Open();
        await using var _ = link;
        var got = new List<(Guid, byte[])>();
        link.Notification += (g, d) => got.Add((g, d));

        conn.Emit(Ftms, FtmsCodec.IndoorBikeData, [1, 2, 3]);
        var (uuid, data) = Assert.Single(got);
        Assert.Equal(FtmsCodec.IndoorBikeData, uuid);
        Assert.Equal(new byte[] { 1, 2, 3 }, data);
    }

    [Fact]
    public async Task Disconnection_is_raised_once_and_not_after_dispose()
    {
        var (link, conn, _) = await Open();
        int raised = 0;
        link.Disconnected += () => raised++;

        Assert.True(link.IsConnected);
        conn.Drop();
        conn.Drop();
        Assert.Equal(1, raised);
        Assert.False(link.IsConnected);

        await link.DisposeAsync();
        Assert.True(conn.Disposed);
        await Assert.ThrowsAsync<IOException>(() => link.ReadAsync(FtmsCodec.IndoorBikeData));
    }

    [Fact]
    public async Task Voluntary_dispose_does_not_raise_disconnected()
    {
        var (link, conn, _) = await Open();
        int raised = 0;
        link.Disconnected += () => raised++;

        await link.DisposeAsync();
        conn.Drop();
        Assert.Equal(0, raised);
    }
}

public class BridgeServicePlatformTests
{
    [Fact]
    public async Task Bridge_scans_and_connects_through_the_injected_platform()
    {
        var scanner = new FakeBleScanner();
        var conn = new FakeBleConnection(new BleServiceInfo(ZwiftConstants.CustomService,
        [
            new(ZwiftConstants.AsyncCharacteristic, 0x10),
            new(ZwiftConstants.SyncTxCharacteristic, 0x20),
            new(ZwiftConstants.SyncRxCharacteristic, 0x04),
        ]));
        var platform = new FakePlatform(scanner, new FakeBleCentral { Connection = conn });

        await using var bridge = new ShiftBridge.Platform.BridgeService(new ShiftBridge.Core.Config.AppConfig(), platform);
        bridge.StartScan();
        Assert.True(scanner.IsRunning);

        scanner.Emit(FakeBleScanner.Adv(0xAABBCCDDEEFF, "Zwift Click", manufacturer: [(ZwiftConstants.ManufacturerId, [0x09])]));
        var device = Assert.Single(bridge.SeenDevices);

        bridge.Connect(device);
        var until = Environment.TickCount64 + 3000;
        while (conn.Writes.Count == 0 && Environment.TickCount64 < until) await Task.Delay(10);
        Assert.Equal(ZwiftConstants.RideOn, Assert.Single(conn.Writes).Value);

        bridge.StopScan();
        Assert.False(scanner.IsRunning);
    }

    [Fact]
    public async Task Without_a_platform_the_bridge_still_works_but_finds_nothing()
    {
        await using var bridge = new ShiftBridge.Platform.BridgeService(new ShiftBridge.Core.Config.AppConfig());
        bridge.StartScan();
        Assert.Empty(bridge.SeenDevices);
        Assert.False(await bridge.Platform.IsPeripheralSupportedAsync());
        Assert.Throws<NotSupportedException>(() => bridge.Platform.CreateObcPeripheral());
    }

    private sealed class FakePlatform(FakeBleScanner scanner, FakeBleCentral central) : IBlePlatform
    {
        public IBleCentral Central => central;
        public IBleScanner CreateScanner() => scanner;
        public Task<bool> IsPeripheralSupportedAsync() => Task.FromResult(false);
        public ShiftBridge.Platform.Obc.IObcTransport CreateObcPeripheral() => throw new NotSupportedException();
    }
}
