using System.Net.Sockets;
using ShiftBridge.Core.Config;
using ShiftBridge.Core.Input;
using ShiftBridge.Core.Mapping;
using ShiftBridge.Core.Obc;
using ShiftBridge.Core.Trainer;
using ShiftBridge.Core.Zwift;
using ShiftBridge.Platform;
using ShiftBridge.Platform.Ble;
using ShiftBridge.Platform.Controllers;
using ShiftBridge.Platform.Obc;

namespace ShiftBridge.Core.Tests;

/// <summary>Cycle de vie : déconnexions, filtrage d'état périmé, clients bloqués, configuration (audit de la v0.8.0).</summary>
public class LifecycleTests
{
    private const ulong ClickAddress = 0xAABBCCDDEEFF;
    private const string ClickAddressText = "AA:BB:CC:DD:EE:FF";

    private static BleServiceInfo ZwiftService() => new(ZwiftConstants.CustomService,
    [
        new(ZwiftConstants.AsyncCharacteristic, 0x10),
        new(ZwiftConstants.SyncTxCharacteristic, 0x20),
        new(ZwiftConstants.SyncRxCharacteristic, 0x04),
    ]);

    private static BleAdvertisement ClickAdvert() =>
        FakeBleScanner.Adv(ClickAddress, "Zwift Click", manufacturer: [(ZwiftConstants.ManufacturerId, [0x09])]);

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > until) throw new TimeoutException("condition non atteinte");
            await Task.Delay(10);
        }
    }

    private static async Task<byte[]> ReadAsync(NetworkStream s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        await s.ReadExactlyAsync(buf, ct);
        return buf;
    }

    // ---------- Filtre « boutons supportés » ----------

    [Fact]
    public async Task Declared_buttons_of_a_gone_app_no_longer_filter_the_next_app()
    {
        await using var bridge = new BridgeService(new AppConfig());
        var tcp = new ObcTcpServer("LifecycleTest");
        var infoReceived = new TaskCompletionSource();
        var noClient = new TaskCompletionSource();
        tcp.AppInfoReceived += _ => infoReceived.TrySetResult();
        await bridge.AddTransportAsync(tcp);
        tcp.ClientsChanged += () => { if (!tcp.HasClient) noClient.TrySetResult(); }; // après le gestionnaire du BridgeService
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        // L'appli A ne déclare que ShiftUp, puis se déconnecte.
        using (var a = new TcpClient())
        {
            await a.ConnectAsync("127.0.0.1", tcp.Port);
            var sa = a.GetStream();
            await ReadAsync(sa, 3, cts.Token);
            await sa.WriteAsync(ObcMessages.EncodeAppInfo(new ObcAppInfo("a", "1", [0x01])), cts.Token);
            await infoReceived.Task.WaitAsync(cts.Token);
        }
        await noClient.Task.WaitAsync(cts.Token);

        // L'appli B n'envoie pas d'App Info : « Back » ne doit plus être filtré.
        using var b = new TcpClient();
        await b.ConnectAsync("127.0.0.1", tcp.Port);
        var sb = b.GetStream();
        await ReadAsync(sb, 3, cts.Token);

        bridge.InjectEvents([new ButtonEvent(PhysicalButton.RightB, true)]);
        var expected = ObcMessages.EncodeButtonState([(ObcButtonId.Back, (byte)1)]).First();
        Assert.Equal(expected, await ReadAsync(sb, expected.Length, cts.Token));
    }

    [Fact]
    public void Always_allowed_actions_pass_the_apps_declared_list()
    {
        var engine = new MappingEngine { SupportedActions = new HashSet<ObcButtonId> { ObcButtonId.Back } };

        // L'appli n'a déclaré que Back : ShiftUp est filtré...
        Assert.Empty(engine.Translate([new ButtonEvent(PhysicalButton.ClickPlus, true)]));
        engine.Translate([new ButtonEvent(PhysicalButton.ClickPlus, false)]);

        // ...sauf quand le proxy trainer applique lui-même les vitesses.
        engine.AlwaysAllowed = new HashSet<ObcButtonId> { ObcButtonId.ShiftUp, ObcButtonId.ShiftDown };
        var changes = engine.Translate([new ButtonEvent(PhysicalButton.ClickPlus, true)]);
        Assert.Equal([(ObcButtonId.ShiftUp, (byte)1)], changes);
    }

    // ---------- Manettes ----------

    [Fact]
    public async Task A_controller_disconnected_by_the_user_is_not_reconnected_by_the_scan()
    {
        var scanner = new FakeBleScanner();
        var central = new FakeBleCentral { Connection = new FakeBleConnection(ZwiftService()) };
        var config = new AppConfig { KnownDevices = [ClickAddressText] };
        await using var bridge = new BridgeService(config, new FakeBlePlatform(scanner, central)) { ScanSettleDelay = TimeSpan.FromMinutes(5) };
        bridge.AutoConnectKnown();
        bridge.StartScan();

        scanner.Emit(ClickAdvert());
        await WaitFor(() => bridge.Controllers.Any(c => c.State == ControllerState.Connected));
        Assert.Single(central.Requests);

        await bridge.DisconnectAsync(ClickAddress);
        scanner.Emit(ClickAdvert()); // la manette annonce encore : l'ancien code la reconnectait aussitôt
        scanner.Emit(ClickAdvert());
        await Task.Delay(300);
        Assert.Empty(bridge.Controllers);
        Assert.Single(central.Requests);

        // Un clic sur « Connecter » la veut à nouveau.
        central.Connection = new FakeBleConnection(ZwiftService());
        bridge.Connect(bridge.SeenDevices.First());
        await WaitFor(() => bridge.Controllers.Any(c => c.State == ControllerState.Connected));
        Assert.Equal(2, central.Requests.Count);
    }

    [Fact]
    public async Task A_saved_uuid_controller_reconnects_with_its_custom_name_and_can_be_forgotten()
    {
        const string uuid = "af8261aa-5d30-4875-ba57-279a823a65bb";
        var id = new BleDeviceId(Guid.Parse(uuid));
        string path = Path.Combine(Path.GetTempPath(), $"shiftbridge-test-{Guid.NewGuid():N}.json");
        try
        {
            var config = new AppConfig { KnownDevices = [id.ToString()], DeviceNames = new() { [id.ToString()] = "Left hood" } };
            ConfigStore.Save(config, path);

            var scanner = new FakeBleScanner();
            var central = new FakeBleCentral { Connection = new FakeBleConnection(ZwiftService()) };
            await using var bridge = new BridgeService(ConfigStore.Load(path), new FakeBlePlatform(scanner, central));
            bridge.AutoConnectKnown();
            bridge.StartScan();
            var adv = ClickAdvert() with { Address = id };
            scanner.Emit(adv);
            await WaitFor(() => bridge.Controllers.Any(c => c.State == ControllerState.Connected));

            var device = Assert.Single(bridge.SeenDevices);
            Assert.Equal(uuid, device.AddressText);
            Assert.Equal(id, Assert.Single(central.Requests).Address);
            Assert.Equal("Left hood", bridge.DisplayName(device));
            bridge.SetDeviceName(device.AddressText, "Right hood");
            Assert.Equal("Right hood", bridge.DisplayName(device));

            await bridge.DisconnectAsync(id, forget: true);
            scanner.Emit(adv);
            Assert.Empty(bridge.Controllers);
            Assert.Empty(bridge.Config.KnownDevices);
            Assert.Null(bridge.CustomName(uuid));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Scan_stops_once_known_controllers_are_connected_and_restarts_when_one_is_lost()
    {
        var scanner = new FakeBleScanner();
        var conn = new FakeBleConnection(ZwiftService());
        var central = new FakeBleCentral { Connection = conn };
        var config = new AppConfig { KnownDevices = [ClickAddressText] };
        await using var bridge = new BridgeService(config, new FakeBlePlatform(scanner, central)) { ScanSettleDelay = TimeSpan.FromMilliseconds(100) };
        bridge.AutoConnectKnown();
        bridge.StartScan();
        Assert.True(scanner.IsRunning);

        scanner.Emit(ClickAdvert());
        await WaitFor(() => bridge.Controllers.Any(c => c.State == ControllerState.Connected));
        await WaitFor(() => !scanner.IsRunning); // plus rien à chercher : la radio est libérée

        central.Connection = null; // la reconnexion échouera : la manette reste perdue
        conn.Drop();
        await WaitFor(() => scanner.IsRunning); // il faut la voir réapparaître
        Assert.Equal(2, scanner.StartCount);
    }

    [Fact]
    public async Task First_run_scan_keeps_going_until_a_controller_is_known()
    {
        var scanner = new FakeBleScanner();
        await using var bridge = new BridgeService(new AppConfig(), new FakeBlePlatform(scanner, new FakeBleCentral()))
            { ScanSettleDelay = TimeSpan.FromMilliseconds(50) };
        bridge.StartScan();
        await Task.Delay(300);
        Assert.True(scanner.IsRunning); // aucune manette connue : on ne s'arrête pas
    }

    [Fact]
    public async Task Stopping_a_controller_by_hand_releases_the_buttons_it_was_holding()
    {
        var conn = new FakeBleConnection(ZwiftService());
        var device = new DiscoveredZwiftDevice(ClickAddress, "Zwift Click",
            new ZwiftDeviceType(ZwiftModel.Click, ZwiftSide.None, 0x09), -50, [0x09]);
        await using var c = new ZwiftController(new FakeBleCentral { Connection = conn }, device, autoReconnect: false);
        var events = new List<ButtonEvent>();
        c.ButtonEvents += events.AddRange;
        c.Start();
        await WaitFor(() => c.State == ControllerState.Connected);

        conn.Emit(ZwiftConstants.CustomService, ZwiftConstants.AsyncCharacteristic, [0x37, 0x08, 0x00, 0x10, 0x01]); // « + » tenu
        Assert.Contains(new ButtonEvent(PhysicalButton.ClickPlus, true), events);

        await c.StopAsync(); // « Déconnecter » : l'annulation sortait de la boucle avant le relâchement
        Assert.Contains(new ButtonEvent(PhysicalButton.ClickPlus, false), events);
    }

    // ---------- Vitesse mémorisée ----------

    [Fact]
    public async Task Last_gear_is_restored_at_start()
    {
        await using var bridge = new BridgeService(new AppConfig { LastGear = 17 });
        Assert.Equal(17, bridge.Gear.Gear);

        await using var fresh = new BridgeService(new AppConfig());
        Assert.Equal(12, fresh.Gear.Gear); // jamais mémorisée : vitesse centrale
    }

    // ---------- Appui long répété ----------

    private static AppConfig RepeatConfig(int lastGear = 0, bool repeat = true)
    {
        var config = new AppConfig { LastGear = lastGear, RepeatSpeed = RepeatSpeed.VeryFast };
        config.Profile.Bindings[PhysicalButton.ClickPlus] = new ButtonBinding
        {
            Actions = [ObcButtonId.ShiftUp], RepeatOnHold = repeat,
        };
        return config;
    }

    [Fact]
    public async Task Repeat_speed_setting_controls_how_fast_a_held_button_shifts()
    {
        async Task<int> ShiftsWhileHeld(RepeatSpeed speed)
        {
            var config = RepeatConfig();
            config.RepeatSpeed = speed;
            await using var bridge = new BridgeService(config);
            int start = bridge.Gear.Gear;
            bridge.InjectEvents([new ButtonEvent(PhysicalButton.ClickPlus, true)]);
            await Task.Delay(1000);
            bridge.InjectEvents([new ButtonEvent(PhysicalButton.ClickPlus, false)]);
            return bridge.Gear.Gear - start;
        }

        int slow = await ShiftsWhileHeld(RepeatSpeed.Slow);      // ~1 + 3 répétitions
        int veryFast = await ShiftsWhileHeld(RepeatSpeed.VeryFast); // ~1 + 10 répétitions
        Assert.True(veryFast >= slow + 4, $"lente={slow}, très rapide={veryFast}");
    }

    [Fact]
    public async Task Holding_a_repeat_button_keeps_shifting_until_it_is_released()
    {
        await using var bridge = new BridgeService(RepeatConfig());
        int start = bridge.Gear.Gear;

        bridge.InjectEvents([new ButtonEvent(PhysicalButton.ClickPlus, true)]);
        await WaitFor(() => bridge.Gear.Gear >= start + 4); // l'appui initial + plusieurs répétitions

        bridge.InjectEvents([new ButtonEvent(PhysicalButton.ClickPlus, false)]);
        await Task.Delay(100); // une répétition déjà en cours peut encore aboutir
        int stopped = bridge.Gear.Gear;
        await Task.Delay(300);
        Assert.Equal(stopped, bridge.Gear.Gear); // plus de répétition après le relâchement
    }

    [Fact]
    public async Task Without_the_repeat_option_a_long_press_shifts_once()
    {
        await using var bridge = new BridgeService(RepeatConfig(repeat: false));
        int start = bridge.Gear.Gear;

        bridge.InjectEvents([new ButtonEvent(PhysicalButton.ClickPlus, true)]);
        await Task.Delay(400);
        bridge.InjectEvents([new ButtonEvent(PhysicalButton.ClickPlus, false)]);

        Assert.Equal(start + 1, bridge.Gear.Gear);
    }

    [Fact]
    public async Task Repeating_stops_by_itself_at_the_top_gear()
    {
        await using var bridge = new BridgeService(RepeatConfig(lastGear: 22));

        bridge.InjectEvents([new ButtonEvent(PhysicalButton.ClickPlus, true)]);
        await WaitFor(() => bridge.Gear.Gear == 24);
        await Task.Delay(300);
        Assert.Equal(24, bridge.Gear.Gear);
        bridge.InjectEvents([new ButtonEvent(PhysicalButton.ClickPlus, false)]);
    }

    [Fact]
    public async Task Repeats_reach_the_app_as_release_then_press_while_the_key_stays_held()
    {
        await using var bridge = new BridgeService(RepeatConfig());
        var tcp = new ObcTcpServer("RepeatTest");
        await bridge.AddTransportAsync(tcp);
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", tcp.Port);
        var s = client.GetStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await ReadAsync(s, 3, cts.Token); // statut initial

        bridge.InjectEvents([new ButtonEvent(PhysicalButton.ClickPlus, true)]);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x01 }, await ReadAsync(s, 3, cts.Token)); // appui initial
        Assert.Equal(new byte[] { 0x01, 0x01, 0x00 }, await ReadAsync(s, 3, cts.Token)); // répétition : relâché...
        Assert.Equal(new byte[] { 0x01, 0x01, 0x01 }, await ReadAsync(s, 3, cts.Token)); // ...puis appuyé de nouveau

        bridge.InjectEvents([new ButtonEvent(PhysicalButton.ClickPlus, false)]);
    }

    // ---------- Client TCP bloqué ----------

    [Fact]
    public async Task A_client_that_stops_reading_is_dropped_instead_of_freezing_the_sender()
    {
        var old = TcpSend.WriteTimeout;
        TcpSend.WriteTimeout = TimeSpan.FromMilliseconds(400);
        try
        {
            var tcp = new ObcTcpServer("SlowClientTest");
            await tcp.StartAsync();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", tcp.Port);
                await WaitFor(() => tcp.HasClient);

                // Le client ne lit jamais : une écriture bien plus grosse que les tampons TCP ne peut pas aboutir.
                // Le client ne lit jamais. Windows absorbe d'abord plusieurs Mo dans ses tampons : on envoie jusqu'à ce qu'une
                // écriture ne passe plus. Sans délai d'écriture, cette écriture-là ne se terminait jamais.
                var chunk = new byte[16 * 1024 * 1024];
                bool blocked = false;
                for (int i = 0; i < 200 && !blocked; i++)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    await tcp.SendButtonStateAsync(chunk).WaitAsync(TimeSpan.FromSeconds(10));
                    blocked = watch.ElapsedMilliseconds >= 300;
                }
                Assert.True(blocked, "les tampons TCP n'ont jamais fini par être pleins");
                await WaitFor(() => !tcp.HasClient); // le client bloqué a été coupé
            }
            finally { await tcp.DisposeAsync(); }
        }
        finally { TcpSend.WriteTimeout = old; }
    }

    // ---------- Configuration ----------

    [Fact]
    public void Config_values_out_of_range_are_brought_back_to_safe_values()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shiftbridge-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                { "GearCount": 99, "TrainerPort": 0, "TrainerTransport": "usb", "RiderWeightKg": 5000,
                  "BikeWeightKg": 0, "OverlayScale": 50, "OverlayOpacity": 0, "LastGear": 80, "KnownDevices": null }
                """);
            var c = ConfigStore.Load(path);
            Assert.Equal(24, c.GearCount);
            Assert.Equal(36866, c.TrainerPort);
            Assert.Equal("wifi", c.TrainerTransport);
            Assert.Equal(200, c.RiderWeightKg);
            Assert.Equal(3, c.BikeWeightKg);
            Assert.Equal(3.0, c.OverlayScale);
            Assert.Equal(0.2, c.OverlayOpacity);
            Assert.Equal(24, c.LastGear);
            Assert.NotNull(c.KnownDevices);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Corrupt_config_is_kept_aside_and_defaults_are_used()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shiftbridge-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ ceci n'est pas du json");
            var c = ConfigStore.Load(path);
            Assert.Equal(24, c.GearCount);
            Assert.True(File.Exists(path + ".bad"));
        }
        finally { File.Delete(path); File.Delete(path + ".bad"); }
    }

    [Fact]
    public async Task Concurrent_saves_and_list_changes_never_fail()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shiftbridge-test-{Guid.NewGuid():N}.json");
        try
        {
            var config = new AppConfig();
            var writer = Task.Run(() =>
            {
                for (int i = 0; i < 300; i++)
                    lock (config.SyncRoot) { config.KnownDevices.Add($"AA:BB:CC:DD:EE:{i % 256:X2}"); if (config.KnownDevices.Count > 20) config.KnownDevices.RemoveAt(0); }
            });
            var savers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => { for (int i = 0; i < 40; i++) ConfigStore.Save(config, path); }));
            await Task.WhenAll(savers.Append(writer));

            var back = ConfigStore.Load(path);
            Assert.NotNull(back);
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }
}
