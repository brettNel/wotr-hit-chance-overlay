using Kingmaker.Blueprints;
using Kingmaker.ElementsSystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.RuleSystem;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.UnitLogic.Mechanics.Conditions;
using Kingmaker.UnitLogic.Mechanics.Components;

namespace HitChanceOverlay;

/// <summary>
/// Spells that affect "N HD of creatures" with N rolled on dice (Hypnotism: 2d4 HD). The game rolls the pool once per cast
/// (ContextCalculateSharedValue) and only affects creatures whose HD fits in it (ContextConditionHitDice). Instead of
/// rolling, the overlay analyses the spell with the largest possible pool and multiplies in the exact probability that the
/// roll covers this creature. Fixed pools (Sleep's 4 HD) aren't dice and need nothing.
/// The pool decides who is picked (AbilityTargetsAround's condition, all against the same starting pool); the spell's
/// "subtract HD" step runs afterwards and changes nothing about who was picked, so this per-creature chance is exact.
/// </summary>
internal sealed class HitDicePool {
    public AbilitySharedValue Type;
    public string Dice;
    private int m_Offset;
    private Dictionary<int, long> m_Counts;
    private long m_Total;

    public int Max => m_Counts.Keys.Max();

    /// <summary>The dice-rolled HD pool this spell checks creatures against, or null.</summary>
    public static HitDicePool Find(BlueprintAbility blueprint, BlueprintAbility payload, MechanicsContext context) {
        var conditions = Elements(blueprint).Concat(payload != blueprint ? Elements(payload) : Enumerable.Empty<Element>())
            .OfType<ContextConditionHitDice>().Where(c => c.AddSharedValue).ToList();
        if (conditions.Count == 0) {
            return null;
        }
        foreach (var calc in blueprint.GetComponents<ContextCalculateSharedValue>().Concat(payload.GetComponents<ContextCalculateSharedValue>())) {
            var match = conditions.FirstOrDefault(c => c.SharedValue == calc.ValueType);
            if (match == null || calc.Value == null) {
                continue;
            }
            int count = calc.Value.DiceCountValue?.Calculate(context) ?? 0;
            int sides = (int)calc.Value.DiceType;
            if (count <= 0 || sides <= 1) {
                return null; // no dice: a fixed pool, already exact
            }
            int bonus = calc.Value.BonusValue?.Calculate(context) ?? 0;
            // Distribution of count dice with `sides` faces (exact counts by convolution), then as the game computes it:
            // (int)((sum + bonus) * Modifier).
            var sums = new Dictionary<int, long> { [0] = 1 };
            for (int d = 0; d < count; d++) {
                var next = new Dictionary<int, long>();
                foreach (var kv in sums) {
                    for (int face = 1; face <= sides; face++) {
                        next.TryGetValue(kv.Key + face, out long n);
                        next[kv.Key + face] = n + kv.Value;
                    }
                }
                sums = next;
            }
            var counts = new Dictionary<int, long>();
            foreach (var kv in sums) {
                int value = (int)((kv.Key + bonus) * calc.Modifier);
                counts.TryGetValue(value, out long n);
                counts[value] = n + kv.Value;
            }
            return new HitDicePool {
                Type = calc.ValueType,
                Dice = $"{count}d{sides}" + (bonus != 0 ? $"{bonus:+#;-#}" : ""),
                m_Offset = match.HitDice,
                m_Counts = counts,
                m_Total = counts.Values.Sum(),
            };
        }
        return null;
    }

    /// <summary>Exact chance the rolled pool covers this creature (ContextConditionHitDice: level &lt;= HitDice + pool).</summary>
    public double CoverChance(UnitEntityData target) {
        int needed = target.Descriptor.Progression.CharacterLevel - m_Offset;
        long covering = m_Counts.Where(kv => kv.Key >= needed).Sum(kv => kv.Value);
        return (double)covering / m_Total;
    }

    private static IEnumerable<Element> Elements(BlueprintScriptableObject blueprint) =>
        (IEnumerable<Element>)blueprint?.ElementsArray ?? Enumerable.Empty<Element>();
}
