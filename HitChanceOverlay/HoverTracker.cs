using HarmonyLib;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UI;
using TurnBased.Controllers;

namespace HitChanceOverlay;

/// <summary>
/// Works out which unit the mouse is over.
/// Real-time: the game calls CursorController.SetUnitCursor whenever a unit gains or loses hover highlight.
/// Turn-based: the TurnController tracks the hovered unit itself (m_HighlightedUnit) and does NOT call
/// SetUnitCursor while an ability is selected, so we read its field directly.
/// </summary>
[HarmonyPatch(typeof(CursorController), nameof(CursorController.SetUnitCursor))]
internal static class HoverTracker {
    private static UnitEntityData s_RealTimeHovered;

    public static UnitEntityData HoveredUnit => CombatController.IsInTurnBasedCombat()
        ? Game.Instance.TurnBasedCombatController?.CurrentTurn?.m_HighlightedUnit
        : s_RealTimeHovered;

    [HarmonyPostfix]
    private static void Postfix(bool isUnitHighlighted, UnitEntityData hoveredUnit) {
        if (isUnitHighlighted) {
            s_RealTimeHovered = hoveredUnit;
        } else if (s_RealTimeHovered == hoveredUnit) {
            s_RealTimeHovered = null;
        }
    }
}
