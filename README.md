# Hit Chance Overlay

A [Pathfinder: Wrath of the Righteous](https://store.steampowered.com/app/1184370/) mod that shows your odds before you
act. Select an attack or spell, hover over a creature, and a number appears next to the cursor:

| Shown | Meaning |
|---|---|
| **`85%`** | Good odds (green above 70%, yellow 20–70%, red below 20%) |
| **`IMMUNE`** | Nothing the spell does will affect the target |
| **`HEAL`** | It would heal the target |

The odds come from the game's own rules, so feats, enchantments, buffs, flanking, spell resistance and saves are all
included. For spells it's the chance that everything goes your way: hit, beat spell resistance, and the target fails
its save. Built for turn-based mode, and achievements stay enabled.

## Installing

1. Install [Unity Mod Manager](https://www.nexusmods.com/site/mods/21) for Wrath of the Righteous.
2. Drop the release zip onto the *Mods* tab in Unity Mod Manager, or put the `HitChanceOverlay` folder into
   `<game folder>\Mods\`.
3. Start the game. Settings are under **Ctrl+F10**.

## Building

Requires the [.NET SDK](https://dotnet.microsoft.com/download) 8+ and the game installed.

```
dotnet build -c Release
```

This builds and copies the mod into the game's `Mods` folder. If the game isn't in the default Steam folder, create
`GamePath.props` next to the `.sln`:

```xml
<Project><PropertyGroup><WrathInstallDir>D:\Games\Wrath</WrathInstallDir></PropertyGroup></Project>
```

## Credits

Cursor-hover idea from [Prestige Plus](https://github.com/YLMstring/Prestige-Plus), achievement approach from
[ToyBox](https://github.com/cabarius/ToyBox). Built with [Unity Mod Manager](https://www.nexusmods.com/site/mods/21) and
[Harmony](https://github.com/pardeike/Harmony).

## License

[MIT](LICENSE)
