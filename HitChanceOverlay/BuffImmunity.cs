using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Classes.Spells;
using Kingmaker.Designers.Mechanics.Buffs;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.UnitLogic.Buffs.Components;
using Kingmaker.UnitLogic.FactLogic;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.UnitLogic.Mechanics.Components;

namespace HitChanceOverlay;

/// <summary>
/// Whether a buff (debuff) a spell applies would actually do anything to the target. The game has two ways to shrug
/// one off:
///
/// 1. RuleCanApplyBuff: BuffDescriptorImmunity (e.g. immunity to Blindness / MindAffecting / Fear descriptors) and
///    SpecificBuffImmunity set Immunity in OnEventAboutToTrigger, and the buff is never added. We preview just that step.
/// 2. Condition immunity (AddConditionImmunity): the buff is added, but its AddCondition / BuffStatusCondition does
///    nothing ("X is immune to Blinded"). We treat a buff as having no effect when it only applies conditions the target
///    is immune to, plus bookkeeping components.
/// </summary>
internal static class BuffImmunity {
    public static bool IsImmune(UnitEntityData target, BlueprintBuff buff, MechanicsContext parent) {
        var context = parent != null ? parent.CloneFor(buff, target.Descriptor) : new MechanicsContext(null, target.Descriptor, buff);
        if (RulePreview.BuffImmunity(context, target, buff)) {
            return true;
        }
        return OnlyImmuneConditions(target, buff);
    }

    private static bool OnlyImmuneConditions(UnitEntityData target, BlueprintBuff buff) {
        bool anyCondition = false;
        foreach (var component in buff.ComponentsArray) {
            switch (component) {
                case AddCondition c:
                    if (!target.Descriptor.State.HasConditionImmunity(c.Condition)) {
                        return false;
                    }
                    anyCondition = true;
                    break;
                case BuffStatusCondition c:
                    if (!target.Descriptor.State.HasConditionImmunity(c.Condition)) {
                        return false;
                    }
                    anyCondition = true;
                    break;
                // Bookkeeping that does nothing on its own (descriptors, duration helpers, "can't be blinded twice").
                case SpellDescriptorComponent:
                case ContextRankConfig:
                case RemoveWhenCombatEnded:
                case RemoveBuffIfCasterIsMissing:
                case UniqueBuff:
                case RemovedByHeal:
                case SpellImmunityToSpellDescriptor:
                case BuffDescriptorImmunity:
                    break;
                default:
                    return false;
            }
        }
        return anyCondition;
    }
}
