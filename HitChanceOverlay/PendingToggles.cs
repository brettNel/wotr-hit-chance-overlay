using Kingmaker.Blueprints;
using Kingmaker.Designers.Mechanics.Buffs;
using Kingmaker.Designers.Mechanics.Facts;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.Enums;
using Kingmaker.Items;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.ActivatableAbilities;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.UnitLogic.FactLogic;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.UnitLogic.Mechanics.Components;
using Kingmaker.UnitLogic.Parts;

namespace HitChanceOverlay;

/// <summary>
/// Attack-roll toggles whose effect isn't in the attacker's stats yet when we preview, so the game's calculation
/// rules alone would miss them. Two patterns exist in the game's data:
///
/// 1. "OnUnitAction" activatables (Fight Defensively, Combat Expertise): switching them on does nothing yet. Their buff
///    is applied when the attack starts (ActivatableAbility.OnEventAboutToTrigger(RuleAttackWithWeapon) → TryStart).
/// 2. Toggles whose buff only holds an AddInitiatorAttackWithWeaponTrigger(TriggerBeforeAttack) that applies the real
///    "effect" buff right before each attack (Power Attack, Deadly Aim, Piranha Strike).
///
/// 3. The Point-Blank Shot toggle (PointBlankRange) caps ranged weapon range at 30 ft, so against a farther target the
///    character walks in before shooting and "within X ft" bonuses (DistanceAttackBonus) will apply at the firing spot.
///
/// For 1 and 2 we work out the attack bonus the buff WILL add, without applying anything, by evaluating its components
/// with the same context the game would build (BuffCollection.AddBuff → parentContext.CloneFor(buff, owner)).
/// </summary>
internal static class PendingToggles {
    internal readonly record struct Pending(int AttackBonus, string Names);

    public static Pending AttackBonus(UnitEntityData attacker, UnitEntityData target, ItemEntityWeapon weapon) {
        int total = 0;
        List<string> names = null;
        try {
            PointBlankApproach(attacker, target, weapon, ref total, ref names);
        } catch (Exception e) {
            Overlay.LogOnce("Could not preview point-blank approach: " + e);
        }
        foreach (var ability in attacker.ActivatableAbilities.RawFacts) {
            if (!ability.IsOn) {
                continue;
            }
            try {
                int bonus = PendingBonus(attacker, ability, weapon);
                if (bonus != 0) {
                    total += bonus;
                    (names ??= new()).Add($"{ability.Name} {(bonus > 0 ? "+" : "")}{bonus}");
                }
            } catch (Exception e) {
                Overlay.LogOnce($"Could not preview toggle {ability.Blueprint.name}: {e}");
            }
        }
        return new Pending(total, names == null ? null : string.Join(", ", names));
    }

    // Mirrors UnitDescriptor.GetWeaponRange (PointBlankRange caps range at 30 ft) and DistanceAttackBonus.OnEventAboutToTrigger.
    private static void PointBlankApproach(UnitEntityData attacker, UnitEntityData target, ItemEntityWeapon weapon, ref int total, ref List<string> names) {
        if (!attacker.State.Features.PointBlankRange || !weapon.Blueprint.IsRanged || weapon.Blueprint.AttackRange.Value <= 30) {
            return;
        }
        float distance = target.DistanceTo(attacker);
        foreach (var fact in attacker.Facts.List) {
            if (!fact.Active) {
                continue;
            }
            foreach (var component in fact.Components) {
                if (!component.IsActive || component.SourceBlueprintComponent is not DistanceAttackBonus bonus || bonus.Range.Value < 30) {
                    continue;
                }
                // Already applied by the game's own rule when we're within range now.
                if (distance <= bonus.Range.Meters + target.Corpulence + attacker.Corpulence) {
                    continue;
                }
                total += bonus.AttackBonus;
                (names ??= new()).Add($"{fact.Name} {(bonus.AttackBonus > 0 ? "+" : "")}{bonus.AttackBonus} after moving in");
            }
        }
    }

    private static int PendingBonus(UnitEntityData attacker, ActivatableAbility ability, ItemEntityWeapon weapon) {
        var blueprint = ability.Blueprint;
        if (blueprint.Buff == null) {
            return 0;
        }

        // Pattern 1: armed but not started.
        if (!ability.IsStarted) {
            if (!blueprint.ActivateOnUnitAction || blueprint.ActivateOnUnitActionType != AbilityActivateOnUnitActionType.Attack
                || !ability.IsAvailable) {
                return 0;
            }
            var parent = new MechanicsContext(attacker, null, blueprint);
            return BuffAttackBonus(attacker, blueprint.Buff, parent, weapon);
        }

        // Pattern 2: started; look for "apply an effect buff before each attack" triggers on its buff.
        var applied = ability.AppliedBuff;
        if (applied == null) {
            return 0;
        }
        int bonus = 0;
        foreach (var trigger in blueprint.Buff.GetComponents<AddInitiatorAttackWithWeaponTrigger>()) {
            if (!trigger.TriggerBeforeAttack || !trigger.ActionsOnInitiator) {
                continue;
            }
            if (trigger.CheckWeaponRangeType && !trigger.RangeType.IsSuitableWeapon(weapon)) {
                continue;
            }
            foreach (var action in trigger.Action?.Actions ?? Array.Empty<Kingmaker.ElementsSystem.GameAction>()) {
                if (action is not ContextActionApplyBuff apply || apply.ToCaster || apply.Buff == null) {
                    continue;
                }
                // After the first attack this round the effect buff is already on the unit (and in its stats).
                if (attacker.Buffs.GetBuff(apply.Buff) != null) {
                    continue;
                }
                bonus += BuffAttackBonus(attacker, apply.Buff, applied.Context, weapon);
            }
        }
        return bonus;
    }

    private static int BuffAttackBonus(UnitEntityData attacker, BlueprintBuff buff, MechanicsContext parent, ItemEntityWeapon weapon) {
        var context = parent.CloneFor(buff, attacker.Descriptor);
        context.Recalculate();

        int bonus = 0;
        foreach (var component in buff.ComponentsArray) {
            switch (component) {
                case AddStatBonusAbilityValue c when c.Stat == StatType.AdditionalAttackBonus:
                    bonus += c.Value.Calculate(context);
                    break;
                case AddContextStatBonus c when c.Stat == StatType.AdditionalAttackBonus:
                    bonus += c.Value.Calculate(context) * c.Multiplier;
                    break;
                case AddStatBonus c when c.Stat == StatType.AdditionalAttackBonus:
                    int value = c.Value;
                    if (c.ScaleByBasicAttackBonus) {
                        value *= 1 + attacker.Stats.BaseAttackBonus.ModifiedValue / 4;
                    }
                    bonus += value;
                    break;
                // Mirrors AttackTypeAttackBonus.OnEventAboutToTrigger (e.g. Combat Expertise giving ranged attacks the penalty back).
                case AttackTypeAttackBonus c:
                    if (c.CheckFact && !attacker.Descriptor.HasFact(c.RequiredFact)) {
                        break;
                    }
                    if (c.Type.IsSuitableWeapon(weapon.Blueprint) != c.AllTypesExcept) {
                        bonus += c.Value.Calculate(context) * c.AttackBonus;
                    }
                    break;
                case WeaponParametersAttackBonus c:
                    bonus += WeaponParametersBonus(c, attacker, weapon, context);
                    break;
            }
        }
        return bonus;
    }

    // Mirrors WeaponParametersAttackBonus.OnEventAboutToTrigger (Power Attack, Deadly Aim, Piranha Strike effects).
    private static int WeaponParametersBonus(WeaponParametersAttackBonus c, UnitEntityData attacker, ItemEntityWeapon weapon, MechanicsContext context) {
        if (c.OnlyForFullAttack) {
            return 0; // we preview a single attack
        }
        int value = c.UseContextIstead ? c.ContextAttackBonus.Calculate(context) : c.AttackBonus;
        if (c.ScaleByBasicAttackBonus) {
            value *= 1 + attacker.Stats.BaseAttackBonus.ModifiedValue / 4;
        }
        var blueprint = weapon.Blueprint;
        bool finesseOk = true;
        if (c.OnlyFinessable && !blueprint.Type.Category.HasSubCategory(WeaponSubCategory.Finessable)
            && !(attacker.Descriptor.Get<UnitPartDamageGrace>()?.HasEntry(blueprint.Category) ?? false)) {
            finesseOk = c.CanBeUsedWithFightersFinesse && c.FightersFinesse != null && attacker.HasFact(c.FightersFinesse)
                && (attacker.Descriptor.Get<UnitPartWeaponTraining>()?.GetWeaponRank(weapon) ?? 0) > 0;
        }
        bool rangeOk = c.Ranged == blueprint.IsRanged;
        bool twoHandedOk = !c.OnlyTwoHanded || blueprint.IsTwoHanded;
        bool gripOk = !c.m_checkGripStyle
            || (c.m_gripStyle == GripType.TwoHanded && weapon.HoldInTwoHands)
            || (c.m_gripStyle == GripType.OneHanded && !weapon.HoldInTwoHands);
        bool specificOk = c.SpecificWeaponBlueprint && c.WeaponBlueprint == blueprint;
        int multiplier = c.MultiplierFact != null && attacker.HasFact(c.MultiplierFact) ? c.Multiplier : 1;
        if ((gripOk && finesseOk && rangeOk && twoHandedOk && !c.SpecificWeaponBlueprint) || specificOk) {
            return value * multiplier;
        }
        return 0;
    }
}
