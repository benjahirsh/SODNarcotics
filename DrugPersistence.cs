using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using HarmonyLib;

namespace Narcotics
{
    // A drug's presetName is written into the game's save, but the cloned InteractablePreset itself
    // only exists in memory. Keep the small recipe needed to re-create that exact preset on the next load.
    // This is deliberately a separate registry, NOT the live operation state: doses from older cases can
    // still be present in a city after a new operation creates a differently named drug.
    internal sealed class SavedDrugPreset
    {
        public string PresetKey { get; set; }
        public string DisplayName { get; set; }
        public string BasePresetName { get; set; }
    }

    internal static class DrugPersistence
    {
        // Shared across save slots on purpose: the generated keys are unique, and a drug from any city
        // must be available BEFORE the game tries to deserialize its interactables. This file must travel
        // with the player's BepInEx config when moving saves to another PC.
        private static readonly string FilePath = Path.Combine(Paths.ConfigPath, "com.opi.narcotics.drug-presets.json");
        private static readonly Dictionary<string, SavedDrugPreset> _known = new Dictionary<string, SavedDrugPreset>();
        private static bool _read;

        private static void ReadOnce()
        {
            if (_read) return;
            _read = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                var entries = JsonSerializer.Deserialize<List<SavedDrugPreset>>(File.ReadAllText(FilePath));
                if (entries == null) return;
                foreach (var entry in entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.PresetKey) ||
                        !entry.PresetKey.StartsWith("NarcDrug_", StringComparison.Ordinal) ||
                        string.IsNullOrEmpty(entry.DisplayName) || string.IsNullOrEmpty(entry.BasePresetName)) continue;
                    _known[entry.PresetKey] = entry;
                }
                NarcoticsPlugin.Log.LogInfo($"[Narcotics][save] loaded {_known.Count} drug preset recipe(s).");
            }
            catch (Exception e) { NarcoticsPlugin.Log.LogWarning($"[Narcotics][save] reading drug registry failed: {e}"); }
        }

        // Commit immediately when the new drug is generated, NOT on SaveGame: an in-memory dictionary
        // would disappear during shutdown, leaving a save that references a key we can no longer rebuild.
        internal static void Remember(string key, string name, string basePresetName)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(basePresetName)) return;
            ReadOnce();
            _known[key] = new SavedDrugPreset { PresetKey = key, DisplayName = name, BasePresetName = basePresetName };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var json = JsonSerializer.Serialize(new List<SavedDrugPreset>(_known.Values),
                    new JsonSerializerOptions { WriteIndented = true });
                string pending = FilePath + ".tmp";
                File.WriteAllText(pending, json);
                File.Move(pending, FilePath, true);   // replace atomically on the same volume; old file stays if writing fails
            }
            catch (Exception e) { NarcoticsPlugin.Log.LogError($"[Narcotics][save] writing drug registry failed: {e}"); }
        }

        // Runs BEFORE SaveStateController.LoadSaveState reads object preset keys. We recreate the same
        // custom presets (same key/name/base appearance) rather than inventing a fresh Guid on reload.
        // Safe to call more than once: CreateDrug reuses a key that is already in the Toolbox dictionary.
        internal static void RestorePresets()
        {
            ReadOnce();
            var tb = Toolbox.Instance;
            var dict = tb != null ? tb.objectPresetDictionary : null;
            if (dict == null) return;   // game substrate not initialized yet; a later load hook retries

            int restored = 0, missing = 0;
            foreach (var entry in _known.Values)
            {
                try
                {
                    InteractablePreset baseP = null;
                    InteractablePreset already = null;
                    if (!dict.TryGetValue(entry.PresetKey, out already) || already == null)
                    {
                        if (!dict.TryGetValue(entry.BasePresetName, out baseP) || baseP == null)
                        {
                            missing++;
                            NarcoticsPlugin.Log.LogWarning($"[Narcotics][save] base preset '{entry.BasePresetName}' not found for {entry.PresetKey}.");
                            continue;
                        }
                    }
                    // Restoring old evidence must NOT change the current case's DrugItem.Current.
                    if (DrugItem.CreateDrug(entry.PresetKey, entry.DisplayName, baseP,
                        makeCurrent: false, persist: false) != null) restored++;
                }
                catch (Exception e) { missing++; NarcoticsPlugin.Log.LogWarning($"[Narcotics][save] restore {entry.PresetKey}: {e}"); }
            }
            if (restored > 0 || missing > 0)
                NarcoticsPlugin.Log.LogInfo($"[Narcotics][save] drug presets ready: {restored}, unresolved: {missing}.");
        }
    }

    // AssetBundleLoader may resolve a saved interactable's preset during LoadSaveState itself.
    // A Postfix or MurderController.OnStartGame hook is already TOO LATE (the missing-preset dialog
    // has been raised), so restore at the very start of save loading with high Harmony priority.
    [HarmonyPatch(typeof(SaveStateController), nameof(SaveStateController.LoadSaveState),
        new Type[] { typeof(StateSaveData) })]
    internal static class Patch_RestoreSavedDrugPresets
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(StateSaveData __0)
        {
            string saveIdentity = CookSessionPersistence.GetSelectedSaveIdentity();
            InvestigationPersistence.BeginLoad(saveIdentity, __0 != null ? __0.gameTime : float.NaN);
            DrugPersistence.RestorePresets();
        }
    }
}
