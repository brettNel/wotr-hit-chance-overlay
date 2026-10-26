using Kingmaker.ElementsSystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.PubSubSystem;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.Utility;

namespace HitChanceOverlay;

/// <summary>
/// "Half-triggers" a rolling rule to read the exact numbers without rolling anything.
///
/// The game's Rulebook.TriggerEventInternal runs, in order: before-handlers, OnEventAboutToTrigger (where feats,
/// buffs and items add their situational bonuses / DC changes), OnTrigger (the actual dice roll + consequences),
/// after-handlers, OnEventDidTrigger. We run ONLY the OnEventAboutToTrigger step, read the result, then call
/// OnDidTrigger(), which is what removes the temporary stat modifiers handlers attached (RulebookEvent.OnDidTrigger).
/// Nothing is rolled, logged, or reported as "failed".
/// </summary>
internal static class RulePreview {
    internal readonly record struct SaveNumbers(int Bonus, int DC, bool AutoPass, int SuccessBonus, bool Precise);

    internal readonly record struct SpellResistanceNumbers(int Penetration, bool Precise);

    public static SaveNumbers SavingThrow(MechanicsContext context, UnitEntityData target, SavingThrowType type, int dc) {
        try {
            return PreciseSavingThrow(context, target, type, dc);
        } catch (Exception e) {
            Overlay.LogOnce("Precise saving throw preview failed, using plain stat: " + e);
        }
        var stat = target.Stats.GetStat(RuleSavingThrow.GetStatFromSavingThrow(type));
        return new SaveNumbers(stat?.ModifiedValue ?? 0, dc, false, 0, false);
    }

    public static SpellResistanceNumbers SpellPenetration(MechanicsContext context, UnitEntityData target) {
        int casterLevel = context.Params.CasterLevel;
        try {
            var evt = new RuleSpellResistanceCheck(context, target) { Reason = context };
            RunAboutToTrigger(evt, context, target);
            int extra = evt.m_AdditionalSpellPenetration != null ? (int)evt.m_AdditionalSpellPenetration : 0;
            return new SpellResistanceNumbers(casterLevel + extra, true);
        } catch (Exception e) {
            Overlay.LogOnce("Precise spell resistance preview failed, using caster level: " + e);
        }
        return new SpellResistanceNumbers(casterLevel, false);
    }

    /// <summary>Mirrors the immunity part of RuleCanApplyBuff (BuffDescriptorImmunity, SpecificBuffImmunity, ...).</summary>
    public static bool BuffImmunity(MechanicsContext buffContext, UnitEntityData target, Kingmaker.UnitLogic.Buffs.Blueprints.BlueprintBuff buff) {
        var evt = new RuleCanApplyBuff(target, buff, buffContext, null) { Reason = buffContext };
        RunAboutToTrigger(evt, buffContext, target);
        return evt.Immunity && !evt.IgnoreImmunity;
    }

    private static SaveNumbers PreciseSavingThrow(MechanicsContext context, UnitEntityData target, SavingThrowType type, int dc) {
        var evt = new RuleSavingThrow(target, type, dc) { Reason = context };
        try {
            RunAboutToTriggerNoCleanup(evt, context, target);

            // Mirrors RuleSavingThrow.OnTrigger / SetStatValueFromThrow, minus the roll.
            var stat = target.Stats.GetStat(evt.StatType);
            var replace = target.Get<UnitPartSavingThrowsReplace>();
            if (replace != null && replace.SavingThrowStat == evt.StatType) {
                var replacement = target.Stats.GetStat(replace.ReplaceToStat);
                if (replacement.ModifiedValue > stat.ModifiedValue) {
                    stat = replacement;
                }
            }
            int bonus;
            using (stat.GetTemporaryModifiersScope(evt.AllBonuses)) {
                bonus = stat.ModifiedValue;
            }
            return new SaveNumbers(bonus, evt.DifficultyClass, evt.AutoPass, evt.SuccessBonusSource != null ? evt.SuccessBonus : 0, true);
        } finally {
            evt.OnDidTrigger();
        }
    }

    private static void RunAboutToTrigger(RulebookEvent evt, MechanicsContext context, UnitEntityData target) {
        try {
            RunAboutToTriggerNoCleanup(evt, context, target);
        } finally {
            evt.OnDidTrigger();
        }
    }

    private static void RunAboutToTriggerNoCleanup(RulebookEvent evt, MechanicsContext context, UnitEntityData target) {
        using (ContextData<MechanicsContext.Data>.Request().Setup(context, new TargetWrapper(target))) {
            RulebookEventBus.OnEventAboutToTrigger(evt);
        }
    }
}
