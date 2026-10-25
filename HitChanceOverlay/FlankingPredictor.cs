using HarmonyLib;
using Kingmaker;
using Kingmaker.Controllers.Combat;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.UnitLogic;
using TurnBased.Controllers;

namespace HitChanceOverlay;

/// <summary>
/// Predicts whether the target will be flanked once the attacker has walked up to it.
///
/// The game's flanking isn't positional. UnitCombatState.IsFlanked is simply "engaged by 2+ units" (turn-based), or
/// "engaged by 2+ units whose current command targets it" (real-time). A unit engages an enemy that is within its
/// melee threat range (UnitCombatEngagementController.TickUnit → UnitEngagementExtension.IsEngage). So when a melee
/// attacker isn't engaging the target yet, we can predict the count after the approach: the current engagers, plus the
/// attacker, plus the attacker's mount or rider. They share one position but engage separately, so a mounted
/// character can flank on their own.
///
/// Many features check flanking themselves instead of the rule's TargetIsFlanked flag: Outflank and Mad Dog Pack
/// Tactics look for a specific ally or pet in the target's EngagedBy, and Back to Back gives the target AC when it is
/// flanked. To let all of them (including other mods' features) react without mirroring each one, Simulate() makes
/// the game see the predicted state while we run the calculation rules: the target reports IsFlanked, and the
/// attacker and their mount are temporarily in its engaged-by list. Everything is restored straight afterwards,
/// synchronously on the main thread. Nothing else runs in between, and no events are raised.
/// </summary>
internal static class FlankingPredictor {
    private static UnitEntityData s_SimulatedTarget;

    /// <summary>The units that will be engaging the target after the approach, if that makes it flanked; otherwise null.</summary>
    public static HashSet<UnitEntityData> PredictEngagers(UnitEntityData attacker, UnitEntityData target, ItemEntityWeapon weapon) {
        if (!weapon.Blueprint.IsMelee || target.CombatState.IsFlanked || target.Descriptor.State.Features.CannotBeFlanked) {
            return null;
        }
        bool turnBased = CombatController.IsInTurnBasedCombat();
        if (turnBased && !target.IsInCombat) {
            return null; // mirrors IsFlanked: turn-based flanking needs the target in combat
        }

        var engagers = new HashSet<UnitEntityData>();
        foreach (var unit in target.CombatState.EngagedBy) {
            if (turnBased || unit.Commands.AnyCommandTargets(target)) {
                engagers.Add(unit);
            }
        }
        engagers.Add(attacker);

        // Mount + rider: they stand on the same spot, so check whether the partner's own reach covers the target from where
        // the attacker will stand (no farther than the attacking weapon's reach, or where it already is if that's closer).
        var partner = attacker.GetSaddledUnit() ?? attacker.GetRider();
        if (partner != null && !engagers.Contains(partner) && PartnerWillEngage(attacker, partner, target, weapon, turnBased)) {
            engagers.Add(partner);
        }
        return engagers.Count > 1 ? engagers : null;
    }

    /// <summary>While the returned scope is open, the game sees the target as flanked and engaged by <paramref name="engagers"/>.</summary>
    public static IDisposable Simulate(UnitEntityData target, HashSet<UnitEntityData> engagers) {
        var engagedBy = target.CombatState.m_EngagedBy;
        var added = new List<UnitEntityData>();
        var now = Game.Instance.TimeController.GameTime;
        foreach (var unit in engagers) {
            if (!engagedBy.ContainsKey(unit)) {
                engagedBy[unit] = now;
                added.Add(unit);
            }
        }
        s_SimulatedTarget = target;
        return new Restore(target, added);
    }

    private sealed class Restore : IDisposable {
        private readonly UnitEntityData m_Target;
        private readonly List<UnitEntityData> m_Added;

        public Restore(UnitEntityData target, List<UnitEntityData> added) {
            m_Target = target;
            m_Added = added;
        }

        public void Dispose() {
            s_SimulatedTarget = null;
            var engagedBy = m_Target.CombatState.m_EngagedBy;
            foreach (var unit in m_Added) {
                engagedBy.Remove(unit);
            }
        }
    }

    // Real-time IsFlanked counts only engagers whose command targets the unit (and the attacker has no command yet), so
    // force it while simulating. IsFlanked is a large getter, so it isn't inlined and the patch reliably applies.
    [HarmonyPatch(typeof(UnitCombatState), nameof(UnitCombatState.IsFlanked), MethodType.Getter)]
    private static class UnitCombatState_IsFlanked_Patch {
        private static bool Prefix(UnitCombatState __instance, ref bool __result) {
            if (s_SimulatedTarget == null || __instance.Unit != s_SimulatedTarget) {
                return true;
            }
            __result = true;
            return false;
        }
    }

    private static bool PartnerWillEngage(UnitEntityData attacker, UnitEntityData partner, UnitEntityData target, ItemEntityWeapon weapon, bool turnBased) {
        if (partner.Descriptor.State.IsDead || !partner.Descriptor.State.CanAct || !partner.IsEnemy(target)) {
            return false;
        }
        // Real-time only counts engagers that are attacking the target; the partner has to be doing so already.
        if (!turnBased && !partner.Commands.AnyCommandTargets(target)) {
            return false;
        }
        var hand = partner.GetThreatHand();
        float? threatRange = hand != null ? partner.GetThreatRange(hand) : null;
        if (!threatRange.HasValue) {
            return false;
        }
        float attackDistance = Math.Min(attacker.DistanceTo(target), attacker.Corpulence + target.Corpulence + weapon.AttackRange.Meters);
        // Mirrors UnitHelper.IsAttackOfOpportunityReach (strict <), using the predicted distance. The approach ends a little
        // inside the attacker's reach, so allow a small margin; otherwise equal reaches would compare as "not engaged".
        const float ApproachMargin = 0.1f;
        return attackDistance - ApproachMargin < partner.Corpulence + target.Corpulence + threatRange.Value;
    }
}
