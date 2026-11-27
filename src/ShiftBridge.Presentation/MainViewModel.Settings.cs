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
    // ---------- Langue de l'interface ----------

    /// <summary>Automatique (langue du système), français, anglais. Les noms des langues ne sont jamais traduits.</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } =
    [
        new(AppLanguage.Auto, Strings.Lang_Auto),
        new(AppLanguage.French, "Français"),
        new(AppLanguage.English, "English"),
    ];

    [ObservableProperty] private LanguageOption? _selectedLanguage;

    /// <summary>La langue choisie diffère de celle appliquée au démarrage : elle ne sera prise en compte qu'au redémarrage.</summary>
    [ObservableProperty] private bool _languageRestartNeeded;

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (_loading || value is null) return;
        _config.Language = value.Code == AppLanguage.Auto ? null : value.Code;
        SaveConfig();
        LanguageRestartNeeded = AppLanguage.Resolve(value.Code, AppLanguage.SystemUiCulture) != AppLanguage.ActiveLanguage;
    }

    // ---------- Application Windows : zone de notification et démarrage ----------
    // Les cases n'ont de sens que là où l'hôte a une zone de notification (SupportsTray : Windows) ; c'est MainWindow qui réagit
    // à ces propriétés (icône, registre).

    public bool SupportsTray => _host.SupportsTray;

    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startWithWindows;

    partial void OnStartMinimizedChanged(bool value) { if (!_loading) { _config.StartMinimized = value; SaveConfig(); } }
    partial void OnMinimizeToTrayChanged(bool value) { if (!_loading) { _config.MinimizeToTray = value; SaveConfig(); } }
    partial void OnStartWithWindowsChanged(bool value) { if (!_loading) { _config.StartWithWindows = value; SaveConfig(); } }
}
