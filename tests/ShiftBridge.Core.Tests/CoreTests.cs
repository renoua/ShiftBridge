using System.Text;
using ShiftBridge.Core.Config;
using ShiftBridge.Core.Gears;
using ShiftBridge.Core.Input;
using ShiftBridge.Core.Mapping;
using ShiftBridge.Core.Obc;
using ShiftBridge.Core.Zwift;
using ShiftBridge.Core.Zwift.Parsers;

namespace ShiftBridge.Core.Tests;

// NB : les trames ci-dessous sont construites à la main d'après les champs documentés publiquement
// (qdomyos-zwift, Makinolo). Elles seront complétées par de vraies captures (mode capture du Diagnostic).
public class ZwiftParserTests
{
    private static readonly ZwiftDeviceType Ride = new(ZwiftModel.Ride, ZwiftSide.Right, 0x07);

    private static HashSet<PhysicalButton> Pressed(ZwiftDeviceType d, params byte[] data) =>
        ZwiftFrameParser.Parse(d, data)!.Pressed!;

    [Fact]
    public void Ride_buttonMap_is_active_low()
    {
        // buttonMap = 0xEFFF : le bit 0x1000 (ShiftUp droit) est à 0 => appuyé
        var p = Pressed(Ride, 0x23, 0x08, 0xFF, 0xDF, 0x03);
        Assert.Equal([PhysicalButton.RightShiftUp], p);
    }

    [Fact]
    public void Ride_all_bits_set_means_nothing_pressed()
    {
        // buttonMap = 0xFFFF
        var p = Pressed(Ride, 0x23, 0x08, 0xFF, 0xFF, 0x03);
        Assert.Empty(p);
    }

    [Fact]
    public void Ride_analog_paddle_above_threshold_is_pressed()
    {
        // champ 3 = { location=1, valeur zigzag(50)=100 }
        var p = Pressed(Ride, 0x23, 0x08, 0xFF, 0xFF, 0x03, 0x1A, 0x04, 0x08, 0x01, 0x10, 0x64);
        Assert.Equal([PhysicalButton.RightPaddle], p);
    }

    [Fact]
    public void Ride_analog_paddle_below_threshold_is_ignored()
    {
        // zigzag(10)=20
        var p = Pressed(Ride, 0x23, 0x08, 0xFF, 0xFF, 0x03, 0x1A, 0x04, 0x08, 0x01, 0x10, 0x14);
        Assert.Empty(p);
    }

    [Fact]
    public void Play_right_controller_maps_Y()
    {
        // champ1=0 (droite), champ2=0 (Y appuyé), champ4=1 (A relâché)
        var p = Pressed(new(ZwiftModel.Play, ZwiftSide.Right, 0x02), 0x07, 0x08, 0x00, 0x10, 0x00, 0x20, 0x01);
        Assert.Equal([PhysicalButton.RightY], p);
    }

    [Fact]
    public void Play_left_controller_maps_down()
    {
        // champ1=1 (gauche), champ5=0 (B/bas appuyé)
        var p = Pressed(new(ZwiftModel.Play, ZwiftSide.Left, 0x03), 0x07, 0x08, 0x01, 0x28, 0x00);
        Assert.Equal([PhysicalButton.LeftDown], p);
    }

    [Fact]
    public void Play_side_button_is_field_6_and_onoff_is_field_7()
    {
        // Confirmé sur Play réel : le bouton latéral droit allume le champ 6 (0x30), pas « on/off ».
        var right = new ZwiftDeviceType(ZwiftModel.Play, ZwiftSide.Right, 0x02);
        Assert.Equal([PhysicalButton.RightShiftUp], Pressed(right, 0x07, 0x08, 0x00, 0x30, 0x00));
        Assert.Equal([PhysicalButton.RightOnOff], Pressed(right, 0x07, 0x08, 0x00, 0x38, 0x00));

        var left = new ZwiftDeviceType(ZwiftModel.Play, ZwiftSide.Left, 0x03);
        Assert.Equal([PhysicalButton.LeftShiftDown], Pressed(left, 0x07, 0x08, 0x01, 0x30, 0x00));
        Assert.Equal([PhysicalButton.LeftOnOff], Pressed(left, 0x07, 0x08, 0x01, 0x38, 0x00));
    }

    [Fact]
    public void Play_0x0E_side_comes_from_the_frame()
    {
        // Les deux Play d'un firmware récent peuvent annoncer 0x0E : le côté se lit alors dans le champ 1 de la trame.
        var t = ZwiftDeviceType.FromManufacturerData([0x0E]);
        Assert.Equal(ZwiftModel.Play, t.Model);
        Assert.Equal(ZwiftSide.None, t.Side);
        Assert.Equal([PhysicalButton.RightShiftUp], Pressed(t, 0x07, 0x08, 0x00, 0x30, 0x00));
        Assert.Equal([PhysicalButton.LeftShiftDown], Pressed(t, 0x07, 0x08, 0x01, 0x30, 0x00));
    }

    [Fact]
    public void Click_plus_pressed()
    {
        var p = Pressed(new(ZwiftModel.Click, ZwiftSide.None, 0x09), 0x37, 0x08, 0x00, 0x10, 0x01);
        Assert.Equal([PhysicalButton.ClickPlus], p);
    }

    // ---- Trames RÉELLES capturées sur un Zwift Play (journal Diagnostic, 2026-11-19) ----
    private static readonly ZwiftDeviceType PlayLeft = new(ZwiftModel.Play, ZwiftSide.Left, 0x03);

    [Theory]
    [InlineData("07080110011801200128013001380140374800")] // champ 8 = 0x37 => -28 : bruit de repos
    [InlineData("07080110011801200128013001380140334800")] // 0x33 => -26
    [InlineData("070801100118012001280130013801402F4800")] // 0x2F => -24
    public void Real_Play_idle_frames_press_nothing(string hex)
    {
        Assert.Empty(Pressed(PlayLeft, Convert.FromHexString(hex)));
    }

    [Fact]
    public void Real_Play_battery_frame_is_protobuf_field_2()
    {
        // « 19 10 45 » : champ 2 = 0x45 = 69 % (et non 0x10 = 16 %)
        var f = ZwiftFrameParser.Parse(PlayLeft, Convert.FromHexString("191045"));
        Assert.Equal(69, f!.BatteryPercent);
    }

    [Theory]
    [InlineData("15")]                                          // trame vide
    [InlineData("2A08521210120E081810001890032205FFFFFFFF1F")]  // statut inconnu
    public void Real_Play_non_button_frames_are_ignored(string hex)
    {
        Assert.Null(ZwiftFrameParser.Parse(PlayLeft, Convert.FromHexString(hex)));
    }

    [Fact]
    public void Play_paddle_pressed_only_at_full_travel()
    {
        // champ 8 = zigzag(100) = 200 => varint C8 01
        var full = Pressed(PlayLeft, 0x07, 0x08, 0x01, 0x40, 0xC8, 0x01);
        Assert.Equal([PhysicalButton.LeftPaddle], full);
    }

    [Fact]
    public void Play_side_comes_from_advertisement_not_field_1()
    {
        // Champ 1 = 1 (« gauche ») mais l'annonce BLE dit manette droite : l'annonce prime.
        var p = Pressed(new(ZwiftModel.Play, ZwiftSide.Right, 0x02), 0x07, 0x08, 0x01, 0x10, 0x00);
        Assert.Equal([PhysicalButton.RightY], p);
    }

    [Fact]
    public void Battery_frame()
    {
        var f = ZwiftFrameParser.Parse(Ride, [0x19, 0x10, 85]);
        Assert.Equal(85, f!.BatteryPercent);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x15 })]
    [InlineData(new byte[] { 0x99, 0x01 })]
    public void Unknown_or_empty_frames_are_ignored(byte[] data) =>
        Assert.Null(ZwiftFrameParser.Parse(Ride, data));

    [Theory]
    [InlineData(new byte[] { 0x23, 0x08, 0xFF })]               // varint tronqué
    [InlineData(new byte[] { 0x23, 0x1A, 0x40, 0x01 })]          // longueur annoncée > données
    [InlineData(new byte[] { 0x07, 0x0B })]                      // type de fil inconnu
    public void Corrupted_frames_never_throw(byte[] data)
    {
        var f = ZwiftFrameParser.Parse(Ride, data);
        Assert.Empty(f?.Pressed ?? []);
    }

    [Theory]
    [InlineData(0x02, ZwiftModel.Play, ZwiftSide.Right)]
    [InlineData(0x08, ZwiftModel.Ride, ZwiftSide.Left)]
    [InlineData(0x09, ZwiftModel.Click, ZwiftSide.None)]
    [InlineData(0x0B, ZwiftModel.ClickV2, ZwiftSide.Left)]
    public void Device_type_from_manufacturer_data(byte code, ZwiftModel model, ZwiftSide side)
    {
        var t = ZwiftDeviceType.FromManufacturerData([code, 0x00]);
        Assert.Equal(model, t.Model);
        Assert.Equal(side, t.Side);
    }
}

public class ObcMessageTests
{
    [Fact]
    public void ButtonState_matches_spec_example()
    {
        var msgs = ObcMessages.EncodeButtonState([(ObcButtonId.ShiftUp, 1)]).ToList();
        Assert.Single(msgs);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x01 }, msgs[0]);
    }

    [Fact]
    public void ButtonState_multiple_pairs()
    {
        var m = ObcMessages.EncodeButtonState([(ObcButtonId.ShiftUp, 1), (ObcButtonId.ShiftDown, 0)]).Single();
        Assert.Equal(new byte[] { 0x01, 0x01, 0x01, 0x02, 0x00 }, m);
    }

    [Fact]
    public void ButtonState_is_chunked_at_nine_pairs()
    {
        var changes = Enumerable.Range(0, 10).Select(i => ((ObcButtonId)(0x10 + i), (byte)1));
        var msgs = ObcMessages.EncodeButtonState(changes).ToList();
        Assert.Equal(2, msgs.Count);
        Assert.Equal(19, msgs[0].Length);
        Assert.Equal(3, msgs[1].Length);
    }

    [Fact]
    public void AppInfo_decodes_spec_example()
    {
        var bytes = new List<byte> { 0x04, 0x01, 0x05 };
        bytes.AddRange(Encoding.ASCII.GetBytes("zwift"));
        bytes.Add(0x06);
        bytes.AddRange(Encoding.ASCII.GetBytes("1.52.0"));
        bytes.AddRange([0x04, 0x01, 0x02, 0x10, 0x14]);

        Assert.True(ObcMessages.TryDecodeAppInfo(bytes.ToArray(), out var info));
        Assert.Equal("zwift", info!.AppId);
        Assert.Equal("1.52.0", info.AppVersion);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x10, 0x14 }, info.ButtonIds);
    }

    [Fact]
    public void AppInfo_roundtrip_and_rejects_truncated()
    {
        var enc = ObcMessages.EncodeAppInfo(new ObcAppInfo("mywhoosh", "5.0", []));
        Assert.True(ObcMessages.TryDecodeAppInfo(enc, out var info));
        Assert.Equal("mywhoosh", info!.AppId);
        Assert.Empty(info.ButtonIds);
        Assert.False(ObcMessages.TryDecodeAppInfo(enc.AsSpan(0, enc.Length - 2), out _));
    }

    [Fact]
    public void Haptic_decode_and_status_encode()
    {
        Assert.True(ObcMessages.TryDecodeHaptic([0x03, 0x02, 0x14, 0x80], out var h));
        Assert.Equal(new ObcHaptic(ObcHapticPattern.Double, 0x14, 0x80), h);
        Assert.Equal(new byte[] { 0x02, 80, 0x01 }, ObcMessages.EncodeDeviceStatus(80, true));
        Assert.Equal(new byte[] { 0x02, 0xFF, 0x00 }, ObcMessages.EncodeDeviceStatus(null, false));
    }
}

public class MappingTests
{
    [Fact]
    public void Press_and_release_produce_obc_changes()
    {
        var engine = new MappingEngine();
        var down = engine.Translate([new ButtonEvent(PhysicalButton.ClickPlus, true)]);
        var up = engine.Translate([new ButtonEvent(PhysicalButton.ClickPlus, false)]);
        Assert.Equal([(ObcButtonId.ShiftUp, (byte)1)], down);
        Assert.Equal([(ObcButtonId.ShiftUp, (byte)0)], up);
    }

    [Fact]
    public void Shared_action_is_released_only_when_last_button_is_released()
    {
        var engine = new MappingEngine();
        engine.Translate([new ButtonEvent(PhysicalButton.RightShiftUp, true), new ButtonEvent(PhysicalButton.RightPaddle, true)]);
        var first = engine.Translate([new ButtonEvent(PhysicalButton.RightShiftUp, false)]);
        var second = engine.Translate([new ButtonEvent(PhysicalButton.RightPaddle, false)]);
        Assert.Empty(first);
        Assert.Equal([(ObcButtonId.ShiftUp, (byte)0)], second);
    }

    // Journal réel Click v2 : la manette gauche relaie les appuis de la droite, la même touche est donc signalée deux fois.
    [Fact]
    public void Same_button_reported_by_two_sources_acts_as_one_press()
    {
        var engine = new MappingEngine();
        var press = new ButtonEvent(PhysicalButton.RightShiftUp, true);
        var release = new ButtonEvent(PhysicalButton.RightShiftUp, false);

        var down1 = engine.Translate([press]);
        var down2 = engine.Translate([press]);
        Assert.Equal([(ObcButtonId.ShiftUp, (byte)1)], down1);
        Assert.Empty(down2);

        var up1 = engine.Translate([release]);
        var up2 = engine.Translate([release]);
        Assert.Empty(up1);
        Assert.Equal([(ObcButtonId.ShiftUp, (byte)0)], up2);

        // Régression : avant, l'action restait tenue et les appuis suivants ne produisaient plus rien.
        Assert.Equal([(ObcButtonId.ShiftUp, (byte)1)], engine.Translate([press]));
    }

    [Fact]
    public void Default_profile_maps_left_click_v2_button_to_shift_down()
    {
        var p = Profile.ZwiftLike();
        Assert.Equal([ObcButtonId.ShiftDown], p.Bindings[PhysicalButton.LeftShiftUp].Actions);
        Assert.Equal([ObcButtonId.ShiftUp], p.Bindings[PhysicalButton.RightShiftUp].Actions);
    }

    [Fact]
    public void Actions_not_supported_by_app_are_filtered()
    {
        var engine = new MappingEngine { SupportedActions = new HashSet<ObcButtonId> { ObcButtonId.ShiftUp } };
        Assert.Empty(engine.Translate([new ButtonEvent(PhysicalButton.RightB, true)]));
        Assert.Empty(engine.Translate([new ButtonEvent(PhysicalButton.RightB, false)]));
    }

    [Fact]
    public void Release_uses_actions_pressed_even_if_profile_changed()
    {
        var engine = new MappingEngine();
        engine.Translate([new ButtonEvent(PhysicalButton.ClickPlus, true)]);
        engine.Profile = new Profile();
        var up = engine.Translate([new ButtonEvent(PhysicalButton.ClickPlus, false)]);
        Assert.Equal([(ObcButtonId.ShiftUp, (byte)0)], up);
    }
}

public class StateAndGearTests
{
    [Fact]
    public void Tracker_emits_diff_and_reset_releases_all()
    {
        var t = new ButtonStateTracker();
        var e1 = t.Update(new HashSet<PhysicalButton> { PhysicalButton.ClickPlus });
        var e2 = t.Update(new HashSet<PhysicalButton> { PhysicalButton.ClickPlus });
        var e3 = t.Reset();
        Assert.Equal([new ButtonEvent(PhysicalButton.ClickPlus, true)], e1);
        Assert.Empty(e2);
        Assert.Equal([new ButtonEvent(PhysicalButton.ClickPlus, false)], e3);
    }

    [Fact]
    public void Gears_clamp_at_limits()
    {
        var g = new VirtualGearState(gearCount: 3, initialGear: 2);
        Assert.Equal(GearChange.Changed, g.Apply(ObcButtonId.ShiftUp));
        Assert.Equal(GearChange.AtTopLimit, g.Apply(ObcButtonId.ShiftUp));
        Assert.Equal(3, g.Gear);
        g.Apply(ObcButtonId.ShiftDown); g.Apply(ObcButtonId.ShiftDown);
        Assert.Equal(GearChange.AtBottomLimit, g.Apply(ObcButtonId.ShiftDown));
        Assert.Equal(1, g.Gear);
    }
}

public class ConfigTests
{
    [Fact]
    public void Config_roundtrip_and_corrupt_file_fallback()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "config.json");
            var cfg = new AppConfig { GearCount = 20, EnableDirCon = false };
            ConfigStore.Save(cfg, path);
            var back = ConfigStore.Load(path);
            Assert.Equal(20, back.GearCount);
            Assert.False(back.EnableDirCon);
            Assert.Equal([ObcButtonId.ShiftUp], back.Profile.Bindings[PhysicalButton.ClickPlus].Actions);

            File.WriteAllText(path, "{ pas du json");
            Assert.Equal(24, ConfigStore.Load(path).GearCount);
        }
        finally { dir.Delete(true); }
    }
}
