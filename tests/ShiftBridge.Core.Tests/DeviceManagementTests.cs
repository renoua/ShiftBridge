using ShiftBridge.Core.Config;
using ShiftBridge.Core.Controllers;
using ShiftBridge.Core.Trainer;
using ShiftBridge.Platform;
using ShiftBridge.Platform.Ble;

namespace ShiftBridge.Core.Tests;

public class DeviceManagementTests
{
    private const ulong Address = 0xAABBCCDDEEFF;

    [Fact]
    public async Task Forgetting_a_disconnected_controller_clears_everything_remembered_about_it()
    {
        string text = BleAddress.ToText(Address);
        var config = new AppConfig();
        config.KnownDevices.Add(text);
        config.KnownControllerKinds[text] = ControllerKind.ThinkriderVs200;
        config.DeviceNames[text] = "Guidon";
        config.KnownDevices.Add("autre");

        await using var bridge = new BridgeService(config);
        await bridge.DisconnectAsync(Address, forget: true); // aucune connexion : l'oubli doit tout de même aboutir

        Assert.Equal(["autre"], config.KnownDevices);
        Assert.Empty(config.KnownControllerKinds);
        Assert.Empty(config.DeviceNames);
    }

    [Fact]
    public async Task Disconnect_without_forget_keeps_the_memory()
    {
        string text = BleAddress.ToText(Address);
        var config = new AppConfig();
        config.KnownDevices.Add(text);
        config.DeviceNames[text] = "Guidon";

        await using var bridge = new BridgeService(config);
        await bridge.DisconnectAsync(Address);

        Assert.Contains(text, config.KnownDevices);
        Assert.Equal("Guidon", config.DeviceNames[text]);
    }

    [Fact]
    public async Task Device_name_is_trimmed_capped_and_removed_when_empty()
    {
        var config = new AppConfig();
        await using var bridge = new BridgeService(config);

        bridge.SetDeviceName("A", "  Gauche  ");
        Assert.Equal("Gauche", bridge.CustomName("A"));

        bridge.SetDeviceName("A", new string('x', 100));
        Assert.Equal(40, bridge.CustomName("A")!.Length);

        bridge.SetDeviceName("A", "   ");
        Assert.Null(bridge.CustomName("A"));
    }

    [Fact]
    public void Config_normalize_drops_blank_device_names_and_caps_long_ones()
    {
        var config = new AppConfig { DeviceNames = new() { ["A"] = "  ", ["B"] = new string('y', 80), ["C"] = " ok " } };
        config.Normalize();
        Assert.False(config.DeviceNames.ContainsKey("A"));
        Assert.Equal(40, config.DeviceNames["B"].Length);
        Assert.Equal("ok", config.DeviceNames["C"]);
    }

    [Theory]
    [InlineData("187723572702975", "AA:BB:CC:DD:EE:FF")]
    [InlineData("af8261aa-5d30-4875-ba57-279a823a65bb", "af8261aa-5d30-4875-ba57-279a823a65bb")]
    public void Saved_trainer_and_heart_rate_identifiers_keep_their_config_format(string saved, string display)
    {
        var path = Path.Combine(Path.GetTempPath(), $"shiftbridge-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, $$"""
                {
                  "TrainerBleAddress": "{{saved}}",
                  "HeartRateBleAddress": "{{saved}}"
                }
                """);
            var config = ConfigStore.Load(path);

            Assert.True(BleDeviceId.TryParseSavedAddress(config.TrainerBleAddress, out var trainer));
            Assert.True(BleDeviceId.TryParseSavedAddress(config.HeartRateBleAddress, out var sensor));
            Assert.Equal(trainer, sensor);
            Assert.Equal(display, trainer.ToString());
            Assert.Equal(saved, trainer.ToSavedAddress());
            Assert.Equal(saved, sensor.ToSavedAddress());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Service_data_helper_handles_16_32_and_128_bit_uuids_and_ignores_others()
    {
        var target = new Dictionary<Guid, byte[]>();
        BleAdvertisementParser.AddServiceData(0x16, [0x26, 0x18, 0x01, 0x02], target);
        BleAdvertisementParser.AddServiceData(0x20, [0x0F, 0x18, 0x00, 0x00, 0x55], target);
        BleAdvertisementParser.AddServiceData(0x16, [0x26], target);   // trop court
        BleAdvertisementParser.AddServiceData(0x09, [0x41, 0x42], target); // pas des données de service

        Assert.Equal(new byte[] { 0x01, 0x02 }, target[FtmsCodec.FitnessMachineService]);
        Assert.Equal(new byte[] { 0x55 }, target[FtmsCodec.BaseUuid(0x180F)]);
        Assert.Equal(2, target.Count);
    }
}
