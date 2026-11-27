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
    // ---------- Touches ----------

    // Clés de ressources (groupe, libellé, info-bulle) : résolues à la construction, dans la langue courante.
    private static readonly (PhysicalButton B, string Group, string Label, string? Tip)[] ButtonLayout =
    [
        (PhysicalButton.ClickPlus, "BtnGroup_Click", "Btn_Plus", null),
        (PhysicalButton.ClickMinus, "BtnGroup_Click", "Btn_Minus", null),
        (PhysicalButton.RightShiftUp, "BtnGroup_Right", "Btn_SideOrShiftUp", "Tip_RightShiftUp"),
        (PhysicalButton.RightShiftDown, "BtnGroup_Right", "Btn_ShiftDown", "Tip_ClickV2"),
        (PhysicalButton.RightPaddle, "BtnGroup_Right", "Btn_Paddle", null),
        (PhysicalButton.RightY, "BtnGroup_Right", "Btn_Y", null),
        (PhysicalButton.RightZ, "BtnGroup_Right", "Btn_Z", null),
        (PhysicalButton.RightA, "BtnGroup_Right", "Btn_A", null),
        (PhysicalButton.RightB, "BtnGroup_Right", "Btn_B", null),
        (PhysicalButton.RightPowerUp, "BtnGroup_Right", "Btn_PowerUp", null),
        (PhysicalButton.RightOnOff, "BtnGroup_Right", "Btn_OnOff", null),
        (PhysicalButton.LeftShiftUp, "BtnGroup_Left", "Btn_ShiftUp", "Tip_ClickV2"),
        (PhysicalButton.LeftShiftDown, "BtnGroup_Left", "Btn_SideOrShiftDown", "Tip_LeftShiftDown"),
        (PhysicalButton.LeftPaddle, "BtnGroup_Left", "Btn_Paddle", null),
        (PhysicalButton.LeftUp, "BtnGroup_Left", "Pad_DPadUp", null),
        (PhysicalButton.LeftDown, "BtnGroup_Left", "Pad_DPadDown", null),
        (PhysicalButton.LeftLeft, "BtnGroup_Left", "Pad_DPadLeft", null),
        (PhysicalButton.LeftRight, "BtnGroup_Left", "Pad_DPadRight", null),
        (PhysicalButton.LeftPowerUp, "BtnGroup_Left", "Btn_PowerUp", null),
        (PhysicalButton.LeftOnOff, "BtnGroup_Left", "Btn_OnOff", null),
    ];

    private static string LocalInputGroup => Strings.Btn_GroupLocal;

    private void BuildButtonRows()
    {
        foreach (var (b, group, label, tip) in ButtonLayout)
        {
            ButtonKey key = b;
            var row = new ButtonRowViewModel(key, Loc.Get(group), Loc.Get(label), tip is null ? null : Loc.Get(tip), Actions,
                ItemFor(key), OnRowChanged, RepeatFor(key), TargetFor(key), StepsFor(key));
            _rowByButton[key] = row;
            ButtonRows.Add(row);
        }
        // Touches clavier / manette / joystick déjà assignées dans le profil.
        if (HasLocalInput)
            foreach (var key in _editProfile.Bindings.Keys.Where(k => k.IsLocalInput).OrderBy(k => k.Id, StringComparer.Ordinal))
                AddLocalRow(key, rebuild: false);
        AddControllerRows(); // appelle RebuildButtonGroups si besoin
        RebuildButtonGroups();
    }

    private void RebuildButtonGroups() =>
        ButtonGroups = [.. ButtonRows.GroupBy(r => r.Group).Select(g => new ButtonGroupViewModel(g.Key, [.. g]))];

    private IReadOnlyList<ButtonGroupViewModel> _buttonGroups = [];

    /// <summary>Les mêmes lignes regroupées par manette (gauche/droite…), pour les interfaces sans liste groupée native.</summary>
    public IReadOnlyList<ButtonGroupViewModel> ButtonGroups
    {
        get => _buttonGroups;
        private set { _buttonGroups = value; OnPropertyChanged(); }
    }

    // ---- Manettes BLE non Zwift : leurs touches n'apparaissent qu'une fois le modèle connu ----

    private readonly HashSet<ControllerKind> _shownKinds = [];

    private HashSet<ControllerKind> KindsToShow()
    {
        HashSet<ControllerKind> kinds = [];
        lock (_config.SyncRoot) foreach (var k in _config.KnownControllerKinds.Values) kinds.Add(k);
        foreach (var c in _bridge.Controllers) kinds.Add(c.Info.Kind);
        foreach (var key in _editProfile.Bindings.Keys.ToArray())
            if (key.TryAsController(out var kind, out _)) kinds.Add(kind);
        kinds.Remove(ControllerKind.Zwift);
        return kinds;
    }

    /// <summary>Ajoute les lignes de touches des modèles de manette connus (une seule fois par modèle). Vrai si des lignes ont été ajoutées.</summary>
    private bool AddControllerRows()
    {
        bool added = false;
        foreach (var kind in KindsToShow().OrderBy(k => k))
        {
            if (!_shownKinds.Add(kind)) continue;
            foreach (var (name, labelKey) in kind.Buttons())
            {
                if (kind == ControllerKind.SramAxs && !IsAxsButtonForMode(name, _config.SramAxsAdvanced)) continue;
                ButtonKey key = ButtonKey.Controller(kind, name);
                var row = new ButtonRowViewModel(key, Loc.Get("Model_" + kind), Loc.Get(labelKey), null, Actions, ItemFor(key),
                    OnRowChanged, RepeatFor(key), TargetFor(key), StepsFor(key));
                _rowByButton[key] = row;
                ButtonRows.Add(row);
            }
            added = true;
        }
        if (added) RebuildButtonGroups();
        return added;
    }

    /// <summary>
    /// Touches SRAM AXS réellement émises dans le mode choisi : simple/double appui en mode simple, pignon plus petit/plus grand
    /// (Up/Down) en mode avancé. Afficher les autres ne ferait que des lignes qui ne réagissent jamais.
    /// </summary>
    public static bool IsAxsButtonForMode(string name, bool advanced) =>
        advanced ? name is "Up" or "Down" : name is "Single" or "Double";

    /// <summary>Reconstruit les lignes AXS après un changement de mode (les assignations du profil ne sont pas touchées).</summary>
    private void RefreshAxsRows()
    {
        if (!_shownKinds.Remove(ControllerKind.SramAxs)) return;
        foreach (var row in ButtonRows.Where(r => r.Button.TryAsController(out var k, out _) && k == ControllerKind.SramAxs).ToList())
            RemoveRow(row);
        if (!AddControllerRows()) RebuildButtonGroups();
    }

    private ActionItem? ItemFor(ButtonKey b)
    {
        var id = _editProfile.Bindings.TryGetValue(b, out var binding) && binding.Actions.Count > 0
            ? binding.Actions[0] : ObcButtonId.None;
        return Actions.FirstOrDefault(a => a.Id == id) ?? Actions[0];
    }

    private bool RepeatFor(ButtonKey b) =>
        _editProfile.Bindings.TryGetValue(b, out var binding) && binding.RepeatOnHold;

    private int TargetFor(ButtonKey b) =>
        _editProfile.Bindings.TryGetValue(b, out var binding) ? binding.GearTarget : 0;

    private int StepsFor(ButtonKey b) =>
        _editProfile.Bindings.TryGetValue(b, out var binding) ? binding.GearSteps : 0;

    private void OnRowChanged(ButtonRowViewModel row)
    {
        if (_loading) return;
        if (row.Selected is null || row.Selected.Id == ObcButtonId.None)
            _editProfile.RemoveBinding(row.Button);
        else
        {
            // Les délais par touche ne servent plus (la vitesse de répétition est globale) : on les conserve tels quels.
            var previous = _editProfile.Bindings.GetValueOrDefault(row.Button);
            _editProfile.SetBinding(row.Button, new ButtonBinding
            {
                Actions = [row.Selected.Id],
                RepeatOnHold = row.RepeatOnHold && row.CanRepeat,
                RepeatDelayMs = previous?.RepeatDelayMs ?? 500,
                RepeatIntervalMs = previous?.RepeatIntervalMs ?? 250,
                GearTarget = row.CanTarget ? row.GearTarget : 0,
                GearSteps = row.CanStep ? row.GearSteps : 0,
            });
        }
        SaveConfig();
        _bridge.RefreshLocalInput(); // la liste des touches écoutées / avalées dépend des assignations
        RefreshSummaries();
    }

    [RelayCommand]
    private async Task ResetProfileAsync()
    {
        if (!await _host.ConfirmAsync(Strings.Profile_ConfirmReset, Strings.Buttons_Reset)) return;
        _bridge.ResetProfile(_editKey); // un profil neuf remplace l'ancien d'un bloc : le mapping ne voit jamais un état à moitié vidé
        _editProfile = _bridge.GetProfile(_editKey);
        ReloadRows(); // les touches clavier/manette ajoutées disparaissent avec leurs assignations
        SaveConfig();
        _bridge.RefreshLocalInput();
        RefreshSummaries();
    }

    /// <summary>Recharge les lignes de l'onglet Touches d'après le profil édité (changement de profil, réinitialisation).</summary>
    private void ReloadRows()
    {
        foreach (var r in ButtonRows.Where(r => r.CanRemove).ToList()) RemoveRow(r, rebuild: false);
        if (HasLocalInput)
            foreach (var key in _editProfile.Bindings.Keys.Where(k => k.IsLocalInput).OrderBy(k => k.Id, StringComparer.Ordinal))
                AddLocalRow(key, rebuild: false);
        foreach (var r in ButtonRows.Where(r => !r.CanRemove))
            r.SetSilently(ItemFor(r.Button), RepeatFor(r.Button), TargetFor(r.Button), StepsFor(r.Button));
        RebuildButtonGroups();
    }

    // ---------- Profils par appli ----------

    /// <summary>« Par défaut » puis un profil par appli d'entraînement : celui qu'on modifie dans l'onglet Touches.</summary>
    public ObservableCollection<ProfileOption> ProfileOptions { get; } = [];

    [ObservableProperty] private ProfileOption? _selectedProfileOption;
    /// <summary>Profil que ShiftBridge utilise en ce moment (celui de l'appli connectée, ou le profil par défaut).</summary>
    [ObservableProperty] private string _activeProfileText = "";
    [ObservableProperty] private string _createAppProfileText = "";
    /// <summary>L'appli que ShiftBridge a détectée (nom annoncé dans App Info), ou pourquoi aucun profil ne peut lui être proposé.</summary>
    [ObservableProperty] private string _connectedAppText = "";
    /// <summary>Une appli est connectée et n'a pas encore de profil : on propose d'en créer un.</summary>
    [ObservableProperty] private bool _canCreateAppProfile;
    /// <summary>Le profil modifié est celui d'une appli (le profil par défaut ne se supprime pas).</summary>
    [ObservableProperty] private bool _canDeleteProfile;
    private bool _profileRefreshing;

    partial void OnSelectedProfileOptionChanged(ProfileOption? value)
    {
        if (_profileRefreshing || value is null || value.Key == _editKey) return;
        _editKey = value.Key;
        _editProfile = _bridge.GetProfile(_editKey);
        CanDeleteProfile = _editKey.Length > 0;
        ReloadRows();
    }

    private void RefreshProfiles()
    {
        var apps = _bridge.AppProfiles;
        _profileRefreshing = true;
        try
        {
            if (_editKey.Length > 0 && !apps.ContainsKey(_editKey))
            {
                _editKey = ""; // le profil modifié a disparu : retour au profil par défaut
                _editProfile = _bridge.DefaultProfile;
                ReloadRows();
            }
            // Sur place : la liste déroulante garde sa sélection (elle était vidée à chaque changement de profil actif).
            List<ProfileOption> options = [new ProfileOption("", Strings.Profile_Default)];
            foreach (var (key, profile) in apps.OrderBy(kv => kv.Value.Name, StringComparer.CurrentCultureIgnoreCase))
                options.Add(new ProfileOption(key, profile.Name));
            ListMerge.Apply(ProfileOptions, options, o => o.Key, (a, b) => a == b);
            SelectedProfileOption = ProfileOptions.First(o => o.Key == _editKey);
            CanDeleteProfile = _editKey.Length > 0;
        }
        finally { _profileRefreshing = false; }

        string active = _bridge.ActiveProfileKey;
        ActiveProfileText = Loc.Fmt(Strings.Profile_Active, active.Length > 0 && apps.TryGetValue(active, out var current) ? current.Name : Strings.Profile_Default);
        string connected = _bridge.ConnectedAppId;
        CanCreateAppProfile = connected.Length > 0 && !apps.ContainsKey(_bridge.ConnectedAppKey);
        CreateAppProfileText = Loc.Fmt(Strings.Profile_Create, connected);
        ConnectedAppText = connected.Length > 0 ? Loc.Fmt(Strings.Profile_AppConnected, connected) : Strings.Profile_AppNone;
    }

    [RelayCommand]
    private void CreateAppProfile()
    {
        if (_bridge.CreateAppProfile(_bridge.ConnectedAppId) is not { } key) return;
        _editKey = key;
        _editProfile = _bridge.GetProfile(key);
        ReloadRows();
        SaveConfig();
        RefreshProfiles();
        RefreshSummaries();
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (_editKey.Length == 0) return;
        if (!await _host.ConfirmAsync(Strings.Profile_ConfirmDelete, Strings.Confirm_Delete)) return;
        string key = _editKey;
        _editKey = "";
        _editProfile = _bridge.DefaultProfile;
        ReloadRows();
        _bridge.RemoveAppProfile(key);
        SaveConfig();
        RefreshProfiles();
        RefreshSummaries();
    }

    // ---------- Clavier, télécommandes et manettes de jeu ----------

    /// <summary>Faux sur Android : l'interface masque alors tout ce qui touche au clavier et aux manettes de jeu.</summary>
    public bool HasLocalInput { get; }

    public ObservableCollection<LocalDeviceRowViewModel> LocalDevices { get; } = [];

    [ObservableProperty] private bool _keyboardInputEnabled;
    [ObservableProperty] private bool _gamepadInputEnabled;
    [ObservableProperty] private bool _suppressBoundKeys;
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private string _inputCaptureStatus = "";

    partial void OnKeyboardInputEnabledChanged(bool value) => ApplyLocalInputSetting(() => _config.KeyboardInputEnabled = value);
    partial void OnGamepadInputEnabledChanged(bool value) => ApplyLocalInputSetting(() => _config.GamepadInputEnabled = value);
    partial void OnSuppressBoundKeysChanged(bool value) => ApplyLocalInputSetting(() => _config.SuppressBoundKeys = value);

    private void ApplyLocalInputSetting(Action set)
    {
        if (_loading) return;
        set();
        SaveConfig();
        _bridge.RefreshLocalInput();
        if (IsCapturing && !KeyboardInputEnabled && !GamepadInputEnabled) CancelCapture();
        RefreshSummaries(); // l'étape 1 du bandeau de l'Accueil compte le clavier et la manette de jeu
    }

    private void RefreshLocalDevices()
    {
        var rows = _bridge.LocalInput.Devices.Select(d => new LocalDeviceRowViewModel(d.Name, d.Kind switch
        {
            LocalInputKind.Keyboard => Strings.Input_KindKeyboard,
            LocalInputKind.Gamepad => Strings.Input_KindGamepad,
            _ => Strings.Input_KindJoystick,
        }, d.CanVibrate)).ToList();
        // Sur place (record : la ligne est sa propre clé) ; deux périphériques identiques restent deux lignes.
        ListMerge.Apply(LocalDevices, rows, r => r, (a, b) => a == b);
    }

    /// <param name="rebuild">Faux dans une boucle : l'appelant regroupe une seule fois à la fin.</param>
    private ButtonRowViewModel AddLocalRow(ButtonKey key, bool rebuild = true)
    {
        if (_rowByButton.TryGetValue(key, out var existing)) return existing;
        var row = new ButtonRowViewModel(key, LocalInputGroup, ButtonKeyNames.Label(key), null, Actions, ItemFor(key),
            OnRowChanged, RepeatFor(key), TargetFor(key), StepsFor(key)) { CanRemove = true };
        _rowByButton[key] = row;
        ButtonRows.Add(row);
        // Les vues groupées (Android, macOS) lisent ButtonGroups : sans regroupement, la nouvelle ligne n'y apparaissait pas.
        if (rebuild) RebuildButtonGroups();
        return row;
    }

    private void RemoveRow(ButtonRowViewModel row, bool rebuild = true)
    {
        _rowByButton.Remove(row.Button);
        ButtonRows.Remove(row);
        if (rebuild) RebuildButtonGroups();
    }

    private int _captureToken;

    /// <summary>Attend la prochaine touche (clavier, manette ou joystick) pour créer une ligne d'assignation.</summary>
    [RelayCommand]
    private void AddInputKey()
    {
        if (!HasLocalInput) return;
        if (!KeyboardInputEnabled && !GamepadInputEnabled)
        {
            InputCaptureStatus = Strings.Capture_EnableFirst;
            return;
        }
        IsCapturing = true;
        InputCaptureStatus = KeyboardInputEnabled
            ? Strings.Capture_PressKey
            : Strings.Capture_PressPad;
        _bridge.LocalInput.CaptureNext = true;

        // Filet de sécurité : le clavier étant avalé pendant la capture, on ne reste jamais bloqué plus de 15 s.
        int token = ++_captureToken;
        _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ => OnUi(() =>
        {
            if (IsCapturing && token == _captureToken) { CancelCapture(); InputCaptureStatus = Strings.Capture_Timeout; }
        }));
    }

    [RelayCommand]
    private void CancelCapture()
    {
        _captureToken++;
        _bridge.LocalInput.CaptureNext = false;
        IsCapturing = false;
        InputCaptureStatus = "";
    }

    private void OnInputCaptured(ButtonKey key)
    {
        if (!IsCapturing) return;
        _captureToken++;
        IsCapturing = false;
        var row = AddLocalRow(key);
        InputCaptureStatus = row.Selected is { Id: not ObcButtonId.None }
            ? Loc.Fmt(Strings.Capture_AlreadyAssigned, row.Label)
            : Loc.Fmt(Strings.Capture_Added, row.Label);
    }

    [RelayCommand]
    private void RemoveInputKey(ButtonRowViewModel? row)
    {
        if (row is not { CanRemove: true }) return;
        _editProfile.RemoveBinding(row.Button);
        RemoveRow(row);
        SaveConfig();
        _bridge.RefreshLocalInput();
        RefreshSummaries();
    }

    // ---------- Vitesse de répétition (vitesse + / − tenue) ----------

    public IReadOnlyList<RepeatSpeedOption> RepeatSpeedOptions { get; } =
    [
        new(RepeatSpeed.Slow, Strings.RepeatSpeed_Slow),
        new(RepeatSpeed.Normal, Strings.RepeatSpeed_Normal),
        new(RepeatSpeed.Fast, Strings.RepeatSpeed_Fast),
        new(RepeatSpeed.VeryFast, Strings.RepeatSpeed_VeryFast),
    ];

    [ObservableProperty] private RepeatSpeedOption? _selectedRepeatSpeed;

    partial void OnSelectedRepeatSpeedChanged(RepeatSpeedOption? value)
    {
        if (_loading || value is null) return;
        _config.RepeatSpeed = value.Speed; // lu à chaque appui par BridgeService : effet immédiat
        SaveConfig();
    }
}
