using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Items.Weapons;
using Kingmaker.Blueprints.Root;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.Enums;
using Kingmaker.Items;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;
using Kingmaker.Settings;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.Utility;

namespace HitChanceOverlay;

internal enum Tone { Normal, Good, Okay, Bad, Immune, Dim }

internal readonly record struct Line(string Text, Tone Tone = Tone.Normal);

internal sealed class Prediction {
    public string Title;
    /// <summary>The one thing the overlay shows: a hit percentage, "IMMUNE" or "HEAL".</summary>
    public Line Headline;
    /// <summary>Optional details, only drawn when "Show details" is on.</summary>
    public readonly List<Line> Lines = new();
    public void SetPercent(int percent) => Headline = new Line($"{percent}%", OddsCalculator.ToneFor(percent));
    public void Add(string text, Tone tone = Tone.Normal) => Lines.Add(new Line(text, tone));
}

/// <summary>
/// Computes odds using the game's own calculation rules (RuleCalculateAttackBonus, RuleCalculateAC, ...).
/// We deliberately never trigger the "rolling" rules (RuleAttackRoll, RuleSavingThrow, RuleSpellResistanceCheck):
/// those roll dice, write to the combat log, and fire events other features react to. Instead we replicate their
/// success test (see RuleAttackRoll.IsSuccessRoll / RuleSavingThrow.IsSuccessRoll) over all 20 die faces.
/// </summary>
internal static class OddsCalculator {
    private static readonly Dictionary<BlueprintItemWeapon, ItemEntityWeapon> s_WeaponCache = new();

    public static Prediction ForWeapon(UnitEntityData attacker, UnitEntityData target) {
        var weapon = attacker.Body.PrimaryHand.MaybeWeapon ?? attacker.Body.EmptyHandWeapon;
        if (weapon == null) {
            return null;
        }
        var p = new Prediction { Title = weapon.Name };
        var first = CalculateAttack(attacker, target, weapon, 0, weaponAttack: true);
        AddAttackLines(p, first, "to hit");
        p.SetPercent(first.FinalPercent);

        // Iterative attacks (main hand only, from BAB). Shown as a compact row.
        int bab = attacker.Stats.BaseAttackBonus.ModifiedValue;
        int iteratives = bab >= 16 ? 4 : bab >= 11 ? 3 : bab >= 6 ? 2 : 1;
        if (iteratives > 1 && !first.AutoHit) {
            var parts = new List<string>();
            for (int i = 0; i < iteratives; i++) {
                parts.Add($"{CalculateAttack(attacker, target, weapon, 5 * i, weaponAttack: true, firstAttack: i == 0).FinalPercent}%");
            }
            p.Add("Full attack: " + string.Join(" / ", parts), Tone.Dim);
        }

        // Off-hand weapon (two-weapon fighting penalties are applied by the game's own attack bonus rule).
        var offHand = attacker.Body.SecondaryHand;
        if (offHand.MaybeShield == null && offHand.MaybeWeapon is { } offWeapon && offWeapon != weapon) {
            var odds = CalculateAttack(attacker, target, offWeapon, 0, weaponAttack: true);
            p.Add($"Off-hand ({offWeapon.Name}): {odds.FinalPercent}%", ToneFor(odds.FinalPercent));
        }
        return p;
    }

    public static Prediction ForAbility(AbilityData ability, UnitEntityData target) {
        var caster = ability.Caster?.Unit;
        if (caster == null) {
            return null;
        }
        // GetDeliverBlueprint resolves Magus "magic hack" combos; sticky-touch spells (Shocking Grasp etc.)
        // keep their real payload on a separate "delivery" ability.
        var blueprint = ability.GetDeliverBlueprint();
        var payload = blueprint.GetComponent<AbilityEffectStickyTouch>()?.TouchDeliveryAbility ?? blueprint;
        var p = new Prediction { Title = ability.Name };

        if (!ability.CanTarget(new TargetWrapper(target))) {
            return null;
        }

        var abilityParams = ability.CalculateParams();
        var context = ability.CreateExecutionContext(new TargetWrapper(target), abilityParams);
        try {
            // Mirrors AbilityExecutionProcess.PrepareCast: ranks and shared values (e.g. Sleep's "4 HD") are filled in
            // before the spell's actions and conditions run. Only touches this throwaway context.
            context.Recalculate();
        } catch (Exception e) {
            Overlay.LogOnce($"Context recalculation failed for {payload.name}: {e.Message}");
        }

        // Mirrors RuleSpellResistanceCheck.OnTrigger: IsImmune(context) is what the game checks, and it's the only overload
        // that sees the spell's descriptors (poison, mind-affecting, ...). The blueprint overloads catch spell-specific immunities.
        var spellResistancePart = target.Get<UnitPartSpellResistance>();
        if (spellResistancePart != null && (spellResistancePart.IsImmune(context) || spellResistancePart.IsImmune(payload, caster)
            || spellResistancePart.IsImmune(blueprint, caster))) {
            p.Headline = new Line("IMMUNE", Tone.Immune);
            return p;
        }

        EffectSummary effects = null;
        try {
            effects = EffectAnalyzer.Analyze(context, payload, target);
        } catch (Exception e) {
            Overlay.LogOnce($"Effect analysis failed for {payload.name}: {e}");
        }
        if (effects != null) {
            bool hostile = caster.IsEnemy(target);
            if (effects.Heals && !effects.AnyHarm) {
                // Red when it would heal an enemy, green when healing a friend.
                p.Headline = new Line("HEAL", hostile ? Tone.Immune : Tone.Good);
                return p;
            }
            if (effects.ImmuneEnergy.Count > 0 || effects.ImmuneBuffs.Count > 0) {
                string immuneTo = string.Join(", ", effects.ImmuneEnergy.Select(e => e + " damage").Concat(effects.ImmuneBuffs.Distinct()));
                // Every effect the spell has would be shrugged off, so hitting doesn't matter.
                if (!effects.AnyHarm && !effects.Heals) {
                    p.Headline = new Line("IMMUNE", Tone.Immune);
                    p.Add($"Immune to {immuneTo}", Tone.Dim);
                    return p;
                }
                p.Add($"Immune to {immuneTo}", Tone.Bad);
            }
            // The spell's own conditions say nothing happens to this target (Sleep on a creature above its HD limit).
            if (effects.DoesNothing && !effects.DependsOnUnknownCondition) {
                p.Headline = new Line("IMMUNE", Tone.Immune);
                p.Add("No effect on this target", Tone.Dim);
                return p;
            }
            if (effects.DependsOnUnknownCondition && Main.Settings.ShowBreakdown) {
                p.Add("   (some effects depend on conditions we couldn't check)", Tone.Dim);
            }
        }

        var chances = new List<int>();

        // 1. Attack roll (touch / ray / attack-roll projectile)
        var attackWeapon = GetAbilityAttackWeapon(payload);
        if (attackWeapon != null) {
            var attack = CalculateAttack(caster, target, GetWeaponEntity(attackWeapon), 0, weaponAttack: false);
            AddAttackLines(p, attack, attackWeapon.AttackType.IsTouch() ? "to hit (touch)" : "to hit");
            chances.Add(attack.FinalPercent);
        } else {
            // 1b. Abilities that are a weapon attack (Charge, Vital Strike, Cleave, ...): the chance that attack hits.
            WeaponAbilities.Plan? plan = null;
            try {
                plan = WeaponAbilities.Detect(payload, caster, effects);
            } catch (Exception e) {
                Overlay.LogOnce($"Weapon ability detection failed for {payload.name}: {e}");
            }
            if (plan is { } weaponPlan) {
                int chargeBonus = 0;
                if (weaponPlan.Charge) {
                    try {
                        chargeBonus = WeaponAbilities.ChargeBonus(ability, caster, target, weaponPlan.Weapon);
                    } catch (Exception e) {
                        Overlay.LogOnce("Charge bonus preview failed: " + e);
                    }
                }
                var attack = CalculateAttack(caster, target, weaponPlan.Weapon, weaponPlan.Penalty, weaponAttack: true,
                    chargeBonus, chargeBonus != 0 ? $"Charge {FormatBonus(chargeBonus)}" : null, charge: weaponPlan.Charge);
                AddAttackLines(p, attack, $"to hit ({weaponPlan.Weapon.Name})");
                chances.Add(attack.FinalPercent);
            }
        }

        // 2. Spell resistance
        if (UsesSpellResistance(payload, caster, target) && spellResistancePart != null) {
            int sr = GetSpellResistance(spellResistancePart, context, payload, caster);
            if (sr > 0) {
                var penetration = RulePreview.SpellPenetration(context, target);
                // Mirrors RuleSpellResistanceCheck.IsSpellResisted: resisted when SR > penetration + d20 (no auto 1/20).
                int chance = D20Percent(d => penetration.Penetration + d >= sr);
                p.Add($"{chance}% to beat SR", ToneFor(chance));
                if (Main.Settings.ShowBreakdown) {
                    string note = penetration.Precise ? "" : " (spell penetration feats not included)";
                    p.Add($"   {FormatBonus(penetration.Penetration)} vs SR {sr}{note}", Tone.Dim);
                }
                chances.Add(chance);
            }
        }

        // 3. Saving throw
        var saveType = payload.GetComponent<AbilityEffectRunAction>()?.SavingThrowType ?? SavingThrowType.Unknown;
        if (saveType == SavingThrowType.Unknown && effects != null) {
            saveType = effects.NestedSave;
        }
        if (saveType != SavingThrowType.Unknown && abilityParams.DC > 0) {
            var save = RulePreview.SavingThrow(context, target, saveType, abilityParams.DC);
            // Mirrors RuleSavingThrow.IsSuccessRoll (natural 1 fails, natural 20 passes).
            int passPercent = save.AutoPass ? 100
                : D20Percent(d => d == 20 || (d > 1 && d + save.Bonus + save.SuccessBonus >= save.DC));
            int failPercent = 100 - passPercent;
            p.Add($"{failPercent}% to fail {saveType} save", ToneFor(failPercent));
            if (Main.Settings.ShowBreakdown) {
                string note = save.Precise ? "" : " (situational bonuses not included)";
                p.Add($"   DC {save.DC} vs {saveType} {FormatBonus(save.Bonus)}{note}", Tone.Dim);
            }
            chances.Add(failPercent);
        }

        // Chance that every step goes our way (hit, beat SR, target fails save). No roll at all = 100%.
        int overall = (int)Math.Round(chances.Aggregate(1.0, (acc, c) => acc * c / 100.0) * 100);
        p.SetPercent(overall);
        return p;
    }

    // ---------- attack rolls ----------

    internal readonly record struct AttackOdds(int AttackBonus, int TargetAC, int RollPercent, int ConcealmentPercent, int MirrorImages, bool AutoHit, bool TouchAC, string PendingToggles = null) {
        // Concealment is rolled first, then the d20; a hit then strikes the real target with chance 1/(images+1).
        public int FinalPercent => AutoHit ? 100 : (int)Math.Round(RollPercent * (100 - ConcealmentPercent) / 100.0 / (MirrorImages + 1));
    }

    private static AttackOdds CalculateAttack(UnitEntityData attacker, UnitEntityData target, ItemEntityWeapon weapon, int penalty, bool weaponAttack,
        int extraBonus = 0, string extraNote = null, bool firstAttack = true, bool charge = false) {
        var attackType = weapon.Blueprint.AttackType;

        // Mirrors RuleAttackRoll.OnTrigger: attacking yourself, or touching a non-hostile, always hits.
        bool confused = target.Descriptor.State.HasCondition(UnitCondition.Confusion);
        bool friendly = !target.IsEnemy(attacker) && !target.Faction.Neutral && !confused;
        if (attacker == target || (attackType.IsTouch() && friendly)) {
            return new AttackOdds(0, 0, 100, 0, 0, true, attackType.IsTouch());
        }

        // If walking up to the target will make it flanked, run the calculations with the game seeing that state, so the
        // flanking bonus and every flanking-dependent feature (Outflank, Back to Back, ...) apply as they will in the attack.
        HashSet<UnitEntityData> engagersOnArrival = null;
        try {
            engagersOnArrival = FlankingPredictor.PredictEngagers(attacker, target, weapon);
        } catch (Exception e) {
            Overlay.LogOnce("Flanking prediction failed: " + e);
        }
        bool flankedOnArrival = engagersOnArrival != null;
        int ac;
        RuleCalculateAttackBonus attackBonus;
        using (flankedOnArrival ? FlankingPredictor.Simulate(target, engagersOnArrival) : null) {
            ac = Rulebook.Trigger(new RuleCalculateAC(attacker, target, attackType)).Result;
            // Weapon attacks: hand the weapon calculation a stand-in for the attack (never triggered), as RuleAttackWithWeapon
            // does. Enchantments that depend on the target (Bane, "+2 vs evil outsiders", ModifyWeaponStatsConditional) only
            // apply when RuleCalculateWeaponStats.AttackWithWeapon tells them who is being attacked.
            var weaponStats = weaponAttack
                ? new RuleAttackWithWeapon(attacker, target, weapon, penalty) { IsFirstAttack = firstAttack, IsCharge = charge }.WeaponStats
                : new RuleCalculateWeaponStats(attacker, weapon);
            attackBonus = Rulebook.Trigger(new RuleCalculateAttackBonus(attacker, target, weaponStats, penalty) { TargetIsFlanked = flankedOnArrival });
        }
        int ab = attackBonus.Result;

        // Toggles like Fight Defensively only apply their buff once a weapon attack starts; add what they will add.
        var pending = weaponAttack ? PendingToggles.AttackBonus(attacker, target, weapon) : default;
        if (extraBonus != 0) {
            pending = new PendingToggles.Pending(pending.AttackBonus + extraBonus, pending.Names == null ? extraNote : pending.Names + ", " + extraNote);
        }
        if (pending.AttackBonus != 0) {
            // Mirrors the end of RuleCalculateAttackBonus.OnTrigger: on easier difficulties a party member's attack bonus
            // can't drop below BAB - penalty - 2 (added as a Difficulty modifier). Redo that floor with the toggle included.
            int floor = attackBonus.AllBonuses.Where(m => m.Descriptor == ModifierDescriptor.Difficulty).Sum(m => m.Value);
            ab = ab - floor + pending.AttackBonus;
            if (attacker.IsPlayerFaction && (bool)SettingsRoot.Difficulty.DeadCompanionsRiseAfterCombat) {
                ab = Math.Max(ab, attacker.Stats.BaseAttackBonus.ModifiedValue - penalty - 2);
            }
        }
        bool alwaysChance = attacker.State.Features.AlwaysChance;

        // Mirrors RuleAttackRoll.IsSuccessRoll.
        int rollPercent = D20Percent(d => (d > 1 || alwaysChance) && (d == 20 || d + ab >= ac));

        int concealment = UnitPartConcealment.Calculate(attacker, target, attack: true) switch {
            Concealment.Partial => 20,
            Concealment.Total => 50,
            _ => 0,
        };
        // Mirrors the mirror image block in RuleAttackRoll.OnTrigger (UnitPartMirrorImage.TryAbsorbHit).
        int images = attacker.Descriptor.IsImmuneToVisualEffects ? 0 : target.Get<UnitPartMirrorImage>()?.MechanicsImages.Count ?? 0;
        string notes = flankedOnArrival ? (pending.Names == null ? "flanked on arrival" : pending.Names + ", flanked on arrival") : pending.Names;
        return new AttackOdds(ab, ac, rollPercent, concealment, images, false, attackType.IsTouch(), notes);
    }

    private static void AddAttackLines(Prediction p, AttackOdds odds, string label) {
        p.Add($"{odds.FinalPercent}% {label}", ToneFor(odds.FinalPercent));
        if (!Main.Settings.ShowBreakdown) {
            return;
        }
        if (odds.AutoHit) {
            p.Add("   automatic hit", Tone.Dim);
            return;
        }
        string ac = odds.TouchAC ? "touch AC" : "AC";
        string text = $"   {FormatBonus(odds.AttackBonus)} vs {ac} {odds.TargetAC}";
        if (odds.ConcealmentPercent > 0) {
            text += $", {odds.ConcealmentPercent}% concealment";
        }
        if (odds.MirrorImages > 0) {
            text += $", {odds.MirrorImages} mirror image{(odds.MirrorImages == 1 ? "" : "s")}";
        }
        if (odds.PendingToggles != null) {
            text += $" (incl. {odds.PendingToggles})";
        }
        p.Add(text, Tone.Dim);
    }

    private static BlueprintItemWeapon GetAbilityAttackWeapon(BlueprintAbility payload) {
        var touch = payload.GetComponent<AbilityDeliverTouch>();
        if (touch?.TouchWeapon != null) {
            return touch.TouchWeapon;
        }
        var projectile = payload.GetComponent<AbilityDeliverProjectile>();
        if (projectile != null && projectile.NeedAttackRoll) {
            return projectile.Weapon ?? BlueprintRoot.Instance.SystemMechanics.RayWeapon;
        }
        return null;
    }

    // Spell "weapons" (touch, ray) aren't items anyone owns, so we make one entity per blueprint and reuse it.
    private static ItemEntityWeapon GetWeaponEntity(BlueprintItemWeapon blueprint) {
        if (!s_WeaponCache.TryGetValue(blueprint, out var entity)) {
            entity = blueprint.CreateEntity<ItemEntityWeapon>();
            s_WeaponCache[blueprint] = entity;
        }
        return entity;
    }

    // ---------- spell resistance ----------

    // Mirrors RuleSpellResistanceCheck.HasResistanceRoll / IsSpellResisted preconditions.
    private static bool UsesSpellResistance(BlueprintAbility payload, UnitEntityData caster, UnitEntityData target) {
        if (!payload.SpellResistance || caster.Descriptor.State.Features.PrimalMagicEssence) {
            return false;
        }
        return !payload.IgnoreSpellResistanceForAlly || caster.Group.IsEnemy(target.Group);
    }

    private static int GetSpellResistance(UnitPartSpellResistance part, MechanicsContext context, BlueprintAbility payload, UnitEntityData caster) {
        try {
            // The context-aware overload also accounts for SR bonuses/penalties and Piercing metamagic.
            return part.GetValue(context);
        } catch (Exception e) {
            Overlay.LogOnce("SR via context failed, falling back: " + e.Message);
            return part.GetValue(payload, caster);
        }
    }

    // ---------- helpers ----------

    private static int D20Percent(Func<int, bool> success) {
        int count = 0;
        for (int d = 1; d <= 20; d++) {
            if (success(d)) {
                count++;
            }
        }
        return count * 5;
    }

    // Green above 70%, yellow 20-70%, red below 20%.
    internal static Tone ToneFor(int percent) => percent > 70 ? Tone.Good : percent >= 20 ? Tone.Okay : Tone.Bad;

    private static string FormatBonus(int value) => value >= 0 ? "+" + value : value.ToString();
}
