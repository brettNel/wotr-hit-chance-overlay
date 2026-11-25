using Kingmaker;
using Kingmaker.Designers.Mechanics.Facts;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;

namespace HitChanceOverlay;

/// <summary>
/// Puts a rule on the game's rulebook event stack while we preview, the way Rulebook.TriggerEvent does, so the rules and
/// handlers we run see the same parents they would in a real attack. Some handlers attach a temporary stat modifier to
/// "the current attack" (Rulebook.CurrentContext.PreviousEvent.AddTemporaryModifier, e.g. ModifyD20's +20 attack bonus
/// from the Grey Garrison buff, ShareFavoredEnemies' damage); without a parent there is nothing to attach it to and the
/// modifier stays on the unit forever. On exit: pop, then (when asked) the rule's own cleanup, OnDidTrigger, which removes
/// every temporary modifier registered with it; and, like the game, a fresh context if we started at the top.
/// </summary>
internal sealed class RuleScope : IDisposable {
    private readonly Rulebook m_Rulebook;
    private readonly RulebookEvent m_Event;
    private readonly bool m_Cleanup, m_TopLevel;
    private readonly List<(IncreaseWeaponEnhancementBonusOnTargetFocus Component, int Bonus, UnitReference Target)> m_Focus;

    private RuleScope(RulebookEvent evt, bool cleanup) {
        m_Rulebook = Game.Instance.Rulebook;
        m_Event = evt;
        m_Cleanup = cleanup;
        m_TopLevel = m_Rulebook.Context.CurrentEvent == null;
        m_Rulebook.Context.PushEvent(evt);
    }

    /// <summary>A stand-in rule that's never triggered: on exit its temporary modifiers are removed.</summary>
    public static RuleScope StandIn(RulebookEvent evt) => evt == null ? null : new RuleScope(evt, cleanup: true);

    /// <summary>A rule whose handlers we run directly: it's the current event while they run (the caller cleans it up).</summary>
    public static RuleScope Current(RulebookEvent evt) => new RuleScope(evt, cleanup: false);

    /// <summary>The outermost scope for one preview, so nothing we run ever sees an empty stack.</summary>
    public static RuleScope Root(UnitEntityData unit) {
        var focus = SaveFocus(unit);
        return new RuleScope(new PreviewRoot(unit), cleanup: true, focus);
    }

    private RuleScope(RulebookEvent evt, bool cleanup, List<(IncreaseWeaponEnhancementBonusOnTargetFocus, int, UnitReference)> focus)
        : this(evt, cleanup) {
        m_Focus = focus;
    }

    // Furyborn-style weapons build an enhancement bonus while attacking one target and drop it when an attack bonus is
    // calculated against another (or clamp it during weapon stats). That state lives on the component itself, outside
    // any rule, so it's saved before a preview and put back after.
    private static List<(IncreaseWeaponEnhancementBonusOnTargetFocus, int, UnitReference)> SaveFocus(UnitEntityData unit) {
        var manager = unit == null ? null : RulebookEventBus.InitiatorRulebookSubscribers.Get(unit);
        if (manager == null || !manager.m_Listeners.TryGetValue(typeof(RuleCalculateAttackBonus), out var list)
            || list is not RulebookSubscribersList<RuleCalculateAttackBonus> typed) {
            return null;
        }
        List<(IncreaseWeaponEnhancementBonusOnTargetFocus, int, UnitReference)> saved = null;
        foreach (var entry in typed.List) {
            if (((entry as ISubscriptionProxy)?.GetSubscriber() ?? entry) is IncreaseWeaponEnhancementBonusOnTargetFocus c) {
                (saved ??= new()).Add((c, c.m_CurrentEnhancementBonus, c.m_FocusingTarget));
            }
        }
        return saved;
    }

    public void Dispose() {
        var context = m_Rulebook.Context;
        if (context.CurrentEvent == m_Event) {
            context.PopEvent(m_Event);
        }
        if (m_Cleanup) {
            try {
                m_Event.OnDidTrigger();
            } catch (Exception e) {
                Overlay.LogOnce("Preview cleanup failed: " + e.Message);
            }
        }
        if (m_Focus != null) {
            foreach (var (component, bonus, target) in m_Focus) {
                component.m_CurrentEnhancementBonus = bonus;
                component.m_FocusingTarget = target;
            }
        }
        if (m_TopLevel && context.StackSize == 0) {
            m_Rulebook.m_RuleContext = new RulebookEventContext();
        }
    }

    // Never triggered; only a parent for whatever the preview runs.
    private sealed class PreviewRoot : RulebookEvent {
        public PreviewRoot(UnitEntityData unit) : base(unit) { }

        public override void OnTrigger(RulebookEventContext context) { }
    }
}
