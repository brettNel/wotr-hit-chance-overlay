using System.Reflection;
using Kingmaker.Designers.EventConditionActionSystem.Actions;
using Kingmaker.ElementsSystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.Enums.Damage;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Abilities.Components.AreaEffects;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.UnitLogic.Mechanics.Components;
using Kingmaker.Blueprints.Classes.Spells;
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
    public bool Kills;                             // an instant-death effect that would work on this target
    public readonly List<string> ImmuneBuffs = new(); // buffs the target would shrug off (immunity)
    public bool DependsOnUnknownCondition;
    public SavingThrowType NestedSave = SavingThrowType.Unknown;
    // Saves the target only makes after failing an earlier one (Phantasmal Killer: Will, then Fortitude or die).
    public readonly List<ContextActionSavingThrow> FollowUpSaves = new();
    // Follow-up saves whose failure would only kill a target that can't die: the effect it still suffers comes from passing.
    public readonly HashSet<ContextActionSavingThrow> MustPassSaves = new();
    // Every save the target makes on the way in, in order (Sickening Entanglement: Reflex for entangled, Fortitude for
    // sickened). Separate saves for separate effects: the full effect needs each one failed.
    public readonly List<ContextActionSavingThrow> IndependentSaves = new();
    public bool MeleeWeaponAttack;                 // the effect makes the caster attack the target with a weapon
    public bool RangedWeaponAttack;
    public bool AreaEffect;                        // the effect comes from an area the spell leaves (Grease, Stinking Cloud, ...)

    public int OtherEffects;                       // actions we don't model, but which do something
    public ItemEntityWeapon AttackWeapon;          // the touch/ray "weapon" the spell is delivered with, if any

    public bool AnyHarm => HarmBesidesWeaponAttack || MeleeWeaponAttack || RangedWeaponAttack;

    public bool HarmBesidesWeaponAttack => OtherDamage || EnergyDamage.Count > 0 || Buffs > 0 || Kills;

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

    private static void Walk(ActionList list, EffectSummary summary, UnitEntityData target, int depth, bool afterFailedSave = false) {
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
                        Walk(conditional.IfTrue, summary, target, depth + 1, afterFailedSave);
                        Walk(conditional.IfFalse, summary, target, depth + 1, afterFailedSave);
                    } else {
                        Walk(result.Value ? conditional.IfTrue : conditional.IfFalse, summary, target, depth + 1, afterFailedSave);
                    }
                    break;

                case ContextActionSavingThrow save:
                    if (afterFailedSave) {
                        summary.FollowUpSaves.Add(save);
                        if (target.Descriptor.State.Features.Infallible && OnlyKillsOnFailure(save)) {
                            summary.MustPassSaves.Add(save);
                        }
                    } else {
                        if (summary.NestedSave == SavingThrowType.Unknown) {
                            summary.NestedSave = save.Type;
                        }
                        if (!summary.IndependentSaves.Contains(save)) {
                            summary.IndependentSaves.Add(save);
                        }
                    }
                    Walk(save.Actions, summary, target, depth + 1);
                    break;

                case ContextActionConditionalSaved saved:
                    // We care about what happens when the save is failed (the spell's "real" effect). A save found in
                    // there is a second hurdle for the full effect. If failing would only kill a target that can't die
                    // (Infallible bosses), what it actually suffers is the "saved" outcome (Phantasmal Killer's damage).
                    if (target.Descriptor.State.Features.Infallible && OnlyKills(saved.Failed)) {
                        summary.ImmuneBuffs.Add("instant death");
                        Walk(saved.Succeed, summary, target, depth + 1, afterFailedSave: true);
                    } else {
                        Walk(saved.Failed, summary, target, depth + 1, afterFailedSave: true);
                    }
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

                case ContextActionKill:
                    // ContextActionKill only marks the target; UnitLifeController then spares anyone Infallible (bosses with a
                    // health guard): they "fail to die".
                    if (target.Descriptor.State.Features.Infallible) {
                        summary.ImmuneBuffs.Add("instant death");
                    } else {
                        summary.Kills = true;
                    }
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

                case ContextActionSpawnAreaEffect spawn when spawn.AreaEffect != null:
                    summary.AreaEffect = true;
                    WalkArea(spawn.AreaEffect, summary, target, depth + 1);
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
                        Walk(field.GetValue(action) as ActionList, summary, target, depth + 1, afterFailedSave);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// What one exposure to an area does to the target: AbilityAreaEffectRunAction's UnitEnter actions (or, for areas that
    /// only act on their turn, the Round actions), and the buff AbilityAreaEffectBuff puts on whoever stands inside, with the
    /// actions that buff runs when it starts (Grease: Reflex save or fall prone). The actions run in the spell's context, so
    /// saves use the spell's DC (AreaEffectEntityData).
    /// </summary>
    // The save's failure branch does nothing but kill (Phantasmal Killer's Fortitude save).
    private static bool OnlyKillsOnFailure(ContextActionSavingThrow save) =>
        OnlyKills(save.Actions?.Actions?.OfType<ContextActionConditionalSaved>().FirstOrDefault()?.Failed);

    private static bool OnlyKills(ActionList list) =>
        list?.Actions != null && list.Actions.Length > 0 && list.Actions.All(a => a is ContextActionKill);

    private static void WalkArea(BlueprintAbilityAreaEffect area, EffectSummary summary, UnitEntityData target, int depth) {
        foreach (var run in area.GetComponents<AbilityAreaEffectRunAction>()) {
            Walk(run.UnitEnter != null && run.UnitEnter.HasActions ? run.UnitEnter : run.Round, summary, target, depth);
        }
        foreach (var inside in area.GetComponents<AbilityAreaEffectBuff>()) {
            bool? applies = TryCheck(inside.Condition);
            if (applies == false || inside.Buff == null) {
                continue;
            }
            if (applies == null) {
                summary.DependsOnUnknownCondition = true;
            }
            WalkAreaBuff(inside.Buff, summary, target, depth);
        }
    }

    private static void WalkAreaBuff(BlueprintBuff buff, EffectSummary summary, UnitEntityData target, int depth) {
        var parent = ContextData<MechanicsContext.Data>.Current?.Context;
        bool immune;
        try {
            immune = BuffImmunity.IsImmune(target, buff, parent);
        } catch (Exception e) {
            Overlay.LogOnce($"Buff immunity check failed for {buff.name}: {e}");
            immune = false;
        }
        if (immune) {
            summary.ImmuneBuffs.Add(string.IsNullOrEmpty(buff.Name) ? buff.name : buff.Name);
            return;
        }
        // The buff itself counts as an effect if it does more than run actions (e.g. an entangle or a penalty).
        if (buff.ComponentsArray.Any(c => c is not AddFactContextActions && c is not SpellDescriptorComponent)) {
            summary.Buffs++;
        }
        foreach (var actions in buff.GetComponents<AddFactContextActions>()) {
            Walk(actions.Activated != null && actions.Activated.HasActions ? actions.Activated : actions.NewRound, summary, target, depth + 1);
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
