namespace ShiftBridge.Core.Trainer;

/// <summary>
/// Surveille le flux de mesures (Indoor Bike Data, ou pages FE-C) reçu du trainer : nombre de mesures reçues et relayées à
/// l'appli, plus long silence et dernière puissance. Permet de vérifier, dans le journal, que les watts arrivent en continu même
/// pendant une rafale de changements de rapport. Pour ne pas envahir le journal, un résumé n'est rendu que s'il apprend quelque
/// chose : premier résumé, silence anormal, arrêt complet du flux, journal détaillé, ou au plus tard toutes les
/// <see cref="RoutineIntervalMs"/>. Thread-safe.
/// </summary>
public sealed class BikeDataMonitor
{
    /// <summary>Silence au-delà duquel le flux est jugé anormal (un trainer envoie 1 à 4 mesures par seconde, même à l'arrêt).</summary>
    public const long AbnormalGapMs = 2000;
    /// <summary>Écart maximal entre deux résumés « tout va bien ».</summary>
    public const long RoutineIntervalMs = 5 * 60_000;

    private readonly object _lock = new();
    private long _lastTick = -1;
    private int _count, _relayed;
    private long _maxGapMs;
    private int? _lastWatts;
    private int _powerSamples;
    private long _lastSummaryTick = long.MinValue;

    public void Add(long tickMs, int? watts, bool relayed = true)
    {
        lock (_lock)
        {
            if (_lastTick >= 0) _maxGapMs = Math.Max(_maxGapMs, tickMs - _lastTick);
            _lastTick = tickMs;
            _count++;
            if (relayed) _relayed++;
            if (watts is { } w) { _lastWatts = w; _powerSamples++; }
        }
    }

    /// <summary>
    /// Résumé depuis le dernier appel (remis à zéro), ou null s'il n'y a rien d'utile à dire. <paramref name="verbose"/> (journal
    /// détaillé) rend un résumé à chaque appel.
    /// </summary>
    public string? TakeSummary(long nowMs, bool verbose = false)
    {
        lock (_lock)
        {
            long silenceNow = _lastTick >= 0 ? nowMs - _lastTick : 0;
            if (_count == 0)
            {
                // Flux arrêté depuis le dernier résumé : c'est justement ce qu'on cherche à voir (sans cela, rien ne s'affichait).
                return _lastTick >= 0 && silenceNow > AbnormalGapMs ? $"Aucune mesure du trainer depuis {silenceNow} ms." : null;
            }
            long maxGap = Math.Max(_maxGapMs, silenceNow);
            bool due = verbose || maxGap > AbnormalGapMs || _lastSummaryTick == long.MinValue
                       || nowMs - _lastSummaryTick >= RoutineIntervalMs;
            string? text = null;
            if (due)
            {
                string power = _powerSamples == 0 ? "pas de puissance dans les mesures" : $"dernière puissance {_lastWatts} W";
                string relayed = _relayed == _count ? "" : $" ({_relayed} relayées à l'appli)";
                text = $"Mesures du trainer : {_count} reçues{relayed}, plus long silence {maxGap} ms, {power}.";
                _lastSummaryTick = nowMs;
            }
            _count = 0; _relayed = 0; _maxGapMs = 0; _powerSamples = 0; // le dernier instant est conservé : le silence est mesuré d'un résumé à l'autre
            return text;
        }
    }
}
