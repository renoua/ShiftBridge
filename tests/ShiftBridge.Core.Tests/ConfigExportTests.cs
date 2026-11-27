using ShiftBridge.Core.Config;
using ShiftBridge.Core.Input;
using ShiftBridge.Core.Mapping;
using ShiftBridge.Core.Obc;

namespace ShiftBridge.Core.Tests;

public class ConfigExportTests
{
    private static AppConfig Sample()
    {
        var c = new AppConfig { GearCount = 18, RiderWeightKg = 82, OverlayLeft = 120, OverlayTop = 40, LastGear = 11 };
        c.Profile.Bindings[PhysicalButton.RightA] = new ButtonBinding { Actions = [ObcButtonId.Emote] };
        c.AppProfiles["tpv"] = Profile.ZwiftLike();
        c.SramAxsKeys["AA:BB"] = "secret";
        return c;
    }

    [Fact]
    public void Export_round_trips_settings_and_profiles()
    {
        var current = new AppConfig();
        var back = ConfigStore.Import(ConfigStore.Export(Sample(), "0.26.0"), current);

        Assert.Equal(18, back.GearCount);
        Assert.Equal(82, back.RiderWeightKg);
        Assert.Equal([ObcButtonId.Emote], back.Profile.Bindings[PhysicalButton.RightA].Actions);
        Assert.True(back.AppProfiles.ContainsKey("tpv"));
    }

    [Fact]
    public void Export_leaves_out_secrets_and_machine_specific_values()
    {
        string json = ConfigStore.Export(Sample(), "0.26.0");
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain(nameof(AppConfig.SramAxsKeys), json);
        Assert.DoesNotContain(nameof(AppConfig.OverlayLeft), json);
        Assert.DoesNotContain(nameof(AppConfig.LastGear), json);
        Assert.Contains(ConfigStore.ExportMarker, json);
    }

    [Fact]
    public void Import_keeps_this_machines_keys_overlay_position_and_gear()
    {
        var current = new AppConfig { OverlayLeft = 5, OverlayTop = 6, LastGear = 7 };
        current.SramAxsKeys["CC:DD"] = "mine";
        var back = ConfigStore.Import(ConfigStore.Export(Sample(), "0.26.0"), current);

        Assert.Equal("mine", back.SramAxsKeys["CC:DD"]);
        Assert.False(back.SramAxsKeys.ContainsKey("AA:BB"));
        Assert.Equal((5.0, 6.0), (back.OverlayLeft, back.OverlayTop));
        Assert.Equal(7, back.LastGear);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"Something\": 1}")]                  // JSON valide, mais pas des réglages ShiftBridge
    public void Import_rejects_files_that_are_not_shiftbridge_settings(string text) =>
        Assert.Throws<FormatException>(() => ConfigStore.Import(text, new AppConfig()));

    [Fact]
    public void Import_tolerates_unknown_actions_and_out_of_range_values()
    {
        string json = """
            { "ShiftBridgeExport": "9.9.9", "GearCount": 99, "RiderWeightKg": 500,
              "Profile": { "Name": "x", "Bindings": { "RightA": { "Actions": ["FutureAction", "Emote"] } } } }
            """;
        var back = ConfigStore.Import(json, new AppConfig());

        Assert.Equal(24, back.GearCount);
        Assert.Equal(200, back.RiderWeightKg);
        Assert.Equal([ObcButtonId.Emote], back.Profile.Bindings[PhysicalButton.RightA].Actions);
    }

    [Fact]
    public void A_fresh_install_gets_the_setup_assistant_but_an_existing_config_does_not()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(ConfigStore.Load(Path.Combine(dir, "missing.json")).OnboardingCompleted);   // premier lancement

            string old = Path.Combine(dir, "old.json");                                              // fichier d'avant 0.26
            File.WriteAllText(old, """{ "GearCount": 20 }""");
            Assert.True(ConfigStore.Load(old).OnboardingCompleted);

            string current = Path.Combine(dir, "current.json");                                      // assistant pas encore terminé
            ConfigStore.Save(new AppConfig { OnboardingCompleted = false }, current);
            Assert.False(ConfigStore.Load(current).OnboardingCompleted);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Imported_settings_skip_the_setup_assistant() =>
        Assert.True(ConfigStore.Import(ConfigStore.Export(new AppConfig(), "0.26.0"), new AppConfig()).OnboardingCompleted);

    [Fact]
    public void A_plain_config_json_can_be_imported_too()
    {
        var config = Sample();
        string path = Path.Combine(Path.GetTempPath(), $"sb-{Guid.NewGuid():N}.json");
        try
        {
            ConfigStore.Save(config, path);
            var back = ConfigStore.Import(File.ReadAllText(path), new AppConfig());
            Assert.Equal(18, back.GearCount);
            Assert.Equal("secret", back.SramAxsKeys["AA:BB"]);   // un config.json complet garde ses propres clés
        }
        finally { File.Delete(path); }
    }
}
