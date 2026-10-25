using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using TurnBased.Controllers;
using UnityEngine;

namespace HitChanceOverlay;

/// <summary>
/// Draws the odds box next to the mouse cursor. Odds are recomputed a few times per second in Update(),
/// and only drawn in OnGUI() (which Unity can call several times per frame).
/// </summary>
internal class Overlay : MonoBehaviour {
    private const float RecalculateInterval = 0.15f;
    private static readonly HashSet<string> s_Logged = new();

    private Prediction m_Prediction;
    private float m_NextRecalculate;
    private GUIStyle m_Style;
    private int m_StyleFontSize;

    internal static void LogOnce(string message) {
        if (s_Logged.Add(message)) {
            Main.Log?.Log(message);
        }
    }

    private void Update() {
        if (Time.unscaledTime < m_NextRecalculate) {
            return;
        }
        m_NextRecalculate = Time.unscaledTime + RecalculateInterval;
        try {
            m_Prediction = Compute();
        } catch (Exception e) {
            m_Prediction = null;
            LogOnce("Error computing odds: " + e);
        }
    }

    private static Prediction Compute() {
        if (!Main.Enabled) {
            return null;
        }
        var game = Game.Instance;
        if (game?.Player == null) {
            return null;
        }
        var target = HoverTracker.HoveredUnit;
        if (target == null || !target.IsInGame || target.Descriptor.State.IsDead) {
            return null;
        }

        UnitEntityData attacker;
        AbilityData ability;
        if (CombatController.IsInTurnBasedCombat()) {
            var turn = game.TurnBasedCombatController?.CurrentTurn;
            attacker = turn?.SelectedUnit;
            ability = turn?.CurrentAbility;
        } else {
            ability = game.SelectedAbilityHandler?.SelectedAbility;
            attacker = ability?.Caster?.Unit ?? game.SelectionCharacter?.FirstSelectedUnit;
        }
        string what = ability?.Blueprint.name ?? "weapon";
        if (!Main.Settings.ShowOutOfCombat && !game.Player.IsInCombat) {
            return Hidden($"{what}: not in combat (tick \"Show outside of combat\" to change)");
        }
        // During an enemy's turn SelectedUnit is that enemy; we only care about units the player controls.
        if (attacker == null) {
            return Hidden($"{what}: no attacker found");
        }
        if (!attacker.IsDirectlyControllable) {
            return Hidden($"{what}: {attacker.CharacterName} is not directly controllable");
        }
        // Turn-based: hide when clicking wouldn't attack/cast this turn. Same checks the game uses for the cursor
        // (CursorController.SetUnitTBMCursor): out of range, or a weapon click that would only move.
        if (CombatController.IsInTurnBasedCombat()) {
            var actionState = attacker.CombatState.TBM.ActionState;
            if (actionState.IsOutOfRange) {
                return Hidden($"{what}: target out of range this turn");
            }
            if (ability == null && actionState.IsMove() && !actionState.IsAttack()) {
                return Hidden($"{what}: clicking would only move");
            }
        }

        if (ability != null) {
            var prediction = OddsCalculator.ForAbility(ability, target);
            return prediction ?? Hidden($"{what} on {target.Blueprint.name}: {ability.CantTargetReason(new Kingmaker.Utility.TargetWrapper(target)) ?? "can't target (no reason given)"}");
        }
        if (attacker != target && attacker.IsEnemy(target)) {
            return OddsCalculator.ForWeapon(attacker, target);
        }
        return null;
    }

    // Logs (once per distinct reason) why nothing is shown, so "no overlay" reports can be diagnosed from the log.
    private static Prediction Hidden(string reason) {
        LogOnce("Overlay hidden: " + reason);
        return null;
    }

    private void OnGUI() {
        var prediction = m_Prediction;
        if (prediction?.Headline.Text == null || Event.current.type != EventType.Repaint) {
            return;
        }
        EnsureStyle();

        var text = new System.Text.StringBuilder();
        text.Append("<b>").Append(Colorize(prediction.Headline)).Append("</b>");
        if (Main.Settings.ShowBreakdown) {
            text.Append("\n<size=").Append(Math.Max(10, m_StyleFontSize * 2 / 3)).Append('>');
            text.Append(prediction.Title);
            foreach (var line in prediction.Lines) {
                text.Append('\n').Append(Colorize(line));
            }
            text.Append("</size>");
        }
        var content = new GUIContent(text.ToString());
        Vector2 size = m_Style.CalcSize(content);

        // Mouse position is bottom-left origin; IMGUI is top-left.
        Vector2 mouse = Input.mousePosition;
        float x = mouse.x + 28;
        float y = Screen.height - mouse.y + 28;
        if (x + size.x > Screen.width) x = mouse.x - size.x - 12;
        if (y + size.y > Screen.height) y = Screen.height - mouse.y - size.y - 12;

        GUI.Box(new Rect(x, y, size.x, size.y), content, m_Style);
    }

    private void EnsureStyle() {
        if (m_Style != null && m_StyleFontSize == Main.Settings.FontSize) {
            return;
        }
        var background = new Texture2D(1, 1);
        background.SetPixel(0, 0, new Color(0.05f, 0.04f, 0.03f, 0.85f));
        background.Apply();
        m_StyleFontSize = Main.Settings.FontSize;
        m_Style = new GUIStyle(GUI.skin.box) {
            fontSize = m_StyleFontSize,
            richText = true,
            wordWrap = false,
            alignment = TextAnchor.UpperLeft,
            padding = new RectOffset(8, 8, 4, 5),
        };
        m_Style.normal.background = background;
        m_Style.normal.textColor = new Color(0.93f, 0.89f, 0.80f);
    }

    private static string Colorize(Line line) => line.Tone switch {
        Tone.Good => $"<color=#7CD67C>{line.Text}</color>",
        Tone.Okay => $"<color=#E8C95A>{line.Text}</color>",
        Tone.Bad => $"<color=#E84A3A>{line.Text}</color>",
        Tone.Immune => $"<b><color=#FF4040>{line.Text}</color></b>",
        Tone.Dim => $"<color=#A89F8C>{line.Text}</color>",
        _ => line.Text,
    };
}
