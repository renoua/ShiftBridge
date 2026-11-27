using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShiftBridge.Core.Localization;

namespace ShiftBridge.Presentation;

/// <summary>Étapes de l'assistant de démarrage.</summary>
public enum OnboardingStep { Welcome, Controllers, Trainer, App, Done }

public sealed partial class MainViewModel
{
    // ---------- Assistant de démarrage ----------
    // Plein écran au premier lancement : bienvenue (et permissions sous Android), manettes, trainer, appli, récapitulatif.
    // Chaque étape réutilise les commandes et listes des pages ordinaires : rien n'est propre à l'assistant sauf la navigation.

    private const OnboardingStep LastOnboardingStep = OnboardingStep.Done;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnboardingProgress), nameof(OnboardingNextText), nameof(CanOnboardingGoBack))]
    [NotifyPropertyChangedFor(nameof(IsOnbWelcome), nameof(IsOnbControllers), nameof(IsOnbTrainer), nameof(IsOnbApp), nameof(IsOnbDone))]
    private OnboardingStep _onboardingStep;

    public bool IsOnbWelcome => OnboardingStep == OnboardingStep.Welcome;
    public bool IsOnbControllers => OnboardingStep == OnboardingStep.Controllers;
    public bool IsOnbTrainer => OnboardingStep == OnboardingStep.Trainer;
    public bool IsOnbApp => OnboardingStep == OnboardingStep.App;
    public bool IsOnbDone => OnboardingStep == OnboardingStep.Done;

    /// <summary>L'assistant occupe l'écran : les vues masquent la navigation (barre latérale, onglets).</summary>
    public bool IsOnboarding => CurrentPage == Pages.Onboarding;

    public string OnboardingProgress => Loc.Fmt(Strings.Onb_Progress, (int)OnboardingStep + 1, (int)LastOnboardingStep + 1);
    public string OnboardingNextText => OnboardingStep == LastOnboardingStep ? Strings.Onb_Finish : Strings.Onb_Next;
    public bool CanOnboardingGoBack => OnboardingStep > OnboardingStep.Welcome;

    /// <summary>Récapitulatif : « Activer le virtual shifting » seulement si un trainer a été choisi et que le proxy est arrêté.</summary>
    public bool CanStartFromOnboarding => !ProxyActive && TrainerState != UiState.Off;
    partial void OnTrainerStateChanged(UiState value) => OnPropertyChanged(nameof(CanStartFromOnboarding));

    /// <summary>Explication des permissions sur la page de bienvenue (hôtes qui en demandent : Android).</summary>
    public bool ShowsPermissionNote => _host.AsksPermissions;

    /// <summary>Consigne de l'étape « Appli » : le nom sous lequel ShiftBridge apparaît dans l'appli d'entraînement.</summary>
    public string OnboardingAppText => Loc.Fmt(Strings.Onb_AppBody, ControllerNameForApp());

    /// <summary>Nom annoncé en Bluetooth (celui du PC sous Windows), sinon « ShiftBridge » (nom réseau).</summary>
    private string ControllerNameForApp() => _ble?.AdvertisedName is { Length: > 0 } name ? name : "ShiftBridge";

    partial void OnOnboardingStepChanged(OnboardingStep value)
    {
        if (value == OnboardingStep.Controllers && !IsScanning) StartScan(); // les manettes apparaissent pendant la lecture
        if (value == OnboardingStep.App) OnPropertyChanged(nameof(OnboardingAppText));
    }

    private bool _permissionsRequested;

    [RelayCommand]
    private async Task OnboardingNextAsync()
    {
        // Les permissions sont demandées APRÈS l'explication de la page de bienvenue, et non à froid au lancement.
        if (OnboardingStep == OnboardingStep.Welcome && _host.AsksPermissions && !_permissionsRequested)
        {
            _permissionsRequested = true;
            await _host.RequestPermissionsAsync();
        }
        if (OnboardingStep < LastOnboardingStep) OnboardingStep++;
        else CompleteOnboarding();
    }

    [RelayCommand]
    private void OnboardingBack()
    {
        if (OnboardingStep > OnboardingStep.Welcome) OnboardingStep--;
    }

    [RelayCommand]
    private void SkipOnboarding() => CompleteOnboarding();

    /// <summary>Relance l'assistant (Réglages, ou bandeau de l'Accueil).</summary>
    [RelayCommand]
    private void OpenOnboarding()
    {
        OnboardingStep = OnboardingStep.Welcome;
        CurrentPage = Pages.Onboarding;
    }

    /// <summary>Masque le bandeau « Prochaine étape » de l'Accueil (utilisateur sans trainer, clavier seul…).</summary>
    [RelayCommand]
    private void HideNextStep()
    {
        _config.HideNextStep = true;
        SaveConfig();
        RefreshSummaries();
    }

    private void CompleteOnboarding()
    {
        bool firstTime = !_config.OnboardingCompleted;
        _config.OnboardingCompleted = true;
        // Assistant terminé ou passé : le bandeau de l'Accueil reprend le relais pour ce qui reste à faire.
        if (firstTime) _config.HideNextStep = false;
        SaveConfig();
        // Assistant passé dès la bienvenue : les permissions (et le démarrage du pont qui les suit) n'ont pas encore été demandées.
        if (_host.AsksPermissions && !_permissionsRequested) { _permissionsRequested = true; _ = _host.RequestPermissionsAsync(); }
        CurrentPage = Pages.Home;
        RefreshSummaries();
    }
}
