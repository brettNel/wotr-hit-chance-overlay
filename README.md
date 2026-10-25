# Hit Chance Overlay

A [Pathfinder: Wrath of the Righteous](https://store.steampowered.com/app/1184370/) mod that tells you your odds
before you commit. Select an attack, spell, scroll, wand or ability, hover over a creature, and a small number appears
next to the cursor:

| Shown | Meaning |
|---|---|
| **`85%`** | Above 70% (green) |
| **`45%`** | 20–70% (yellow) |
| **`10%`** | Below 20% (red) |
| **`IMMUNE`** | The target is immune to the spell, or to all of its damage (Fireball on a fire elemental, mind-affecting on a golem) |
| **`HEAL`** | It would heal the target: red on an enemy (Inflict Wounds on undead), green on a friend |

The percentage is the chance that it works, worked out with **the game's own rules**:

- **Weapon attacks:** chance to hit, including concealment (Blur, Displacement) and Mirror Image.
- **Spells:** the chance every step goes your way, multiplied together: hit with the touch/ray attack, beat spell
  resistance, and the target fails its save. A spell that needs no roll shows 100%.

Because it asks the game itself, everything that changes the numbers is included: feats, enchantments, class features,
buffs and debuffs, flanking, size, Spell Penetration, situational save bonuses, and so on. Works in real-time and
turn-based mode.

**Achievements stay enabled.** The game normally turns achievements off whenever Unity Mod Manager is loaded, and marks
your save as modded permanently. This mod prevents both, the same way ToyBox does. You can switch this off in the settings.

## Installing

1. Back up your saves:
   `%USERPROFILE%\AppData\LocalLow\Owlcat Games\Pathfinder Wrath Of The Righteous\Saved Games`
2. Install [Unity Mod Manager](https://www.nexusmods.com/site/mods/21) (or use ModFinder, which installs it for you).
   Run `UnityModManager.exe`, select *Pathfinder: Wrath of the Righteous*, point it at your game folder, click **Install**.
3. Put the `HitChanceOverlay` folder (containing `HitChanceOverlay.dll` and `Info.json`) into `<game folder>\Mods\`,
   or drop the release zip onto the *Mods* tab in Unity Mod Manager.
4. Start the game and press **Ctrl+F10**. *Hit Chance Overlay* should have a green light.

### Settings (Ctrl+F10 → Hit Chance Overlay)

| Setting | Default | |
|---|---|---|
| Keep achievements enabled | on | See above |
| Show outside of combat | off | Handy for testing |
| Show details | off | Shows the breakdown under the number (attack bonus vs AC, DC vs save, SR, full-attack odds) |
| Precise saves & spell penetration | on | Includes situational bonuses; turn off if anything looks odd |
| Font size | 20 | |

## Known limitations

- A few bonuses only switch on during a real attack roll (some "bonus against this enemy" and "bonus while a
  condition is true" effects) and are missed, so the number can be off by a point or two when they apply.
- Fortification and Blind-Fight rerolls aren't modelled.
- Weapon attacks show the first attack only; extra attacks from Haste, flurry or natural weapons aren't listed separately.
- Damage reduction / physical immunity (incorporeal, DR/cold iron) isn't checked for weapon attacks.
- Immunity to a spell's *condition* (immune to paralysis, but not to the spell) isn't detected; spell immunity and energy immunity are.
- Area spells show the odds for the creature under the cursor only.

## Building from source

Requires the [.NET SDK](https://dotnet.microsoft.com/download) 8 or newer and the game installed. Visual Studio is optional.

```
dotnet build -c Release
```

The build references the game's DLLs from the default Steam folder, publicizes them (so private members are accessible),
and copies the mod into `<game>\Mods\HitChanceOverlay\`. Add `-p:SkipDeploy=true` to build without copying. If the game
is installed elsewhere, create `GamePath.props` next to the `.sln` (it's git-ignored):

```xml
<Project><PropertyGroup><WrathInstallDir>D:\Games\Wrath</WrathInstallDir></PropertyGroup></Project>
```

### How it works

| File | What it does |
|---|---|
| `Main.cs` | Unity Mod Manager entry point and settings UI |
| `HoverTracker.cs` | Harmony postfix on `CursorController.SetUnitCursor`; remembers the hovered unit |
| `Overlay.cs` | Works out the attacker and selected ability, recomputes ~7 times a second, draws the number with IMGUI |
| `OddsCalculator.cs` | Weapon and ability odds via `RuleCalculateAttackBonus`, `RuleCalculateAC`, `UnitPartConcealment`, `UnitPartSpellResistance` |
| `RulePreview.cs` | Exact save bonus/DC and spell penetration: runs *only* the "about to trigger" handlers of `RuleSavingThrow` / `RuleSpellResistanceCheck`, then cleans up |
| `EffectAnalyzer.cs` | Walks the spell's action tree, evaluating its conditions against the hovered target (this is how Cure/Inflict decide heal vs. harm) |
| `AchievementsFix.cs` | Keeps achievements enabled and saves unmarked |

**Key rule:** never `Rulebook.Trigger` a *rolling* rule (`RuleAttackRoll`, `RuleSavingThrow`,
`RuleSpellResistanceCheck`, `RuleConcealmentCheck`). Those roll dice, write to the combat log and fire events other
features react to. Instead the mod replicates their success test (`IsSuccessRoll`) over all 20 faces of the die. Pure
*calculation* rules (`RuleCalculateAC`, `RuleCalculateAttackBonus`) are safe to trigger.

### Test checklist

- [ ] Melee attack: with *Show details* on, the bonus vs AC matches the combat log after attacking
- [ ] Ranged attack into melee (the −4 should be in the bonus)
- [ ] Enemy with Blur / Displacement (20% / 50% concealment) and Mirror Image
- [ ] Scorching Ray, Shocking Grasp (held charge), Magic Missile vs. spell resistance
- [ ] Hold Person / Fireball: DC matches the combat log
- [ ] Inflict Light Wounds on undead → red `HEAL`; Cure Light Wounds on a party member → green `HEAL`
- [ ] Fire spell on a fire-immune creature, mind-affecting spell on a golem → `IMMUNE`
- [ ] A scroll and a wand; real-time and turn-based mode
- [ ] Achievements list doesn't say achievements are disabled

**Logs:** Unity Mod Manager's *Logs* tab, or
`%USERPROFILE%\AppData\LocalLow\Owlcat Games\Pathfinder Wrath Of The Righteous\Player.log` (lines start with `[HitChanceOverlay]`).

## Credits

- Cursor-hover idea from the "Estimated THC" option in [Prestige Plus](https://github.com/YLMstring/Prestige-Plus) (MIT).
- Achievement approach from [ToyBox](https://github.com/cabarius/ToyBox) (MIT).
- Built with [Unity Mod Manager](https://www.nexusmods.com/site/mods/21) and [Harmony](https://github.com/pardeike/Harmony).
