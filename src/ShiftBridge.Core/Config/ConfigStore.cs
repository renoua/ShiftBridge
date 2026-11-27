using System.Text.Json;
using System.Text.Json.Serialization;
using ShiftBridge.Core.Controllers;
using ShiftBridge.Core.Input;
using ShiftBridge.Core.Mapping;

namespace ShiftBridge.Core.Config;

public sealed class AppConfig
{
    /// <summary>Profil par défaut, utilisé quand aucune appli connectée n'a son propre profil.</summary>
    public Profile Profile { get; set; } = Profile.ZwiftLike();
    /// <summary>Profils propres à une appli d'entraînement (clé : <see cref="AppProfileKey.Normalize"/> de son AppId), activés à sa connexion.</summary>
    public Dictionary<string, Profile> AppProfiles { get; set; } = [];
    public bool EnableBle { get; set; } = true;
    public bool EnableNetwork { get; set; } = true;
    public bool EnableDirCon { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    /// <summary>Les manettes vibrent aux changements de vitesse et quand l'appli le demande (le test manuel « Faire vibrer » marche toujours).</summary>
    public bool ControllerVibration { get; set; } = true;
    /// <summary>Windows : la fenêtre est cachée au lancement (dans la zone de notification si <see cref="MinimizeToTray"/>, sinon réduite).</summary>
    public bool StartMinimized { get; set; }
    /// <summary>Windows : réduire la fenêtre la cache dans la zone de notification (la croix ferme toujours l'application).</summary>
    public bool MinimizeToTray { get; set; }
    /// <summary>Windows : lance ShiftBridge à l'ouverture de session (valeur « Run » du registre, avec --minimized).</summary>
    public bool StartWithWindows { get; set; }
    /// <summary>Recherche sur GitHub Releases au lancement, puis toutes les 24 h, si l'hôte le permet.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Propose aussi les versions de test (préversions GitHub), que les autres utilisateurs ne reçoivent pas.</summary>
    public bool IncludePrereleases { get; set; }
    /// <summary>Obsolète depuis 0.15 (les sections « Avancé » se replient page par page) : conservé pour que les anciens config.json restent lisibles.</summary>
    public bool ShowAdvanced { get; set; }
    public int GearCount { get; set; } = 24;
    /// <summary>Langue de l'interface : null/"auto" = suit la langue du système, "fr" ou "en" = forcée (prise en compte au redémarrage).</summary>
    public string? Language { get; set; }
    public List<string> KnownDevices { get; set; } = [];

    /// <summary>Modèle de chaque manette BLE non Zwift déjà connectée (adresse → modèle) : sert à afficher ses touches dans l'onglet Touches même hors ligne.</summary>
    public Dictionary<string, ControllerKind> KnownControllerKinds { get; set; } = [];

    /// <summary>Nom personnalisé donné à une manette (adresse → nom), affiché à la place du nom Bluetooth.</summary>
    public Dictionary<string, string> DeviceNames { get; set; } = [];

    /// <summary>Code PIN à trois caractères du groupe L-TWOO (appliqué à la prochaine connexion).</summary>
    public string LtwooPin { get; set; } = "";

    /// <summary>SRAM AXS, mode avancé (expérimental) : appairage et lecture du pignon au lieu du simple/double appui. Appliqué à la prochaine connexion.</summary>
    public bool SramAxsAdvanced { get; set; }

    /// <summary>Clés des dérailleurs AXS appairés (numéro de série → clé hexadécimale de 16 octets). Secret : ne pas partager ce fichier.</summary>
    public Dictionary<string, string> SramAxsKeys { get; set; } = [];

    /// <summary>Les assignations par défaut des touches Haut/Bas de l'AXS avancé ont déjà été ajoutées une fois.</summary>
    public bool SramAxsAdvancedSeeded { get; set; }

    /// <summary>Écoute le clavier (et les télécommandes HID) comme source de touches.</summary>
    public bool KeyboardInputEnabled { get; set; }
    /// <summary>Écoute les manettes de jeu (Xbox/XInput et joysticks génériques) comme source de touches.</summary>
    public bool GamepadInputEnabled { get; set; }
    /// <summary>Les touches clavier assignées sont avalées : l'appli au premier plan (Zwift…) ne les reçoit pas.</summary>
    public bool SuppressBoundKeys { get; set; } = true;

    /// <summary>Trainer relayé par le proxy de virtual shifting (adresse IP et port DirCon du KICKR).</summary>
    public string TrainerHost { get; set; } = "";
    public int TrainerPort { get; set; } = 36866;

    /// <summary>"wifi" (DirCon) ou "ble" (Bluetooth).</summary>
    public string TrainerTransport { get; set; } = "wifi";
    public string TrainerBleAddress { get; set; } = "";
    public string TrainerBleName { get; set; } = "";

    /// <summary>Capteur cardio Bluetooth (ceinture, montre) republié par le proxy trainer ; vide = aucun.</summary>
    public string HeartRateBleAddress { get; set; } = "";
    public string HeartRateBleName { get; set; } = "";

    /// <summary>Relaie TOUS les services du trainer Bluetooth (diagnostic) au lieu de la liste d'autorisation.</summary>
    public bool FullRelay { get; set; }

    /// <summary>Braquet physique du vélo : dents du plateau et du pignon (Zwift Cog : 14 dents). Sert de référence au virtual shifting.</summary>
    public int ChainringTeeth { get; set; } = 34;
    public int CogTeeth { get; set; } = 14;
    /// <summary>Circonférence de roue (m) supposée pour la mesure du rapport réel (700×25c ≈ 2,105).</summary>
    public double WheelCircumferenceM { get; set; } = 2.105;

    /// <summary>
    /// Mode natif Zwift (le trainer applique lui-même le rapport) quand il expose le service Zwift ; repli automatique sur la
    /// mise à l'échelle FTMS sinon.
    /// </summary>
    public bool NativeZwiftMode { get; set; } = true;
    /// <summary>
    /// Vrai : l'appli d'entraînement gère ses propres vitesses virtuelles (le proxy relaie le trainer sans rien modifier, + et −
    /// lui sont transmis). Faux (défaut) : ShiftBridge applique les vitesses au trainer.
    /// </summary>
    public bool AppHandlesShifting { get; set; }
    /// <summary>Poids envoyés au trainer en mode natif (il s'en sert pour sa physique ; l'appli ne les transmet pas par FTMS).</summary>
    public double RiderWeightKg { get; set; } = 75;
    public double BikeWeightKg { get; set; } = 9;

    /// <summary>Virtual shifting par la seule pente (trainers qui ignoreraient le roulement et la traînée de la simulation).</summary>
    public bool SlopeOnlyMode { get; set; }

    /// <summary>
    /// Envoie le rapport virtuel à l'appli par OpenBikeControl (bouton « Gear Set », valeur = rapport + 1) pour qu'elle puisse
    /// l'afficher. Expérimental : une appli peut l'ignorer, ou l'appliquer à sa propre simulation (double virtual shifting).
    /// </summary>
    public bool ReportGearToApp { get; set; } = true;

    /// <summary>Superposition ShiftBridge (rapport toujours visible au-dessus de l'appli).</summary>
    public bool ShowOverlay { get; set; }
    /// <summary>Superposition verrouillée : les clics la traversent (elle ne peut plus être déplacée).</summary>
    public bool OverlayLocked { get; set; }
    /// <summary>Position de la superposition ; null = pas encore placée (coin supérieur droit de l'écran).</summary>
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }
    public double OverlayScale { get; set; } = 1.0;
    /// <summary>Opacité du fond de la superposition (0,2 à 1).</summary>
    public double OverlayOpacity { get; set; } = 0.85;
    /// <summary>Barre graphique des vitesses dans la superposition.</summary>
    public bool OverlayShowBar { get; set; } = true;
    /// <summary>Clignotement de la superposition à chaque changement de vitesse (rouge aux butées).</summary>
    public bool OverlayFlash { get; set; } = true;
    /// <summary>Puissance, cadence et vitesse du trainer dans la superposition.</summary>
    public bool OverlayShowTelemetry { get; set; }

    /// <summary>Réactive le virtual shifting au lancement avec le dernier trainer utilisé.</summary>
    public bool AutoStartVirtualShifting { get; set; } = true;
    /// <summary>Dernière vitesse virtuelle (1..24) ; 0 = jamais mémorisée (vitesse centrale au départ).</summary>
    public int LastGear { get; set; }

    /// <summary>Vitesse de répétition des touches vitesse + / − tenues (valable pour toutes les touches).</summary>
    public RepeatSpeed RepeatSpeed { get; set; } = RepeatSpeeds.Default;

    /// <summary>Verrou à prendre pour modifier une liste de la configuration pendant qu'elle peut être sauvegardée.</summary>
    [JsonIgnore]
    public object SyncRoot { get; } = new();

    /// <summary>Ramène les valeurs hors bornes (fichier édité à la main ou ancien) à des valeurs sûres.</summary>
    public void Normalize()
    {
        Profile ??= Profile.ZwiftLike();
        CleanBindings(Profile);
        // Clés normalisées (fichier édité à la main), profils vides ou nuls écartés.
        var appProfiles = new Dictionary<string, Profile>();
        foreach (var (appId, profile) in AppProfiles ?? [])
        {
            string key = AppProfileKey.Normalize(appId);
            if (key.Length == 0 || profile is null) continue;
            CleanBindings(profile);
            appProfiles[key] = profile;
        }
        AppProfiles = appProfiles;
        KnownDevices ??= [];
        // Modèle de manette inconnu (écrit par une version plus récente) : l'appareil sera reconnu de nouveau à la recherche.
        KnownControllerKinds = (KnownControllerKinds ?? []).Where(p => Enum.IsDefined(p.Value)).ToDictionary(p => p.Key, p => p.Value);
        SramAxsKeys ??= [];
        DeviceNames = (DeviceNames ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(p => p.Key, p => p.Value.Trim().Length > 40 ? p.Value.Trim()[..40] : p.Value.Trim());
        GearCount = Math.Clamp(GearCount, 8, 24); // plage proposée par l'interface ; la table (VirtualShiftingModel) en compte 24 au plus
        if (TrainerPort is < 1 or > 65535) TrainerPort = 36866;
        if (TrainerTransport is not ("wifi" or "ble")) TrainerTransport = "wifi";
        TrainerHost ??= ""; TrainerBleAddress ??= ""; TrainerBleName ??= "";
        HeartRateBleAddress ??= ""; HeartRateBleName ??= "";
        RiderWeightKg = double.IsFinite(RiderWeightKg) ? Math.Clamp(RiderWeightKg, 30, 200) : 75;
        BikeWeightKg = double.IsFinite(BikeWeightKg) ? Math.Clamp(BikeWeightKg, 3, 30) : 9;
        WheelCircumferenceM = double.IsFinite(WheelCircumferenceM) ? Math.Clamp(WheelCircumferenceM, 1.5, 2.5) : 2.105;
        OverlayScale = double.IsFinite(OverlayScale) ? Math.Clamp(OverlayScale, 0.5, 3.0) : 1.0;
        OverlayOpacity = double.IsFinite(OverlayOpacity) ? Math.Clamp(OverlayOpacity, 0.2, 1.0) : 0.85;
        LastGear = Math.Clamp(LastGear, 0, GearCount);
        if (!Enum.IsDefined(RepeatSpeed)) RepeatSpeed = RepeatSpeeds.Default;
    }

    /// <summary>
    /// Retire les actions inconnues de cette version (lues par <see cref="TolerantEnumConverter{T}"/>) et les assignations
    /// restées sans action : le reste du profil est conservé.
    /// </summary>
    private static void CleanBindings(Profile profile)
    {
        var cleaned = new Dictionary<ButtonKey, ButtonBinding>();
        foreach (var (key, binding) in profile.Bindings ?? [])
        {
            if (binding is null) continue;
            binding.Actions = (binding.Actions ?? []).Where(a => Enum.IsDefined(a)).ToList();
            if (binding.Actions.Count == 0) continue;
            binding.GearTarget = Math.Max(0, binding.GearTarget);
            binding.GearSteps = Math.Max(0, binding.GearSteps);
            cleaned[key] = binding;
        }
        profile.Bindings = cleaned;
    }
}

/// <summary>Configuration JSON dans %APPDATA%\ShiftBridge\config.json. Un fichier illisible ne bloque jamais le démarrage.</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new TolerantEnumConverterFactory() },
    };

    /// <summary>Sérialise les écritures du fichier (sauvegardes simultanées), sans bloquer ceux qui modifient la config.</summary>
    private static readonly object FileLock = new();

    /// <summary>Tentatives de lecture quand le fichier est momentanément verrouillé (antivirus, synchronisation, autre instance).</summary>
    private const int ReadAttempts = 3;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShiftBridge", "config.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        for (int attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            string text;
            try
            {
                if (!File.Exists(path)) return new AppConfig();
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fichier momentanément inaccessible : surtout NE PAS le mettre de côté, il est probablement valide.
                if (attempt < ReadAttempts) { Thread.Sleep(150 * attempt); continue; }
                return new AppConfig();
            }

            try
            {
                var loaded = JsonSerializer.Deserialize<AppConfig>(text, Options) ?? new AppConfig();
                loaded.Normalize();
                return loaded;
            }
            catch (JsonException)
            {
                // Contenu réellement illisible : on le garde pour diagnostic (sans écraser un précédent) et on repart des défauts.
                string bad = $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
                try { File.Move(path, bad, overwrite: false); } catch { /* ignoré */ }
                return new AppConfig();
            }
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Sérialisation sous le verrou de la config (une liste modifiée pendant ce temps ne la fait plus échouer), écriture
        // disque en dehors : le fil Bluetooth, qui prend ce verrou à chaque annonce, n'attend plus le disque.
        string json;
        lock (config.SyncRoot) json = JsonSerializer.Serialize(config, Options);
        lock (FileLock)
        {
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true); // sans cela, une coupure de courant peut laisser un fichier vide
            }
            File.Move(tmp, path, overwrite: true); // remplacement atomique
        }
    }
}
