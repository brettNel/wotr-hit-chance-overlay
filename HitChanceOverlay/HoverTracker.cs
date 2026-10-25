using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UI;

namespace HitChanceOverlay;

/// <summary>
/// Remembers which unit the mouse is over. The game calls CursorController.SetUnitCursor
/// whenever a unit gains or loses hover highlight, in both real-time and turn-based modes.
/// </summary>
[HarmonyPatch(typeof(CursorController), nameof(CursorController.SetUnitCursor))]
internal static class HoverTracker {
    public static UnitEntityData HoveredUnit { get; private set; }

    [HarmonyPostfix]
    private static void Postfix(bool isUnitHighlighted, UnitEntityData hoveredUnit) {
        if (isUnitHighlighted) {
            HoveredUnit = hoveredUnit;
        } else if (HoveredUnit == hoveredUnit) {
            HoveredUnit = null;
        }
    }
}
