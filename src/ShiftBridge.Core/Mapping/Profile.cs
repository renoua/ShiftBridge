using ShiftBridge.Core.Controllers;
using ShiftBridge.Core.Input;
using ShiftBridge.Core.Obc;

namespace ShiftBridge.Core.Mapping;

/// <summary>Association d'une touche physique à une ou plusieurs actions OBC.</summary>
public sealed class ButtonBinding
{
    public List<ObcButtonId> Actions { get; set; } = [];

    /// <summary>Si vrai, l'action est répétée tant que la touche reste enfoncée.</summary>
    public bool RepeatOnHold { get; set; }
    public int RepeatDelayMs { get; set; } = 500;
    public int RepeatIntervalMs { get; set; } = 250;

    /// <summary>Rapport visé par l'action « Aller au rapport » (1..N) ; 0 = non réglé.</summary>
    public int GearTarget { get; set; }

    /// <summary>Nombre de vitesses des actions « Monter / Descendre de X vitesses » (1..23) ; 0 = non réglé.</summary>
    public int GearSteps { get; set; }
}

/// <summary>Clé d'un profil d'appli : l'AppId reçu dans App Info, sans espaces ni casse (« TrainingPeaks Virtual » = « trainingpeaks virtual »).</summary>
public static class AppProfileKey
{
    public static string Normalize(string? appId) => (appId ?? "").Trim().ToLowerInvariant();
}

public sealed class Profile
{
    public string Name { get; set; } = "Par défaut";

    /// <summary>
    /// Assignations. Le dictionnaire publié n'est JAMAIS modifié en place une fois le profil en service : chaque changement
    /// (<see cref="SetBinding"/>, <see cref="ApplyControllerDefaults"/>) publie une copie. Le mapping (fil Bluetooth), la
    /// sauvegarde et l'interface peuvent donc le lire sans verrou, sans jamais voir un dictionnaire à moitié modifié.
    /// </summary>
    public Dictionary<ButtonKey, ButtonBinding> Bindings
    {
        get => Volatile.Read(ref _bindings);
        set => Volatile.Write(ref _bindings, value);
    }
    private Dictionary<ButtonKey, ButtonBinding> _bindings = [];

    /// <summary>Sérialise les écrivains entre eux (deux copies concurrentes perdraient l'une des modifications).</summary>
    private readonly object _writeLock = new();

    /// <summary>Assigne (ou retire, si <paramref name="binding"/> est null) une touche, par copie du dictionnaire.</summary>
    public void SetBinding(ButtonKey key, ButtonBinding? binding)
    {
        lock (_writeLock)
        {
            var copy = new Dictionary<ButtonKey, ButtonBinding>(Bindings);
            if (binding is null) { if (!copy.Remove(key)) return; }
            else copy[key] = binding;
            Bindings = copy;
        }
    }

    public void RemoveBinding(ButtonKey key) => SetBinding(key, null);

    public Profile Clone()
    {
        var p = new Profile { Name = Name };
        foreach (var (k, v) in Bindings)
            p.Bindings[k] = new ButtonBinding
            {
                Actions = [.. v.Actions], RepeatOnHold = v.RepeatOnHold,
                RepeatDelayMs = v.RepeatDelayMs, RepeatIntervalMs = v.RepeatIntervalMs, GearTarget = v.GearTarget,
                GearSteps = v.GearSteps,
            };
        return p;
    }

    /// <summary>Assignations proposées à la première connexion d'un modèle de manette BLE (haut = vitesse +, bas = vitesse −).</summary>
    public static IReadOnlyList<(string Name, ObcButtonId Action)> ControllerDefaults(ControllerKind kind) => kind switch
    {
        ControllerKind.ThinkriderVs200 or ControllerKind.CycplusBc2 =>
            [("Up", ObcButtonId.ShiftUp), ("Down", ObcButtonId.ShiftDown)],
        ControllerKind.OnelapKey =>
            [("Up", ObcButtonId.ShiftUp), ("Down", ObcButtonId.ShiftDown), ("Left", ObcButtonId.Left), ("Right", ObcButtonId.Right), ("Center", ObcButtonId.Select)],
        ControllerKind.ShimanoDi2 => [("Ch1", ObcButtonId.ShiftUp), ("Ch2", ObcButtonId.ShiftDown)],
        ControllerKind.SramAxs => [("Single", ObcButtonId.ShiftUp), ("Double", ObcButtonId.ShiftDown), ("Up", ObcButtonId.ShiftUp), ("Down", ObcButtonId.ShiftDown)],
        ControllerKind.WahooKickrBike =>
        [
            ("RightUp", ObcButtonId.ShiftUp), ("RightDown", ObcButtonId.ShiftUp), ("RightSteer", ObcButtonId.Right),
            ("LeftUp", ObcButtonId.ShiftDown), ("LeftDown", ObcButtonId.ShiftDown), ("LeftSteer", ObcButtonId.Left),
            ("ShiftUpRight", ObcButtonId.ShiftUp), ("ShiftDownRight", ObcButtonId.ShiftDown),
            ("ShiftUpLeft", ObcButtonId.ShiftUp), ("ShiftDownLeft", ObcButtonId.ShiftDown),
            ("RightBrake", ObcButtonId.ShiftUp), ("LeftBrake", ObcButtonId.ShiftDown),
        ],
        ControllerKind.EliteSquare =>
        [
            ("Up", ObcButtonId.Up), ("Down", ObcButtonId.Down), ("Left", ObcButtonId.Left), ("Right", ObcButtonId.Right),
            ("LeftShift1", ObcButtonId.ShiftUp), ("LeftShift2", ObcButtonId.ShiftDown),
            ("RightShift1", ObcButtonId.ShiftUp), ("RightShift2", ObcButtonId.ShiftDown),
            ("A", ObcButtonId.Select), ("B", ObcButtonId.Back), ("Y", ObcButtonId.Emote), ("Z", ObcButtonId.CameraView),
        ],
        ControllerKind.StagesSb20 =>
        [
            ("LeftUp", ObcButtonId.ShiftUp), ("LeftDown", ObcButtonId.ShiftDown), ("LeftThird", ObcButtonId.Left),
            ("RightUp", ObcButtonId.ShiftUp), ("RightDown", ObcButtonId.ShiftDown), ("RightThird", ObcButtonId.Right),
        ],
        ControllerKind.CycplusT7h =>
        [
            ("L.ShiftUp", ObcButtonId.ShiftUp), ("L.ShiftDown", ObcButtonId.ShiftDown), ("L.RearArrow", ObcButtonId.Left), ("L.FrontArrow", ObcButtonId.Left),
            ("R.ShiftUp", ObcButtonId.ShiftUp), ("R.ShiftDown", ObcButtonId.ShiftDown), ("R.RearArrow", ObcButtonId.Right), ("R.FrontArrow", ObcButtonId.Right),
        ],
        ControllerKind.WheeltopEds =>
        [
            ("Up", ObcButtonId.ShiftUp), ("Down", ObcButtonId.ShiftDown),
            ("L.Up", ObcButtonId.ShiftUp), ("L.Down", ObcButtonId.ShiftDown),
            ("R.Up", ObcButtonId.ShiftUp), ("R.Down", ObcButtonId.ShiftDown),
        ],
        ControllerKind.Ltwoo => [("Up", ObcButtonId.ShiftUp), ("Down", ObcButtonId.ShiftDown)],
        _ => [],
    };

    /// <summary>Ajoute les assignations par défaut d'un modèle, sans toucher à celles qui existent déjà. Vrai si le profil a changé.</summary>
    public bool ApplyControllerDefaults(ControllerKind kind, IReadOnlyCollection<string>? only = null)
    {
        lock (_writeLock)
        {
            Dictionary<ButtonKey, ButtonBinding>? copy = null;
            foreach (var (name, action) in ControllerDefaults(kind))
            {
                if (only is not null && !only.Contains(name)) continue;
                var key = ButtonKey.Controller(kind, name);
                if ((copy ?? Bindings).ContainsKey(key)) continue;
                copy ??= new Dictionary<ButtonKey, ButtonBinding>(Bindings);
                copy[key] = new ButtonBinding { Actions = [action] };
            }
            if (copy is null) return false;
            Bindings = copy;
            return true;
        }
    }

    /// <summary>
    /// Profil proche de Zwift : + / ShiftUp, − / ShiftDown, croix directionnelle en navigation.
    /// Les manettes Play et Ride partagent la même table (RightShiftUp = bouton latéral du Play droit).
    /// </summary>
    public static Profile ZwiftLike()
    {
        var p = new Profile { Name = "Défaut (type Zwift)" };
        void Map(PhysicalButton b, params ObcButtonId[] a) => p.Bindings[b] = new ButtonBinding { Actions = [.. a] };

        Map(PhysicalButton.ClickPlus, ObcButtonId.ShiftUp);
        Map(PhysicalButton.ClickMinus, ObcButtonId.ShiftDown);

        Map(PhysicalButton.RightShiftUp, ObcButtonId.ShiftUp);
        Map(PhysicalButton.RightShiftDown, ObcButtonId.ShiftDown);
        Map(PhysicalButton.LeftShiftUp, ObcButtonId.ShiftDown); // Click v2 gauche : gauche = −, droite = + (convention BikeControl)
        Map(PhysicalButton.LeftShiftDown, ObcButtonId.ShiftDown);
        Map(PhysicalButton.RightPaddle, ObcButtonId.ShiftUp);
        Map(PhysicalButton.LeftPaddle, ObcButtonId.ShiftDown);

        Map(PhysicalButton.LeftUp, ObcButtonId.Up);
        Map(PhysicalButton.LeftDown, ObcButtonId.Down);
        Map(PhysicalButton.LeftLeft, ObcButtonId.Left);
        Map(PhysicalButton.LeftRight, ObcButtonId.Right);

        Map(PhysicalButton.RightA, ObcButtonId.Select);
        Map(PhysicalButton.RightB, ObcButtonId.Back);
        Map(PhysicalButton.RightY, ObcButtonId.Emote);
        Map(PhysicalButton.RightZ, ObcButtonId.CameraView);
        Map(PhysicalButton.LeftOnOff, ObcButtonId.Menu);
        Map(PhysicalButton.RightOnOff, ObcButtonId.Menu);
        Map(PhysicalButton.LeftPowerUp, ObcButtonId.PowerUp1);
        Map(PhysicalButton.RightPowerUp, ObcButtonId.PowerUp1);
        return p;
    }
}
