namespace ShiftBridge.Core.Controllers;

/// <summary>
/// Résultat du décodage d'une trame.
/// <paramref name="Held"/> : ensemble COMPLET des touches actuellement enfoncées (null = la trame n'en dit rien, l'état est conservé).
/// <paramref name="Taps"/> : appuis instantanés sans trame de relâchement (la manette ne signale jamais le relâchement) ; chaque élément = un appui.
/// <paramref name="Detail"/> : précision pour le journal (octets reçus, écart entre deux appuis…), sans effet sur les touches.
/// </summary>
public sealed record DecodedFrame(IReadOnlySet<string>? Held, IReadOnlyList<string> Taps, int? BatteryPercent = null, string? Detail = null)
{
    public static readonly IReadOnlyList<string> NoTaps = [];
    /// <summary>Trame reconnue qui ne change rien (état, compteur…).</summary>
    public static readonly DecodedFrame Nothing = new(null, NoTaps);
    public static DecodedFrame Hold(params string[] held) => new(new HashSet<string>(held), NoTaps);
    public static DecodedFrame Tap(params string[] taps) => new(null, taps);
}

/// <summary>
/// Traduit les notifications d'une manette BLE en touches. Une instance par connexion (elle peut garder un état entre deux trames) ;
/// aucune dépendance Bluetooth, donc testable avec des trames brutes.
/// </summary>
public interface IButtonDecoder
{
    /// <summary>Null si la trame n'est pas reconnue (elle est alors journalisée).</summary>
    DecodedFrame? Decode(Guid characteristic, byte[] data);
}

/// <summary>Décodeur qui décide d'un appui APRÈS un délai (p. ex. simple ou double appui) : il lève <see cref="Deferred"/> plus tard.</summary>
public interface IDeferredDecoder : IDisposable
{
    event Action<DecodedFrame>? Deferred;
}

/// <summary>
/// Manette qui ne notifie pas ses boutons mais doit être interrogée (L-TWOO) : trames d'initialisation écrites à la connexion,
/// puis trames d'interrogation écrites à intervalle régulier.
/// </summary>
public interface IPollingDecoder
{
    IReadOnlyList<byte[]> InitFrames { get; }
    IReadOnlyList<byte[]> PollFrames { get; }
    TimeSpan PollInterval { get; }
}
