using ShiftBridge.Platform.Ble;
using System.Collections.ObjectModel;
using ShiftBridge.Platform.Trainer;

namespace ShiftBridge.Presentation;

/// <summary>
/// Met la liste affichée des trainers Bluetooth à jour SUR PLACE, au lieu de la vider et de la remplir à chaque annonce :
/// pas de clignotement, la sélection n'est pas perdue, et le trainer mémorisé reste proposé tant qu'on ne l'a pas revu.
/// </summary>
public static class TrainerListMerge
{
    /// <summary>
    /// Réconcilie <paramref name="list"/> avec les trainers vus par la recherche. Les trainers de <paramref name="keep"/>
    /// (sélectionné, mémorisé) restent dans la liste même s'ils ne sont pas vus : ce que la recherche voit l'emporte sur eux.
    /// La liste est triée par nom ; seules les lignes qui changent sont touchées.
    /// </summary>
    public static void Apply(ObservableCollection<DiscoveredBleTrainer> list, IEnumerable<DiscoveredBleTrainer> seen,
        IEnumerable<DiscoveredBleTrainer?> keep)
    {
        var wanted = new Dictionary<BleDeviceId, DiscoveredBleTrainer>();
        foreach (var k in keep) if (k is not null) wanted[k.Address] = k;
        foreach (var s in seen) wanted[s.Address] = s;
        var ordered = wanted.Values.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Address).ToList();

        for (int i = list.Count - 1; i >= 0; i--)
            if (!wanted.ContainsKey(list[i].Address)) list.RemoveAt(i);

        for (int i = 0; i < ordered.Count; i++)
        {
            var target = ordered[i];
            if (i < list.Count && list[i].Address == target.Address)
            {
                if (list[i] != target) list[i] = target; // même trainer : seule la ligne change (nom, protocoles, signal)
                continue;
            }
            int existing = IndexOf(list, target.Address, i + 1);
            if (existing >= 0)
            {
                list.Move(existing, i);
                if (list[i] != target) list[i] = target;
            }
            else list.Insert(i, target);
        }
    }

    private static int IndexOf(ObservableCollection<DiscoveredBleTrainer> list, BleDeviceId address, int from)
    {
        for (int i = from; i < list.Count; i++) if (list[i].Address == address) return i;
        return -1;
    }
}
