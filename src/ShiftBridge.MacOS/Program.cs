using Avalonia;
using ShiftBridge.Core.Config;
using ShiftBridge.Core.Localization;

namespace ShiftBridge.MacOS;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppLanguage.Apply(ConfigStore.Load().Language);
        AppBuilder.Configure<MacApp>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }
}
