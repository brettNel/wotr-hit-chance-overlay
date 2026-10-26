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
using Kingmaker.Items;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules.Damage;

namespace HitChanceOverlay;

/// <summary>
/// What a spell's action tree would do to one specific target.
/// </summary>
internal sealed class EffectSummary {
    public bool Heals;
    public bool OtherDamage;                       // physical / untyped / ability damage / energy drain
    public readonly HashSet<DamageEnergyType> EnergyDamage = new();
    public readonly HashSet<string> ImmuneDamage = new(); // damage the target would take none of ("Fire damage", ...)
    public int Buffs;                              // buffs that would take effect
    public readonly List<string> ImmuneBuffs = new(); // buffs the target would shrug off (immunity)
    public bool DependsOnUnknownCondition;
    public SavingThrowType NestedSave = SavingThrowType.Unknown;
    public bool MeleeWeaponAttack;                 // the effect makes the caster attack the target with a weapon
    public bool RangedWeaponAttack;

    public int OtherEffects;                       // actions we don't model, but which do something
    public ItemEntityWeapon AttackWeapon;          // the touch/ray "weapon" the spell is delivered with, if any

    public bool AnyHarm => HarmBesidesWeaponAttack || MeleeWeaponAttack || RangedWeaponAttack;

    public bool HarmBesidesWeaponAttack => OtherDamage || EnergyDamage.Count > 0 || Buffs > 0;

    /// <summary>Nothing at all would happen to the target, not even something it's immune to.</summary>
    public bool DoesNothing => !AnyHarm && !Heals && OtherEffects == 0 && ImmuneBuffs.Count == 0 && ImmuneDamage.Count == 0;
}

/// <summary>
/// Walks an ability's action tree (AbilityEffectRunAction.Actions) the way the game would run it, but only
/// *evaluating conditions*, never running actions. Conditions are checked inside a real execution context with
/// the hovered unit as target, so e.g. Cure/Inflict spells correctly take their "target is undead → heal" branch.
/// </summary>
internal static class EffectAnalyzer {
    private const int MaxDepth = 12;
    private static readonly Dictionary<Type, FieldInfo[]> s_ActionListFields = new();

    public static EffectSummary Analyze(AbilityExecutionContext context, BlueprintAbility payload, UnitEntityData target, ItemEntityWeapon attackWeapon) {
        var run = payload.GetComponent<AbilityEffectRunAction>();
        if (run?.Actions == null || !run.Actions.HasActions) {
            return null;
        }
        var summary = new EffectSummary { AttackWeapon = attackWeapon };
        var wrapper = new TargetWrapper(target);
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
                    if (damage.IsSimpleDamage && damage.DamageType != null) {
                        if (IsImmuneToDamage(damage.DamageType, target, summary.AttackWeapon)) {
                            summary.ImmuneDamage.Add(DamageLabel(damage.DamageType));
                        } else if (damage.DamageType.IsEnergy) {
                            summary.EnergyDamage.Add(damage.DamageType.Energy);
                        } else {
                            summary.OtherDamage = true;
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
                    break;

                case ContextActionRangedAttack ranged when !ranged.AutoHit:
                    summary.RangedWeaponAttack = true;
                    break;

                case ContextActionApplyBuff buff:
                    // A buff with no components is a marker (e.g. a hex's "already used on this target" cooldown), not harm.
                    if (buff.ToCaster || (buff.Buff != null && buff.Buff.ComponentsArray.Length == 0)) {
                        summary.OtherEffects++;
                    } else if (buff.Buff != null) {
                        var parent = buff.IgnoreParentContext ? null : ContextData<MechanicsContext.Data>.Current?.Context;
                        bool immune;
                        try {
                            immune = BuffImmunity.IsImmune(target, buff.Buff, parent);
                        } catch (Exception e) {
                            Overlay.LogOnce($"Buff immunity check failed for {buff.Buff.name}: {e}");
                            immune = false;
                        }
                        if (immune) {
                            summary.ImmuneBuffs.Add(string.IsNullOrEmpty(buff.Buff.Name) ? buff.Buff.name : buff.Buff.Name);
                        } else {
                            summary.Buffs++;
                        }
                    }
                    break;

                default:
                    // Unknown action types: follow any nested action lists (randomizers, "on targets around", ...),
                    // except ones that obviously act on the caster or cast a different spell.
                    var typeName = action.GetType().Name;
                    if (typeName.Contains("Caster")) {
                        summary.OtherEffects++; // acts on the caster: not harm, but the ability still does something
                        break;
                    }
                    var fields = GetActionListFields(action.GetType());
                    // A plain action we don't model (knockdown, dispel, casting another spell, ...): it still does something.
                    if (fields.Length == 0 || typeName.Contains("CastSpell")) {
                        summary.OtherEffects++;
                        break;
                    }
                    foreach (var field in fields) {
                        Walk(field.GetValue(action) as ActionList, summary, target, depth + 1);
                    }
                    break;
            }
        }
    }

    // Builds the damage like ContextActionDealDamage.GetDamageRule (dice don't matter) and asks the game whether all of it
    // would be declined. The delivery weapon matters: e.g. swarms only shrug off damage that comes with a weapon.
    private static bool IsImmuneToDamage(DamageTypeDescription type, UnitEntityData target, ItemEntityWeapon attackWeapon) {
        var context = ContextData<MechanicsContext.Data>.Current?.Context;
        var caster = context?.MaybeCaster;
        if (caster == null) {
            return false;
        }
        try {
            var bundle = new DamageBundle(type.CreateDamage(new DiceFormula(1, DiceType.D6), 0)) { Weapon = attackWeapon };
            var dealDamage = new RuleDealDamage(caster, target, bundle) { SourceAbility = context.SourceAbility, Reason = context };
            return RulePreview.DamageImmunity(dealDamage, context);
        } catch (Exception e) {
            Overlay.LogOnce($"Damage immunity check failed for {type}: {e}");
            return false;
        }
    }

    private static string DamageLabel(DamageTypeDescription type) => type.Type switch {
        DamageType.Energy => $"{type.Energy} damage",
        DamageType.Physical => "physical damage",
        _ => $"{type.Type} damage",
    };

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
