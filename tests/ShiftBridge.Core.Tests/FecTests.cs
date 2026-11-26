using ShiftBridge.Core.DirCon;
using ShiftBridge.Core.Trainer;
using ShiftBridge.Platform.Trainer;
using Xunit;

namespace ShiftBridge.Core.Tests;

/// <summary>Tacx NEO Smart (T2800) tel que relevé : pas de FTMS, FE-C sur Bluetooth, puissance et vitesse/cadence.</summary>
internal sealed class FakeTacxLink(bool withFec = true) : ITrainerLink
{
    public static readonly Guid TacxVendor = new("669aa501-0c08-969e-e211-86ad5062675f");
    private static Guid S(ushort u) => FtmsCodec.BaseUuid(u);

    public readonly List<(Guid Ch, byte[] Value)> Writes = [];
    public bool IsConnected => true;
    public event Action<Guid, byte[]>? Notification;
#pragma warning disable CS0067
    public event Action? Disconnected;
    public event Action<string>? Log;
#pragma warning restore CS0067

    public Task<List<Guid>> DiscoverServicesAsync() => Task.FromResult(withFec
        ? new List<Guid> { FecCodec.Service, S(0x180A), S(0x1816), S(0x1818), TacxVendor }
        : new List<Guid> { S(0x180A), S(0x1816), S(0x1818), TacxVendor });

    public Task<List<(Guid Uuid, byte Props)>> DiscoverCharacteristicsAsync(Guid service) => Task.FromResult(
        service == FecCodec.Service ? new List<(Guid, byte)> { (FecCodec.Rx, DirConProps.Notify), (FecCodec.Tx, DirConProps.Write) }
        : service == S(0x1818) ? [(S(0x2A63), DirConProps.Notify), (S(0x2A65), DirConProps.Read)]
        : service == S(0x1816) ? [(S(0x2A5B), DirConProps.Notify)]
        : service == TacxVendor ? [(new Guid("669aac01-0c08-969e-e211-86ad5062675f"), (byte)(DirConProps.Read | DirConProps.Write))]
        : [(S(0x2A29), DirConProps.Read)]);

    public Task<byte[]> ReadAsync(Guid ch) => Task.FromResult(new byte[] { 0x54 });
    public Task WriteAsync(Guid ch, byte[] value) { lock (Writes) Writes.Add((ch, value)); return Task.CompletedTask; }
    public Task EnableNotificationsAsync(Guid ch, bool enable = true) => Task.CompletedTask;
    public void Push(Guid ch, byte[] v) => Notification?.Invoke(ch, v);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public List<byte[]> FecWrites() { lock (Writes) return [.. Writes.Where(w => w.Ch == FecCodec.Tx).Select(w => w.Value)]; }
}

public class FecTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    /// <summary>Message FE-C acquitté (4F) sur le canal 5, somme calculée.</summary>
    private static byte[] Msg(params byte[] page)
    {
        var m = new byte[13];
        m[0] = 0xA4; m[1] = 0x09; m[2] = 0x4F; m[3] = 0x05;
        page.CopyTo(m, 4);
        FecCodec.Seal(m);
        return m;
    }

    /// <summary>Page 0x33 : pente en %, roulement (unité 5e-5).</summary>
    private static byte[] Track(double gradePercent, byte crr = 80)
    {
        int raw = (int)Math.Round((gradePercent + 200) * 100);
        return Msg(0x33, 0xFF, 0xFF, 0xFF, 0xFF, (byte)(raw & 0xFF), (byte)(raw >> 8), crr);
    }

    private static async Task Until(Func<bool> cond, int ms = 3000)
    {
        var end = Environment.TickCount64 + ms;
        while (!cond()) { if (Environment.TickCount64 > end) throw new TimeoutException(); await Task.Delay(10); }
    }

    // ---------- Codec, sur les trames réelles du NEO ----------

    [Theory]
    [InlineData("A4094E05190400CE5200402007")] // page 0x19 trainer à l'arrêt
    [InlineData("A4094E05101937DA0000FF24D9")] // page 0x10 générale
    [InlineData("A4094E0550FFFF015900F00A14")] // page 0x50 : Tacx, modèle 2800
    [InlineData("A4094E0551FF0007BAB7000042")] // page 0x51 : n° de série 47034
    public void Captured_neo_frames_have_valid_checksums(string hex)
    {
        Assert.True(FecCodec.TrySplit(H(hex), out var messages));
        Assert.Single(messages);
    }

    [Fact]
    public void Several_messages_in_one_value_are_split_and_a_bad_checksum_rejects_the_value()
    {
        Assert.True(FecCodec.TrySplit(H("A4094E05190400CE5200402007" + "A4094E05101937DA0000FF24D9"), out var two));
        Assert.Equal(2, two.Count);
        Assert.False(FecCodec.TrySplit(H("A4094E05190400CE5200402008"), out _));
        Assert.False(FecCodec.TrySplit(H("A4094E0519"), out _));
    }

    [Fact]
    public void Captured_trainer_data_and_speed_are_read()
    {
        Assert.True(FecCodec.TryGetTrainerData(H("A4094E05190400CE5200402007"), out var cadence, out var power));
        Assert.Equal(0, cadence);
        Assert.Equal(0, power);
        Assert.True(FecCodec.TryGetSpeedMs(H("A4094E05101937DA0000FF24D9"), out var v));
        Assert.Equal(0, v);
    }

    [Fact]
    public void Trainer_data_reads_cadence_and_12_bit_power()
    {
        // cadence 90, puissance 0x12C = 300 W (octet de poids fort dans le quartet bas, statut 4 dans le quartet haut)
        Assert.True(FecCodec.TryGetTrainerData(Msg(0x19, 0x01, 90, 0x00, 0x00, 0x2C, 0x41, 0x20), out var cadence, out var power));
        Assert.Equal(90, cadence);
        Assert.Equal(300, power);
    }

    [Fact]
    public void Track_resistance_grade_uses_the_minus_200_percent_offset()
    {
        Assert.True(FecCodec.TryParseTrack(Track(5.0), out short grade, out double crr));
        Assert.Equal(500, grade);
        Assert.Equal(0.004, crr, 6);

        Assert.True(FecCodec.TryParseTrack(Track(-3.5), out grade, out _));
        Assert.Equal(-350, grade);
    }

    [Fact]
    public void Rewritten_grade_keeps_the_other_bytes_and_a_valid_checksum()
    {
        var original = Track(2.0);
        var rewritten = FecCodec.WithGrade(original, 750);
        Assert.True(FecCodec.TrySplit(rewritten, out _));
        Assert.True(FecCodec.TryParseTrack(rewritten, out short grade, out _));
        Assert.Equal(750, grade);
        Assert.Equal(original[..9], rewritten[..9]);
        Assert.Equal(original[11], rewritten[11]);
    }

    [Fact]
    public void Speed_is_scaled_with_a_valid_checksum()
    {
        var page = Msg(0x10, 0x19, 0x00, 0x00, 0x10, 0x27, 0xFF, 0x24); // 10 000 mm/s = 10 m/s
        var scaled = FecCodec.ScaleSpeed(page, 1.5);
        Assert.True(FecCodec.TrySplit(scaled, out _));
        Assert.True(FecCodec.TryGetSpeedMs(scaled, out var v));
        Assert.Equal(15.0, v, 3);
    }

    // ---------- Proxy ----------

    private static async Task<(FakeTacxLink trainer, VirtualShiftingModel model, DirConTrainerProxy proxy, DirConClient app)> StartAsync(bool withFec = true)
    {
        var trainer = new FakeTacxLink(withFec);
        var model = new VirtualShiftingModel();
        // Liste d'autorisation, comme pour un trainer Bluetooth : le FE-C n'y figure pas, il doit être ajouté faute de FTMS.
        var proxy = new DirConTrainerProxy(trainer, model, name: "FecTest", policy: new GattRelayPolicy(full: false));
        await proxy.StartAsync();
        var app = new DirConClient();
        await app.ConnectAsync("127.0.0.1", proxy.Port);
        return (trainer, model, proxy, app);
    }

    [Fact]
    public async Task Fec_service_is_relayed_when_the_trainer_has_no_ftms_but_the_vendor_service_is_not()
    {
        var (_, _, proxy, app) = await StartAsync();
        await using var _1 = proxy; await using var _2 = app;

        Assert.Equal(TrainerControl.FeC, proxy.Control);
        var services = await app.DiscoverServicesAsync();
        Assert.Contains(FecCodec.Service, services);
        Assert.DoesNotContain(FakeTacxLink.TacxVendor, services);
    }

    [Fact]
    public async Task Trainer_without_ftms_or_fec_cannot_be_controlled()
    {
        var (_, _, proxy, app) = await StartAsync(withFec: false);
        await using var _1 = proxy; await using var _2 = app;
        Assert.Equal(TrainerControl.None, proxy.Control);
    }

    [Fact]
    public async Task Simulation_is_left_alone_at_the_reference_gear_and_rewritten_otherwise()
    {
        var (trainer, model, proxy, app) = await StartAsync();
        await using var _1 = proxy; await using var _2 = app;

        var track = Track(3.0);
        await app.WriteAsync(FecCodec.Tx, track);
        await Until(() => trainer.FecWrites().Count == 1);
        Assert.Equal(track, trainer.FecWrites()[0]); // rapport de référence : rien à changer

        model.Gear = 20; // plus dur
        await app.WriteAsync(FecCodec.Tx, track);
        await Until(() => trainer.FecWrites().Count == 2);
        var sent = trainer.FecWrites()[1];
        Assert.True(FecCodec.TrySplit(sent, out _));
        Assert.True(FecCodec.TryParseTrack(sent, out short grade, out _));
        Assert.True(grade > 300, $"pente envoyée {grade}");
    }

    [Fact]
    public async Task Gear_change_reapplies_the_last_simulation_but_not_in_erg()
    {
        var (trainer, model, proxy, app) = await StartAsync();
        await using var _1 = proxy; await using var _2 = app;

        await app.WriteAsync(FecCodec.Tx, Track(1.0));
        await Until(() => trainer.FecWrites().Count == 1);

        model.Gear = 5; // plus facile
        await proxy.ReapplyAsync();
        await Until(() => trainer.FecWrites().Count == 2);
        Assert.True(FecCodec.TryParseTrack(trainer.FecWrites()[1], out short easier, out _));
        Assert.True(easier < 100, $"pente envoyée {easier}");

        await app.WriteAsync(FecCodec.Tx, Msg(0x31, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x20, 0x03)); // ERG 200 W
        await Until(() => trainer.FecWrites().Count == 3);
        model.Gear = 8;
        await proxy.ReapplyAsync();
        await Task.Delay(150);
        Assert.Equal(3, trainer.FecWrites().Count);
    }

    [Fact]
    public async Task Speed_sent_to_the_app_is_the_virtual_speed_and_measurements_are_read()
    {
        var (trainer, model, proxy, app) = await StartAsync();
        await using var _1 = proxy; await using var _2 = app;

        var received = new List<byte[]>();
        app.Notification += (ch, v) => { if (ch == FecCodec.Rx) lock (received) received.Add(v); };
        await app.EnableNotificationsAsync(FecCodec.Rx);

        model.Gear = 20;
        trainer.Push(FecCodec.Rx, Msg(0x19, 0x01, 85, 0x00, 0x00, 0xFA, 0x40, 0x20)); // 85 tr/min, 250 W
        trainer.Push(FecCodec.Rx, Msg(0x10, 0x19, 0x00, 0x00, 0x10, 0x27, 0xFF, 0x24)); // 10 m/s
        await Until(() => { lock (received) return received.Count == 2; });

        Assert.Equal(250, proxy.LastPowerW);
        Assert.Equal(85.0, proxy.LastCadenceRpm);
        Assert.Equal(10.0, proxy.LastSpeedMs, 3);
        Assert.True(FecCodec.TryGetSpeedMs(received[1], out var virtualSpeed));
        Assert.Equal(10.0 * model.Factor, virtualSpeed, 2);
    }

    [Fact]
    public void Report_names_the_control_protocol_and_the_services_not_relayed()
    {
        var tree = new Dictionary<Guid, List<(Guid Uuid, byte Props)>>
        {
            [FecCodec.Service] = [(FecCodec.Rx, DirConProps.Notify), (FecCodec.Tx, DirConProps.Write)],
        };
        Assert.Equal(TrainerControl.FeC, TrainerReport.DetectControl(tree));
        var text = TrainerReport.Build("Tacx Neo", "Bluetooth", tree, null, [FakeTacxLink.TacxVendor], TrainerControl.FeC);
        Assert.Contains("669aa501", text);
        Assert.Contains("FE-C", text);
        Assert.Equal(TrainerControl.None, TrainerReport.DetectControl(new Dictionary<Guid, List<(Guid Uuid, byte Props)>>()));
    }
}
