using HarmonyLib;
using System.Reflection;
using UnityEngine;
using UnityModManagerNet;

namespace HitChanceOverlay;

public static class Main {
    internal static Harmony HarmonyInstance;
    internal static UnityModManager.ModEntry.ModLogger Log;
    internal static Settings Settings;
    internal static bool Enabled;

    private static GameObject s_OverlayObject;

    public static bool Load(UnityModManager.ModEntry modEntry) {
        Log = modEntry.Logger;
        Settings = UnityModManager.ModSettings.Load<Settings>(modEntry);
        modEntry.OnGUI = OnGUI;
        modEntry.OnSaveGUI = entry => Settings.Save(entry);
        modEntry.OnToggle = OnToggle;

        HarmonyInstance = new Harmony(modEntry.Info.Id);
        try {
            HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly());
        } catch {
            HarmonyInstance.UnpatchAll(HarmonyInstance.Id);
            throw;
        }

        s_OverlayObject = new GameObject("HitChanceOverlay");
        UnityEngine.Object.DontDestroyOnLoad(s_OverlayObject);
        s_OverlayObject.AddComponent<Overlay>();
        Enabled = true;
        Log.Log("Loaded");
        return true;
    }

    private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value) {
        Enabled = value;
        return true;
    }

    private static void OnGUI(UnityModManager.ModEntry modEntry) {
        Settings.KeepAchievements = GUILayout.Toggle(Settings.KeepAchievements, " Keep achievements enabled while using mods (and don't mark saves as modded)");
        Settings.AreaLabels = GUILayout.Toggle(Settings.AreaLabels, " Area spells: show odds over every creature in the area (turn off if it slows your game)");
        GUILayout.BeginHorizontal();
        GUILayout.Label($"Font size: {Settings.FontSize}", GUILayout.Width(120));
        Settings.FontSize = (int)GUILayout.HorizontalSlider(Settings.FontSize, 10, 32, GUILayout.Width(200));
        GUILayout.EndHorizontal();
    }
}

public class Settings : UnityModManager.ModSettings {
    public bool KeepAchievements = true;
    public bool AreaLabels = true;
    public int FontSize = 20;

    public override void Save(UnityModManager.ModEntry modEntry) => Save(this, modEntry);
}
