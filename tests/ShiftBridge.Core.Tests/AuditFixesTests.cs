using ShiftBridge.Core.Config;
using ShiftBridge.Core.Controllers;
using ShiftBridge.Core.Input;
using ShiftBridge.Core.Mapping;
using ShiftBridge.Core.Obc;
using ShiftBridge.Core.Trainer;

namespace ShiftBridge.Core.Tests;

/// <summary>Corrections de l'audit 0.25.2 : concurrence, cas limites, robustesse.</summary>
public class AuditFixesTests
{
    // ---------- Assignations en copie à l'écriture ----------

    [Fact]
    public void SetBinding_publishes_a_new_dictionary_and_leaves_readers_snapshot_untouched()
    {
        var p = new Profile();
        var before = p.Bindings;
        p.SetBinding(PhysicalButton.RightA, new ButtonBinding { Actions = [ObcButtonId.Select] });
        Assert.NotSame(before, p.Bindings);
        Assert.Empty(before);
        Assert.Single(p.Bindings);

        p.RemoveBinding(PhysicalButton.RightA);
        Assert.Empty(p.Bindings);
        var same = p.Bindings;
        p.RemoveBinding(PhysicalButton.RightA); // absente : pas de nouvelle copie
        Assert.Same(same, p.Bindings);
    }

    [Fact]
    public void ApplyControllerDefaults_copies_only_when_something_changes()
    {
        var p = new Profile();
        Assert.True(p.ApplyControllerDefaults(ControllerKind.ShimanoDi2));
        var after = p.Bindings;
        Assert.False(p.ApplyControllerDefaults(ControllerKind.ShimanoDi2));
        Assert.Same(after, p.Bindings);
    }

    [Fact]
    public async Task Concurrent_binding_changes_and_reads_never_throw()
    {
        var p = Profile.ZwiftLike();
        var keys = Enumerable.Range(0, 50).Select(i => ButtonKey.Controller(ControllerKind.CycplusBc2, $"K{i}")).ToArray();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var writers = Enumerable.Range(0, 3).Select(w => Task.Run(() =>
        {
            int i = 0;
            while (!cts.IsCancellationRequested)
            {
                var k = keys[(i++ * 7 + w) % keys.Length];
                if (i % 2 == 0) p.SetBinding(k, new ButtonBinding { Actions = [ObcButtonId.ShiftUp] });
                else p.RemoveBinding(k);
            }
        }));
        var readers = Enumerable.Range(0, 3).Select(r => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                foreach (var (k, b) in p.Bindings) _ = b.Actions.Count;
                _ = p.Clone();
            }
        }));
        await Task.WhenAll(writers.Concat(readers));
        // Toutes les écritures sont sérialisées : aucune assignation d'origine n'a été perdue.
        Assert.True(p.Bindings.ContainsKey(PhysicalButton.ClickPlus));
    }

    // ---------- Optimisations ----------

    [Fact]
    public void Button_tracker_reports_nothing_for_an_identical_frame_and_changes_otherwise()
    {
        var t = new ButtonStateTracker();
        var down = t.Update(new HashSet<PhysicalButton> { PhysicalButton.RightA });
        Assert.Equal([new ButtonEvent(PhysicalButton.RightA, true)], down);
        Assert.Empty(t.Update(new HashSet<PhysicalButton> { PhysicalButton.RightA }));
        Assert.Equal([new ButtonEvent(PhysicalButton.RightA, false)], t.Update(new HashSet<PhysicalButton>()));
        Assert.Empty(t.Reset());
    }

    [Fact]
    public void List_merge_updates_in_place_and_keeps_unchanged_rows()
    {
        var list = new System.Collections.ObjectModel.ObservableCollection<(int Id, string Text)> { (1, "a"), (2, "b"), (3, "c") };
        var kept = list[0];
        int resets = 0;
        list.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };

        ShiftBridge.Presentation.ListMerge.Apply<(int Id, string Text), int>(list, [(1, "a"), (3, "C"), (4, "d")], r => r.Id, (x, y) => x == y);

        Assert.Equal([(1, "a"), (3, "C"), (4, "d")], list);
        Assert.Equal(kept, list[0]);
        Assert.Equal(0, resets);
    }

    [Fact]
    public void List_merge_drops_a_duplicate_that_disappeared()
    {
        var list = new System.Collections.ObjectModel.ObservableCollection<string> { "pad", "pad", "kbd" };
        ShiftBridge.Presentation.ListMerge.Apply<string, string>(list, ["pad", "kbd"], r => r, (x, y) => x == y);
        Assert.Equal(["pad", "kbd"], list);
    }

    // ---------- Convertisseur d'énumérations ----------

    [Fact]
    public void Tolerant_enum_converter_maps_unknown_names_to_an_undefined_value()
    {
        Assert.False(Enum.IsDefined(TolerantEnumConverter<ObcButtonId>.Unknown));
        Assert.False(Enum.IsDefined(TolerantEnumConverter<ControllerKind>.Unknown));
    }

    // ---------- Réassemblage App Info ----------

    private static byte[] AppInfo(string id) => ObcMessages.EncodeAppInfo(new ObcAppInfo(id, "1.0", [0x01, 0x02]));

    [Fact]
    public void AppInfo_reassembler_joins_fragments_per_client()
    {
        var r = new ObcAppInfoReassembler();
        var msg = AppInfo("trainingpeaks");
        Assert.Empty(r.Feed("A", msg.AsSpan(0, 5), 0));
        Assert.Empty(r.Feed("B", AppInfo("other").AsSpan(0, 4), 10)); // un autre client n'interfère pas
        var done = Assert.Single(r.Feed("A", msg.AsSpan(5), 20));
        Assert.Equal("trainingpeaks", done.AppId);
    }

    [Fact]
    public void AppInfo_reassembler_drops_a_stale_partial_message()
    {
        var r = new ObcAppInfoReassembler();
        var first = AppInfo("interrupted");
        Assert.Empty(r.Feed("A", first.AsSpan(0, 6), 0));
        // Le client s'est coupé au milieu ; sa prochaine App Info arrive bien plus tard et doit être lue telle quelle.
        var info = Assert.Single(r.Feed("A", AppInfo("fresh"), ObcAppInfoReassembler.StaleAfterMs + 1));
        Assert.Equal("fresh", info.AppId);
    }

    // ---------- Proxy trainer ----------

    [Fact]
    public void Ftms_status_echo_restores_every_simulation_field_requested_by_the_app()
    {
        var app = new SimulationParams(-1.5, 350, 40, 51);
        byte[] rewritten = [FtmsCodec.StatusSimulationChanged, 0x10, 0x00, 0x20, 0x03, 0x5A, 0xFF];
        var back = FtmsCodec.RewriteStatusSimulation(rewritten, app);
        Assert.Equal([0x12, 0x24, 0xFA, 0x5E, 0x01, 40, 51], back); // vent −1500 = 0xFA24, pente 350 = 0x015E
    }

    [Fact]
    public void Fec_user_configuration_page_gives_rider_and_bike_weight()
    {
        // Page 0x37 : cycliste 72,50 kg (7250 = 0x1C52), vélo 8,5 kg (170 × 0,05 = 0x0AA sur 12 bits)
        byte[] m = [FecCodec.Sync, 0x09, 0x4F, 0x05, 0x37, 0x52, 0x1C, 0xFF, 0xA0, 0x0A, 0x46, 0x00, 0x00];
        FecCodec.Seal(m);
        Assert.True(FecCodec.TryParseUserConfig(m, out var rider, out var bike));
        Assert.Equal(72.5, rider!.Value, 3);
        Assert.Equal(8.5, bike!.Value, 3);

        // Poids « invalides » de la norme
        byte[] invalid = [FecCodec.Sync, 0x09, 0x4F, 0x05, 0x37, 0xFF, 0xFF, 0xFF, 0xF0, 0xFF, 0x46, 0x00, 0x00];
        FecCodec.Seal(invalid);
        Assert.True(FecCodec.TryParseUserConfig(invalid, out rider, out bike));
        Assert.Null(rider);
        Assert.Null(bike);
    }

    [Fact]
    public void Advertised_service_list_drops_services_the_proxy_does_not_relay()
    {
        var ftms = FtmsCodec.FitnessMachineService;
        var cps = FtmsCodec.BaseUuid(0x1818);
        var zwift = Guid.Parse("00000001-19ca-4651-86e5-fa29dcdd09d1");
        string fromTrainer = $"0x1818,0x1826,{zwift}";
        var list = ShiftBridge.Platform.Trainer.DirConTrainerProxy.AdvertisedServiceList(fromTrainer, [ftms, cps]);
        Assert.Equal("0x1818,0x1826", list);

        // Cardio republié : ajouté s'il manque
        var withHr = ShiftBridge.Platform.Trainer.DirConTrainerProxy.AdvertisedServiceList(fromTrainer, [ftms, cps, HeartRateCodec.Service]);
        Assert.Equal("0x1818,0x1826,0x180D", withHr);

        // Sans modèle : construit d'après l'arbre
        var built = ShiftBridge.Platform.Trainer.DirConTrainerProxy.AdvertisedServiceList(null, [ftms]);
        Assert.Equal("0x1826", built);
    }

    [Theory]
    [InlineData("0x1826", "00001826-0000-1000-8000-00805f9b34fb")]
    [InlineData("180d", "0000180d-0000-1000-8000-00805f9b34fb")]
    [InlineData("6e40fec1-b5a3-f393-e0a9-e50e24dcca9e", "6e40fec1-b5a3-f393-e0a9-e50e24dcca9e")]
    public void Uuid_parsing_accepts_short_and_full_forms(string text, string expected) =>
        Assert.Equal(Guid.Parse(expected), TrainerReport.TryParseUuid(text));

    // ---------- Arbitre du Control Point ----------

    private static readonly byte[] SimCp = [0x11, 0, 0, 0x2C, 0x01, 40, 51];

    [Fact]
    public async Task Arbiter_answers_operation_failed_to_the_app_when_the_trainer_stays_silent_and_swallows_the_late_reply()
    {
        var sent = new List<byte[]>();
        using var arbiter = new ControlPointArbiter((cp, _) => { lock (sent) sent.Add(cp); return Task.CompletedTask; },
            responseTimeout: TimeSpan.FromMilliseconds(100));
        var forApp = new List<byte[]>();
        int foreign = 0;
        arbiter.ResponseForApp += r => { lock (forApp) forApp.Add(r); };
        arbiter.ForeignResponse += _ => Interlocked.Increment(ref foreign);

        Assert.True(await arbiter.SubmitFromAppAsync(SimCp));
        await WaitUntilAsync(() => { lock (forApp) return forApp.Count == 1; });
        Assert.Equal([FtmsCodec.OpResponse, (byte)0x11, ControlPointArbiter.ResultOperationFailed], forApp[0]);

        // La vraie réponse arrive après coup : elle n'est pas prise pour celle d'un autre client.
        arbiter.OnResponse([FtmsCodec.OpResponse, 0x11, 0x01]);
        Assert.Equal(0, foreign);
        // Une seconde réponse, elle, est bien étrangère.
        arbiter.OnResponse([FtmsCodec.OpResponse, 0x11, 0x01]);
        Assert.Equal(1, foreign);
    }

    [Fact]
    public async Task Arbiter_applies_the_rewrite_when_the_command_is_sent_not_when_it_is_queued()
    {
        var sent = new List<byte[]>();
        var gate = new TaskCompletionSource();
        using var arbiter = new ControlPointArbiter(async (cp, _) => { lock (sent) sent.Add(cp); if (cp[0] == FtmsCodec.OpRequestControl) await gate.Task; },
            responseTimeout: TimeSpan.FromSeconds(5));
        _ = arbiter.SubmitFromAppAsync([FtmsCodec.OpRequestControl]); // bloque l'envoi
        await WaitUntilAsync(() => { lock (sent) return sent.Count == 1; });

        short grade = 100;
        var pending = arbiter.SubmitFromAppAsync(SimCp, cp => FtmsCodec.WithGrade(cp, grade));
        grade = 900; // changement de rapport pendant l'attente
        gate.SetResult();
        arbiter.OnResponse([FtmsCodec.OpResponse, FtmsCodec.OpRequestControl, 0x01]);
        Assert.True(await pending);
        Assert.True(FtmsCodec.TryParseSimulation(sent[1], out var p));
        Assert.Equal(900, p.GradeRaw);
    }

    [Fact]
    public async Task Arbiter_dispose_releases_app_commands_still_waiting()
    {
        var never = new TaskCompletionSource();
        var arbiter = new ControlPointArbiter((_, _) => never.Task, responseTimeout: TimeSpan.FromSeconds(5));
        _ = arbiter.SubmitFromAppAsync([FtmsCodec.OpRequestControl]); // en vol, jamais acquitté
        var queued = arbiter.SubmitFromAppAsync(SimCp);
        await Task.Delay(50);
        arbiter.Dispose();
        Assert.False(await queued.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await arbiter.SubmitFromAppAsync(SimCp));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    // ---------- Scan Bluetooth ----------

    [Fact]
    public async Task Hub_restarts_a_scan_stopped_by_the_platform_while_someone_listens()
    {
        var scanner = new FakeBleScanner();
        using var hub = new ShiftBridge.Platform.Ble.BleAdvertisementHub(scanner) { RestartDelay = TimeSpan.FromMilliseconds(20) };
        using var sub = hub.Subscribe(_ => { });
        Assert.Equal(1, scanner.StartCount);

        scanner.StopByItself("Bluetooth coupé");
        await WaitUntilAsync(() => scanner.StartCount == 2);
        Assert.True(scanner.IsRunning);
    }

    [Fact]
    public async Task Hub_does_not_restart_once_nobody_listens()
    {
        var scanner = new FakeBleScanner();
        using var hub = new ShiftBridge.Platform.Ble.BleAdvertisementHub(scanner) { RestartDelay = TimeSpan.FromMilliseconds(20) };
        hub.Subscribe(_ => { }).Dispose();
        scanner.StopByItself("arrêt");
        await Task.Delay(150);
        Assert.Equal(1, scanner.StartCount);
    }

    // ---------- Flux DirCon ----------

    [Fact]
    public void DirCon_parser_reassembles_fragmented_and_concatenated_packets()
    {
        var a = new ShiftBridge.Core.DirCon.DirConPacket(0x03, 7, 0, [1, 2, 3]).Encode();
        var b = new ShiftBridge.Core.DirCon.DirConPacket(0x06, 0, 0, new byte[300]).Encode();
        byte[] stream = [.. a, .. b];
        var parser = new ShiftBridge.Core.DirCon.DirConStreamParser();
        var got = new List<ShiftBridge.Core.DirCon.DirConPacket>();
        foreach (var chunk in stream.Chunk(5)) got.AddRange(parser.Feed(chunk));
        Assert.Equal(2, got.Count);
        Assert.Equal([1, 2, 3], got[0].Payload);
        Assert.Equal(7, got[0].Sequence);
        Assert.Equal(300, got[1].Payload.Length);
    }

    [Fact]
    public void DirCon_parser_resynchronises_after_garbage_and_impossible_lengths()
    {
        var good = new ShiftBridge.Core.DirCon.DirConPacket(0x01, 3, 0, [9]).Encode();
        byte[] corrupt = [0x01, 0x04, 0x00, 0x00, 0xFF, 0xFF]; // longueur 65535 : jamais attendue
        byte[] stream = [0x55, 0xAA, .. corrupt, .. good];
        var packets = new ShiftBridge.Core.DirCon.DirConStreamParser().Feed(stream).ToList();
        var p = Assert.Single(packets);
        Assert.Equal([9], p.Payload);
    }

    // ---------- Mesure du braquet réel ----------

    [Fact]
    public async Task RealRatioDetector_tolerates_concurrent_samples_and_reads()
    {
        var d = new RealRatioDetector();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var feeder = Task.Run(() =>
        {
            var rnd = new Random(1);
            while (!cts.IsCancellationRequested) d.AddSample(8 + rnd.NextDouble(), 85 + rnd.NextDouble() * 10);
        });
        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested) { d.TryGetResult(out _, out _, out _); _ = d.CurrentSpread; }
        });
        await Task.WhenAll(feeder, reader);
        Assert.True(d.TotalSamples > 0);
    }
}
