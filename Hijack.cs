using System;
using HarmonyLib;
using UnityEngine;

namespace Narcotics
{
    // AUTO-START + case override (no F5 needed). The base game commits each proc-gen "case of the day" through
    // MurderController.ExecuteNewMurder (the universal case-creation entry; the same method the Motives mod hooks).
    //
    // SKIP-CREATION model (reverted from the shell-murder approach 2026-10-15): when this slot should become narcotics
    // we SKIP ExecuteNewMurder entirely (return false => NO Murder object is created) and start the narcotics Operation
    // in its place. We do NOT create a real murder to hold the slot - that shell fought the game's watchdogs (which drag
    // the victim toward the den and can force-kill it) and leaked real murders, so the simpler + safer model is: no
    // murder object at all, zero kill risk. Cadence is our own: InCaseGap holds the next narcotics case to the 12h grid
    // (CookIntervalHours) anchored to the previous case, mirroring vanilla's ~12h murder rhythm.
    //
    // Only ORDINARY proc-gen murders are eligible: story/ChapterIntro murders (procGenLoopActive == false) and the
    // special sniper/kidnap case types are left to vanilla / other mods.
    [HarmonyPatch(typeof(MurderController), nameof(MurderController.ExecuteNewMurder))]
    [HarmonyPriority(Priority.First)]
    internal static class Patch_ExecuteNewMurder_Hijack
    {
        private static void DbgLog(string why)
        {
            try { if (NarcoticsConfig.EnableDebugKeys) NarcoticsPlugin.Log.LogInfo($"[Narcotics][hijack] {why}"); } catch { }
        }

        // The game re-fires ExecuteNewMurder every tick while it has no active murder (our op isn't one), so the skip
        // paths below repeat constantly - throttle their log to once every ~20s so they don't flood the console.
        private static float _lastSkipLog;
        private static void SkipLog(string why)
        {
            try
            {
                if (!NarcoticsConfig.EnableDebugKeys) return;
                if (Time.time - _lastSkipLog < 20f) return;
                _lastSkipLog = Time.time;
                NarcoticsPlugin.Log.LogInfo($"[Narcotics][hijack] skipping the game's murder ({why}); throttled log.");
            }
            catch { }
        }

        // Return false  => SKIP ExecuteNewMurder (no vanilla Murder is created this slot).
        // Return true   => let the vanilla murder proceed normally.
        static bool Prefix(MurderController __instance, MurderPreset preset)
        {
            try
            {
                if (!NarcoticsConfig.Enable) { DbgLog("mod disabled -> vanilla murder."); return true; }
                if (__instance == null) return true;
                bool loop = false; try { loop = __instance.procGenLoopActive; } catch { }
                if (!loop) { DbgLog("procGenLoopActive=false (story/intro/direct call) -> vanilla murder."); return true; }

                // A narcotics case is already running: skip this murder so no parallel case spawns (vanilla also runs
                // one murder case at a time). The game re-fires while our op (not a murder) is active, hence the log.
                if (Operation.Active) { SkipLog("a narcotics case is already active"); return false; }
                // The case board may already contain a saved narcotics case while its managed
                // operation is being reconstructed. Never overwrite it with a new operation.
                if (InvestigationPersistence.Loading || InvestigationPersistence.HasUnresolvedCase())
                { SkipLog("saved narcotics investigation pending restoration"); return false; }

                // Leave sniper/kidnap to vanilla / other mods - we only ever replace ORDINARY murders.
                bool ordinaryMurder = true;
                try { ordinaryMurder = preset == null || preset.caseType == MurderPreset.CaseType.murder; } catch { ordinaryMurder = true; }
                if (!ordinaryMurder) { DbgLog("this is a sniper/kidnap case -> leaving it to vanilla (not a narcotics slot)."); return true; }

                float chance = NarcoticsConfig.HijackChance;
                bool alwaysNarc = chance >= 0.999f;
                if (chance <= 0f) { DbgLog("HijackChance=0 -> vanilla murder (narcotics only via F5)."); return true; }
                if (chance < 1f && UnityEngine.Random.value > chance) { DbgLog("dice: this slot stays a vanilla murder."); return true; }

                // Within the 12h grid gap since the last narcotics case -> the next case isn't due yet. At HijackChance~1
                // (narcotics-only) suppress the murder so NOTHING happens until the grid point (the 12h quiet between
                // cases); while mixing, let this slot be a normal vanilla murder so the gap isn't dead air.
                if (Operation.InCaseGap())
                {
                    if (alwaysNarc) { SkipLog("within the 12h grid gap (narcotics-only: no case until the next grid point)"); return false; }
                    DbgLog("within the 12h grid gap -> this slot stays a vanilla murder (mixing mode)."); return true;
                }

                // CONVERT: start the narcotics case in this slot and SKIP the murder (no Murder object created). If Start
                // fails (e.g. no vacant lab right now): at HijackChance~1 skip anyway (no vanilla; the game re-picks and
                // we retry next tick); while mixing, let the vanilla murder proceed.
                if (Operation.Start())
                {
                    NarcoticsPlugin.Log.LogInfo("[Narcotics][hijack] replaced the game's selected murder with a narcotics case (no murder created; 12h-grid cadence).");
                    return false;
                }
                if (alwaysNarc) { SkipLog("Operation.Start() refused (narcotics-only: no vanilla; will retry)"); return false; }
                NarcoticsPlugin.Log.LogInfo("[Narcotics][hijack] Operation.Start() refused; this slot stays a vanilla murder (mixing mode).");
                return true;
            }
            catch (Exception e)
            {
                try { NarcoticsPlugin.Log.LogWarning($"[Narcotics][hijack] prefix threw ({e.Message}); vanilla murder proceeds."); } catch { }
                return true;
            }
        }
    }
}
