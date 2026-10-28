using Kingmaker.Designers.Mechanics.Buffs;
using Kingmaker.Designers.Mechanics.EquipmentEnchants;
using Kingmaker.Designers.Mechanics.Facts;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;

namespace HitChanceOverlay;

/// <summary>
/// Some effects are "armed" when the attack starts: the component remembers a bonus in OnEventAboutToTrigger(RuleAttackWithWeapon
/// / RuleAttackRoll), applies it in RuleCalculateAttackBonus or RuleCalculateAC, and forgets it in OnEventDidTrigger. Others
/// change the attack roll itself (its attack type, an automatic miss) or the attacker's concealment rules for that attack.
/// Examples: Shortbow of Code (+1 vs chaotic), the charge effect's +2, a target's AC bonus against evil or against a
/// creature type, weapons that ignore concealment. Previewing the calculation rules alone misses them, so we arm just these
/// components with stand-in rules (never triggered) and disarm them afterwards. Other handlers of those rules are not
/// called: some have real side effects (rolls, spent attacks of opportunity, removed buffs).
/// </summary>
internal static class ArmedAttackBonuses {
    // The attacker's facts.
    private static readonly HashSet<Type> s_Initiator = new() {
        typeof(AttackBonusAgainstAlignment),
        typeof(AttackBonusAgainstFactOwner),
        typeof(AttackBonusConditional),
        typeof(ShareFavoredEnemies),
        // The charge effect's +2 on non-AoO attacks (and AoO-only bonuses, which our non-AoO stand-in never arms).
        typeof(AttackOfOpportunityAttackBonus),
        typeof(ChargeAttackBonus),
        typeof(ACBonusAgainstTarget),
        typeof(AttackTypeChange),
        typeof(MissAgainstFactOwner),
        typeof(IgnoreConcealmentAgainstFactOwner),
        typeof(IgnorePartialConcealmentOnRangedAttacks),
    };

    // The target's facts: AC bonuses against certain attackers (alignment, size, facts).
    private static readonly HashSet<Type> s_Target = new() {
        typeof(ACBonusAgainstFactOwner),
        typeof(ACBonusAgainstFactOwnerMultiple),
        typeof(ACBonusAgainstBuffOwner),
        typeof(ACBonusAgainstSize),
        typeof(ACBonusUnlessFactMultiple),
        typeof(ACContextBonusAgainstFactOwner),
    };

    // The target's facts armed by a weapon attack starting: Protection from Good/Evil's AC against that alignment, AC
    // against melee/ranged/touch attacks, AC against attacks of opportunity (our stand-in is never one).
    private static readonly HashSet<Type> s_TargetWeaponAttack = new() {
        typeof(ArmorClassBonusAgainstAlignment),
        typeof(ACBonusAgainstAttacks),
        typeof(ACBonusAgainstAttackOfOpportunity),
    };

    // Equipment enchantments subscribe globally and check the wearer themselves.
    private static readonly HashSet<Type> s_Global = new() {
        typeof(ACBonusAgainstFactOwnerEquipment),
    };

    public static IDisposable Arm(UnitEntityData attacker, UnitEntityData target, RuleAttackWithWeapon attackWithWeapon, RuleAttackRoll attackRoll) {
        var initiator = RulebookEventBus.InitiatorRulebookSubscribers.Get(attacker);
        var disarm = new Disarm();
        // Same order as the game: the weapon attack starts, then its attack roll (global, target, then initiator handlers).
        var targetManager = RulebookEventBus.TargetRulebookSubscribers.Get(target);
        if (attackWithWeapon != null) {
            Run(targetManager, attackWithWeapon, s_TargetWeaponAttack, disarm);
            Run(initiator, attackWithWeapon, s_Initiator, disarm);
        }
        Run(RulebookEventBus.GlobalRulebookSubscribers, attackRoll, s_Global, disarm);
        Run(targetManager, attackRoll, s_Target, disarm);
        Run(initiator, attackRoll, s_Initiator, disarm);
        return disarm;
    }

    /// <summary>
    /// Calls OnEventAboutToTrigger on the handlers of <typeparamref name="T"/> registered with <paramref name="manager"/>
    /// whose type is in <paramref name="allowed"/>, and queues their OnEventDidTrigger on <paramref name="disarm"/>.
    /// T is the type the handlers subscribe to, which may be a base of the rule (RulebookTargetEvent).
    /// </summary>
    public static void Run<T, TSubscriber>(RulebookSubscriptionManager<TSubscriber> manager, T evt, HashSet<Type> allowed, Disarm disarm)
        where T : RulebookEvent where TSubscriber : class {
        if (manager == null || !manager.m_Listeners.TryGetValue(typeof(T), out var list) || list is not RulebookSubscribersList<T> typed) {
            return;
        }
        foreach (var entry in typed.List.ToArray()) {
            // Like RulebookSubscribersList: an entry is either a proxy (component delegates, called inside its event
            // context) or the handler itself (runtime components such as SetAttackerMissChance.Runtime).
            var proxy = entry as ISubscriptionProxy;
            if ((proxy != null ? proxy.GetSubscriber() : entry) is not IRulebookHandler<T> handler || !allowed.Contains(handler.GetType())) {
                continue;
            }
            try {
                using (proxy?.RequestEventContext()) {
                    handler.OnEventAboutToTrigger(evt);
                }
            } catch (Exception e) {
                Overlay.LogOnce($"Could not arm {handler.GetType().Name}: {e.Message}");
            }
            // Mirrors the handlers' OnEventDidTrigger, which clears what they remembered.
            disarm.Actions.Add(() => {
                using (proxy?.RequestEventContext()) {
                    handler.OnEventDidTrigger(evt);
                }
            });
        }
    }

    internal sealed class Disarm : IDisposable {
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
