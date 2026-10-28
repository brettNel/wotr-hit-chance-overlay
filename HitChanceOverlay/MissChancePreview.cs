using Kingmaker.Designers.Mechanics.Facts;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.Items;
using Kingmaker.PubSubSystem;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;
using Kingmaker.UnitLogic.Mechanics.Components;
using Kingmaker.UnitLogic.Parts;

namespace HitChanceOverlay;

/// <summary>
/// The chance an attack is lost before the d20: concealment (invisibility, Blur, darkness, ...) or a miss chance set on the
/// attack itself. Mirrors RuleAttackRoll.TryOvercomeTargetConcealmentAndMissChance and RuleConcealmentCheck, including
/// rerolls (Blind-Fight). Only the components that change these numbers are run; see <see cref="ArmedAttackBonuses.Run"/>.
/// </summary>
internal static class MissChancePreview {
    internal readonly record struct Result(double MissPercent, string Note);

    // Handlers of RuleAttackRoll that set its miss chance (the target's facts, the attacker's facts).
    private static readonly HashSet<Type> s_AttackTargetHandlers = new() { typeof(SetAttackerMissChance.Runtime) };
    private static readonly HashSet<Type> s_AttackInitiatorHandlers = new() { typeof(SetFactOwnerMissChance.Runtime) };
    // Subscribes to RulebookTargetEvent and changes the miss chance of any rule that has one (attack rolls, concealment).
    private static readonly HashSet<Type> s_ModifyHandlers = new() { typeof(ModifyAttackerMissChance) };

    public static Result Calculate(UnitEntityData attacker, UnitEntityData target, RuleAttackRoll attackRoll, ItemEntityWeapon weapon, bool weaponAttack) {
        var targetManager = RulebookEventBus.TargetRulebookSubscribers.Get(target);
        var initiatorManager = RulebookEventBus.InitiatorRulebookSubscribers.Get(attacker);
        var concealment = UnitPartConcealment.Calculate(attacker, target, attack: true);

        int attackMissChance;
        using (var disarm = new ArmedAttackBonuses.Disarm()) {
            ArmedAttackBonuses.Run(targetManager, attackRoll, s_AttackTargetHandlers, disarm);
            ArmedAttackBonuses.Run<RulebookTargetEvent, ITargetRulebookSubscriber>(targetManager, attackRoll, s_ModifyHandlers, disarm);
            ArmedAttackBonuses.Run(initiatorManager, attackRoll, s_AttackInitiatorHandlers, disarm);
            attackMissChance = attackRoll.missChance.Calculate();
        }
        // An attack's own miss chance replaces concealment when it is higher.
        if (attackMissChance > 0 && (concealment != Concealment.Total || attackMissChance > 50)
            && (concealment != Concealment.Partial || attackMissChance > 20)) {
            return new Result(attackMissChance, $"{attackMissChance}% miss chance");
        }
        if (concealment == Concealment.None) {
            return new Result(0, null);
        }

        var check = new RuleConcealmentCheck(attacker, target, attack: true);
        using (var disarm = new ArmedAttackBonuses.Disarm()) {
            ArmedAttackBonuses.Run<RulebookTargetEvent, ITargetRulebookSubscriber>(targetManager, check, s_ModifyHandlers, disarm);
        }
        check.missChance.Set(concealment == Concealment.Partial ? 20 : 50);
        int value = check.missChance.Calculate();
        if (value <= 0) {
            return new Result(0, null);
        }
        // Each reroll source rerolls a failed check once, keeping the better roll.
        int rerolls = CountRerolls(initiatorManager, weapon, weaponAttack);
        double miss = 100 * Math.Pow(value / 100.0, rerolls + 1);
        return new Result(miss, rerolls > 0 ? $"{value}% concealment, rerolled" : $"{value}% concealment");
    }

    // Mirrors RerollConcealment.ShouldReroll. Without a weapon attack (rays, touch spells) its weapon filter finds no
    // RuleAttackWithWeapon and rerolls anyway.
    private static int CountRerolls(RulebookSubscriptionManager<IInitiatorRulebookSubscriber> manager, ItemEntityWeapon weapon, bool weaponAttack) {
        if (manager == null || !manager.m_Listeners.TryGetValue(typeof(RuleConcealmentCheck), out var list)
            || list is not RulebookSubscribersList<RuleConcealmentCheck> typed) {
            return 0;
        }
        int count = 0;
        foreach (var entry in typed.List) {
            if (((entry as ISubscriptionProxy)?.GetSubscriber() ?? entry) is not RerollConcealment reroll) {
                continue;
            }
            bool applies = reroll.m_WeaponCoverage switch {
                RerollConcealment.WeaponCoverage.Melee => !weaponAttack || weapon.Blueprint.IsMelee,
                RerollConcealment.WeaponCoverage.Ranged => !weaponAttack || weapon.Blueprint.IsRanged,
                _ => true,
            };
            if (applies) {
                count++;
            }
        }
        return count;
    }
}
