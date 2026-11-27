namespace ShiftBridge.Core.Input;

public readonly record struct ButtonEvent(ButtonKey Button, bool Pressed);

/// <summary>
/// Compare chaque trame à la précédente pour produire des événements appuyé/relâché.
/// Une manette qui se déconnecte doit appeler <see cref="Reset"/> pour relâcher les touches restées enfoncées.
/// </summary>
public sealed class ButtonStateTracker
{
    private HashSet<ButtonKey> _current = [];

    public IReadOnlyCollection<ButtonKey> Current => _current;

    private static readonly List<ButtonEvent> NoEvents = [];

    /// <returns>Les changements ; une liste vide PARTAGÉE (à ne pas modifier) quand rien n'a changé, le cas de presque toutes les trames.</returns>
    public List<ButtonEvent> Update(IReadOnlySet<ButtonKey> pressedNow)
    {
        if (pressedNow.Count == _current.Count && _current.SetEquals(pressedNow)) return NoEvents; // trame identique : aucun tri, aucune copie
        var events = new List<ButtonEvent>();
        foreach (var b in _current)
            if (!pressedNow.Contains(b)) events.Add(new ButtonEvent(b, false));
        foreach (var b in pressedNow)
            if (!_current.Contains(b)) events.Add(new ButtonEvent(b, true));
        _current = [.. pressedNow];
        return events;
    }

    /// <summary>Variante pour les manettes Zwift, dont les trames décodées sont des <see cref="PhysicalButton"/>.</summary>
    public List<ButtonEvent> Update(IReadOnlySet<PhysicalButton> pressedNow)
    {
        // Trames au repos ou touche tenue (~30 par seconde) : comparées sans rien allouer.
        if (pressedNow.Count == _current.Count)
        {
            bool same = true;
            foreach (var b in pressedNow) if (!_current.Contains(b)) { same = false; break; }
            if (same) return NoEvents;
        }
        var keys = new HashSet<ButtonKey>(pressedNow.Count);
        foreach (var b in pressedNow) keys.Add(b);
        return Update(keys);
    }

    public List<ButtonEvent> Reset() => Update(new HashSet<ButtonKey>());
}
