using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShiftBridge.Core.Config;
using ShiftBridge.Core.Controllers;
using ShiftBridge.Core.Gears;
using ShiftBridge.Core.Input;
using ShiftBridge.Core.Localization;
using ShiftBridge.Core.Mapping;
using ShiftBridge.Core.Obc;
using ShiftBridge.Core.Trainer;
using ShiftBridge.Platform;
using ShiftBridge.Platform.Ble;
using ShiftBridge.Platform.Controllers;
using ShiftBridge.Platform.Input;
using ShiftBridge.Platform.Obc;
using ShiftBridge.Platform.Sensors;
using ShiftBridge.Platform.Trainer;

namespace ShiftBridge.Presentation;

public sealed partial class MainViewModel
{
    // ---------- Rapport affiché dans l'appli (OBC) et superposition ----------

    [ObservableProperty] private bool _reportGearToApp = true;
    [ObservableProperty] private bool _autoStartVirtualShifting = true;

    partial void OnAutoStartVirtualShiftingChanged(bool value)
    {
        if (_loading) return;
        _config.AutoStartVirtualShifting = value;
        SaveConfig();
    }
    [ObservableProperty] private bool _showOverlay;
    [ObservableProperty] private bool _overlayLocked;
    [ObservableProperty] private double _overlayScale = 1.0;
    [ObservableProperty] private string _overlayGear = "";
    [ObservableProperty] private string _overlayDetail = "";

    partial void OnReportGearToAppChanged(bool value)
    {
        if (_loading) return;
        _bridge.ReportGearToApp = value;
        SaveConfig();
    }

    // Réglages de la superposition (Windows). Elle montre la vitesse virtuelle ; en option, une barre des vitesses, un
    // clignotement à chaque changement (rouge aux butées) et la puissance, la cadence et la vitesse du trainer.
    [ObservableProperty] private double _overlayOpacity = 0.85;
    [ObservableProperty] private bool _overlayShowBar = true;
    [ObservableProperty] private bool _overlayFlash = true;
    [ObservableProperty] private bool _overlayShowTelemetry;
    [ObservableProperty] private string _overlayTelemetry = "";
    [ObservableProperty] private IReadOnlyList<GearSegment> _overlaySegments = [];

    /// <summary>Demande un clignotement de la superposition (changement normal, ou butée atteinte).</summary>
    public event Action<GearChange>? GearFlashRequested;

    partial void OnShowOverlayChanged(bool value)
    {
        UpdateTelemetryLoop();
        if (!_loading) { _config.ShowOverlay = value; SaveConfig(); }
    }
    partial void OnOverlayLockedChanged(bool value) { if (!_loading) { _config.OverlayLocked = value; SaveConfig(); } }
    partial void OnOverlayScaleChanged(double value) { if (!_loading) { _config.OverlayScale = value; Debounce(ref _opacitySaveCts, SaveConfig); } } // une écriture par cran de molette serait inutile
    partial void OnOverlayOpacityChanged(double value) { if (!_loading) { _config.OverlayOpacity = value; Debounce(ref _opacitySaveCts, SaveConfig); } }
    partial void OnOverlayShowBarChanged(bool value) { if (!_loading) { _config.OverlayShowBar = value; SaveConfig(); } }
    partial void OnOverlayFlashChanged(bool value) { if (!_loading) { _config.OverlayFlash = value; SaveConfig(); } }
    partial void OnOverlayShowTelemetryChanged(bool value)
    {
        UpdateTelemetryLoop();
        if (!_loading) { _config.OverlayShowTelemetry = value; SaveConfig(); }
    }

    private CancellationTokenSource? _opacitySaveCts, _telemetryCts;

    /// <summary>Texte puissance · cadence · cardio · vitesse en direct (bloc principal de l'Accueil), vide tant que le proxy est arrêté.</summary>
    [ObservableProperty] private string _liveTelemetry = "";

    private bool OverlayWantsTelemetry => ShowOverlay && OverlayShowTelemetry;

    /// <summary>
    /// Rafraîchit une fois par seconde le texte puissance · cadence · cardio · vitesse, seulement tant que le proxy tourne (Accueil)
    /// ou que la superposition l'affiche. Sans effet si la boucle est déjà dans le bon état : appelée à chaque RefreshSummaries.
    /// </summary>
    private void UpdateTelemetryLoop()
    {
        bool wanted = _bridge.TrainerProxyActive || OverlayWantsTelemetry;
        if (wanted && _telemetryCts is not null) { OverlayTelemetry = OverlayWantsTelemetry ? LiveTelemetry : ""; return; }
        _telemetryCts?.Cancel();
        _telemetryCts = null;
        LiveTelemetry = "";
        OverlayTelemetry = "";
        if (!wanted) return;
        var cts = _telemetryCts = new CancellationTokenSource();
        _ = TelemetryLoopAsync(cts.Token);
    }

    private async Task TelemetryLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct))
                _ui.Post(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    LiveTelemetry = BuildTelemetry();
                    OverlayTelemetry = OverlayWantsTelemetry ? LiveTelemetry : "";
                });
        }
        catch (OperationCanceledException) { }
    }

    private string BuildTelemetry()
    {
        if (!_bridge.TrainerProxyActive) return "";
        var parts = new List<string>();
        if (_bridge.TrainerPowerW is { } watts) parts.Add($"{watts} W");
        if (_bridge.TrainerCadenceRpm is { } rpm) parts.Add(Loc.Fmt(Strings.Unit_Rpm, rpm));
        if (_bridge.HeartRateBpm is { } bpm) parts.Add(Loc.Fmt(Strings.Unit_Bpm, bpm));
        double kmh = _bridge.TrainerDisplaySpeedMs * 3.6;
        if (kmh >= 0.1) parts.Add($"{kmh:0.0} km/h");
        return string.Join("  ·  ", parts);
    }

    public double? SavedOverlayLeft => _config.OverlayLeft;
    public double? SavedOverlayTop => _config.OverlayTop;

    public void SaveOverlayPosition(double left, double top)
    {
        _config.OverlayLeft = left;
        _config.OverlayTop = top;
        SaveConfig();
    }

    private void UpdateOverlay()
    {
        string mode = !_bridge.TrainerProxyActive ? Strings.Overlay_ProxyStopped
            : _bridge.AppHandlesShifting ? Strings.Overlay_AppShifts
            : _bridge.TrainerNativeActive ? Strings.Overlay_Native : "FTMS";
        OverlayGear = _bridge.Gear.Gear.ToString();
        int current = _bridge.Gear.Gear, count = _bridge.Gear.GearCount;
        // Appelé plusieurs fois par changement de vitesse : la barre n'est recréée (et redessinée) que si elle a changé.
        if ((current, count) != _overlaySegmentsFor)
        {
            _overlaySegmentsFor = (current, count);
            OverlaySegments = [.. Enumerable.Range(1, count).Select(i => new GearSegment(i, i == current, i < current))];
        }
        OverlayDetail = $"/ {_bridge.Gear.GearCount}  ·  {_bridge.GearRatio:0.00}  ·  {mode}";
        GearRatioText = Loc.Fmt(Strings.Gear_Ratio, _bridge.GearRatio);
        // Des phrases plutôt que « Natif / Compatible » : le premier mot ne dit rien à qui ne connaît pas les protocoles.
        ShiftModeText = !_bridge.TrainerProxyActive ? Strings.Mode_Stopped
            : _bridge.AppHandlesShifting ? Strings.Mode_AppShifts
            : _bridge.TrainerNativeActive ? Strings.Mode_ByTrainer : Strings.Mode_ByApp;
        IsGearApplied = _bridge.TrainerProxyActive && !_bridge.AppHandlesShifting;
    }

    private (int Gear, int Count) _overlaySegmentsFor = (-1, -1);

    /// <summary>La vitesse affichée est réellement appliquée au trainer (virtual shifting actif).</summary>
    [ObservableProperty] private bool _isGearApplied;

    // Vitesse + / − hors manette (boutons de la notification Android) : mêmes effets qu'un appui de manette.
    [RelayCommand]
    private void ShiftUp() => _bridge.Shift(ObcButtonId.ShiftUp);

    [RelayCommand]
    private void ShiftDown() => _bridge.Shift(ObcButtonId.ShiftDown);

    /// <summary>Vitesse centrale (menu de la zone de notification).</summary>
    [RelayCommand]
    private void CenterGear() => _bridge.Shift(ObcButtonId.CenterGear);
}
