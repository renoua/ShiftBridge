using ShiftBridge.Core.Obc;

namespace ShiftBridge.Core.Gears;

public enum GearChange { None, Changed, AtTopLimit, AtBottomLimit }

/// <summary>
/// Rapport virtuel local, affiché dans l'interface et utilisé pour le retour haptique (butées).
/// L'appli d'entraînement tient son propre état : on ne fait que transmettre ShiftUp/ShiftDown.
/// </summary>
public sealed class VirtualGearState(int gearCount = 24, int initialGear = 12)
{
    public int GearCount { get; private set; } = Math.Max(1, gearCount);
    public int Gear { get; private set; } = Math.Clamp(initialGear, 1, Math.Max(1, gearCount));

    /// <summary>
    /// Change le nombre de rapports et le rapport courant. Les deux valeurs sont écrites dans l'ordre qui garde toujours
    /// <c>1 ≤ Gear ≤ GearCount</c> pour un lecteur concurrent (l'interface lit sans verrou).
    /// </summary>
    public void Reconfigure(int gearCount, int gear)
    {
        int count = Math.Max(1, gearCount);
        if (count < GearCount) { Gear = Math.Clamp(gear, 1, count); GearCount = count; }
        else { GearCount = count; Gear = Math.Clamp(gear, 1, count); }
    }

    /// <summary>Applique une action de vitesse. <paramref name="target"/> n'est lu que par « Aller au rapport » (rapport visé) et « Monter / Descendre de X vitesses » (X).</summary>
    public GearChange Apply(ObcButtonId action, int target = 0)
    {
        switch (action)
        {
            case ObcButtonId.CenterGear:
                return GoTo((GearCount + 1) / 2);
            case ObcButtonId.GoToGear:
                // Cible non réglée (champ vide, profil changé entre-temps) : ne rien faire plutôt que sauter au rapport 1.
                if (target <= 0) return GearChange.None;
                return GoTo(target);
            case ObcButtonId.ShiftUp:
                if (Gear >= GearCount) return GearChange.AtTopLimit;
                Gear++;
                return GearChange.Changed;
            case ObcButtonId.ShiftDown:
                if (Gear <= 1) return GearChange.AtBottomLimit;
                Gear--;
                return GearChange.Changed;
            case ObcButtonId.ShiftUpBy:
                if (target <= 0) return GearChange.None;
                if (Gear >= GearCount) return GearChange.AtTopLimit;
                return GoTo(Gear + target);
            case ObcButtonId.ShiftDownBy:
                if (target <= 0) return GearChange.None;
                if (Gear <= 1) return GearChange.AtBottomLimit;
                return GoTo(Gear - target);
            default:
                return GearChange.None;
        }
    }

    /// <summary>Saute directement au rapport demandé (borné à 1..N). <see cref="GearChange.None"/> si on y est déjà : ni clignotement ni vibration.</summary>
    public GearChange GoTo(int gear)
    {
        int next = Math.Clamp(gear, 1, GearCount);
        if (next == Gear) return GearChange.None;
        Gear = next;
        return GearChange.Changed;
    }

    public void Reset(int gear) => Gear = Math.Clamp(gear, 1, GearCount);
}
