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
    private List<AreaLabels.Label> m_AreaLabels;
    private float m_NextRecalculate;
    private GUIStyle m_Style;
    private int m_StyleFontSize;
    private bool m_StyleNoBackground;

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
            // Area abilities: a label over each creature in the area instead of one at the cursor.
            m_AreaLabels = ComputeArea();
            m_Prediction = m_AreaLabels != null ? null : Compute();
            // The breakdown goes to the log only (once per distinct result), for checking odds against the combat log.
            if (m_Prediction?.Headline.Text != null) {
                LogOnce($"Odds: {m_Prediction.Title} -> {m_Prediction.Headline.Text} | {string.Join("; ", m_Prediction.Lines.Select(l => l.Text.Trim()))}");
            }
        } catch (Exception e) {
            m_Prediction = null;
            m_AreaLabels = null;
            LogOnce("Error computing odds: " + e);
        }
    }

    private static List<AreaLabels.Label> ComputeArea() {
        if (!Main.Enabled || !Main.Settings.AreaLabels) {
            return null;
        }
        var game = Game.Instance;
        if (game?.Player == null || !CombatController.IsInTurnBasedCombat()) {
            return null;
        }
        var turn = game.TurnBasedCombatController?.CurrentTurn;
        var attacker = turn?.SelectedUnit;
        if (attacker == null || !attacker.IsDirectlyControllable || turn.CurrentAbility == null) {
            return null;
        }
        return AreaLabels.Compute(attacker, turn.CurrentAbility);
    }

    private static Prediction Compute() {
        if (!Main.Enabled) {
            return null;
        }
        // Turn-based combat only: outside it, nothing is read or simulated.
        var game = Game.Instance;
        if (game?.Player == null || !CombatController.IsInTurnBasedCombat()) {
            return null;
        }
        // The turn controller tracks the hovered unit itself (it doesn't go through CursorController while an ability is selected).
        var turn = game.TurnBasedCombatController?.CurrentTurn;
        var target = turn?.m_HighlightedUnit;
        if (target == null || !target.IsInGame || target.Descriptor.State.IsDead) {
            return null;
        }

        UnitEntityData attacker = turn.SelectedUnit;
        AbilityData ability = turn.CurrentAbility;
        string what = ability?.Blueprint.name ?? "weapon";
        // During an enemy's turn SelectedUnit is that enemy; we only care about units the player controls.
        if (attacker == null) {
            return Hidden($"{what}: no attacker found");
        }
        if (!attacker.IsDirectlyControllable) {
            return Hidden($"{what}: {attacker.CharacterName} is not directly controllable");
        }
        // Hide when clicking wouldn't attack/cast this turn. Same checks the game uses for the cursor
        // (CursorController.SetUnitTBMCursor): out of range, or a weapon click that would only move.
        var actionState = attacker.CombatState.TBM.ActionState;
        if (actionState.IsOutOfRange) {
            return Hidden($"{what}: target out of range this turn");
        }
        if (ability == null && actionState.IsMove() && !actionState.IsAttack()) {
            return Hidden($"{what}: clicking would only move");
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
        if (Event.current.type != EventType.Repaint) {
            return;
        }
        var area = m_AreaLabels;
        if (area != null) {
            DrawAreaLabels(area);
            return;
        }
        var prediction = m_Prediction;
        if (prediction?.Headline.Text == null || !ShouldShow(prediction.Headline)) {
            return;
        }
        EnsureStyle();

        var content = new GUIContent($"<b>{Colorize(prediction.Headline)}</b>");
        Vector2 size = m_Style.CalcSize(content);

        // Mouse position is bottom-left origin; IMGUI is top-left.
        Vector2 mouse = Input.mousePosition;
        float x = mouse.x + 28;
        float y = Screen.height - mouse.y + 28;
        if (x + size.x > Screen.width) x = mouse.x - size.x - 12;
        if (y + size.y > Screen.height) y = Screen.height - mouse.y - size.y - 12;

        DrawLabel(new Rect(x, y, size.x, size.y), content);
    }

    // One label above each creature's head.
    private void DrawAreaLabels(List<AreaLabels.Label> labels) {
        var camera = Camera.main;
        if (camera == null || labels.Count == 0) {
            return;
        }
        EnsureStyle();
        foreach (var label in labels) {
            if (label.Unit == null || !label.Unit.IsInGame || !ShouldShow(label.Prediction.Headline)) {
                continue;
            }
            Vector3 screen = camera.WorldToScreenPoint(label.Unit.EyePosition + Vector3.up * 0.7f);
            if (screen.z <= 0) {
                continue;
            }
            var content = new GUIContent($"<b>{Colorize(label.Prediction.Headline)}</b>");
            Vector2 size = m_Style.CalcSize(content);
            DrawLabel(new Rect(screen.x - size.x / 2, Screen.height - screen.y - size.y, size.x, size.y), content);
        }
    }

    // The "only warnings" setting hides the percentages and keeps IMMUNE and HEAL.
    internal static bool ShouldShow(Line headline) =>
        !Main.Settings.WarningsOnly || headline.Text == "IMMUNE" || headline.Text == "HEAL";

    private void EnsureStyle() {
        if (m_Style != null && m_StyleFontSize == Main.Settings.FontSize && m_StyleNoBackground == Main.Settings.NoBackground) {
            return;
        }
        var background = new Texture2D(1, 1);
        background.SetPixel(0, 0, new Color(0.05f, 0.04f, 0.03f, 0.85f));
        background.Apply();
        m_StyleFontSize = Main.Settings.FontSize;
        m_StyleNoBackground = Main.Settings.NoBackground;
        m_Style = new GUIStyle(GUI.skin.box) {
            fontSize = m_StyleFontSize,
            richText = true,
            wordWrap = false,
            alignment = TextAnchor.UpperLeft,
            padding = new RectOffset(8, 8, 4, 5),
        };
        m_Style.normal.background = m_StyleNoBackground ? null : background;
        m_Style.normal.textColor = new Color(0.93f, 0.89f, 0.80f);
    }

    // With no background the text gets a dark outline (the label drawn in black around it) so it stays readable on bright ground.
    private static readonly System.Text.RegularExpressions.Regex s_ColorTag = new(@"</?color[^>]*>");

    private void DrawLabel(Rect rect, GUIContent content) {
        if (!m_StyleNoBackground) {
            GUI.Box(rect, content, m_Style);
            return;
        }
        var outline = new GUIContent($"<color=#000000E0>{s_ColorTag.Replace(content.text, "")}</color>");
        float w = Mathf.Max(1f, m_StyleFontSize / 12f);
        for (int dx = -1; dx <= 1; dx++) {
            for (int dy = -1; dy <= 1; dy++) {
                if (dx != 0 || dy != 0) {
                    GUI.Label(new Rect(rect.x + dx * w, rect.y + dy * w, rect.width, rect.height), outline, m_Style);
                }
            }
        }
        GUI.Label(rect, content, m_Style);
    }

    // Text colour choices; the first keeps the green / yellow / red scale.
    internal static readonly (string Name, string Hex)[] TextColors = {
        ("By odds", null), ("White", "#F2EEE4"), ("Yellow", "#F5D547"), ("Cyan", "#5FD7F0"), ("Orange", "#FF9F3A"), ("Pink", "#FF79C6"),
    };

    // Several numbers in one label (full attacks "95%×2 · 75%×1", "95% · ST25%"): each gets its own colour, as it would on its
    // own, instead of the first number's colour for all of them.
    private static readonly System.Text.RegularExpressions.Regex s_Number = new(@"(ST)?(\d+)%(×\d+)?");

    private static string Colorize(Line line) {
        int pick = Main.Settings.TextColor;
        if (pick > 0 && pick < TextColors.Length) {
            string text = $"<color={TextColors[pick].Hex}>{line.Text}</color>";
            return line.Tone == Tone.Immune ? $"<b>{text}</b>" : text;
        }
        bool multi = line.Text != null && (line.Text.Contains(" · ") || line.Text.Contains("\n"))
                     && line.Tone is Tone.Good or Tone.Okay or Tone.Bad;
        if (!multi) {
            return ColorizeWhole(line);
        }
        return s_Number.Replace(line.Text, m => ColorizeWhole(new Line(m.Value, OddsCalculator.ToneFor(int.Parse(m.Groups[2].Value)))));
    }

    private static string ColorizeWhole(Line line) => line.Tone switch {
        Tone.Good => $"<color=#7CD67C>{line.Text}</color>",
        Tone.Okay => $"<color=#E8C95A>{line.Text}</color>",
        Tone.Bad => $"<color=#E84A3A>{line.Text}</color>",
        Tone.Immune => $"<b><color=#FF4040>{line.Text}</color></b>",
        Tone.Dim => $"<color=#A89F8C>{line.Text}</color>",
        _ => line.Text,
    };
}
