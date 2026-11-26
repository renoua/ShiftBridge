using ShiftBridge.Core.Controllers;
using ShiftBridge.Core.Controllers.Sram;
using ShiftBridge.Core.Input;
using ShiftBridge.Platform.Ble;
using ShiftBridge.Platform.Controllers;

namespace ShiftBridge.Core.Tests;

public class SramAxsFramesTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void Documented_gear_7_plaintext_is_decoded()
    {
        Assert.True(SramAxsFrames.TryParseDrivetrain(H("a00101a80107b0010c"), out int rear, out int? front));
        Assert.Equal(7, rear);
        Assert.Equal(1, front);
    }

    [Fact]
    public void Rear_only_and_unknown_fields_are_tolerated()
    {
        // champ 21 seul (1x : pas de plateau), précédé d'un champ à longueur variable (type 2) à sauter
        Assert.True(SramAxsFrames.TryParseDrivetrain(H("0a03aabbcc" + "a80105"), out int rear, out int? front));
        Assert.Equal(5, rear);
        Assert.Null(front);
    }

    [Theory]
    [InlineData("a00101b0010c")]        // pas de pignon arrière
    [InlineData("a80100")]              // pignon 0
    [InlineData("a80119")]              // 25 : hors plage
    [InlineData("a801")]                // tronqué
    [InlineData("")]
    public void Implausible_or_truncated_plaintext_is_rejected(string hex) =>
        Assert.False(SramAxsFrames.TryParseDrivetrain(H(hex), out _, out _));

    [Fact]
    public void All_integer_fields_are_described_for_diagnostics()
    {
        // champ 2 (longueur variable) sauté, puis 20, 21 et 22 dans l'ordre de la trame
        Assert.Equal("20=1 21=7 22=12", SramAxsFrames.DescribeFields(H("0a03aabbcc" + "a00101a80107b0010c")));
        Assert.Equal("", SramAxsFrames.DescribeFields([]));
        Assert.Equal("20=1", SramAxsFrames.DescribeFields(H("a00101a801")));   // tronqué : on garde ce qui précède
    }

    [Theory]
    [InlineData(false, "Single", true)]
    [InlineData(false, "Double", true)]
    [InlineData(false, "Up", false)]
    [InlineData(false, "Down", false)]
    [InlineData(true, "Single", false)]
    [InlineData(true, "Double", false)]
    [InlineData(true, "Up", true)]
    [InlineData(true, "Down", true)]
    public void Buttons_page_only_lists_the_axs_keys_of_the_chosen_mode(bool advanced, string name, bool shown) =>
        Assert.Equal(shown, ShiftBridge.Presentation.MainViewModel.IsAxsButtonForMode(name, advanced));

    [Fact]
    public void Every_axs_key_is_shown_in_exactly_one_mode()
    {
        foreach (var (name, _) in ControllerKind.SramAxs.Buttons())
            Assert.True(ShiftBridge.Presentation.MainViewModel.IsAxsButtonForMode(name, false) ^ ShiftBridge.Presentation.MainViewModel.IsAxsButtonForMode(name, true), name);
    }

    [Fact]
    public void Gear_count_comes_from_field_24()
    {
        Assert.Equal(12, SramAxsFrames.TryParseGearCount(H("c0010c")));
        Assert.Null(SramAxsFrames.TryParseGearCount(H("a80107")));
        Assert.Null(SramAxsFrames.TryParseGearCount(H("c00100")));
    }

    [Fact]
    public void Tracker_turns_position_changes_into_directed_taps()
    {
        var t = new SramAxsShiftTracker { GearCount = 12 };
        Assert.Empty(t.Update(7));                                  // référence
        Assert.Empty(t.Update(7));
        Assert.Equal(["Up"], t.Update(6));                          // position qui diminue = vitesse +
        Assert.Equal(["Down", "Down", "Down"], t.Update(9));
        Assert.Equal(["Up", "Up", "Up", "Up"], t.Update(2));        // 7 crans : plafonné à 4 appuis
        Assert.Empty(t.Update(2 + 13));                             // aberrant (> nombre de pignons) : ignoré…
        Assert.Equal(["Down"], t.Update(3));                        // …et la référence n'a pas bougé
        t.Reset();
        Assert.Empty(t.Update(10));
    }
}

public class SramAxsAdvancedControllerTests
{
    private sealed class MemoryKeys : ISramKeyStore
    {
        public Dictionary<string, byte[]> Keys { get; } = [];
        public byte[]? Get(string serial) => Keys.GetValueOrDefault(serial);
        public void Set(string serial, byte[] key) => Keys[serial] = key;
        public void Remove(string serial) => Keys.Remove(serial);
    }

    private static readonly Guid BondSvc = new("d905ee51-90aa-4c7c-b036-1e01fb8eb7ee");
    private static readonly Guid Bond = new("d905ee52-90aa-4c7c-b036-1e01fb8eb7ee");
    private static readonly Guid LiveSvc = new("d9050053-90aa-4c7c-b036-1e01fb8eb7ee");
    private static readonly Guid Live = new("d905000b-90aa-4c7c-b036-1e01fb8eb7ee");
    private static readonly Guid Changed = new("d9050054-90aa-4c7c-b036-1e01fb8eb7ee");
    private static readonly Guid Config = new("d9050025-90aa-4c7c-b036-1e01fb8eb7ee");
    private static readonly Guid Serial = new("d905fe54-90aa-4c7c-b036-1e01fb8eb7ee");
    private static readonly byte[] DeviceKey = Convert.FromHexString("b0690781867fde13ac1b9d30bbb4004f");
    private static readonly DiscoveredController Info = new(0xAABB0000EEEE, "SRAM 12345678", ControllerKind.SramAxs, -50);

    private static FakeBleConnection Derailleur(byte[]? firstLive = null)
    {
        var conn = new FakeBleConnection(
            new BleServiceInfo(BondSvc, [new BleCharacteristicInfo(Bond, 0x1E)]),
            new BleServiceInfo(LiveSvc,
            [
                new BleCharacteristicInfo(Live, 0x12), new BleCharacteristicInfo(Changed, 0x12),
                new BleCharacteristicInfo(Config, 0x02), new BleCharacteristicInfo(Serial, 0x02),
            ]));
        conn.ReadValues[(LiveSvc, Serial)] = BitConverter.GetBytes(12345678u);
        if (firstLive is not null) conn.ReadValues[(LiveSvc, Live)] = firstLive;
        return conn;
    }

    private static byte[] Encrypted(byte[] key, int rear, int front = 1)
    {
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var plain = new byte[] { 0xA0, 0x01, (byte)front, 0xA8, 0x01, (byte)rear, 0xB0, 0x01, 0x0C };
        var (ct, tag) = SramCrypto.EaxEncrypt(key, nonce, plain);
        return [.. nonce, .. ct, .. tag];
    }

    private static byte[] EncryptedConfig(byte[] key)
    {
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var (ct, tag) = SramCrypto.EaxEncrypt(key, nonce, [0xC0, 0x01, 0x0C]);
        return [.. nonce, .. ct, .. tag];
    }

    /// <summary>Dérailleur simulé : répond à l'appairage avec une vraie clé d'appareil chiffrée sous le secret partagé.</summary>
    private static void AttachBondResponder(FakeBleConnection conn, byte[] deviceKey, bool corruptBlob = false)
    {
        var devPriv = SramCrypto.NewPrivateKey();
        int writes = 0;
        conn.OnWrite = (svc, chr, value) =>
        {
            if (chr != Bond || ++writes != 2) return; // 1re écriture : amorce, 2e : clé publique du client
            var shared = SramCrypto.SharedSecret(value, devPriv);
            var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            var (ct, tag) = SramCrypto.EaxEncrypt(shared, nonce, deviceKey);
            if (corruptBlob) tag[0] ^= 1;
            conn.ReadQueues[(BondSvc, Bond)] = new Queue<byte[]>([SramCrypto.PublicKey(devPriv), [.. nonce, .. ct, .. tag]]);
        };
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 4000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > until) throw new TimeoutException("condition non atteinte");
            await Task.Delay(10);
        }
    }

    private static SramAxsAdvancedController Make(FakeBleConnection conn, MemoryKeys keys) =>
        new(new FakeBleCentral { Connection = conn }, Info, keys, autoReconnect: false) { TapHoldMs = 1, KeepAliveInterval = TimeSpan.FromHours(1) };

    [Fact]
    public async Task Without_a_key_it_waits_for_pairing_then_pairs_and_stores_the_key()
    {
        var conn = Derailleur(Encrypted(DeviceKey, 7));
        AttachBondResponder(conn, DeviceKey);
        var keys = new MemoryKeys();
        await using var c = Make(conn, keys);
        c.Start();
        await WaitFor(() => c.State == ControllerState.Locked);
        Assert.True(c.CanPair);
        Assert.Empty(keys.Keys);

        Assert.True(await c.PairAsync());
        Assert.Equal(DeviceKey, keys.Keys["12345678"]);
        Assert.Equal(ControllerState.Connected, c.State);
        Assert.Equal(new byte[] { 0x73 }, conn.Writes.Last(w => w.Char == Bond).Value);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), conn.Writes.First(w => w.Char == Bond).Value);
        Assert.All(conn.Writes, w => Assert.True(w.WithResponse));
    }

    [Fact]
    public async Task A_refused_blob_leaves_the_controller_unpaired()
    {
        var conn = Derailleur();
        AttachBondResponder(conn, DeviceKey, corruptBlob: true);
        var keys = new MemoryKeys();
        var logs = new List<string>();
        await using var c = Make(conn, keys);
        c.Log += logs.Add;
        c.Start();
        await WaitFor(() => c.State == ControllerState.Locked);

        Assert.False(await c.PairAsync());
        Assert.Empty(keys.Keys);
        Assert.Equal(ControllerState.Locked, c.State);
        Assert.Contains(logs, l => l.Contains("appairage échoué"));
        Assert.DoesNotContain(conn.Writes, w => w.Char == Bond && w.Value.SequenceEqual(new byte[] { 0x73 }));
    }

    [Fact]
    public async Task Pairing_falls_back_to_notifications_when_reads_are_unusable()
    {
        var conn = Derailleur();
        var devPriv = SramCrypto.NewPrivateKey();
        int writes = 0;
        conn.OnWrite = (svc, chr, value) =>
        {
            if (chr != Bond || ++writes != 2) return;
            var shared = SramCrypto.SharedSecret(value, devPriv);
            var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            var (ct, tag) = SramCrypto.EaxEncrypt(shared, nonce, DeviceKey);
            // La lecture échoue (aucune valeur) : tout passe par des notifications, éventuellement coupées en morceaux.
            _ = Task.Run(() =>
            {
                conn.Emit(BondSvc, Bond, SramCrypto.PublicKey(devPriv));
                byte[] blob = [.. nonce, .. ct, .. tag];
                conn.Emit(BondSvc, Bond, blob[..20]);
                conn.Emit(BondSvc, Bond, blob[20..]);
            });
        };
        var keys = new MemoryKeys();
        await using var c = Make(conn, keys);
        c.Start();
        await WaitFor(() => c.State == ControllerState.Locked);
        Assert.True(await c.PairAsync());
        Assert.Equal(DeviceKey, keys.Keys["12345678"]);
    }

    [Fact]
    public async Task With_a_key_gear_changes_become_presses()
    {
        var conn = Derailleur(Encrypted(DeviceKey, 7));
        conn.ReadValues[(LiveSvc, Config)] = EncryptedConfig(DeviceKey);
        var keys = new MemoryKeys();
        keys.Set("12345678", DeviceKey);
        await using var c = Make(conn, keys);
        var events = new List<ButtonEvent>();
        c.ButtonEvents += e => { lock (events) events.AddRange(e); };
        c.Start();
        await WaitFor(() => c.State == ControllerState.Connected);
        Assert.Empty(events); // la première lecture sert de référence

        conn.ReadValues[(LiveSvc, Live)] = Encrypted(DeviceKey, 6);
        conn.Emit(LiveSvc, Live, [0xFF]); // sonnette : l'état se lit
        await WaitFor(() => { lock (events) return events.Count == 2; });
        var up = ButtonKey.Controller(ControllerKind.SramAxs, "Up");
        Assert.Equal([new ButtonEvent(up, true), new ButtonEvent(up, false)], events);

        conn.ReadValues[(LiveSvc, Live)] = Encrypted(DeviceKey, 8);
        conn.Emit(LiveSvc, Changed, [0xFF]);
        await WaitFor(() => { lock (events) return events.Count == 6; });
        var down = ButtonKey.Controller(ControllerKind.SramAxs, "Down");
        Assert.Equal(new ButtonEvent(down, true), events[2]);
    }

    [Fact]
    public async Task A_stale_key_is_detected_and_pairing_becomes_possible_again()
    {
        var conn = Derailleur(Encrypted(DeviceKey, 7)); // l'appareil chiffre avec SA clé…
        var keys = new MemoryKeys();
        var stale = (byte[])DeviceKey.Clone();
        stale[0] ^= 0xFF;
        keys.Set("12345678", stale); // …mais nous en avons mémorisé une autre
        var logs = new List<string>();
        await using var c = Make(conn, keys);
        c.Log += logs.Add;
        c.Start();
        await WaitFor(() => c.State == ControllerState.Locked);
        Assert.True(c.CanPair);
        Assert.Contains(logs, l => l.Contains("clé périmée"));

        c.ForgetKey();
        Assert.Empty(keys.Keys);
    }

    [Fact]
    public async Task Missing_live_characteristic_is_an_error_not_a_silent_failure()
    {
        var conn = new FakeBleConnection(new BleServiceInfo(BondSvc, [new BleCharacteristicInfo(Bond, 0x1E)]));
        await using var c = Make(conn, new MemoryKeys());
        var states = new List<ControllerState>();
        c.StateChanged += s => { lock (states) states.Add(s); };
        c.Start();
        await WaitFor(() => { lock (states) return states.Contains(ControllerState.Disconnected); });
        lock (states) Assert.Contains(ControllerState.Error, states);
    }
}
