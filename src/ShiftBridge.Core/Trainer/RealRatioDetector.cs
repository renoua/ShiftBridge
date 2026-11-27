namespace ShiftBridge.Core.Trainer;

/// <summary>
/// Mesure le rapport RÉEL du vélo (roues par tour de pédale) à partir de la vitesse et de la cadence que rapporte le trainer :
///   r = v / (cadence/60 × circonférence).
/// Le principe est celui des implémentations ouvertes (Wattroom, Zwift qui détecte le braquet « dans les premières secondes »),
/// réécrit ici : seules les mesures à cadence régulière comptent (60–110 tr/min, vitesse ≥ 1 m/s), une médiane glissante de 7
/// mesures écarte les valeurs aberrantes, et le résultat n'est rendu que s'il est stable.
/// Hypothèse non vérifiable ici : la vitesse FTMS du trainer est celle de son volant (et non une vitesse de route simulée).
/// </summary>
public sealed class RealRatioDetector
{
    public const double MinRpm = 60, MaxRpm = 110, MinSpeedMs = 1.0;
    public const int Window = 7;
    public const int MinAccepted = 10;
    /// <summary>Dispersion relative maximale (écart entre les 10e et 90e centiles / médiane) pour une mesure fiable.</summary>
    public const double MaxSpread = 0.05;
    private const double ShiftShare = 0.05;
    private const int ShiftStrikes = 3;
    /// <summary>Part minimale des mesures brutes (depuis le dernier changement de braquet) à ±5 % du résultat.</summary>
    private const double MinConsistentShare = 0.70;

    private readonly double _wheelMetres;
    /// <summary>Les échantillons arrivent sur le fil des notifications du trainer, le résultat est lu depuis un autre thread.</summary>
    private readonly object _lock = new();
    private readonly List<double> _raws = [];
    private readonly List<double> _window = [];
    private readonly List<double> _filtered = [];
    private int _strikes;

    public RealRatioDetector(double wheelCircumferenceM = 2.105)
    {
        _wheelMetres = Math.Clamp(wheelCircumferenceM, 1.5, 2.6);
    }

    /// <summary>Échantillons reçus (tous).</summary>
    public int TotalSamples { get; private set; }
    /// <summary>Échantillons retenus (cadence et vitesse dans la plage de mesure).</summary>
    public int AcceptedSamples { get; private set; }
    /// <summary>Vrai si au moins un échantillon portait une cadence (certains trainers n'en envoient pas).</summary>
    public bool SawCadence { get; private set; }
    public double LastRpm { get; private set; }

    public void Reset()
    {
        lock (_lock)
        {
            _window.Clear(); _filtered.Clear(); _raws.Clear();
            _strikes = 0; TotalSamples = AcceptedSamples = 0; SawCadence = false; LastRpm = 0;
        }
    }

    public void AddSample(double speedMs, double cadenceRpm)
    {
        lock (_lock) AddSampleCore(speedMs, cadenceRpm);
    }

    private void AddSampleCore(double speedMs, double cadenceRpm)
    {
        TotalSamples++;
        if (cadenceRpm > 0) SawCadence = true;
        LastRpm = cadenceRpm;
        if (cadenceRpm < MinRpm || cadenceRpm > MaxRpm || speedMs < MinSpeedMs) return;
        AcceptedSamples++;

        double raw = speedMs / (cadenceRpm / 60.0 * _wheelMetres);
        _raws.Add(raw);
        _window.Add(raw);
        if (_window.Count > Window) _window.RemoveAt(0);
        if (_window.Count < Window) return;

        double m = Median(_window);
        if (_filtered.Count >= 3)
        {
            // Changement de braquet pendant la mesure : on repart de la nouvelle valeur après 3 écarts d'affilée,
            // et on ignore les écarts isolés.
            double anchor = Median(_filtered);
            if (Math.Abs(m / anchor - 1) >= ShiftShare)
            {
                if (++_strikes < ShiftStrikes) return;
                _filtered.Clear(); _raws.Clear(); _strikes = 0;
            }
            else _strikes = 0;
        }
        _filtered.Add(m);
    }

    /// <summary>Résultat si la mesure est assez longue et stable.</summary>
    public bool TryGetResult(out double ratio, out int samples, out double spread)
    {
        lock (_lock) return TryGetResultCore(out ratio, out samples, out spread);
    }

    private bool TryGetResultCore(out double ratio, out int samples, out double spread)
    {
        ratio = 0; spread = 1; samples = AcceptedSamples;
        if (AcceptedSamples < MinAccepted || _filtered.Count < 3) return false;

        var sorted = _filtered.Order().ToList();
        ratio = Median(sorted);
        int n = sorted.Count;
        double lo = sorted[(int)Math.Floor(0.1 * (n - 1))];
        double hi = sorted[(int)Math.Ceiling(0.9 * (n - 1))];
        spread = ratio > 0 ? (hi - lo) / ratio : 1;
        if (spread > MaxSpread) return false;

        // La médiane écarte les valeurs isolées, mais un signal qui oscille entre deux rapports ne doit pas passer pour stable :
        // la grande majorité des mesures BRUTES doit rester proche du résultat.
        if (_raws.Count == 0 || ratio <= 0) return false;
        double center = ratio; // copie locale : un paramètre « out » ne peut pas être capturé par une lambda
        double near = _raws.Count(r => Math.Abs(r / center - 1) <= 0.05) / (double)_raws.Count;
        return near >= MinConsistentShare;
    }

    /// <summary>Spread courant (pour expliquer un échec : mesure trop irrégulière).</summary>
    public double CurrentSpread
    {
        get
        {
            lock (_lock)
            {
                if (_filtered.Count < 3) return 1;
                TryGetResultCore(out _, out _, out var s);
                return s;
            }
        }
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var s = values.Order().ToList();
        int n = s.Count;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
    }
}
