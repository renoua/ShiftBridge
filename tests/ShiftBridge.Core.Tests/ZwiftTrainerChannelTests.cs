using ShiftBridge.Core.DirCon;
using ShiftBridge.Core.Trainer;
using ShiftBridge.Core.Zwift;
using ShiftBridge.Platform.Trainer;

namespace ShiftBridge.Core.Tests;

/// <summary>Trainer en mémoire exposant (ou non) le service Zwift ; répond « RideOn » au handshake si demandé.</summary>
internal sealed class FakeZwiftLink : ITrainerLink
{
    public bool HasService = true;
    public bool UseFc82;
    public bool OmitRxCharacteristic;
    public bool ReplyToRideOn = true;
    public int FailWritesAfter = int.MaxValue;     // à partir de la n-ième écriture, toutes échouent
    public readonly List<byte[]> Writes = [];
    public readonly List<Guid> Subscribed = [];
    public Guid? QueriedService;

    private int _writeCount;
    public bool IsConnected => true;
    public event Action<Guid, byte[]>? Notification;
#pragma warning disable CS0067
    public event Action? Disconnected;
    public event Action<string>? Log;
#pragma warning restore CS0067

    private Guid Service => UseFc82 ? ZwiftConstants.RideCustomService : ZwiftConstants.CustomService;

    public Task<List<Guid>> DiscoverServicesAsync() =>
        Task.FromResult(HasService ? new List<Guid> { FtmsCodec.FitnessMachineService, Service } : [FtmsCodec.FitnessMachineService]);

    public Task<List<(Guid Uuid, byte Props)>> DiscoverCharacteristicsAsync(Guid service)
    {
        QueriedService = service;
        var list = new List<(Guid, byte)>
        {
            (ZwiftConstants.AsyncCharacteristic, DirConProps.Notify),
            (ZwiftConstants.SyncTxCharacteristic, DirConProps.Notify | DirConProps.Indicate),
        };
        if (!OmitRxCharacteristic) list.Add((ZwiftConstants.SyncRxCharacteristic, DirConProps.Write));
        return Task.FromResult(list);
    }

    public Task<byte[]> ReadAsync(Guid ch) => Task.FromResult(Array.Empty<byte>());

    public int WriteDelayMs;                       // écriture lente (liaison Bluetooth chargée) : les rafales s'accumulent

    public async Task WriteAsync(Guid ch, byte[] value)
    {
        if (++_writeCount > FailWritesAfter) throw new IOException("écriture refusée");
        lock (Writes) Writes.Add(value);
        if (ch == ZwiftConstants.SyncRxCharacteristic && ReplyToRideOn && value.AsSpan().SequenceEqual(ZwiftTrainerCodec.RideOnTrainer))
            _ = Task.Run(() => Push(ZwiftConstants.SyncTxCharacteristic, [.. ZwiftConstants.RideOn, 0x01, 0x03]));
        if (WriteDelayMs > 0) await Task.Delay(WriteDelayMs);
    }

    public Task EnableNotificationsAsync(Guid ch, bool enable = true) { if (enable) Subscribed.Add(ch); return Task.CompletedTask; }

    public void Push(Guid ch, byte[] v) => Notification?.Invoke(ch, v);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public int WriteCount { get { lock (Writes) return Writes.Count; } }
    public List<string> WritesHex() { lock (Writes) return Writes.Select(Convert.ToHexString).ToList(); }
}

public class ZwiftTrainerChannelTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(5);

    private static Task<ZwiftTrainerChannel?> Open(FakeZwiftLink link, SimulationParams? sim = null, int attempts = 3,
        TimeSpan? handshake = null, TimeSpan? keepAlive = null, List<string>? logs = null) =>
        ZwiftTrainerChannel.TryOpenAsync(link, 2.4, 80, 8, sim, m => { if (logs is not null) lock (logs) logs.Add(m); },
            initGap: Fast, keepAlive: keepAlive ?? TimeSpan.FromMinutes(5), handshakeTimeout: handshake ?? TimeSpan.FromMilliseconds(300), attempts: attempts);

    private static async Task Until(Func<bool> cond, int ms = 3000)
    {
        var end = Environment.TickCount64 + ms;
        while (!cond()) { if (Environment.TickCount64 > end) throw new TimeoutException(); await Task.Delay(10); }
    }

    [Fact]
    public async Task Successful_handshake_plays_the_init_sequence_in_order()
    {
        var link = new FakeZwiftLink();
        await using var channel = await Open(link, new SimulationParams(0, 400, 40, 51));
        Assert.NotNull(channel);
        Assert.True(channel!.IsActive);

        // RideOn → 41 08 05 → Physical(rapport + poids) → infos → simulation complète → Physical(rapport) → requête du rapport
        Assert.Equal(
        [
            Convert.ToHexString(ZwiftTrainerCodec.RideOnTrainer),
            "410805",
            Convert.ToHexString(ZwiftTrainerCodec.EncodePhysical(2.4, 8, 80)),
            "000800",
            Convert.ToHexString(ZwiftTrainerCodec.EncodeSimulation(new SimulationParams(0, 400, 40, 51), full: true)),
            Convert.ToHexString(ZwiftTrainerCodec.EncodePhysical(2.4)),
            "00088804",
        ], link.WritesHex());
        Assert.Contains(ZwiftConstants.AsyncCharacteristic, link.Subscribed);
        Assert.Contains(ZwiftConstants.SyncTxCharacteristic, link.Subscribed);
    }

    [Fact]
    public async Task Without_a_current_simulation_the_init_uses_a_neutral_one()
    {
        var link = new FakeZwiftLink();
        await using var channel = await Open(link);
        Assert.NotNull(channel);
        Assert.Contains(Convert.ToHexString(ZwiftTrainerCodec.EncodeSimulation(new SimulationParams(0, 0, 40, 51), full: true)), link.WritesHex());
    }

    [Fact]
    public async Task Silent_trainer_returns_null_after_the_retries()
    {
        var link = new FakeZwiftLink { ReplyToRideOn = false };
        var logs = new List<string>();
        var channel = await Open(link, attempts: 2, handshake: TimeSpan.FromMilliseconds(80), logs: logs);

        Assert.Null(channel);
        Assert.Equal(2, link.WritesHex().Count(h => h == Convert.ToHexString(ZwiftTrainerCodec.RideOnTrainer)));
        Assert.Equal(2, link.WriteCount);                                    // aucune commande de résistance n'est partie
        lock (logs) Assert.Contains(logs, l => l.Contains("pas de réponse"));
    }

    [Fact]
    public async Task Trainer_without_the_zwift_service_returns_null_without_writing()
    {
        var link = new FakeZwiftLink { HasService = false };
        var logs = new List<string>();
        Assert.Null(await Open(link, logs: logs));
        Assert.Equal(0, link.WriteCount);
        lock (logs) Assert.Contains(logs, l => l.Contains("service Zwift"));
    }

    [Fact]
    public async Task Incomplete_zwift_service_returns_null()
    {
        var link = new FakeZwiftLink { OmitRxCharacteristic = true };
        Assert.Null(await Open(link));
        Assert.Equal(0, link.WriteCount);
    }

    [Fact]
    public async Task Newer_fc82_service_is_used_when_present()
    {
        var link = new FakeZwiftLink { UseFc82 = true };
        await using var channel = await Open(link);
        Assert.NotNull(channel);
        Assert.Equal(ZwiftConstants.RideCustomService, link.QueriedService);
    }

    [Fact]
    public async Task New_weights_are_sent_at_once_with_the_current_gear_ratio()
    {
        var link = new FakeZwiftLink();
        await using var channel = (await Open(link))!;
        Assert.True(await channel.SendGearAsync(3.24));   // le rapport courant n'est plus celui d'ouverture (2,40)
        int before = link.WriteCount;

        Assert.True(await channel.SendWeightsAsync(riderKg: 92.5, bikeKg: 10));

        var writes = link.WritesHex();
        Assert.Equal(before + 1, writes.Count);
        Assert.Equal(Convert.ToHexString(ZwiftTrainerCodec.EncodePhysical(3.24, 10, 92.5)), writes[before]);
    }

    [Fact]
    public async Task Weights_are_not_sent_once_the_channel_is_no_longer_active()
    {
        var link = new FakeZwiftLink();
        await using var channel = (await Open(link))!;
        await channel.DisposeAsync();
        int before = link.WriteCount;

        Assert.False(await channel.SendWeightsAsync(80, 9));
        Assert.Equal(before, link.WriteCount);
    }

    [Fact]
    public async Task Gear_change_writes_the_physical_frame_then_asks_for_the_trainers_gear()
    {
        var link = new FakeZwiftLink();
        await using var channel = (await Open(link))!;
        int before = link.WriteCount;

        Assert.True(await channel.SendGearAsync(5.49));
        var writes = link.WritesHex();
        Assert.Equal(Convert.ToHexString(ZwiftTrainerCodec.EncodePhysical(5.49)), writes[before]);
        Assert.Equal("00088804", writes[before + 1]);
    }

    [Fact]
    public async Task Simulation_is_sent_complete_only_when_coefficients_change()
    {
        var link = new FakeZwiftLink();
        await using var channel = (await Open(link, new SimulationParams(0, 0, 40, 51)))!;
        int before = link.WriteCount;

        await channel.SendSimulationAsync(new SimulationParams(0, 350, 40, 51));          // mêmes coefficients : vent + pente seulement
        await channel.SendSimulationAsync(new SimulationParams(0, 350, 40, 60));          // traînée modifiée : trame complète
        var writes = link.WritesHex();
        Assert.Equal(Convert.ToHexString(ZwiftTrainerCodec.EncodeSimulation(new SimulationParams(0, 350, 40, 51), full: false)), writes[before]);
        Assert.Equal(Convert.ToHexString(ZwiftTrainerCodec.EncodeSimulation(new SimulationParams(0, 350, 40, 60), full: true)), writes[before + 1]);
    }

    [Fact]
    public async Task Keep_alive_repeats_the_simulation_only_while_a_simulation_is_the_target()
    {
        var link = new FakeZwiftLink();
        await using var channel = (await Open(link, keepAlive: TimeSpan.FromMilliseconds(60)))!;
        await channel.SendSimulationAsync(new SimulationParams(0, 200, 40, 51));
        string expected = Convert.ToHexString(ZwiftTrainerCodec.EncodeSimulation(new SimulationParams(0, 200, 40, 51), full: false));

        await Until(() => link.WritesHex().Count(h => h == expected) >= 3);          // la trame est renvoyée en boucle

        channel.SetSimulationActive(false);                                           // l'appli passe en ERG
        await Task.Delay(120);
        int frozen = link.WriteCount;
        await Task.Delay(250);
        Assert.Equal(frozen, link.WriteCount);                                        // plus rien n'est renvoyé
    }

    [Fact]
    public async Task Write_failure_raises_failed_once_and_disables_the_channel()
    {
        var link = new FakeZwiftLink { FailWritesAfter = 7 };                          // le handshake + l'init passent, puis les écritures échouent
        await using var channel = (await Open(link))!;
        int failed = 0;
        channel.Failed += () => failed++;

        Assert.False(await channel.SendGearAsync(3.0));
        Assert.False(await channel.SendSimulationAsync(new SimulationParams(0, 100, 40, 51)));
        Assert.Equal(1, failed);
        Assert.False(channel.IsActive);
    }

    [Fact]
    public async Task Release_sends_gear_ratio_zero()
    {
        var link = new FakeZwiftLink();
        await using var channel = (await Open(link))!;
        await channel.ReleaseAsync();
        Assert.Equal("042A021000", link.WritesHex()[^1]);
    }

    [Fact]
    public async Task Trainer_replies_and_riding_data_are_logged_and_decoded()
    {
        var link = new FakeZwiftLink();
        var logs = new List<string>();
        await using var channel = (await Open(link, logs: logs))!;
        ZwiftRidingData? ride = null;
        channel.RidingData += r => ride = r;

        link.Push(ZwiftConstants.SyncTxCharacteristic, [0x3C, 0x08, 0x01]);
        link.Push(ZwiftConstants.AsyncCharacteristic, Convert.FromHexString("0308BE01105018BD062000"));
        lock (logs) Assert.Contains(logs, l => l.Contains("3C0801"));
        Assert.Equal(190u, ride!.Value.Power);
        Assert.Equal(80u, ride.Value.Cadence);
    }

    [Fact]
    public async Task The_trainers_gear_confirmation_is_decoded_and_a_mismatch_is_flagged()
    {
        var link = new FakeZwiftLink();
        var logs = new List<string>();
        await using var channel = (await Open(link, logs: logs))!;        // rapport demandé : 2,40
        var confirmed = new List<double>();
        channel.GearConfirmed += confirmed.Add;

        link.Push(ZwiftConstants.SyncTxCharacteristic, Convert.FromHexString("3C08880412060A0440C0BB01"));   // réponse réelle : 2,40
        Assert.Equal(2.40, confirmed[0], 6);
        lock (logs) Assert.Contains(logs, l => l.Contains("confirme"));

        link.Push(ZwiftConstants.SyncTxCharacteristic, Convert.FromHexString("3C08880412060A0440C8DF02"));   // 45 000 = C8 DF 02 : 4,50, autre rapport retenu
        Assert.Equal(4.50, confirmed[1], 6);
        lock (logs) Assert.Contains(logs, l => l.Contains("retenu"));
    }

    private static byte[] GearReply(double ratio)
    {
        var w = new ShiftBridge.Core.Protobuf.ProtoWriter()
            .Varint(1, 520)
            .Message(2, new ShiftBridge.Core.Protobuf.ProtoWriter()
                .Message(1, new ShiftBridge.Core.Protobuf.ProtoWriter().Varint(8, (ulong)Math.Round(ratio * 10000))));
        return [0x3C, .. w.ToArray()];
    }

    [Fact]
    public async Task A_burst_of_gear_changes_is_coalesced_in_order_and_ends_on_the_last_ratio()
    {
        var link = new FakeZwiftLink();
        await using var channel = (await Open(link))!;
        int before = link.WriteCount;
        link.WriteDelayMs = 40;                                                    // liaison lente : les demandes s'empilent

        double[] ratios = [2.61, 2.82, 3.03, 3.24, 3.49, 3.76, 4.04, 4.35];
        var results = ratios.Select(r => channel.SendGearAsync(r)).ToArray();     // rafale quasi instantanée
        Assert.All(await Task.WhenAll(results), Assert.True);

        var writes = link.WritesHex().Skip(before).ToList();
        var physical = writes.Where(h => h.StartsWith("042A")).ToList();
        Assert.True(physical.Count < ratios.Length, $"rafale non regroupée : {physical.Count} écritures pour {ratios.Length} demandes");
        Assert.Equal(Convert.ToHexString(ZwiftTrainerCodec.EncodePhysical(4.35)), physical[^1]);       // la dernière valeur gagne
        // Ordre croissant conservé (aucun rapport ancien ne repart après un plus récent).
        var sent = physical.Select(h => ratios.First(r => Convert.ToHexString(ZwiftTrainerCodec.EncodePhysical(r)) == h)).ToList();
        Assert.Equal(sent.OrderBy(x => x), sent);
        // Une seule requête de rapport, après la valeur finale.
        Assert.Equal("00088804", writes[^1]);
        Assert.Equal(1, writes.Count(h => h == "00088804"));
    }

    [Fact]
    public async Task Late_replies_are_matched_to_their_own_request_without_false_warnings()
    {
        var link = new FakeZwiftLink();
        var logs = new List<string>();
        await using var channel = (await Open(link, logs: logs))!;
        var latency = new LatencyTracker();
        channel.Latency = latency;
        link.Push(ZwiftConstants.SyncTxCharacteristic, GearReply(2.4));            // réponse de la requête d'initialisation

        await channel.SendGearAsync(2.61);
        await channel.SendGearAsync(2.82);
        await channel.SendGearAsync(3.03);                                         // trois requêtes, 3 réponses qui arrivent après coup
        link.Push(ZwiftConstants.SyncTxCharacteristic, GearReply(2.61));
        link.Push(ZwiftConstants.SyncTxCharacteristic, GearReply(2.82));
        link.Push(ZwiftConstants.SyncTxCharacteristic, GearReply(3.03));

        lock (logs) Assert.DoesNotContain(logs, l => l.Contains("retenu"));
        lock (logs) Assert.Equal(4, logs.Count(l => l.Contains("confirme")));
        Assert.Contains("natif", latency.TakeSummary());
    }

    [Fact]
    public async Task A_duplicate_confirmation_does_not_consume_the_next_request()
    {
        var link = new FakeZwiftLink();
        var logs = new List<string>();
        await using var channel = (await Open(link, logs: logs))!;
        link.Push(ZwiftConstants.SyncTxCharacteristic, GearReply(2.4));

        await channel.SendGearAsync(3.74);
        await channel.SendGearAsync(3.99);
        link.Push(ZwiftConstants.SyncTxCharacteristic, GearReply(3.74));
        link.Push(ZwiftConstants.SyncTxCharacteristic, GearReply(3.74));          // le trainer répète sa réponse
        link.Push(ZwiftConstants.SyncTxCharacteristic, GearReply(3.99));

        lock (logs) Assert.DoesNotContain(logs, l => l.Contains("retenu"));
        lock (logs) Assert.Equal(3, logs.Count(l => l.Contains("confirme")));    // 2,40 + 3,74 + 3,99, sans le doublon
    }

    [Fact]
    public async Task A_real_mismatch_is_still_flagged()
    {
        var link = new FakeZwiftLink();
        var logs = new List<string>();
        await using var channel = (await Open(link, logs: logs))!;
        link.Push(ZwiftConstants.SyncTxCharacteristic, GearReply(2.4));
        await channel.SendGearAsync(5.49);
        link.Push(ZwiftConstants.SyncTxCharacteristic, GearReply(4.50));          // le trainer borne le rapport

        lock (logs) Assert.Contains(logs, l => l.Contains("a retenu le rapport 4,50") || l.Contains("a retenu le rapport 4.50"));
    }

    [Fact]
    public async Task Reopen_replays_the_handshake_and_init_after_a_reconnection()
    {
        var link = new FakeZwiftLink();
        await using var channel = (await Open(link))!;
        int first = link.WriteCount;

        Assert.True(await channel.ReopenAsync());
        Assert.True(channel.IsActive);
        Assert.Equal(first * 2, link.WriteCount);                                      // même séquence rejouée
    }
}
