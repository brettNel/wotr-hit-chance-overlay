using HarmonyLib;
using Kingmaker;
using Kingmaker.Modding;

namespace HitChanceOverlay;

/// <summary>
/// Keeps achievements working while mods are installed (same idea as ToyBox's "Allow Achievements While Using Mods").
///
/// The game disables achievements in AchievementEntity.IsDisabled when either
///  - OwlcatModificationsManager.IsAnyModActive is true (true whenever Unity Mod Manager is loaded), or
///  - Player.ModsUser is true. That flag is written into the save file, so it would stick to the save forever,
///    even after uninstalling every mod.
/// Making both getters return false fixes both. Because the save serializer reads the ModsUser getter, saves made
/// with this on are not marked as modded either. Nothing else in the game reads these two values (bug reports do
/// their own Unity Mod Manager check, so they are still honest).
/// </summary>
internal static class AchievementsFix {
    [HarmonyPatch(typeof(Player), nameof(Player.ModsUser), MethodType.Getter)]
    private static class Player_ModsUser_Patch {
        private static void Postfix(ref bool __result) {
            if (Main.Settings?.KeepAchievements ?? true) {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(OwlcatModificationsManager), nameof(OwlcatModificationsManager.IsAnyModActive), MethodType.Getter)]
    private static class OwlcatModificationsManager_IsAnyModActive_Patch {
        private static void Postfix(ref bool __result) {
            if (Main.Settings?.KeepAchievements ?? true) {
                __result = false;
            }
        }
    }
}
