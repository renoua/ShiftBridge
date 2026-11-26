using ShiftBridge.Core.Controllers;
using ShiftBridge.Core.Controllers.Decoders;
using ShiftBridge.Core.Input;
using ShiftBridge.Platform.Ble;
using ShiftBridge.Platform.Controllers;

namespace ShiftBridge.Core.Tests;

public class GroupsetDecoderTests
{
    private static byte[] Hex(string h) => Convert.FromHexString(h);

    // ---------- Shimano Di2 ----------

    [Fact]
    public void Di2_first_frame_is_a_baseline_then_short_press_is_a_tap()
    {
        var d = new ShimanoDi2Decoder();
        var g = ShimanoDi2Decoder.Channels;
        var first = d.Decode(g, Hex("21F0F0F0F0"))!;
        Assert.Empty(first.Taps);

        Assert.Equal(["Ch1"], d.Decode(g, Hex("2211F0F0F0"))!.Taps);   // appui court voie 1
        Assert.Empty(d.Decode(g, Hex("23F0F0F0F0"))!.Taps);            // retour au repos : pas d'appui
        Assert.Equal(["Ch3"], d.Decode(g, Hex("24F0F012F0"))!.Taps);   // voie 3
    }

    [Fact]
    public void Di2_first_frame_carrying_a_press_is_processed()
    {
        var d = new ShimanoDi2Decoder();
        Assert.Equal(["Ch2"], d.Decode(ShimanoDi2Decoder.Channels, Hex("20F011F0F0"))!.Taps);
    }

    [Fact]
    public void Di2_long_press_is_held_and_its_follow_up_is_not_a_new_press()
    {
        var d = new ShimanoDi2Decoder();
        var g = ShimanoDi2Decoder.Channels;
        d.Decode(g, Hex("21F0F0F0F0"));
        var held = d.Decode(g, Hex("22F022F0F0"))!;   // voie 2 : appui long
        Assert.Equal(["Ch2"], held.Held!);
        Assert.Empty(held.Taps);

        var still = d.Decode(g, Hex("23F012F0F0"))!;  // 0x1N après un appui long : toujours tenu
        Assert.Equal(["Ch2"], still.Held!);
        Assert.Empty(still.Taps);

        var released = d.Decode(g, Hex("24F0F0F0F0"))!;
        Assert.Empty(released.Held!);
    }

    [Fact]
    public void Di2_double_press_gives_two_taps()
    {
        var d = new ShimanoDi2Decoder();
        d.Decode(ShimanoDi2Decoder.Channels, Hex("21F0F0F0F0"));
        Assert.Equal(["Ch1", "Ch1"], d.Decode(ShimanoDi2Decoder.Channels, Hex("2241F0F0F0"))!.Taps);
    }

    // ---------- SRAM AXS ----------

    [Fact]
    public async Task Axs_single_and_double_taps()
    {
        using var d = new SramAxsDecoder(doubleWindowMs: 80);
        var got = new List<DecodedFrame>();
        d.Deferred += f => { lock (got) got.Add(f); };

        Assert.Same(DecodedFrame.Nothing, d.Decode(SramAxsDecoder.Trigger, [0xFF]));
        await Task.Delay(400);
        lock (got) Assert.Equal(["Single"], Assert.Single(got).Taps);

        d.Decode(SramAxsDecoder.Trigger, [0xFF]);
        d.Decode(SramAxsDecoder.Trigger, [0xFF]);
        await Task.Delay(300);
        lock (got) { Assert.Equal(2, got.Count); Assert.Equal(["Double"], got[1].Taps); } // pas de « Single » en plus
    }

    [Fact]
    public async Task Axs_taps_carry_the_raw_bytes_and_the_gap_for_the_log()
    {
        using var d = new SramAxsDecoder(doubleWindowMs: 300);
        var got = new List<DecodedFrame>();
        d.Deferred += f => { lock (got) got.Add(f); };

        d.Decode(SramAxsDecoder.Trigger, [0xFF]);
        await Task.Delay(500);
        lock (got) Assert.Equal("FF", Assert.Single(got).Detail);

        d.Decode(SramAxsDecoder.Trigger, [0xFF]);
        await Task.Delay(50);
        d.Decode(SramAxsDecoder.Trigger, [0x01, 0x02]);
        lock (got)
        {
            Assert.Equal(2, got.Count);
            Assert.Equal(["Double"], got[1].Taps); // les noms de touches ne changent pas : les assignations restent valables
            Assert.Matches(@"^FF \+ 0102, écart \d+ ms$", got[1].Detail);
        }
    }

    [Fact]
    public async Task Axs_ignores_a_single_zero_byte_and_other_characteristics()
    {
        using var d = new SramAxsDecoder(doubleWindowMs: 40);
        int fired = 0;
        d.Deferred += _ => Interlocked.Increment(ref fired);
        d.Decode(SramAxsDecoder.Trigger, [0x00]);
        Assert.Null(d.Decode(SramAxsDecoder.LiveState, [0xFF]));
        await Task.Delay(200);
        Assert.Equal(0, fired);
    }

    // ---------- Wahoo KICKR Bike ----------

    [Fact]
    public void Kickr_press_and_release_by_the_high_bit()
    {
        var d = new WahooKickrDecoder();
        var n = WahooKickrDecoder.Notify;
        Assert.Equal(["RightUp"], d.Decode(n, Hex("0001E6"))!.Held!);
        Assert.Equal(["LeftBrake", "RightUp"], d.Decode(n, Hex("01008F"))!.Held!.OrderBy(x => x));
        Assert.Equal(["LeftBrake"], d.Decode(n, Hex("000166"))!.Held!);          // relâchement de RightUp (bit 7 effacé)
        Assert.Empty(d.Decode(n, Hex("010004"))!.Held!);                          // relâchement de LeftBrake
        Assert.Null(d.Decode(n, Hex("123456")));                                   // touche inconnue
        Assert.Null(d.Decode(n, Hex("0001")));                                     // autre longueur
    }

    // ---------- Elite Square ----------

    [Fact]
    public void Square_reads_a_32_bit_mask_from_bytes_3_to_6()
    {
        var d = new EliteSquareDecoder();
        Assert.Equal(["Up"], d.Decode(EliteSquareDecoder.Notify43, Hex("00000000000200"))!.Held!);
        Assert.Equal(["LeftShift1", "Up"], d.Decode(EliteSquareDecoder.Notify43, Hex("00000000000202"))!.Held!.OrderBy(x => x));
        Assert.Equal(["RightShift2"], d.Decode(EliteSquareDecoder.Notify43, Hex("00000000010000"))!.Held!);
        Assert.Equal(["CampyRight"], d.Decode(EliteSquareDecoder.Notify43, Hex("00000080000000"))!.Held!);
        Assert.Empty(d.Decode(EliteSquareDecoder.Notify43, Hex("00000000000000"))!.Held!);
        Assert.Null(d.Decode(EliteSquareDecoder.Notify43, Hex("000000")));
    }

    // ---------- Stages SB20 ----------

    [Fact]
    public void Sb20_held_masks_and_releases()
    {
        var d = new StagesSb20Decoder();
        var n = StagesSb20Decoder.Notify;
        Assert.Equal(["LeftUp"], d.Decode(n, Hex("01000100"))!.Held!);
        Assert.Equal(["LeftUp", "RightUp"], d.Decode(n, Hex("01000900"))!.Held!.OrderBy(x => x));
        Assert.Null(d.Decode(n, Hex("03000100"))!.Held);   // validation en cours d'appui : ignorée
        Assert.Empty(d.Decode(n, Hex("04000000"))!.Held!);
        Assert.Empty(d.Decode(n, Hex("08000000"))!.Held!);
        Assert.Null(d.Decode(n, Hex("0100")));
    }

    // ---------- CYCPLUS T7H ----------

    [Fact]
    public void T7h_checksum_side_and_bits()
    {
        var d = new CycplusT7hDecoder();
        var n = BleUuids.NordicUartTx;
        Assert.Equal(["L.ShiftUp"], d.Decode(n, Hex("AA0001AB"))!.Held!);
        Assert.Equal(["L.ShiftUp", "R.ShiftDown"], d.Decode(n, Hex("AA00822C"))!.Held!.OrderBy(x => x)); // l'autre levier garde son état
        Assert.Equal(["R.ShiftDown"], d.Decode(n, Hex("AA0000AA"))!.Held!);                               // gauche relâché
        Assert.Null(d.Decode(n, Hex("AA0001AC")));                                                          // somme fausse
        Assert.Null(d.Decode(n, Hex("AB0001AC")));                                                          // en-tête
    }

    // ---------- Wheeltop EDS ----------

    [Fact]
    public void Wheeltop_ox_and_tx_frames()
    {
        var d = new WheeltopEdsDecoder();
        var n = BleUuids.NordicUartTx;
        Assert.Equal(["Up"], d.Decode(n, Hex("040206"))!.Held!);                        // OX : xor = 04 ^ 02
        Assert.Equal(["L.Down", "Up"], d.Decode(n, Hex("0436013B"))!.Held!.OrderBy(x => x)); // TX gauche : 04+36+01 = 3B
        Assert.Equal(["Up"], d.Decode(n, Hex("04360943"))!.Held!);                      // relâchement (somme 04+36+09 = 43)
        Assert.Empty(d.Decode(n, Hex("040A0E"))!.Held!);
        Assert.Null(d.Decode(n, Hex("040207")));                                         // xor faux
    }

    [Fact]
    public void Wheeltop_status_and_hold_codes_change_nothing()
    {
        var d = new WheeltopEdsDecoder();
        var n = BleUuids.NordicUartTx;
        Assert.Null(d.Decode(n, Hex("0436104A"))!.Held);   // état 1 Hz : 04+36+10 = 4A
        Assert.Null(d.Decode(n, Hex("040400"))!.Held);     // maintien
    }
}

public class LtwooDecoderTests
{
    private static byte[] WithXor(params byte[] body)
    {
        byte x = 0;
        foreach (var b in body) x ^= b;
        return [.. body, x];
    }

    private static byte[] Reply(byte opcode, byte value) => WithXor(0x5A, 0xFF, 0xFF, 0xFF, 0x46, 0x46, 0x46, opcode, 0x00, value);

    [Fact]
    public void Requests_follow_the_published_layout()
    {
        // A5 | "123" | 46 46 46 | 20 01 00 | xor
        Assert.Equal(Convert.FromHexString("A5313233464646200100F2"), LtwooDecoder.Request("123", 0x20, 0x01, 0x00));
        Assert.Equal("000", LtwooDecoder.NormalizePin(""));
        Assert.Equal("120", LtwooDecoder.NormalizePin("12"));
        Assert.Equal("123", LtwooDecoder.NormalizePin("12345"));
        var d = new LtwooDecoder("123");
        Assert.Equal(2, d.InitFrames.Count);
        Assert.Equal(2, d.PollFrames.Count);
    }

    [Fact]
    public void Battery_reply_from_the_public_test_vector()
    {
        var f = new LtwooDecoder().Decode(BleUuids.NordicUartTx, Convert.FromHexString("5affffff4646460a01638b"))!;
        Assert.Equal(99, f.BatteryPercent);
    }

    [Fact]
    public void Rear_gear_changes_become_taps()
    {
        var d = new LtwooDecoder();
        var n = BleUuids.NordicUartTx;
        Assert.Empty(d.Decode(n, Reply(0x09, 5))!.Taps);                                // première lecture : référence
        Assert.Empty(d.Decode(n, Reply(0x09, 5))!.Taps);                                // inchangé
        Assert.Equal(["Up"], d.Decode(n, Reply(0x09, 4))!.Taps);                        // pignon brut qui diminue = vitesse +
        Assert.Equal(["Down", "Down", "Down"], d.Decode(n, Reply(0x09, 7))!.Taps);     // +3
        Assert.Equal(["Down", "Down", "Down"], d.Decode(n, Reply(0x09, 12))!.Taps);    // plafonné à 3 appuis
        Assert.Empty(d.Decode(n, Reply(0x11, 1))!.Taps);                                // plateau : référence
        Assert.Equal(["Front"], d.Decode(n, Reply(0x11, 2))!.Taps);
    }

    [Fact]
    public void Corrupt_or_foreign_frames_are_not_recognised_and_wrong_pin_is_reported()
    {
        var d = new LtwooDecoder();
        var n = BleUuids.NordicUartTx;
        var bad = Reply(0x09, 5); bad[^1] ^= 0x01;
        Assert.Null(d.Decode(n, bad));
        Assert.Null(d.Decode(n, Convert.FromHexString("AA0001AB000000000000")));
        var wrongPin = WithXor(0x5A, 0xEE, 0xEE, 0xEE, 0x46, 0x46, 0x46, 0x20, 0x01, 0x00);
        Assert.Throws<InvalidOperationException>(() => d.Decode(n, wrongPin));
    }
}

public class NewControllerCatalogTests
{
    private static BleAdvertisement Adv(string? name, Guid[]? services = null, Dictionary<Guid, byte[]>? serviceData = null,
        (ushort, byte[])[]? manufacturer = null) =>
        FakeBleScanner.Adv(0xAABB0000CCDD, name, services: services, serviceData: serviceData, manufacturer: manufacturer);

    [Theory]
    [InlineData("RDR-1234", ControllerKind.ShimanoDi2)]
    [InlineData("EWWU111", ControllerKind.ShimanoDi2)]
    [InlineData("KICKR BIKE 4A2F", ControllerKind.WahooKickrBike)]
    [InlineData("SQUARE", ControllerKind.EliteSquare)]
    [InlineData("Square 1A2B", ControllerKind.EliteSquare)]
    [InlineData("Stages Bike 1234", ControllerKind.StagesSb20)]
    [InlineData("T7H-L 1234", ControllerKind.CycplusT7h)]
    [InlineData("T7H-R 1234", ControllerKind.CycplusT7h)]
    [InlineData("LTOED2501AB", ControllerKind.Ltwoo)]
    [InlineData("SRAM 123456", ControllerKind.SramAxs)]
    public void Identifies_by_name(string name, ControllerKind expected) =>
        Assert.Equal(expected, ControllerCatalog.Identify(Adv(name)));

    [Theory]
    [InlineData("LTOED00AB")]    // ancienne génération L-TWOO
    [InlineData("SQUARE X")]
    [InlineData("KICKR CORE")]   // trainer
    [InlineData("Stages Power")]
    [InlineData("FC-R9200-P")]   // capteur de puissance Shimano
    public void Ignores_non_controllers(string name) => Assert.Null(ControllerCatalog.Identify(Adv(name)));

    [Fact]
    public void Shimano_is_also_recognised_by_its_dfly_service_without_a_name()
    {
        Assert.Equal(ControllerKind.ShimanoDi2, ControllerCatalog.Identify(Adv(null, [ShimanoDi2Decoder.Service])));
        Assert.Null(ControllerCatalog.Identify(Adv("FC-R9200-P", [ShimanoDi2Decoder.ConfigService])));
    }

    [Fact]
    public void Sram_only_accepts_the_rear_derailleur_when_the_class_is_readable()
    {
        var fe51 = ShiftBridge.Core.Trainer.FtmsCodec.BaseUuid(0xFE51);
        byte[] Data(byte cls) => [0x0D, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x04, cls];
        Assert.Equal(ControllerKind.SramAxs, ControllerCatalog.Identify(Adv("SRAM 123456", serviceData: new() { [fe51] = Data(0x81) })));
        Assert.Null(ControllerCatalog.Identify(Adv("SRAM 123456", serviceData: new() { [fe51] = Data(0x84) })));  // tige de selle
        Assert.Null(ControllerCatalog.Identify(Adv("SRAM 123456", serviceData: new() { [fe51] = Data(0x00) })));  // levier
        Assert.Null(ControllerCatalog.Identify(Adv("Casque")));
    }

    [Fact]
    public void Wheeltop_is_recognised_by_manufacturer_prefix_and_names_its_unit()
    {
        // préfixe 07 0F 00 14 55 6A 84 = company id 0x0F07 (petit-boutiste) + charge utile ; octet 7 = type d'unité
        var adv = Adv(null, manufacturer: [(0x0F07, [0x00, 0x14, 0x55, 0x6A, 0x84, 0x36, 0x01, 0x02])]);
        var d = ControllerCatalog.Detect(adv)!;
        Assert.Equal(ControllerKind.WheeltopEds, d.Kind);
        Assert.Contains("TX Left", d.Name);
        Assert.Null(ControllerCatalog.Identify(Adv(null, manufacturer: [(0x0F07, [0x00, 0x14, 0x55, 0x6A, 0x85, 0x36, 0x01, 0x02])])));
    }

    [Fact]
    public void Only_pure_remotes_are_hidden_from_the_trainer_list()
    {
        Assert.True(ControllerKind.ThinkriderVs200.IsPureRemote());
        Assert.True(ControllerKind.ShimanoDi2.IsPureRemote());
        Assert.False(ControllerKind.WahooKickrBike.IsPureRemote());
        Assert.False(ControllerKind.StagesSb20.IsPureRemote());

        var scanner = new FakeBleScanner();
        using var hub = new BleAdvertisementHub(scanner);
        var ts = new ShiftBridge.Platform.Trainer.BleTrainerScanner(hub);
        var list = new List<ShiftBridge.Platform.Trainer.DiscoveredBleTrainer>();
        ts.TrainerSeen += list.Add;
        ts.Start();
        var ftms = new[] { ShiftBridge.Core.Trainer.FtmsCodec.BaseUuid(0x1826) };
        scanner.Emit(FakeBleScanner.Adv(0x30, "KICKR BIKE 1234", services: ftms));
        scanner.Emit(FakeBleScanner.Adv(0x31, "SQUARE", services: ftms));
        Assert.Equal(["KICKR BIKE 1234"], list.Select(t => t.Name));
    }

    [Fact]
    public void Every_kind_has_a_spec_buttons_defaults_and_label_keys()
    {
        foreach (var kind in Enum.GetValues<ControllerKind>().Where(k => k != ControllerKind.Zwift))
        {
            Assert.NotNull(ControllerSpec.For(kind));
            var names = kind.Buttons().Select(b => b.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
            foreach (var (name, _) in ShiftBridge.Core.Mapping.Profile.ControllerDefaults(kind)) Assert.Contains(name, names);
            foreach (var (name, labelKey) in kind.Buttons())
                Assert.NotEqual(labelKey, ShiftBridge.Core.Localization.Loc.Get(labelKey)); // la ressource existe
            Assert.True(ControllerKinds.TryFromSlug(kind.Slug(), out var back) && back == kind);
        }
    }
}

public class NewControllerLifecycleTests
{
    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 4000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > until) throw new TimeoutException("condition non atteinte");
            await Task.Delay(10);
        }
    }

    private static BleButtonController Make(FakeBleConnection conn, ControllerKind kind, string? pin = null) =>
        new(new FakeBleCentral { Connection = conn }, new DiscoveredController(0xAABB00000009, "Test", kind, -50),
            ControllerSpec.For(kind, pin)!, autoReconnect: false) { TapHoldMs = 1 };

    [Fact]
    public async Task Di2_subscribes_with_indications_and_reports_taps()
    {
        var conn = new FakeBleConnection(new BleServiceInfo(ShimanoDi2Decoder.Service, [new BleCharacteristicInfo(ShimanoDi2Decoder.Channels, 0x22)]));
        await using var c = Make(conn, ControllerKind.ShimanoDi2);
        var events = new List<ButtonEvent>();
        c.ButtonEvents += e => { lock (events) events.AddRange(e); };
        c.Start();
        await WaitFor(() => c.State == ControllerState.Connected);
        Assert.Equal(BleNotifyMode.Indicate, conn.Notify[(ShimanoDi2Decoder.Service, ShimanoDi2Decoder.Channels)]);

        conn.Emit(ShimanoDi2Decoder.Service, ShimanoDi2Decoder.Channels, Convert.FromHexString("21F0F0F0F0"));
        conn.Emit(ShimanoDi2Decoder.Service, ShimanoDi2Decoder.Channels, Convert.FromHexString("2211F0F0F0"));
        await WaitFor(() => { lock (events) return events.Count == 2; });
        var ch1 = ButtonKey.Controller(ControllerKind.ShimanoDi2, "Ch1");
        Assert.Equal([new ButtonEvent(ch1, true), new ButtonEvent(ch1, false)], events);
    }

    [Fact]
    public async Task Elite_square_subscribes_to_whichever_characteristic_exists()
    {
        var conn = new FakeBleConnection(new BleServiceInfo(EliteSquareDecoder.Service, [new BleCharacteristicInfo(EliteSquareDecoder.Notify45, 0x10)]));
        await using var c = Make(conn, ControllerKind.EliteSquare);
        var events = new List<ButtonEvent>();
        c.ButtonEvents += e => { lock (events) events.AddRange(e); };
        c.Start();
        await WaitFor(() => c.State == ControllerState.Connected);
        Assert.Single(conn.Notify);
        Assert.True(conn.Notify.ContainsKey((EliteSquareDecoder.Service, EliteSquareDecoder.Notify45)));

        conn.Emit(EliteSquareDecoder.Service, EliteSquareDecoder.Notify45, Convert.FromHexString("00000000000200"));
        await WaitFor(() => { lock (events) return events.Count == 1; });
        Assert.Equal(new ButtonEvent(ButtonKey.Controller(ControllerKind.EliteSquare, "Up"), true), events[0]);
    }

    [Fact]
    public async Task Axs_double_tap_reaches_the_bridge_as_one_press()
    {
        var conn = new FakeBleConnection(new BleServiceInfo(SramAxsDecoder.Service, [new BleCharacteristicInfo(SramAxsDecoder.Trigger, 0x10)]));
        await using var c = Make(conn, ControllerKind.SramAxs);
        var events = new List<ButtonEvent>();
        c.ButtonEvents += e => { lock (events) events.AddRange(e); };
        c.Start();
        await WaitFor(() => c.State == ControllerState.Connected);

        conn.Emit(SramAxsDecoder.Service, SramAxsDecoder.Trigger, [0xFF]);
        conn.Emit(SramAxsDecoder.Service, SramAxsDecoder.Trigger, [0xFF]);
        await WaitFor(() => { lock (events) return events.Count == 2; });
        var dbl = ButtonKey.Controller(ControllerKind.SramAxs, "Double");
        Assert.Equal([new ButtonEvent(dbl, true), new ButtonEvent(dbl, false)], events);
    }

    [Fact]
    public async Task Ltwoo_writes_hello_then_polls_and_turns_gear_changes_into_presses()
    {
        var nus = new BleServiceInfo(BleUuids.NordicUartService,
        [
            new BleCharacteristicInfo(BleUuids.NordicUartTx, 0x10),
            new BleCharacteristicInfo(BleUuids.NordicUartRx, 0x04),
        ]);
        var conn = new FakeBleConnection(nus);
        await using var c = Make(conn, ControllerKind.Ltwoo, pin: "abc");
        var events = new List<ButtonEvent>();
        c.ButtonEvents += e => { lock (events) events.AddRange(e); };
        c.Start();
        await WaitFor(() => c.State == ControllerState.Connected);

        // Hello puis batterie, puis l'interrogation périodique.
        await WaitFor(() => conn.Writes.Count >= 4);
        Assert.All(conn.Writes, w => Assert.Equal(BleUuids.NordicUartRx, w.Char));
        Assert.Equal(LtwooDecoder.Request("abc", 0x20, 0x01, 0x00), conn.Writes[0].Value);
        Assert.False(conn.Writes[0].WithResponse);

        byte[] Rear(byte v) { byte[] b = [0x5A, 0xFF, 0xFF, 0xFF, 0x46, 0x46, 0x46, 0x09, 0x00, v, 0]; b[^1] = b.Take(10).Aggregate((byte)0, (x, y) => (byte)(x ^ y)); return b; }
        conn.Emit(BleUuids.NordicUartService, BleUuids.NordicUartTx, Rear(5));
        conn.Emit(BleUuids.NordicUartService, BleUuids.NordicUartTx, Rear(4));
        await WaitFor(() => { lock (events) return events.Count == 2; });
        var up = ButtonKey.Controller(ControllerKind.Ltwoo, "Up");
        Assert.Equal([new ButtonEvent(up, true), new ButtonEvent(up, false)], events);
    }

    [Fact]
    public async Task Ltwoo_without_its_write_characteristic_is_an_error()
    {
        var conn = new FakeBleConnection(new BleServiceInfo(BleUuids.NordicUartService, [new BleCharacteristicInfo(BleUuids.NordicUartTx, 0x10)]));
        await using var c = Make(conn, ControllerKind.Ltwoo);
        var states = new List<ControllerState>();
        c.StateChanged += s => { lock (states) states.Add(s); };
        c.Start();
        await WaitFor(() => { lock (states) return states.Contains(ControllerState.Disconnected); });
        lock (states) Assert.Contains(ControllerState.Error, states);
    }
}
