using Windows.Devices.Radios;

namespace ShiftBridge.Platform.Windows;

/// <summary>
/// État de la radio Bluetooth du PC (Windows.Devices.Radios) : sans elle, la recherche ne trouvait simplement rien, sans
/// explication. <see cref="IsOn"/> reste vrai tant que l'état est inconnu (pas d'adaptateur lisible) : on ne bloque jamais
/// l'utilisateur sur une supposition.
/// </summary>
public sealed class WindowsBluetoothRadio : IDisposable
{
    private Radio? _radio;
    private volatile bool _isOn = true;

    /// <summary>Levé (thread quelconque) quand la radio s'allume (true) ou s'éteint (false).</summary>
    public event Action<bool>? StateChanged;

    public bool IsOn => _isOn;

    /// <summary>Repère la radio Bluetooth et suit ses changements d'état ; sans effet si aucune n'est accessible.</summary>
    public async Task StartAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            _radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (_radio is null) return;
            _isOn = _radio.State != RadioState.Off;
            _radio.StateChanged += OnStateChanged;
            if (!_isOn) StateChanged?.Invoke(false);
        }
        catch { _radio = null; /* API indisponible (service arrêté, version de Windows) : on suppose la radio allumée */ }
    }

    private void OnStateChanged(Radio sender, object args)
    {
        bool on = sender.State != RadioState.Off;
        if (on == _isOn) return;
        _isOn = on;
        StateChanged?.Invoke(on);
    }

    /// <summary>
    /// Tente d'allumer la radio (Windows le permet aux applis de bureau) ; à défaut, ouvre la page Bluetooth des Paramètres.
    /// </summary>
    public async Task RequestOnAsync()
    {
        try
        {
            if (_radio is not null && await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed
                && await _radio.SetStateAsync(RadioState.On) == RadioAccessStatus.Allowed)
                return;
        }
        catch { /* repli sur les Paramètres */ }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch { /* Paramètres indisponibles */ }
    }

    public void Dispose()
    {
        if (_radio is { } r) r.StateChanged -= OnStateChanged;
        _radio = null;
    }
}
