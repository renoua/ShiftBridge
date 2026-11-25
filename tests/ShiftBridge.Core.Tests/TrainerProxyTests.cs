using System.Net;
using System.Net.Sockets;
using ShiftBridge.Core.DirCon;
using ShiftBridge.Core.Trainer;
using ShiftBridge.Core.Zwift;
using ShiftBridge.Platform;
using ShiftBridge.Platform.Trainer;

namespace ShiftBridge.Core.Tests;

/// <summary>Faux trainer DirCon (type KICKR) : service FTMS avec Indoor Bike Data (notify) et Control Point (write + indicate).</summary>
internal sealed class FakeTrainer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<NetworkStream> _streams = [];
    private readonly HashSet<Guid> _subscribed = [];
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public List<byte[]> ControlPointWrites { get; } = [];
    public bool UuidOnlyEnable { get; init; }

    /// <summary>Expose le service Zwift (virtual shifting natif) en plus de FTMS.</summary>
    public bool WithZwiftService { get; init; }
    public bool RejectNativeWrites { get; set; }
    public List<byte[]> NativeWrites { get; } = [];
    public List<string> NativeWritesHex() { lock (NativeWrites) return NativeWrites.Select(Convert.ToHexString).ToList(); }

    /// <summary>Fonctions FTMS annoncées : par défaut cibles puissance (bit 3) + simulation (bit 13).</summary>
    public byte[] FeatureBytes { get; init; } =
        [.. BitConverter.GetBytes((1u << 1) | (1u << 14)), .. BitConverter.GetBytes((1u << 3) | (1u << 13))];

    public async Task PushNotificationAsync(Guid ch, byte[] value)
    {
        NetworkStream[] snapshot;
        lock (_streams) snapshot = [.. _streams];
        foreach (var s in snapshot) await s.WriteAsync(DirConPacket.NotificationPacket(ch, value).Encode());
    }

    private readonly List<TcpClient> _tcpClients = [];

    public FakeTrainer() { _listener.Start(); _ = AcceptAsync(); }

    /// <summary>Coupe toutes les connexions en cours (le trainer « disparaît » puis revient : l'écoute reste ouverte).</summary>
    public void DropAllConnections()
    {
        lock (_streams)
        {
            foreach (var c in _tcpClients) { try { c.Dispose(); } catch { } }
            _tcpClients.Clear();
            _streams.Clear();
            _subscribed.Clear();
        }
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(); } catch { return; }
            var s = c.GetStream();
            lock (_streams) { _streams.Add(s); _tcpClients.Add(c); }
            _ = ServeAsync(s);
        }
    }

    private async Task ServeAsync(NetworkStream s)
    {
        var parser = new DirConStreamParser();
        var buf = new byte[1024];
        try
        {
            while (true)
            {
                int n = await s.ReadAsync(buf);
                if (n == 0) return;
                foreach (var p in parser.Feed(buf.AsSpan(0, n)))
                {
                    DirConPacket r;
                    switch ((DirConId)p.Id)
                    {
                        case DirConId.DiscoverServices:
                            r = DirConPacket.ServicesResponse(p.Sequence, WithZwiftService
                                ? [FtmsCodec.FitnessMachineService, ZwiftConstants.CustomService]
                                : [FtmsCodec.FitnessMachineService]); break;
                        case DirConId.DiscoverCharacteristics when WithZwiftService && DirConPacket.ReadUuid(p.Payload) == ZwiftConstants.CustomService:
                            r = DirConPacket.CharacteristicsResponse(p.Sequence, ZwiftConstants.CustomService,
                            [
                                (ZwiftConstants.AsyncCharacteristic, DirConProps.Notify),
                                (ZwiftConstants.SyncRxCharacteristic, DirConProps.Write),
                                (ZwiftConstants.SyncTxCharacteristic, DirConProps.Notify | DirConProps.Indicate),
                            ]); break;
                        case DirConId.DiscoverCharacteristics:
                            r = DirConPacket.CharacteristicsResponse(p.Sequence, FtmsCodec.FitnessMachineService,
                                [(FtmsCodec.IndoorBikeData, DirConProps.Notify), (FtmsCodec.ControlPoint, DirConProps.Write | DirConProps.Indicate),
                                 (FtmsCodec.FitnessMachineFeature, DirConProps.Read), (FtmsCodec.FitnessMachineStatus, DirConProps.Notify)]); break;
                        case DirConId.ReadCharacteristic:
                            r = DirConPacket.ReadResponse(p.Sequence, DirConPacket.ReadUuid(p.Payload), FeatureBytes); break;
                        case DirConId.EnableNotifications:
                            if (UuidOnlyEnable && p.Payload.Length > 16) { r = DirConPacket.Error(p.Id, p.Sequence, DirConResponse.UnexpectedError); break; }
                            _subscribed.Add(DirConPacket.ReadUuid(p.Payload));
                            r = DirConPacket.AckResponse(DirConId.EnableNotifications, p.Sequence, DirConPacket.ReadUuid(p.Payload)); break;
                        case DirConId.WriteCharacteristic:
                        {
                            var ch = DirConPacket.ReadUuid(p.Payload);
                            var value = p.Payload[16..];
                            if (ch == ZwiftConstants.SyncRxCharacteristic)
                            {
                                // Canal natif Zwift : on enregistre la trame ; « RideOn » + 02 01 reçoit sa réponse sur 0004.
                                if (RejectNativeWrites) { await s.WriteAsync(DirConPacket.Error(p.Id, p.Sequence, DirConResponse.WriteFailed).Encode()); continue; }
                                lock (NativeWrites) NativeWrites.Add(value);
                                await s.WriteAsync(DirConPacket.AckResponse(DirConId.WriteCharacteristic, p.Sequence, ch).Encode());
                                if (value.AsSpan().SequenceEqual(ZwiftTrainerCodec.RideOnTrainer))
                                    await s.WriteAsync(DirConPacket.NotificationPacket(ZwiftConstants.SyncTxCharacteristic, [.. ZwiftConstants.RideOn, 0x01, 0x03]).Encode());
                                continue;
                            }
                            lock (ControlPointWrites) ControlPointWrites.Add(value);
                            r = DirConPacket.AckResponse(DirConId.WriteCharacteristic, p.Sequence, ch);
                            await s.WriteAsync(r.Encode());
                            // Indication de réponse FTMS : [0x80, opcode, 0x01 = succès]
                            await s.WriteAsync(DirConPacket.NotificationPacket(FtmsCodec.ControlPoint, [0x80, value[0], 0x01]).Encode());
                            continue;
                        }
                        default: r = DirConPacket.Error(p.Id, p.Sequence, DirConResponse.UnknownMessageType); break;
                    }
                    await s.WriteAsync(r.Encode());
                }
            }
        }
        catch { }
    }

    /// <summary>Indoor Bike Data complet (flags 0x44) : vitesse, cadence (0,5 tr/min) et puissance.</summary>
    public async Task PushBikeDataAsync(double speedMs, double cadenceRpm)
    {
        int speed = (int)Math.Round(speedMs * 3.6 * 100);
        int cadence = (int)Math.Round(cadenceRpm * 2);
        await PushRawBikeDataAsync([0x44, 0x00, (byte)(speed & 0xFF), (byte)(speed >> 8), (byte)(cadence & 0xFF), (byte)(cadence >> 8), 0x50, 0x00]);
    }

    public async Task PushRawBikeDataAsync(byte[] data)
    {
        NetworkStream[] snapshot;
        lock (_streams) snapshot = [.. _streams];
        foreach (var s in snapshot) await s.WriteAsync(DirConPacket.NotificationPacket(FtmsCodec.IndoorBikeData, data).Encode());
    }

    public async Task PushBikeDataAsync(double speedMs)
    {
        int raw = (int)Math.Round(speedMs * 3.6 * 100);
        byte[] data = [0x44, 0x00, (byte)(raw & 0xFF), (byte)(raw >> 8), 0x50, 0x00];
        NetworkStream[] snapshot;
        lock (_streams) snapshot = [.. _streams];
        foreach (var s in snapshot) await s.WriteAsync(DirConPacket.NotificationPacket(FtmsCodec.IndoorBikeData, data).Encode());
    }

    public async ValueTask DisposeAsync() { _listener.Stop(); await Task.CompletedTask; }
}

public class TrainerProxyTests
{
    private static readonly byte[] SimFlat = [0x11, 0x00, 0x00, 0x00, 0x00, 40, 51]; // pente 0 %

    private static async Task<(DirConClient upstream, DirConTrainerProxy proxy, DirConClient app)> SetupAsync(
        FakeTrainer trainer, VirtualShiftingModel model)
    {
        var upstream = new DirConClient();
        await upstream.ConnectAsync("127.0.0.1", trainer.Port);
        var proxy = new DirConTrainerProxy(upstream, model, name: "ProxyTest");
        await proxy.StartAsync();
        var app = new DirConClient();
        await app.ConnectAsync("127.0.0.1", proxy.Port);
        return (upstream, proxy, app);
    }

    private static async Task WaitUntil(Func<bool> cond, int ms = 3000)
    {
        var end = Environment.TickCount64 + ms;
        while (!cond()) { if (Environment.TickCount64 > end) throw new TimeoutException("condition non atteinte"); await Task.Delay(20); }
    }

    [Fact]
    public async Task App_sees_the_same_service_tree_as_the_trainer()
    {
        await using var trainer = new FakeTrainer();
        var (up, proxy, app) = await SetupAsync(trainer, new VirtualShiftingModel());
        await using var _1 = up; await using var _2 = proxy; await using var _3 = app;

        var services = await app.DiscoverServicesAsync();
        Assert.Equal([FtmsCodec.FitnessMachineService], services);
        var chars = await app.DiscoverCharacteristicsAsync(FtmsCodec.FitnessMachineService);
        Assert.Equal(4, chars.Count);
        Assert.Contains(chars, c => c.Uuid == FtmsCodec.ControlPoint && (c.Props & DirConProps.Write) != 0);
    }

    [Fact]
    public async Task Reference_gear_is_a_pure_passthrough()
    {
        await using var trainer = new FakeTrainer();
        var model = new VirtualShiftingModel { Gear = 12 };
        var (up, proxy, app) = await SetupAsync(trainer, model);
        await using var _1 = up; await using var _2 = proxy; await using var _3 = app;

        await app.WriteAsync(FtmsCodec.ControlPoint, [0x11, 0x00, 0x00, 0x2C, 0x01, 40, 51]); // +3 %
        await WaitUntil(() => trainer.ControlPointWrites.Count == 1);
        Assert.Equal(new byte[] { 0x11, 0x00, 0x00, 0x2C, 0x01, 40, 51 }, trainer.ControlPointWrites[0]);
    }

    [Fact]
    public async Task Bigger_gear_rewrites_grade_and_scales_reported_speed()
    {
        await using var trainer = new FakeTrainer();
        var model = new VirtualShiftingModel { Gear = 18 };
        var (up, proxy, app) = await SetupAsync(trainer, model);
        await using var _1 = up; await using var _2 = proxy; await using var _3 = app;

        double? seen = null;
        app.Notification += (ch, v) => { if (ch == FtmsCodec.IndoorBikeData && FtmsCodec.TryGetSpeedMs(v, out var s)) seen = s; };
        await app.EnableNotificationsAsync(FtmsCodec.IndoorBikeData);

        await trainer.PushBikeDataAsync(8.0);                     // vitesse physique 8 m/s
        await WaitUntil(() => seen is not null);
        Assert.Equal(8.0 * model.Factor, seen!.Value, 2);          // vitesse virtuelle = k · v

        await app.WriteAsync(FtmsCodec.ControlPoint, SimFlat);    // route plate demandée par l'appli
        await WaitUntil(() => trainer.ControlPointWrites.Count == 1);
        Assert.True(FtmsCodec.TryParseSimulation(trainer.ControlPointWrites[0], out var sent));
        // Mise à l'échelle complète : au rapport 18 (k > 1) la route est plus « lourde » qu'à plat natif, donc roulement et
        // traînée augmentent (la pente, elle, n'a pas à bouger sur une route plate).
        Assert.True(sent.CwRaw > 51, $"traînée envoyée au trainer : {sent.CwRaw}");
        Assert.True(sent.CrrRaw > 40, $"roulement envoyé au trainer : {sent.CrrRaw}");
    }

    [Fact]
    public async Task Gear_change_reapplies_last_simulation_and_swallows_the_extra_response()
    {
        await using var trainer = new FakeTrainer();
        var model = new VirtualShiftingModel { Gear = 12 };
        var (up, proxy, app) = await SetupAsync(trainer, model);
        await using var _1 = up; await using var _2 = proxy; await using var _3 = app;

        var responsesSeenByApp = 0;
        app.Notification += (ch, v) => { if (ch == FtmsCodec.ControlPoint && FtmsCodec.IsResponseTo(v, 0x11)) Interlocked.Increment(ref responsesSeenByApp); };

        await app.WriteAsync(FtmsCodec.ControlPoint, SimFlat);
        await WaitUntil(() => trainer.ControlPointWrites.Count == 1 && responsesSeenByApp == 1);

        model.Gear = 20;
        await proxy.ReapplyAsync();                                 // l'appli n'a rien renvoyé : le proxy ré-applique
        await WaitUntil(() => trainer.ControlPointWrites.Count == 2);
        Assert.True(FtmsCodec.TryParseSimulation(trainer.ControlPointWrites[1], out var second));
        Assert.True(second.CwRaw > 51);                              // rapport 20 : résistance plus forte qu'à la référence

        await Task.Delay(300);
        Assert.Equal(1, responsesSeenByApp);                        // la réponse à la commande du proxy n'est pas transmise à l'appli
    }

    [Fact]
    public async Task Bridge_with_active_proxy_applies_shifts_to_trainer_and_keeps_them_away_from_the_app()
    {
        await using var trainer = new FakeTrainer();
        await using var bridge = new BridgeService(new ShiftBridge.Core.Config.AppConfig());
        var obcServer = new ShiftBridge.Platform.Obc.ObcTcpServer("VsTest");
        await bridge.AddTransportAsync(obcServer);
        await bridge.StartTrainerProxyAsync(new TrainerEndpoint.DirCon("127.0.0.1", trainer.Port));

        await using var app = new DirConClient();
        await app.ConnectAsync("127.0.0.1", bridge.TrainerProxyPort);
        await trainer.PushBikeDataAsync(8.0);                       // le proxy connaît ainsi la vitesse physique
        await WaitUntil(() => bridge.TrainerSpeedMs > 7);
        await app.WriteAsync(FtmsCodec.ControlPoint, SimFlat);
        await WaitUntil(() => trainer.ControlPointWrites.Count == 1);

        using var obc = new TcpClient();
        await obc.ConnectAsync("127.0.0.1", obcServer.Port);
        var s = obc.GetStream();
        var status = new byte[3];
        await s.ReadExactlyAsync(status);

        // Vitesse + : rapport 12 -> 13. Le trainer reçoit une nouvelle pente ; l'appli OBC ne reçoit PAS ShiftUp.
        int gearBefore = bridge.Gear.Gear;
        bridge.InjectEvents([new ShiftBridge.Core.Input.ButtonEvent(ShiftBridge.Core.Input.PhysicalButton.ClickPlus, true)]);
        bridge.InjectEvents([new ShiftBridge.Core.Input.ButtonEvent(ShiftBridge.Core.Input.PhysicalButton.ClickPlus, false)]);
        await WaitUntil(() => trainer.ControlPointWrites.Count == 2);
        Assert.Equal(gearBefore + 1, bridge.Gear.Gear);
        Assert.True(FtmsCodec.TryParseSimulation(trainer.ControlPointWrites[1], out var reapplied));
        Assert.True(reapplied.CwRaw > 51, "un rapport plus grand doit durcir la résistance envoyée au trainer");

        // Les autres actions continuent d'aller à l'appli : « Valider » (Select) arrive, jamais ShiftUp/ShiftDown.
        // Le rapport virtuel est annoncé en « Gear Set » (rapport + 1) : 12 au départ (état 13), puis 13 (état 14).
        bridge.InjectEvents([new ShiftBridge.Core.Input.ButtonEvent(ShiftBridge.Core.Input.PhysicalButton.RightA, true)]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var received = new List<(byte Id, byte State)>();
        while (!received.Contains(((byte)ShiftBridge.Core.Obc.ObcButtonId.Select, (byte)1)))
        {
            var msg = new byte[3];
            await s.ReadExactlyAsync(msg, cts.Token);
            Assert.Equal(0x01, msg[0]);
            received.Add((msg[1], msg[2]));
        }
        Assert.DoesNotContain(received, m => m.Id is (byte)ShiftBridge.Core.Obc.ObcButtonId.ShiftUp or (byte)ShiftBridge.Core.Obc.ObcButtonId.ShiftDown);
        Assert.Contains(((byte)ShiftBridge.Core.Obc.ObcButtonId.GearSet, (byte)(gearBefore + 2)), received);
    }

    [Fact]
    public async Task When_the_app_handles_gears_shifts_go_to_the_app_and_the_trainer_is_relayed_unchanged()
    {
        await using var trainer = new FakeTrainer();
        var config = new ShiftBridge.Core.Config.AppConfig { AppHandlesShifting = true, LastGear = 22 }; // rapport dur : ShiftBridge réécrirait la pente
        await using var bridge = new BridgeService(config);
        var obcServer = new ShiftBridge.Platform.Obc.ObcTcpServer("AppShifts");
        await bridge.AddTransportAsync(obcServer);
        await bridge.StartTrainerProxyAsync(new TrainerEndpoint.DirCon("127.0.0.1", trainer.Port));

        await using var app = new DirConClient();
        await app.ConnectAsync("127.0.0.1", bridge.TrainerProxyPort);
        await trainer.PushBikeDataAsync(8.0);
        await WaitUntil(() => bridge.TrainerSpeedMs > 7);
        await app.WriteAsync(FtmsCodec.ControlPoint, SimFlat);
        await WaitUntil(() => trainer.ControlPointWrites.Count == 1);
        Assert.Equal(SimFlat, trainer.ControlPointWrites[0]);           // relais pur

        using var obc = new TcpClient();
        await obc.ConnectAsync("127.0.0.1", obcServer.Port);
        var s = obc.GetStream();
        await s.ReadExactlyAsync(new byte[3]);

        // Vitesse + : l'appli reçoit ShiftUp (appui puis relâchement), le trainer ne reçoit rien de plus, pas de « Gear Set ».
        bridge.InjectEvents([new ShiftBridge.Core.Input.ButtonEvent(ShiftBridge.Core.Input.PhysicalButton.ClickPlus, true)]);
        bridge.InjectEvents([new ShiftBridge.Core.Input.ButtonEvent(ShiftBridge.Core.Input.PhysicalButton.ClickPlus, false)]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var received = new List<(byte Id, byte State)>();
        while (!received.Contains(((byte)ShiftBridge.Core.Obc.ObcButtonId.ShiftUp, (byte)0)))
        {
            var msg = new byte[3];
            await s.ReadExactlyAsync(msg, cts.Token);
            received.Add((msg[1], msg[2]));
        }
        Assert.Contains(((byte)ShiftBridge.Core.Obc.ObcButtonId.ShiftUp, (byte)1), received);
        Assert.DoesNotContain(received, m => m.Id == (byte)ShiftBridge.Core.Obc.ObcButtonId.GearSet);
        await Task.Delay(200);
        Assert.Single(trainer.ControlPointWrites);

        // Retour à ShiftBridge à chaud : la pente est de nouveau réécrite pour le rapport courant…
        await bridge.SetAppHandlesShiftingAsync(false);
        await WaitUntil(() => trainer.ControlPointWrites.Count == 2);
        Assert.NotEqual(SimFlat, trainer.ControlPointWrites[1]);
        // … puis rendue à l'appli : le trainer retrouve la simulation brute.
        await bridge.SetAppHandlesShiftingAsync(true);
        await WaitUntil(() => trainer.ControlPointWrites.Count == 3);
        Assert.Equal(SimFlat, trainer.ControlPointWrites[2]);
    }

    [Fact]
    public async Task Gear_report_is_sent_on_app_info_and_released_when_the_proxy_stops_and_not_when_disabled()
    {
        await using var trainer = new FakeTrainer();
        var config = new ShiftBridge.Core.Config.AppConfig();
        await using var bridge = new BridgeService(config);
        var obcServer = new ShiftBridge.Platform.Obc.ObcTcpServer("GearReport");
        await bridge.AddTransportAsync(obcServer);
        await bridge.StartTrainerProxyAsync(new TrainerEndpoint.DirCon("127.0.0.1", trainer.Port));

        using var obc = new TcpClient();
        await obc.ConnectAsync("127.0.0.1", obcServer.Port);
        var s = obc.GetStream();
        await s.ReadExactlyAsync(new byte[3]);                                       // état du périphérique

        // L'appli se connecte et se présente (App Info, liste vide = tout) : le rapport courant lui est annoncé.
        var appInfo = ShiftBridge.Core.Obc.ObcMessages.EncodeAppInfo(new ShiftBridge.Core.Obc.ObcAppInfo("tpv", "1.0", []));
        await s.WriteAsync(appInfo);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var msg = new byte[3];
        await s.ReadExactlyAsync(msg, cts.Token);
        Assert.Equal(new byte[] { 0x01, (byte)ShiftBridge.Core.Obc.ObcButtonId.GearSet, (byte)(bridge.Gear.Gear + 1) }, msg);

        await bridge.StopTrainerProxyAsync();                                        // arrêt : « Gear Set » relâché (0)
        await s.ReadExactlyAsync(msg, cts.Token);
        Assert.Equal(new byte[] { 0x01, (byte)ShiftBridge.Core.Obc.ObcButtonId.GearSet, 0x00 }, msg);
    }

    [Fact]
    public async Task Gear_change_during_erg_does_not_touch_the_trainer()
    {
        await using var trainer = new FakeTrainer();
        var model = new VirtualShiftingModel { Gear = 12 };
        var (up, proxy, app) = await SetupAsync(trainer, model);
        await using var _1 = up; await using var _2 = proxy; await using var _3 = app;

        await app.WriteAsync(FtmsCodec.ControlPoint, SimFlat);                       // simulation…
        await WaitUntil(() => trainer.ControlPointWrites.Count == 1);
        await app.WriteAsync(FtmsCodec.ControlPoint, [0x05, 0xC8, 0x00]);            // …puis ERG 200 W
        await WaitUntil(() => trainer.ControlPointWrites.Count == 2);

        await trainer.PushBikeDataAsync(8.0);
        model.Gear = 20;
        await proxy.ReapplyAsync();                                                  // changement de rapport pendant l'ERG
        await Task.Delay(700);
        Assert.Equal(2, trainer.ControlPointWrites.Count);                           // aucune ré-application : le trainer reste en ERG
    }

    [Fact]
    public async Task Write_ack_reaches_the_app_before_the_control_point_indication()
    {
        await using var trainer = new FakeTrainer();
        var (up, proxy, app) = await SetupAsync(trainer, new VirtualShiftingModel());
        await using var _1 = up; await using var _2 = proxy; await using var _3 = app;

        // Client brut : on observe l'ordre exact des paquets reçus.
        using var raw = new TcpClient();
        await raw.ConnectAsync("127.0.0.1", proxy.Port);
        var s = raw.GetStream();
        var parser = new DirConStreamParser();
        var received = new List<DirConPacket>();

        byte[] write = [.. DirConPacket.UuidBytes(FtmsCodec.ControlPoint), .. new byte[] { 0x00 }]; // Request Control
        await s.WriteAsync(new DirConPacket((byte)DirConId.WriteCharacteristic, 1, 0, write).Encode());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buf = new byte[512];
        while (received.Count < 2)
        {
            int n = await s.ReadAsync(buf, cts.Token);
            received.AddRange(parser.Feed(buf.AsSpan(0, n)));
        }
        Assert.Equal((byte)DirConId.WriteCharacteristic, received[0].Id);            // l'acquittement d'abord
        Assert.Equal((byte)DirConId.Notification, received[1].Id);                    // puis l'indication 0x80
    }

    [Fact]
    public async Task Control_point_response_nobody_asked_for_is_reported_as_foreign_control()
    {
        await using var trainer = new FakeTrainer();
        var (up, proxy, app) = await SetupAsync(trainer, new VirtualShiftingModel());
        await using var _1 = up; await using var _2 = proxy; await using var _3 = app;
        bool detected = false;
        proxy.ForeignControlDetected += () => detected = true;

        await trainer.PushNotificationAsync(FtmsCodec.ControlPoint, [0x80, 0x11, 0x01]); // un autre client a écrit sur le trainer
        await WaitUntil(() => detected);
    }

    [Fact]
    public async Task Features_are_read_at_start_and_missing_simulation_is_flagged()
    {
        var noSim = new FakeTrainer
        {
            FeatureBytes = [.. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(1u << 3)] // cible de puissance seulement
        };
        await using var trainer = noSim;
        var upstream = new DirConClient();
        await upstream.ConnectAsync("127.0.0.1", trainer.Port);
        var proxy = new DirConTrainerProxy(upstream, new VirtualShiftingModel(), name: "FeatTest");
        var logs = new List<string>();
        proxy.Log += m => { lock (logs) logs.Add(m); };
        await proxy.StartAsync();
        await using var _1 = upstream; await using var _2 = proxy;

        Assert.NotNull(proxy.Features);
        Assert.False(proxy.Features!.SupportsIndoorBikeSimulation);
        lock (logs) Assert.Contains(logs, l => l.Contains("ne déclare pas la simulation"));
    }

    [Fact]
    public async Task Status_with_rewritten_grade_is_shown_to_the_app_with_its_own_grade()
    {
        await using var trainer = new FakeTrainer();
        var model = new VirtualShiftingModel { Gear = 20 };
        var (up, proxy, app) = await SetupAsync(trainer, model);
        await using var _1 = up; await using var _2 = proxy; await using var _3 = app;

        byte[]? status = null;
        app.Notification += (ch, v) => { if (ch == FtmsCodec.FitnessMachineStatus) status = v; };
        await app.EnableNotificationsAsync(FtmsCodec.FitnessMachineStatus);
        await app.WriteAsync(FtmsCodec.ControlPoint, SimFlat);                       // l'appli demande 0 %
        await WaitUntil(() => trainer.ControlPointWrites.Count == 1);

        await trainer.PushNotificationAsync(FtmsCodec.FitnessMachineStatus, [0x12, 0, 0, 0x90, 0x01, 40, 51]); // le trainer rapporte 4,00 %
        await WaitUntil(() => status is not null);
        Assert.Equal(0, status![3] | (status[4] << 8));                              // l'appli revoit 0 %, pas la pente réécrite
    }

    private static async Task<(ReconnectingTrainerLink link, DirConTrainerProxy proxy, DirConClient app)> SetupReconnectingAsync(
        FakeTrainer trainer, VirtualShiftingModel model, TimeSpan reconnectDelay)
    {
        var link = new ReconnectingTrainerLink(async ct =>
        {
            var c = new DirConClient();
            await c.ConnectAsync("127.0.0.1", trainer.Port, ct);
            return c;
        }, initialDelay: reconnectDelay, maxDelay: reconnectDelay, silenceTimeout: TimeSpan.FromMinutes(5));
        await link.ConnectAsync();
        var proxy = new DirConTrainerProxy(link, model, name: "ReconnTest");
        await proxy.StartAsync();
        var app = new DirConClient();
        await app.ConnectAsync("127.0.0.1", proxy.Port);
        return (link, proxy, app);
    }

    [Fact]
    public async Task After_a_trainer_drop_the_proxy_replays_control_and_the_adjusted_simulation()
    {
        await using var trainer = new FakeTrainer();
        var model = new VirtualShiftingModel { Gear = 18 };
        var (link, proxy, app) = await SetupReconnectingAsync(trainer, model, TimeSpan.FromMilliseconds(50));
        await using var _1 = link; await using var _2 = proxy; await using var _3 = app;

        int responsesSeenByApp = 0;
        app.Notification += (ch, v) => { if (ch == FtmsCodec.ControlPoint) Interlocked.Increment(ref responsesSeenByApp); };
        await trainer.PushBikeDataAsync(8.0);
        await WaitUntil(() => proxy.LastSpeedMs > 7);

        await app.WriteAsync(FtmsCodec.ControlPoint, [FtmsCodec.OpRequestControl]);
        await WaitUntil(() => trainer.ControlPointWrites.Count == 1);
        await app.WriteAsync(FtmsCodec.ControlPoint, [FtmsCodec.OpStart]);
        await WaitUntil(() => trainer.ControlPointWrites.Count == 2);
        await app.WriteAsync(FtmsCodec.ControlPoint, SimFlat);
        await WaitUntil(() => trainer.ControlPointWrites.Count == 3 && responsesSeenByApp == 3);

        trainer.DropAllConnections();                                              // le trainer disparaît puis revient
        await WaitUntil(() => link.ReconnectCount == 1);
        await WaitUntil(() => trainer.ControlPointWrites.Count == 6);

        // Rejeu dans l'ordre : contrôle, démarrage, puis la simulation ré-ajustée au rapport courant.
        Assert.Equal(FtmsCodec.OpRequestControl, trainer.ControlPointWrites[3][0]);
        Assert.Equal(FtmsCodec.OpStart, trainer.ControlPointWrites[4][0]);
        Assert.True(FtmsCodec.TryParseSimulation(trainer.ControlPointWrites[5], out var replayed));
        Assert.True(replayed.CwRaw > 51);                                           // simulation ré-ajustée au rapport 18
        await Task.Delay(300);
        Assert.Equal(3, responsesSeenByApp);                                       // les réponses au rejeu n'arrivent pas à l'appli
    }

    [Fact]
    public async Task App_command_sent_during_an_outage_is_acknowledged_and_replayed_after_reconnection()
    {
        await using var trainer = new FakeTrainer();
        var (link, proxy, app) = await SetupReconnectingAsync(trainer, new VirtualShiftingModel { Gear = 12 }, TimeSpan.FromMilliseconds(500));
        await using var _1 = link; await using var _2 = proxy; await using var _3 = app;

        var responses = new List<byte[]>();
        app.Notification += (ch, v) => { if (ch == FtmsCodec.ControlPoint) lock (responses) responses.Add(v); };

        trainer.DropAllConnections();
        await WaitUntil(() => link.State == LinkState.Reconnecting);

        await app.WriteAsync(FtmsCodec.ControlPoint, [0x05, 0xC8, 0x00]);         // ERG 200 W pendant la coupure : écriture acquittée (sinon exception)
        await WaitUntil(() => { lock (responses) return responses.Count == 1; });
        lock (responses) Assert.Equal(new byte[] { 0x80, 0x05, 0x01 }, responses[0]); // réponse de succès synthétisée

        await WaitUntil(() => link.ReconnectCount == 1);
        await WaitUntil(() => trainer.ControlPointWrites.Count >= 1);
        Assert.Equal(new byte[] { 0x05, 0xC8, 0x00 }, trainer.ControlPointWrites[^1]); // la dernière cible est rejouée au retour du trainer
    }

    [Fact]
    public async Task Measure_returns_the_real_ratio_from_the_trainers_speed_and_cadence()
    {
        await using var trainer = new FakeTrainer();
        var (up, proxy, app) = await SetupAsync(trainer, new VirtualShiftingModel());
        await using var _1 = up; await using var _2 = proxy; await using var _3 = app;

        var measure = proxy.MeasureRealRatioAsync(TimeSpan.FromSeconds(8));
        double speed = 2.43 * 90 / 60.0 * 2.105;                                   // braquet réel 2,43 à 90 tr/min
        for (int i = 0; i < 25 && !measure.IsCompleted; i++) { await trainer.PushBikeDataAsync(speed, 90); await Task.Delay(30); }

        var result = await measure;
        Assert.True(result.Success, result.Message);
        Assert.InRange(result.Ratio / 2.43, 0.99, 1.01);
    }

    [Fact]
    public async Task Measure_explains_why_it_failed()
    {
        await using var trainer = new FakeTrainer();
        var (up, proxy, app) = await SetupAsync(trainer, new VirtualShiftingModel());
        await using var _1 = up; await using var _2 = proxy; await using var _3 = app;

        // 1) cadence hors plage (40 tr/min) : on dit à quelle cadence pédaler
        var slow = proxy.MeasureRealRatioAsync(TimeSpan.FromSeconds(1.5));
        for (int i = 0; i < 40 && !slow.IsCompleted; i++) { await trainer.PushBikeDataAsync(4.0, 40); await Task.Delay(30); }
        var r1 = await slow;
        Assert.False(r1.Success);
        Assert.Contains("60", r1.Message);
        Assert.Contains("110", r1.Message);

        // 2) trainer qui n'envoie pas la cadence (flags 0x40 : vitesse + puissance seulement)
        var noCadence = proxy.MeasureRealRatioAsync(TimeSpan.FromSeconds(1.5));
        for (int i = 0; i < 40 && !noCadence.IsCompleted; i++) { await trainer.PushRawBikeDataAsync([0x40, 0x00, 0x10, 0x0E, 0x50, 0x00]); await Task.Delay(30); }
        var r2 = await noCadence;
        Assert.False(r2.Success);
        Assert.Contains("cadence", r2.Message);
        Assert.Contains("saisissez", r2.Message);

        // 3) aucune donnée du tout
        var silent = await proxy.MeasureRealRatioAsync(TimeSpan.FromSeconds(0.6));
        Assert.False(silent.Success);
        Assert.Contains("Aucune mesure", silent.Message);
    }

    [Fact]
    public async Task Changing_the_drivetrain_updates_the_reference_and_reapplies_the_resistance()
    {
        await using var trainer = new FakeTrainer();
        await using var bridge = new BridgeService(new ShiftBridge.Core.Config.AppConfig());
        await bridge.StartTrainerProxyAsync(new TrainerEndpoint.DirCon("127.0.0.1", trainer.Port));
        await using var app = new DirConClient();
        await app.ConnectAsync("127.0.0.1", bridge.TrainerProxyPort);

        await app.WriteAsync(FtmsCodec.ControlPoint, SimFlat);
        await WaitUntil(() => trainer.ControlPointWrites.Count == 1);
        // Braquet par défaut 34×14 = 2,43 : rapport 12 (2,40) ≈ neutre, k = 0,99.
        Assert.InRange(bridge.GearFactor, 0.98, 1.0);

        bridge.SetDrivetrain(new ShiftBridge.Core.Trainer.Drivetrain(50, 14));   // grand plateau : 3,57
        await WaitUntil(() => trainer.ControlPointWrites.Count == 2);
        Assert.Equal(2.40 / (50.0 / 14.0), bridge.GearFactor, 3);                // le rapport 12 devient ~1,5× plus facile
        Assert.True(FtmsCodec.TryParseSimulation(trainer.ControlPointWrites[1], out var easier));
        Assert.True(easier.CwRaw < 51, $"traînée envoyée : {easier.CwRaw}");     // résistance réduite, sans nouvelle commande de l'appli
        Assert.Equal(50, bridge.Drivetrain.ChainringTeeth);

        bridge.SetDrivetrain(new ShiftBridge.Core.Trainer.Drivetrain(5, 14));    // invalide : ignoré
        Assert.Equal(50, bridge.Drivetrain.ChainringTeeth);
    }

    [Fact]
    public async Task Measure_without_an_active_proxy_asks_to_activate_it_first()
    {
        await using var bridge = new BridgeService(new ShiftBridge.Core.Config.AppConfig());
        var r = await bridge.MeasureDrivetrainAsync();
        Assert.False(r.Success);
        Assert.Contains("virtual shifting", r.Message);
    }

    [Fact]
    public async Task Disabled_proxy_never_modifies_anything()
    {
        await using var trainer = new FakeTrainer();
        var model = new VirtualShiftingModel { Gear = 22 };
        var (up, proxy, app) = await SetupAsync(trainer, model);
        await using var _1 = up; await using var _2 = proxy; await using var _3 = app;
        proxy.Enabled = false;

        await app.WriteAsync(FtmsCodec.ControlPoint, SimFlat);
        await WaitUntil(() => trainer.ControlPointWrites.Count == 1);
        Assert.Equal(SimFlat, trainer.ControlPointWrites[0]);
    }

    [Fact]
    public async Task Client_falls_back_to_uuid_only_enable_when_trainer_rejects_the_flag_byte()
    {
        await using var trainer = new FakeTrainer { UuidOnlyEnable = true };
        var (up, proxy, app) = await SetupAsync(trainer, new VirtualShiftingModel());
        await using var _1 = up; await using var _2 = proxy; await using var _3 = app;
        // Si l'abonnement amont avait échoué, l'appli ne recevrait jamais de vitesse.
        double? seen = null;
        app.Notification += (ch, v) => { if (FtmsCodec.TryGetSpeedMs(v, out var s)) seen = s; };
        await app.EnableNotificationsAsync(FtmsCodec.IndoorBikeData);
        await trainer.PushBikeDataAsync(5.0);
        await WaitUntil(() => seen is not null);
        Assert.Equal(5.0, seen!.Value, 2);
    }
}
