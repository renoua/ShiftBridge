using System.Collections.ObjectModel;

namespace ShiftBridge.Presentation;

/// <summary>
/// Réconcilie une liste affichée avec son contenu voulu SUR PLACE : seules les lignes ajoutées, retirées, déplacées ou
/// modifiées sont touchées. Vider puis remplir la liste à chaque événement (batterie, signal…) faisait reconstruire toute la
/// liste à l'écran (clignotement, menu ouvert refermé, défilement perdu).
/// </summary>
public static class ListMerge
{
    /// <param name="ordered">Contenu voulu, dans l'ordre d'affichage.</param>
    /// <param name="key">Identité d'une ligne (l'appareil) : deux lignes de même clé sont la même ligne, éventuellement modifiée.</param>
    /// <param name="same">Vrai si la ligne affichée est déjà identique à la nouvelle (elle n'est alors pas remplacée).</param>
    public static void Apply<T, TKey>(ObservableCollection<T> list, IReadOnlyList<T> ordered, Func<T, TKey> key, Func<T, T, bool> same)
        where TKey : notnull
    {
        var wanted = new HashSet<TKey>(ordered.Select(key));
        for (int i = list.Count - 1; i >= 0; i--)
            if (!wanted.Contains(key(list[i]))) list.RemoveAt(i);

        for (int i = 0; i < ordered.Count; i++)
        {
            var target = ordered[i];
            var k = key(target);
            if (i < list.Count && EqualityComparer<TKey>.Default.Equals(key(list[i]), k))
            {
                if (!same(list[i], target)) list[i] = target;
                continue;
            }
            int existing = -1;
            for (int j = i + 1; j < list.Count; j++)
                if (EqualityComparer<TKey>.Default.Equals(key(list[j]), k)) { existing = j; break; }
            if (existing >= 0)
            {
                list.Move(existing, i);
                if (!same(list[i], target)) list[i] = target;
            }
            else list.Insert(i, target);
        }
    }
}
