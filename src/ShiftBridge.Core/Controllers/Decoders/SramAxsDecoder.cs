namespace ShiftBridge.Core.Controllers.Decoders;

/// <summary>
/// SRAM AXS / eTap, mode simple SANS chiffrement : chaque notification de la caractéristique « trigger » (d9050054) est un appui
/// de levier (le dérailleur passe aussi sa vitesse physiquement). On ne sait pas distinguer gauche et droite : un appui isolé
/// donne « Single », deux appuis dans la fenêtre donnent « Double ». La décision d'un appui isolé tombe après la fenêtre
/// (<see cref="Deferred"/>). Une notification d'un seul octet à zéro (relâchement supposé) est ignorée.
/// </summary>
public sealed class SramAxsDecoder : IButtonDecoder, IDeferredDecoder
{
    public static readonly Guid Service = new("d9050053-90aa-4c7c-b036-1e01fb8eb7ee");
    public static readonly Guid Trigger = new("d9050054-90aa-4c7c-b036-1e01fb8eb7ee");
    /// <summary>« État en direct » chiffré : lu périodiquement pour que le dérailleur ne coupe pas la liaison inactive (~3 min).</summary>
    public static readonly Guid LiveState = new("d905000b-90aa-4c7c-b036-1e01fb8eb7ee");
    public static bool IsAxsService(Guid uuid) => uuid.ToString().EndsWith("-90aa-4c7c-b036-1e01fb8eb7ee", StringComparison.Ordinal);

    private readonly int _windowMs;
    private readonly object _lock = new();
    private Timer? _timer;
    private int _taps;
    private string _firstHex = "";
    private long _firstTick;

    public SramAxsDecoder(int doubleWindowMs = 300) => _windowMs = Math.Max(20, doubleWindowMs);

    public event Action<DecodedFrame>? Deferred;

    public DecodedFrame? Decode(Guid characteristic, byte[] data)
    {
        if (characteristic != Trigger) return null;
        if (data.Length == 0 || (data.Length == 1 && data[0] == 0x00)) return DecodedFrame.Nothing;
        string hex = Convert.ToHexString(data);
        long now = Environment.TickCount64;
        string? doubleDetail = null;
        lock (_lock)
        {
            _taps++;
            if (_taps == 1)
            {
                _firstHex = hex;
                _firstTick = now;
                _timer = new Timer(_ => Fire(), null, _windowMs, Timeout.Infinite);
            }
            else
            {
                _timer?.Dispose(); _timer = null;
                _taps = 0;
                // Octets des deux notifications et écart : de quoi vérifier sur les journaux si un levier se distingue de l'autre.
                doubleDetail = $"{_firstHex} + {hex}, écart {now - _firstTick} ms";
            }
        }
        if (doubleDetail is not null) Deferred?.Invoke(DecodedFrame.Tap("Double") with { Detail = doubleDetail });
        return DecodedFrame.Nothing;
    }

    private void Fire()
    {
        string hex;
        lock (_lock)
        {
            if (_taps == 0) return;
            _taps = 0;
            hex = _firstHex;
            _timer?.Dispose(); _timer = null;
        }
        Deferred?.Invoke(DecodedFrame.Tap("Single") with { Detail = hex });
    }

    public void Dispose()
    {
        lock (_lock) { _timer?.Dispose(); _timer = null; _taps = 0; }
    }
}
