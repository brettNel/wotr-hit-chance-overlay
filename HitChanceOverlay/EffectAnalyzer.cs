using System.Reflection;
using Kingmaker.Designers.EventConditionActionSystem.Actions;
using Kingmaker.ElementsSystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.Enums.Damage;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.Utility;
using Kingmaker.Blueprints;

namespace HitChanceOverlay;

/// <summary>
/// What a spell's action tree would do to one specific target.
/// </summary>
internal sealed class EffectSummary {
    public bool Heals;
    public bool OtherDamage;                       // physical / untyped / ability damage / energy drain
    public readonly HashSet<DamageEnergyType> EnergyDamage = new();
    public readonly HashSet<DamageEnergyType> ImmuneEnergy = new();
    public int Buffs;
    public bool DependsOnUnknownCondition;
    public SavingThrowType NestedSave = SavingThrowType.Unknown;
    public bool MeleeWeaponAttack;                 // the effect makes the caster attack the target with a weapon
    public bool RangedWeaponAttack;

    public bool AnyHarm => OtherDamage || EnergyDamage.Count > 0 || Buffs > 0;
}

/// <summary>
/// Walks an ability's action tree (AbilityEffectRunAction.Actions) the way the game would run it, but only
/// *evaluating conditions*, never running actions. Conditions are checked inside a real execution context with
/// the hovered unit as target, so e.g. Cure/Inflict spells correctly take their "target is undead → heal" branch.
/// </summary>
internal static class EffectAnalyzer {
    private const int MaxDepth = 12;
    private static readonly Dictionary<Type, FieldInfo[]> s_ActionListFields = new();

    public static EffectSummary Analyze(AbilityData ability, BlueprintAbility payload, UnitEntityData target) {
        var run = payload.GetComponent<AbilityEffectRunAction>();
        if (run?.Actions == null || !run.Actions.HasActions) {
            return null;
        }
        var summary = new EffectSummary();
        var wrapper = new TargetWrapper(target);
        var context = ability.CreateExecutionContext(wrapper, ability.CalculateParams());
        using (ContextData<MechanicsContext.Data>.Request().Setup(context, wrapper)) {
            Walk(run.Actions, summary, target, 0);
        }
        return summary;
    }

    private static void Walk(ActionList list, EffectSummary summary, UnitEntityData target, int depth) {
        if (list?.Actions == null || depth > MaxDepth) {
            return;
        }
        foreach (var action in list.Actions) {
            if (action == null) {
                continue;
            }
            switch (action) {
                case Conditional conditional:
                    bool? result = TryCheck(conditional.ConditionsChecker);
                    if (result == null) {
                        summary.DependsOnUnknownCondition = true;
                        Walk(conditional.IfTrue, summary, target, depth + 1);
                        Walk(conditional.IfFalse, summary, target, depth + 1);
                    } else {
                        Walk(result.Value ? conditional.IfTrue : conditional.IfFalse, summary, target, depth + 1);
                    }
                    break;

                case ContextActionSavingThrow save:
                    if (summary.NestedSave == SavingThrowType.Unknown) {
                        summary.NestedSave = save.Type;
                    }
                    Walk(save.Actions, summary, target, depth + 1);
                    break;

                case ContextActionConditionalSaved saved:
                    // We care about what happens when the save is failed (the spell's "real" effect).
                    Walk(saved.Failed, summary, target, depth + 1);
                    break;

                case ContextActionDealDamage damage:
                    if (damage.IsSimpleDamage && damage.DamageType != null && damage.DamageType.IsEnergy) {
                        var energy = damage.DamageType.Energy;
                        if (target.Get<UnitPartDamageReduction>()?.IsImmune(energy) == true) {
                            summary.ImmuneEnergy.Add(energy);
                        } else {
                            summary.EnergyDamage.Add(energy);
                        }
                    } else {
                        summary.OtherDamage = true;
                    }
                    break;

                case ContextActionHealTarget:
                    summary.Heals = true;
                    break;

                case ContextActionMeleeAttack:
                    summary.MeleeWeaponAttack = true;
                    summary.OtherDamage = true;
                    break;

                case ContextActionRangedAttack ranged when !ranged.AutoHit:
                    summary.RangedWeaponAttack = true;
                    summary.OtherDamage = true;
                    break;

                case ContextActionApplyBuff buff:
                    if (!buff.ToCaster) {
                        summary.Buffs++;
                    }
                    break;

                default:
                    // Unknown action types: follow any nested action lists (randomizers, "on targets around", ...),
                    // except ones that obviously act on the caster or cast a different spell.
                    var typeName = action.GetType().Name;
                    if (typeName.Contains("Caster") || typeName.Contains("CastSpell")) {
                        break;
                    }
                    foreach (var field in GetActionListFields(action.GetType())) {
                        Walk(field.GetValue(action) as ActionList, summary, target, depth + 1);
                    }
                    break;
            }
        }
    }

    private static bool? TryCheck(ConditionsChecker checker) {
        if (checker == null || !checker.HasConditions) {
            return true;
        }
        try {
            return checker.Check();
        } catch (Exception e) {
            Overlay.LogOnce("Condition check failed: " + e.Message);
            return null;
        }
    }

    private static FieldInfo[] GetActionListFields(Type type) {
        if (!s_ActionListFields.TryGetValue(type, out var fields)) {
            fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.FieldType == typeof(ActionList)).ToArray();
            s_ActionListFields[type] = fields;
        }
        return fields;
    }
}
