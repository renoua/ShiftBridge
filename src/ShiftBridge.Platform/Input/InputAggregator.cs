using ShiftBridge.Core.Input;

namespace ShiftBridge.Platform.Input;

/// <summary>
/// Fusionne les boutons de plusieurs périphériques : une touche est « appuyée » dès qu'au moins un périphérique la tient,
/// « relâchée » quand le dernier la lâche. Évite que deux manettes qui envoient le même « pad:A » se marchent dessus
/// dans <c>MappingEngine</c> (état par touche, pas par périphérique). Thread-safe.
/// </summary>
public sealed class InputAggregator
{
    private readonly object _lock = new();
    private readonly Dictionary<string, HashSet<ButtonKey>> _bySource = [];
    private readonly Dictionary<ButtonKey, int> _count = [];

    /// <summary>Remplace l'état complet d'une source (manette, joystick…) et renvoie les changements visibles.</summary>
    public List<ButtonEvent> Replace(string source, IReadOnlySet<ButtonKey> pressed)
    {
        lock (_lock)
        {
            var events = new List<ButtonEvent>();
            var old = _bySource.GetValueOrDefault(source) ?? [];
            // Joysticks : jusqu'à 1000 rapports par seconde, presque tous identiques au précédent. Rien à copier ni à comparer.
            if (old.Count == pressed.Count && old.SetEquals(pressed)) return events;
            foreach (var k in old)
                if (!pressed.Contains(k)) Release(k, events);
            foreach (var k in pressed)
                if (!old.Contains(k)) Press(k, events);
            if (pressed.Count == 0) _bySource.Remove(source);
            else _bySource[source] = [.. pressed];
            return events;
        }
    }

    /// <summary>Un seul bouton d'une source (clavier : un événement par touche).</summary>
    public List<ButtonEvent> Set(string source, ButtonKey key, bool down)
    {
        lock (_lock)
        {
            var events = new List<ButtonEvent>();
            if (!_bySource.TryGetValue(source, out var set)) _bySource[source] = set = [];
            if (down) { if (set.Add(key)) Press(key, events); }
            else if (set.Remove(key)) Release(key, events);
            if (set.Count == 0) _bySource.Remove(source);
            return events;
        }
    }

    /// <summary>Une source disparaît (manette débranchée) : tout ce qu'elle tenait est relâché.</summary>
    public List<ButtonEvent> RemoveSource(string source) => Replace(source, new HashSet<ButtonKey>());

    /// <summary>Relâche tout (clavier désactivé, arrêt).</summary>
    public List<ButtonEvent> Clear()
    {
        lock (_lock)
        {
            var events = _count.Keys.Select(k => new ButtonEvent(k, false)).ToList();
            _count.Clear();
            _bySource.Clear();
            return events;
        }
    }

    private void Press(ButtonKey k, List<ButtonEvent> events)
    {
        int n = _count.GetValueOrDefault(k) + 1;
        _count[k] = n;
        if (n == 1) events.Add(new ButtonEvent(k, true));
    }

    private void Release(ButtonKey k, List<ButtonEvent> events)
    {
        int n = _count.GetValueOrDefault(k) - 1;
        if (n <= 0) { _count.Remove(k); events.Add(new ButtonEvent(k, false)); }
        else _count[k] = n;
    }
}
