using Kingmaker;
using Kingmaker.Designers;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using UnityEngine;

namespace HitChanceOverlay;

/// <summary>
/// For area spells (bursts, cones, lines, areas left on the ground): who the spell would catch from where it's aimed,
/// each with their own odds. Uses the game's own "would this unit be affected" check (AbilityData.WouldCurrentlyTarget,
/// what its AoE highlight uses), so the shapes and filters are the game's. Recomputed only when the aim moves.
/// </summary>
internal static class AreaLabels {
    private const float MoveThreshold = 0.5f;        // metres the aim must move before we recompute
    private const float RefreshInterval = 1.0f;      // and at least this often (buffs, positions change)
    private const float SearchRadius = 30f;          // metres around the aim point to look for units

    internal readonly record struct Label(UnitEntityData Unit, Prediction Prediction);

    private static AbilityData s_Ability;
    private static Vector3 s_Anchor;
    private static float s_ComputedAt;
    private static List<Label> s_Labels = new();

    /// <summary>The labels to draw, or null when the selected ability isn't an area ability (then the cursor label is used).</summary>
    public static List<Label> Compute(UnitEntityData caster, AbilityData ability) {
        if (ability == null || !IsAreaAbility(ability)) {
            s_Ability = null;
            return null;
        }
        var game = Game.Instance;
        var pointer = game.ClickEventsController;
        var anchor = game.SelectedAbilityHandler?.GetTarget(pointer.PointerOn, pointer.WorldPosition, ability);
        Vector3 point = anchor?.Point ?? pointer.WorldPosition;

        if (s_Ability == ability && (point - s_Anchor).sqrMagnitude < MoveThreshold * MoveThreshold
            && Time.unscaledTime - s_ComputedAt < RefreshInterval) {
            return s_Labels;
        }
        s_Ability = ability;
        s_Anchor = point;
        s_ComputedAt = Time.unscaledTime;

        s_Labels = ComputeAt(ability, point);
        return s_Labels;
    }

    /// <summary>Who the ability would catch when aimed at <paramref name="point"/>, each with their odds.</summary>
    internal static List<Label> ComputeAt(AbilityData ability, Vector3 point) {
        var game = Game.Instance;
        var shape = IsConeOrLine(ability) ? IShowAoEAffectedUIHandler.AoeType.ConeOrLine : IShowAoEAffectedUIHandler.AoeType.Any;
        var labels = new List<Label>();
        foreach (var unit in game.State.Units) {
            // Only units the player can see: never reveal hidden or invisible-and-unseen enemies.
            if (!unit.IsInGame || unit.Descriptor.State.IsDead || !unit.IsVisibleForPlayer
                || (unit.Position - point).sqrMagnitude > SearchRadius * SearchRadius) {
                continue;
            }
            try {
                if (!ability.WouldCurrentlyTarget(point, unit, shape)) {
                    continue;
                }
                var prediction = OddsCalculator.ForAbility(ability, unit, inArea: true);
                if (prediction?.Headline.Text != null) {
                    labels.Add(new Label(unit, prediction));
                    Overlay.LogOnce($"Odds (area): {prediction.Title} -> {prediction.Headline.Text} | {string.Join("; ", prediction.Lines.Select(l => l.Text.Trim()))}");
                }
            } catch (Exception e) {
                Overlay.LogOnce($"Area odds failed for {unit.CharacterName}: {e}");
            }
        }
        return labels;
    }

    // Same tests the game uses to decide an ability has an area to highlight (AbilityData.WouldCurrentlyTarget).
    internal static bool IsAreaAbility(AbilityData ability) {
        var blueprint = ability.GetDeliverBlueprint(checkAdditionalAoeForMagicHack: true);
        return blueprint.GetAoERadiusProvider(false) != null || IsConeOrLine(ability);
    }

    private static bool IsConeOrLine(AbilityData ability) {
        var projectile = ability.GetDeliverBlueprint().GetDeliverProjectile(false);
        return ability.TargetAnchor == AbilityTargetAnchor.Point && projectile != null && projectile.Type != AbilityProjectileType.Simple;
    }
}
