using CommunityToolkit.Mvvm.ComponentModel;
using ShiftBridge.Core.Input;
using ShiftBridge.Core.Localization;
using ShiftBridge.Core.Mapping;
using ShiftBridge.Core.Obc;
using ShiftBridge.Core.Zwift;
using ShiftBridge.Platform.Controllers;

namespace ShiftBridge.Presentation;

public sealed record ActionItem(ObcButtonId Id, string Category, string Label)
{
    public string Display => Id == ObcButtonId.None ? Label : $"{Category} · {Label}";
}

public sealed record ButtonGroupViewModel(string Name, IReadOnlyList<ButtonRowViewModel> Rows);

/// <summary>Un profil proposé dans l'onglet Touches (<see cref="Key"/> vide = profil par défaut).</summary>
public sealed record ProfileOption(string Key, string Name)
{
    /// <summary>Le texte affiché par les listes déroulantes qui n'appliquent pas DisplayMemberPath (gabarit du thème).</summary>
    public override string ToString() => Name;
}

/// <summary>Une langue proposée dans le sélecteur (<see cref="Code"/> : « auto », « fr » ou « en »).</summary>
public sealed record LanguageOption(string Code, string Name)
{
    /// <summary>Le texte affiché par les listes déroulantes qui n'appliquent pas DisplayMemberPath (gabarit du thème).</summary>
    public override string ToString() => Name;
}

/// <summary>Une vitesse de répétition proposée dans l'onglet Touches.</summary>
public sealed record RepeatSpeedOption(RepeatSpeed Speed, string Name)
{
    /// <summary>Le texte affiché par les listes déroulantes qui n'appliquent pas DisplayMemberPath (gabarit du thème).</summary>
    public override string ToString() => Name;
}

/// <summary>Un cran de la barre des vitesses de la superposition.</summary>
public sealed record GearSegment(int Number, bool IsCurrent, bool IsBelow);

/// <summary>Une ligne de l'onglet « Touches » : touche physique, action choisie, voyant d'appui en direct.</summary>
public sealed partial class ButtonRowViewModel : ObservableObject
{
    private readonly Action<ButtonRowViewModel> _changed;

    public ButtonRowViewModel(ButtonKey button, string group, string label, string? tooltip,
        IReadOnlyList<ActionItem> actions, ActionItem? selected, Action<ButtonRowViewModel> changed, bool repeatOnHold = false,
        int gearTarget = 0, int gearSteps = 0)
    {
        Button = button; Group = group; Label = label; Tooltip = tooltip; Actions = actions; _changed = changed;
        SetSilently(selected, repeatOnHold, gearTarget, gearSteps);
    }

    public ButtonKey Button { get; }
    /// <summary>Touches clavier/manette ajoutées par l'utilisateur : supprimables (les touches Zwift sont fixes).</summary>
    public bool CanRemove { get; init; }
    /// <summary>Actions proposées pour cette touche (la même liste pour toutes les lignes) : évite aux vues de remonter au parent.</summary>
    public IReadOnlyList<ActionItem> Actions { get; }
    public string Group { get; }
    public string Label { get; }
    /// <summary>Détail de compatibilité (modèles de manette), affiché en info-bulle.</summary>
    public string? Tooltip { get; }

    [ObservableProperty] private ActionItem? _selected;
    [ObservableProperty] private bool _isPressed;
    /// <summary>Répète la vitesse tant que la touche est tenue (seulement pour « vitesse + » et « vitesse − »).</summary>
    [ObservableProperty] private bool _repeatOnHold;

    /// <summary>L'option « répéter » n'a de sens que pour une action de vitesse.</summary>
    public bool CanRepeat => Selected is { Id: ObcButtonId.ShiftUp or ObcButtonId.ShiftDown };

    /// <summary>Texte saisi pour « Aller au rapport » (champ numérique) ; <see cref="GearTarget"/> en est la valeur utilisable.</summary>
    [ObservableProperty] private string _gearTargetText = "";

    /// <summary>Rapport visé par « Aller au rapport » (1..24), 0 tant que le champ est vide ou illisible.</summary>
    public int GearTarget { get; private set; }

    /// <summary>Le champ « rapport visé » n'a de sens que pour l'action « Aller au rapport ».</summary>
    public bool CanTarget => Selected is { Id: ObcButtonId.GoToGear };

    /// <summary>Rapport proposé quand on choisit « Aller au rapport » : le rapport central.</summary>
    private const int DefaultGearTarget = 12;

    /// <summary>Texte saisi pour « Monter / Descendre de X vitesses » ; <see cref="GearSteps"/> en est la valeur utilisable.</summary>
    [ObservableProperty] private string _gearStepsText = "";

    /// <summary>Nombre de vitesses (1..23), 0 tant que le champ est vide ou illisible.</summary>
    public int GearSteps { get; private set; }

    /// <summary>Le champ « nombre de vitesses » n'a de sens que pour « Monter / Descendre de X vitesses ».</summary>
    public bool CanStep => Selected is { Id: ObcButtonId.ShiftUpBy or ObcButtonId.ShiftDownBy };

    /// <summary>Nombre de vitesses proposé quand on choisit « Monter / Descendre de X vitesses ».</summary>
    private const int DefaultGearSteps = 2;

    private bool _silent;

    partial void OnGearTargetTextChanged(string value)
    {
        GearTarget = int.TryParse(value, out int n) ? Math.Clamp(n, 1, 24) : 0;
        if (!_silent) _changed(this);
    }

    partial void OnGearStepsTextChanged(string value)
    {
        GearSteps = int.TryParse(value, out int n) ? Math.Clamp(n, 1, 23) : 0;
        if (!_silent) _changed(this);
    }

    partial void OnSelectedChanged(ActionItem? value)
    {
        OnPropertyChanged(nameof(CanRepeat));
        OnPropertyChanged(nameof(CanTarget));
        OnPropertyChanged(nameof(CanStep));
        bool wasSilent = _silent;
        _silent = true;
        try
        {
            if (!CanRepeat && RepeatOnHold)
                RepeatOnHold = false; // une touche qui n'est plus « vitesse » ne doit pas garder une répétition invisible
            if (CanTarget && GearTarget == 0)
                GearTargetText = DefaultGearTarget.ToString();
            if (CanStep && GearSteps == 0)
                GearStepsText = DefaultGearSteps.ToString();
        }
        finally { _silent = wasSilent; }
        if (!_silent) _changed(this);
    }

    partial void OnRepeatOnHoldChanged(bool value)
    {
        if (!_silent) _changed(this);
    }

    /// <summary>Met à jour la sélection sans redéclencher la sauvegarde (rechargement de profil).</summary>
    public void SetSilently(ActionItem? item, bool repeatOnHold = false, int gearTarget = 0, int gearSteps = 0)
    {
        _silent = true;
        try
        {
            GearTargetText = gearTarget > 0 ? gearTarget.ToString() : "";
            GearStepsText = gearSteps > 0 ? gearSteps.ToString() : "";
            Selected = item;
            RepeatOnHold = repeatOnHold && CanRepeat;
        }
        finally { _silent = false; }
    }
}

/// <summary>Un clavier, une manette de jeu ou un joystick détecté (sources locales).</summary>
public sealed record LocalDeviceRowViewModel(string Name, string KindText, bool CanVibrate);

/// <summary>Un appareil BLE vu par l'inspecteur de la page Diagnostic.</summary>
public sealed class InspectedDeviceRowViewModel(ShiftBridge.Platform.Ble.InspectedDevice device, bool isCapturing)
{
    public ShiftBridge.Platform.Ble.InspectedDevice Device { get; } = device;
    public bool IsCapturing { get; } = isCapturing;
    public string Title => $"{Device.Name} · {Device.Rssi} dBm";
    public string Detail => string.IsNullOrEmpty(Device.Details) ? Device.AddressText : $"{Device.AddressText} · {Device.Details}";
    public string ActionText => IsCapturing ? Strings.Inspect_StopCapture : Strings.Inspect_Capture;
}

/// <summary>Une manette détectée dans l'onglet « Appareils ».</summary>
public sealed partial class DeviceRowViewModel : ObservableObject
{
    public DeviceRowViewModel(DiscoveredController device, IController? controller, string? customName = null)
    {
        Device = device;
        Controller = controller;
        CustomName = string.IsNullOrWhiteSpace(customName) ? null : customName.Trim();
    }

    public DiscoveredController Device { get; }
    public IController? Controller { get; }
    /// <summary>Nom donné par l'utilisateur (null = nom du modèle).</summary>
    public string? CustomName { get; }

    /// <summary>Nom du modèle, sans personnalisation.</summary>
    public string ModelTitle => Device is DiscoveredZwiftDevice z
        ? $"{ModelName(z.Type.Model)}{SideText(z)}"
        : $"{Loc.Get("Model_" + Device.Kind)} ({Strings.Dev_Beta})";
    public string Title => CustomName ?? ModelTitle;
    /// <summary>Détail technique (modèle si renommée, adresse, signal, micrologiciel) : infobulle de la ligne sous Windows, titre du menu « ⋮ » sous Android.</summary>
    public string Detail
    {
        get
        {
            string text = Loc.Fmt(Strings.Dev_DetailSignal, Device.AddressText, Device.Rssi);
            if (Controller?.FirmwareVersion is { Length: > 0 } fw) text = Loc.Fmt(Strings.Dev_DetailFirmware, text, fw);
            return CustomName is null ? text : $"{ModelTitle} · {text}";
        }
    }
    public int? BatteryPercent => Controller?.BatteryPercent;
    public bool HasBattery => BatteryPercent.HasValue;

    /// <summary>Vrai si les deux lignes affichent exactement la même chose (la liste n'a alors pas à remplacer la ligne).</summary>
    public bool ShowsSameAs(DeviceRowViewModel other) =>
        Device == other.Device && ReferenceEquals(Controller, other.Controller) && CustomName == other.CustomName
        && State == other.State && BatteryPercent == other.BatteryPercent && Controller?.FirmwareVersion == other.Controller?.FirmwareVersion;

    public UiState State => Controller?.State switch
    {
        null => UiState.Off,
        ControllerState.Connected => UiState.Ok,
        ControllerState.Connecting => UiState.Waiting,
        ControllerState.Locked => UiState.Warning,
        ControllerState.Error => UiState.Error,
        _ => UiState.Warning,
    };
    public bool IsConnected => Controller is not null;
    /// <summary>AXS en mode avancé, connecté mais sans clé valable : le bouton « Appairer » est proposé.</summary>
    public bool CanPair => Controller is SramAxsAdvancedController { CanPair: true };
    public bool CanVibrate => Controller is { CanVibrate: true, State: ControllerState.Connected };
    public string ActionText => IsConnected ? Strings.Dev_Disconnect : Strings.Dev_Connect;

    public string StateText => Controller?.State switch
    {
        null => Strings.Dev_NotConnected,
        ControllerState.Connecting => Strings.Dev_Connecting,
        ControllerState.Connected => Strings.Dev_Connected,
        ControllerState.Locked => Strings.Dev_Locked,
        ControllerState.Error => Strings.Dev_Error,
        _ => Strings.Dev_Disconnected,
    };

    public string BatteryText => Controller?.BatteryPercent is { } b ? Loc.Fmt(Strings.Dev_BatteryPct, b) : "";

    private static string SideText(DiscoveredZwiftDevice z) => z.Type.Side switch { ZwiftSide.Left => " " + Strings.Dev_SideLeft, ZwiftSide.Right => " " + Strings.Dev_SideRight, _ => "" };

    private static string ModelName(ZwiftModel m) => m switch
    {
        ZwiftModel.Play => "Zwift Play",
        ZwiftModel.Click => "Zwift Click",
        ZwiftModel.ClickV2 => "Zwift Click v2",
        ZwiftModel.Ride => "Zwift Ride",
        _ => Strings.Group_Zwift,
    };
}
