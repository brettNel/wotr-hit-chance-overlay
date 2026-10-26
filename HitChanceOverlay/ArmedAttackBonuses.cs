using Kingmaker.Designers.Mechanics.Facts;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;

namespace HitChanceOverlay;

/// <summary>
/// Some attack bonuses are "armed" when the attack starts: the component remembers the bonus and the target in
/// OnEventAboutToTrigger(RuleAttackWithWeapon / RuleAttackRoll), adds it in RuleCalculateAttackBonus, and forgets it in
/// OnEventDidTrigger. Examples: Shortbow of Code (+1 vs evil), bonuses against creatures with a fact, favored enemy shared
/// with a pet, the charge effect's +2. Previewing RuleCalculateAttackBonus alone misses them, so we arm just these components with stand-in rules
/// (never triggered) and disarm them afterwards. Other handlers of those rules are not called: some have real side effects.
/// </summary>
internal static class ArmedAttackBonuses {
    private static readonly HashSet<Type> s_Allowed = new() {
        typeof(AttackBonusAgainstAlignment),
        typeof(AttackBonusAgainstFactOwner),
        typeof(AttackBonusConditional),
        typeof(ShareFavoredEnemies),
        // The charge effect's +2 on non-AoO attacks (and AoO-only bonuses, which our non-AoO stand-in never arms).
        typeof(AttackOfOpportunityAttackBonus),
    };

    public static IDisposable Arm(UnitEntityData attacker, RuleAttackWithWeapon attackWithWeapon, RuleAttackRoll attackRoll) {
        var manager = RulebookEventBus.InitiatorRulebookSubscribers.Get(attacker);
        if (manager == null) {
            return null;
        }
        var disarm = new Disarm();
        // Same order as the game: the weapon attack starts, then its attack roll.
        if (attackWithWeapon != null) {
            Arm(manager, attackWithWeapon, disarm);
        }
        if (attackRoll != null) {
            Arm(manager, attackRoll, disarm);
        }
        return disarm;
    }

    private static void Arm<T>(RulebookSubscriptionManager<IInitiatorRulebookSubscriber> manager, T evt, Disarm disarm) where T : RulebookEvent {
        if (!manager.m_Listeners.TryGetValue(typeof(T), out var list) || list is not RulebookSubscribersList<T> typed) {
            return;
        }
        foreach (var entry in typed.List.ToArray()) {
            if (entry is not ISubscriptionProxy proxy || proxy.GetSubscriber() is not IRulebookHandler<T> handler
                || !s_Allowed.Contains(handler.GetType())) {
                continue;
            }
            try {
                using (proxy.RequestEventContext()) {
                    handler.OnEventAboutToTrigger(evt);
                }
            } catch (Exception e) {
                Overlay.LogOnce($"Could not arm {handler.GetType().Name}: {e.Message}");
            }
            // Mirrors the handlers' OnEventDidTrigger, which clears what they remembered.
            disarm.Actions.Add(() => {
                using (proxy.RequestEventContext()) {
                    handler.OnEventDidTrigger(evt);
                }
            });
        }
    }

    private sealed class Disarm : IDisposable {
        public readonly List<Action> Actions = new();

        public void Dispose() {
            foreach (var action in Actions) {
                try {
                    action();
                } catch (Exception e) {
                    Overlay.LogOnce("Could not disarm attack bonus: " + e.Message);
                }
            }
        }
    }
}
