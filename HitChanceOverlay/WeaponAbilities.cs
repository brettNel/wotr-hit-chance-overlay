using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Root;
using Kingmaker.Designers.EventConditionActionSystem.Actions;
using Kingmaker.ElementsSystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Buffs.Components;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.Utility;

namespace HitChanceOverlay;

/// <summary>
/// Abilities that are really "make a weapon attack" (Charge, Vital Strike, Cleave, Devastating Blow, and effects with
/// ContextActionMeleeAttack / ContextActionRangedAttack). They have no attack roll of their own, so without this the
/// overlay would show 100% ("the ability works") instead of the chance the attack hits.
/// </summary>
internal static class WeaponAbilities {
    internal readonly record struct Plan(ItemEntityWeapon Weapon, int Penalty, bool Charge);

    public static Plan? Detect(BlueprintAbility payload, UnitEntityData caster, EffectSummary effects) {
        // Mirrors which weapon each AbilityCustom*.Deliver attacks with.
        if (payload.GetComponent<AbilityCustomCharge>() != null || payload.GetComponent<AbilityDemonCharge>() != null
            || payload.GetComponent<AbilityGriffinAttack>() != null) {
            return Make(caster.GetThreatHandMelee(excludeLimbs: true)?.Weapon, 0, charge: true);
        }
        var custom = payload.GetComponent<AbilityCustomMeleeAttack>();
        if (custom != null) {
            // The only attack type is Devastating Blow: RuleAttackWithWeapon with a -5 penalty.
            return Make(caster.GetThreatHandMelee()?.Weapon, custom.m_Type == AbilityCustomMeleeAttack.AttackType.DevastatingBlow ? 5 : 0, false);
        }
        if (payload.GetComponent<AbilityCustomVitalStrike>() != null) {
            return Make(caster.GetFirstWeapon(), 0, false);
        }
        if (payload.GetComponent<AbilityCustomCleave>() != null || payload.GetComponent<AbilityCustomFly>() != null) {
            return Make(caster.GetThreatHand()?.Weapon, 0, false);
        }
        if (effects?.MeleeWeaponAttack == true) {
            return Make(caster.GetThreatHandMelee()?.Weapon, 0, false);
        }
        if (effects?.RangedWeaponAttack == true) {
            var weapon = caster.Body.PrimaryHand.MaybeWeapon;
            return Make(weapon != null && weapon.Blueprint.IsRanged ? weapon : null, 0, false);
        }
        return null;
    }

    private static Plan? Make(ItemEntityWeapon weapon, int penalty, bool charge) => weapon == null ? null : new Plan(weapon, penalty, charge);

    /// <summary>
    /// The attack bonus the charge buff will add. Mirrors AbilityCustomCharge.Deliver: AddBuff(ChargeBuff, context), whose
    /// AddBuffActions pick an effect buff with a Conditional (Cavalier's Charge while mounted: +4, otherwise +2).
    /// </summary>
    public static int ChargeBonus(AbilityData ability, UnitEntityData caster, UnitEntityData target, ItemEntityWeapon weapon) {
        var chargeBuff = BlueprintRoot.Instance.SystemMechanics.ChargeBuff;
        if (chargeBuff == null || caster.Buffs.GetBuff(chargeBuff) != null) {
            return 0;
        }
        var abilityContext = ability.CreateExecutionContext(new TargetWrapper(target));
        var context = abilityContext.CloneFor(chargeBuff, caster.Descriptor);
        context.Recalculate();
        int bonus = 0;
        foreach (var actions in chargeBuff.GetComponents<AddBuffActions>()) {
            bonus += Walk(actions.Activated, caster, context, weapon, 0);
        }
        return bonus;
    }

    private static int Walk(ActionList list, UnitEntityData caster, MechanicsContext context, ItemEntityWeapon weapon, int depth) {
        if (list?.Actions == null || depth > 6) {
            return 0;
        }
        int bonus = 0;
        foreach (var action in list.Actions) {
            switch (action) {
                case Conditional conditional:
                    bool passed;
                    // The buff's actions run with the buff owner (the charger) as target.
                    using (ContextData<MechanicsContext.Data>.Request().Setup(context, new TargetWrapper(caster))) {
                        passed = conditional.ConditionsChecker.Check();
                    }
                    bonus += Walk(passed ? conditional.IfTrue : conditional.IfFalse, caster, context, weapon, depth + 1);
                    break;
                case ContextActionApplyBuff apply when apply.Buff != null && !apply.ToCaster:
                    bonus += PendingToggles.BuffAttackBonus(caster, apply.Buff, context, weapon);
                    break;
            }
        }
        return bonus;
    }
}
