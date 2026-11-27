using ShiftBridge.Core.Config;
using ShiftBridge.Core.Gears;
using ShiftBridge.Core.Input;
using ShiftBridge.Core.Mapping;
using ShiftBridge.Core.Obc;
using ShiftBridge.Platform;

namespace ShiftBridge.Core.Tests;

public class InternalGearActionTests
{
    private static async Task Until(Func<bool> cond, int ms = 3000)
    {
        var end = Environment.TickCount64 + ms;
        while (!cond()) { if (Environment.TickCount64 > end) throw new TimeoutException(); await Task.Delay(10); }
    }

    [Fact]
    public void Center_gear_jumps_to_the_middle_and_does_nothing_when_already_there()
    {
        var gears = new VirtualGearState(24, 3);
        Assert.Equal(GearChange.Changed, gears.Apply(ObcButtonId.CenterGear));
        Assert.Equal(12, gears.Gear);
        Assert.Equal(GearChange.None, gears.Apply(ObcButtonId.CenterGear)); // déjà au centre : ni clignotement ni vibration
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(99, 24)]
    [InlineData(1, 1)]
    [InlineData(0, 12)]  // cible non réglée : on ne bouge pas (avant 0.25.2 : saut au rapport 1)
    [InlineData(-5, 12)]
    public void Go_to_gear_is_bounded_to_the_scale(int target, int expected)
    {
        var gears = new VirtualGearState(24, 12);
        gears.Apply(ObcButtonId.GoToGear, target);
        Assert.Equal(expected, gears.Gear);
    }

    [Theory]
    [InlineData(ObcButtonId.ShiftUpBy, 12, 3, 15, GearChange.Changed)]
    [InlineData(ObcButtonId.ShiftUpBy, 22, 5, 24, GearChange.Changed)]
    [InlineData(ObcButtonId.ShiftUpBy, 24, 1, 24, GearChange.AtTopLimit)]
    [InlineData(ObcButtonId.ShiftDownBy, 12, 4, 8, GearChange.Changed)]
    [InlineData(ObcButtonId.ShiftDownBy, 2, 3, 1, GearChange.Changed)]
    [InlineData(ObcButtonId.ShiftDownBy, 1, 1, 1, GearChange.AtBottomLimit)]
    [InlineData(ObcButtonId.ShiftUpBy, 12, 0, 12, GearChange.None)]
    public void Shift_by_moves_several_gears_and_stops_at_the_limits(ObcButtonId action, int start, int steps, int expected, GearChange change)
    {
        var gears = new VirtualGearState(24, start);
        Assert.Equal(change, gears.Apply(action, steps));
        Assert.Equal(expected, gears.Gear);
    }

    [Fact]
    public async Task Shift_by_sends_that_many_shift_presses_to_the_app_and_never_the_internal_action()
    {
        await using var bridge = new BridgeService(new AppConfig { GearCount = 24, LastGear = 12 });
        var app = new FakeTransport("appli");
        await bridge.AddTransportAsync(app);
        bridge.Profile.Bindings[PhysicalButton.RightA] = new ButtonBinding { Actions = [ObcButtonId.ShiftUpBy], GearSteps = 3 };

        bridge.InjectEvents([new ButtonEvent(PhysicalButton.RightA, true)]);
        bridge.InjectEvents([new ButtonEvent(PhysicalButton.RightA, false)]);
        Assert.Equal(15, bridge.Gear.Gear);

        await Until(() => app.Sent.Count == 6);
        await Task.Delay(100);
        var expected = new List<byte[]>();
        for (int i = 0; i < 3; i++)
        {
            expected.Add([0x01, (byte)ObcButtonId.ShiftUp, 1]);
            expected.Add([0x01, (byte)ObcButtonId.ShiftUp, 0]);
        }
        Assert.Equal(expected, app.Sent.ToArray());
    }

    [Fact]
    public async Task Shift_by_changes_the_gear_but_sends_nothing_when_the_app_declares_no_shift_buttons()
    {
        await using var bridge = new BridgeService(new AppConfig { GearCount = 24, LastGear = 12 });
        var app = new FakeTransport("appli");
        await bridge.AddTransportAsync(app);
        typeof(BridgeService).GetMethod("OnAppInfo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(bridge, [new ObcAppInfo("test", "1", [(byte)ObcButtonId.Back])]);
        bridge.Profile.Bindings[PhysicalButton.RightA] = new ButtonBinding { Actions = [ObcButtonId.ShiftDownBy], GearSteps = 5 };

        bridge.InjectEvents([new ButtonEvent(PhysicalButton.RightA, true)]);
        Assert.Equal(7, bridge.Gear.Gear);
        await Task.Delay(300);
        Assert.Empty(app.Sent);
    }

    [Fact]
    public void The_step_count_is_saved_in_the_profile_and_survives_a_clone()
    {
        var profile = new Profile();
        profile.Bindings[PhysicalButton.RightA] = new ButtonBinding { Actions = [ObcButtonId.ShiftUpBy], GearSteps = 4 };
        Assert.Equal(4, profile.Clone().Bindings[PhysicalButton.RightA].GearSteps);

        string path = Path.Combine(Path.GetTempPath(), $"shiftbridge-test-{Guid.NewGuid():N}.json");
        try
        {
            ConfigStore.Save(new AppConfig { Profile = profile }, path);
            var back = ConfigStore.Load(path).Profile.Bindings[PhysicalButton.RightA];
            Assert.Equal(4, back.GearSteps);
            Assert.Equal([ObcButtonId.ShiftUpBy], back.Actions);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Center_gear_follows_the_gear_count()
    {
        var gears = new VirtualGearState(12, 1);
        gears.Apply(ObcButtonId.CenterGear);
        Assert.Equal(6, gears.Gear);
    }

    [Fact]
    public void Internal_actions_are_known_and_listed_in_the_catalog()
    {
        Assert.True(ObcButtonCatalog.IsInternal(ObcButtonId.CenterGear));
        Assert.True(ObcButtonCatalog.IsInternal(ObcButtonId.GoToGear));
        Assert.True(ObcButtonCatalog.IsInternal(ObcButtonId.ShiftUpBy));
        Assert.True(ObcButtonCatalog.IsInternal(ObcButtonId.ShiftDownBy));
        Assert.False(ObcButtonCatalog.IsInternal(ObcButtonId.ShiftUp));
        var ids = ObcButtonCatalog.Assignable.Select(a => a.Id).ToList();
        Assert.Contains(ObcButtonId.ShiftUpBy, ids);
        Assert.Contains(ObcButtonId.ShiftDownBy, ids);
        Assert.Contains(ObcButtonId.CenterGear, ids);
        Assert.Contains(ObcButtonId.GoToGear, ids);
    }

    [Fact]
    public void Internal_actions_are_never_filtered_by_the_apps_declared_buttons()
    {
        var engine = new MappingEngine { SupportedActions = new HashSet<ObcButtonId> { ObcButtonId.Back } };
        engine.Profile.Bindings[PhysicalButton.RightA] = new ButtonBinding { Actions = [ObcButtonId.CenterGear] };
        engine.Profile.Bindings[PhysicalButton.RightY] = new ButtonBinding { Actions = [ObcButtonId.Emote] };

        Assert.Equal([(ObcButtonId.CenterGear, (byte)1)], engine.Translate([new ButtonEvent(PhysicalButton.RightA, true)]));
        Assert.Empty(engine.Translate([new ButtonEvent(PhysicalButton.RightY, true)])); // une action OBC reste filtrée
    }

    [Fact]
    public async Task Go_to_gear_changes_the_gear_and_is_not_sent_to_the_app()
    {
        await using var bridge = new BridgeService(new AppConfig { GearCount = 24, LastGear = 12 });
        var app = new FakeTransport("appli");
        await bridge.AddTransportAsync(app);
        bridge.Profile.Bindings[PhysicalButton.RightA] = new ButtonBinding { Actions = [ObcButtonId.GoToGear], GearTarget = 20 };
        bridge.Profile.Bindings[PhysicalButton.RightB] = new ButtonBinding { Actions = [ObcButtonId.CenterGear] };

        bridge.InjectEvents([new ButtonEvent(PhysicalButton.RightA, true)]);
        bridge.InjectEvents([new ButtonEvent(PhysicalButton.RightA, false)]);
        Assert.Equal(20, bridge.Gear.Gear);

        bridge.InjectEvents([new ButtonEvent(PhysicalButton.RightB, true)]);
        bridge.InjectEvents([new ButtonEvent(PhysicalButton.RightB, false)]);
        Assert.Equal(12, bridge.Gear.Gear);

        // Une vraie action, ensuite : c'est la seule qui arrive à l'appli (appuyée puis relâchée).
        bridge.InjectEvents([new ButtonEvent(PhysicalButton.ClickPlus, true)]);
        bridge.InjectEvents([new ButtonEvent(PhysicalButton.ClickPlus, false)]);
        await Until(() => app.Sent.Count == 2);
        await Task.Delay(50);
        Assert.Equal(
            [new byte[] { 0x01, (byte)ObcButtonId.ShiftUp, 1 }, new byte[] { 0x01, (byte)ObcButtonId.ShiftUp, 0 }],
            app.Sent.ToArray());
        Assert.Equal(13, bridge.Gear.Gear);
    }

    [Fact]
    public async Task Go_to_gear_works_even_when_the_app_declares_no_gear_buttons()
    {
        await using var bridge = new BridgeService(new AppConfig { GearCount = 24, LastGear = 5 });
        var app = new FakeTransport("appli");
        await bridge.AddTransportAsync(app);
        // Appli qui ne déclare que « Retour » : la touche interne doit passer quand même.
        typeof(BridgeService).GetMethod("OnAppInfo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(bridge, [new ObcAppInfo("test", "1", [(byte)ObcButtonId.Back])]);
        bridge.Profile.Bindings[PhysicalButton.RightA] = new ButtonBinding { Actions = [ObcButtonId.GoToGear], GearTarget = 18 };

        bridge.InjectEvents([new ButtonEvent(PhysicalButton.RightA, true)]);
        Assert.Equal(18, bridge.Gear.Gear);
    }

    [Fact]
    public void The_target_gear_is_saved_in_the_profile_and_survives_a_clone()
    {
        var profile = new Profile();
        profile.Bindings[PhysicalButton.RightA] = new ButtonBinding { Actions = [ObcButtonId.GoToGear], GearTarget = 17 };
        Assert.Equal(17, profile.Clone().Bindings[PhysicalButton.RightA].GearTarget);

        string path = Path.Combine(Path.GetTempPath(), $"shiftbridge-test-{Guid.NewGuid():N}.json");
        try
        {
            ConfigStore.Save(new AppConfig { Profile = profile }, path);
            var back = ConfigStore.Load(path).Profile.Bindings[PhysicalButton.RightA];
            Assert.Equal(17, back.GearTarget);
            Assert.Equal([ObcButtonId.GoToGear], back.Actions);
        }
        finally { File.Delete(path); }
    }
}
