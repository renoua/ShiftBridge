using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ShiftBridge.Core.Localization;
using ShiftBridge.Desktop;
using ShiftBridge.Platform.MacOS;
using ShiftBridge.Presentation;

namespace ShiftBridge.MacOS;

public sealed class MacApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var platform = new MacBlePlatform();
            var window = new MainWindow();
            var dispatcher = new AvaloniaDispatcher();
            var model = new MainViewModel(platform, dispatcher, new MacAppHost(window, platform),
                mdns: new BonjourServiceDiscovery());
            var previous = MacBluetoothState.Starting;
            bool closing = false;
            bool stopped = false;
            Task startup = Task.CompletedTask;

            void RefreshBluetooth()
            {
                if (closing) return;
                var state = platform.State;
                if (state == MacBluetoothState.Starting) return;
                model.SetPermissionProblem(state switch
                {
                    MacBluetoothState.Unauthorized => Strings.Mac_BluetoothDenied,
                    MacBluetoothState.Unsupported => Strings.Err_BleUnavailablePlatform,
                    _ => null
                });
                model.SetBluetoothOff(state == MacBluetoothState.Off);
                if (state == MacBluetoothState.Available && previous is MacBluetoothState.Off or MacBluetoothState.Unauthorized)
                    model.OnBluetoothTurnedOn();
                previous = state;
            }

            void OnBluetoothStateChanged() => dispatcher.Post(RefreshBluetooth);

            async Task StartAsync()
            {
                try { await model.StartAsync(); }
                catch (Exception ex) { model.ReportError(ex.Message); }
            }

            async Task StopAsync()
            {
                if (closing) return;
                closing = true;
                platform.StateChanged -= OnBluetoothStateChanged;
                try
                {
                    await startup;
                    await model.StopAsync();
                }
                finally
                {
                    await platform.DisposeAsync();
                    stopped = true;
                    desktop.Shutdown();
                }
            }

            platform.StateChanged += OnBluetoothStateChanged;
            RefreshBluetooth();
            window.DataContext = model;
            window.Opened += (_, _) => startup = StartAsync();
            window.Closing += (_, e) =>
            {
                if (stopped) return;
                e.Cancel = true;
                _ = StopAsync();
            };
            desktop.ShutdownRequested += (_, e) =>
            {
                if (stopped) return;
                e.Cancel = true;
                _ = StopAsync();
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
