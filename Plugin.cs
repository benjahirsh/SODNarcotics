using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace Narcotics
{
    // Standalone Shadows of Doubt mod: a narcotics/drug-cases layer.
    // A hidden "cook" runs a drug lab, hands doses to homeless dealers, who sell on the
    // street; buyers using in public get arrested by enforcers, opening a case the player
    // traces (CCTV + physical evidence) back to the lab. See DESIGN.md.
    //
    // This scaffold intentionally ships NO gameplay patches yet: it proves the build,
    // deploy and config pipeline for the new project while the engine is designed.
    [BepInPlugin(Guid, "Narcotics", "0.3.0")]
    public class NarcoticsPlugin : BasePlugin
    {
        public const string Guid = "com.opi.narcotics";
        internal static new ManualLogSource Log;
        private Harmony _harmony;

        // Every knob maps to a re-apply action, run at load AND whenever the value changes
        // (in-game ConfigurationManager overlay, a .cfg reload, or our own writes), so edits
        // take effect live. Same pattern as the author's other mod.
        private readonly Dictionary<ConfigEntryBase, Action> _applyByEntry = new Dictionary<ConfigEntryBase, Action>();

        public override void Load()
        {
            Log = base.Log;
            Banner();

            // --- General ---
            BindApply("General", "EnableNarcotics", true,
                "Master switch for the narcotics layer. Off = the mod loads but runs no drug operation.",
                v => NarcoticsConfig.Enable = v);
            BindApply("General", "HijackChance", 1.0f,
                "Share (0.0-1.0) of the game's own cases to replace with a narcotics case. 1.0 = every eligible vanilla murder becomes THE narcotics case of the day (auto-start, no F5 needed); 0.0 = never auto-start (use F5 only).",
                v => NarcoticsConfig.HijackChance = v,
                R01());   // 0..1 -> renders as a SLIDER in the config manager + clamps

            // --- Distribution network (cook -> 1 distributor -> dead drops -> dealers) ---
            BindApply("Distribution", "CookDosesPerBatch", 1,
                "How many doses the cook makes per batch.",
                v => NarcoticsConfig.CookStashCap = v);
            BindApply("Distribution", "CookBatchesPerSession", 6,
                "How many doses the cook makes per day (one at a time, on demand). 5 = about 5 sales and so ~5 arrests per day. The cook stays in the lab until all are made, then goes home for the day.",
                v => NarcoticsConfig.CookBatchesPerSession = v);
            BindApply("Distribution", "CookBatchGapHours", 4f,
                "In-game hours the cook waits (in the lab) between batches. 4h = batch 2 is ready ~4h after batch 1.",
                v => NarcoticsConfig.CookBatchGapHours = v);
            BindApply("Distribution", "CookIntervalHours", 12f,
                "In-game hours between the cook's lab SESSIONS (anchored to case init: +12h, +24h...). Mirrors the vanilla ~12h serial re-strike.",
                v => NarcoticsConfig.CookIntervalHours = v);
            BindApply("Distribution", "CookShiftHours", 8f,
                "In-game hours the cook STAYS at the lab each session before going home. Goes home at the shift END regardless of doses made (the per-session dose count is a CEILING, not a requirement), so the lab is a reliable stakeout window.",
                v => NarcoticsConfig.CookShiftHours = v);
            BindApply("Distribution", "Dealers", 1,
                "How many street dealers (always homeless NPCs) collect from the dead drops. (One distributor always.) 1 = easiest to solve (one person to identify).",
                v => NarcoticsConfig.DealerCount = v);
            BindApply("Distribution", "DosesPerDealer", 1,
                "How many doses a distributor leaves at each dealer's dead drop (also the dealer's target hold). 1 = the dealer returns to the drop between every sale.",
                v => NarcoticsConfig.CarryTarget = v);
            BindApply("Distribution", "DealerSellIntervalHours", 2.0f,
                "In-game hours a dealer waits between sales to passers-by.",
                v => NarcoticsConfig.DealerSellIntervalHours = v);
            BindApply("Distribution", "BuyerSearchRangeM", 12.0f,
                "How close (metres) a passer-by must be for a dealer to sell to them.",
                v => NarcoticsConfig.BuyerSearchRangeM = v);
            BindApply("Distribution", "DealDwellMinutes", 20.0f,
                "In-GAME minutes the dealer and buyer stay locked on the meet node during a deal, so a CCTV capture lands while they're in frame (scales with time fast-forward, unlike real seconds).",
                v => NarcoticsConfig.DealDwellMinutes = v);
            BindApply("Distribution", "DealShiftMinutes", 10.0f,
                "In-GAME minutes into a deal before the pair shift ONCE to a new on-camera spot nearby (so they spend DealShiftMinutes at the first spot, then the remainder at the second). Set >= DealDwellMinutes to never shift.",
                v => NarcoticsConfig.DealShiftMinutes = v);
            BindApply("Distribution", "DropDwellMinutes", 3.0f,
                "In-GAME minutes the DISTRIBUTOR pauses stashing a dose at the dead drop (held visibly so the player can catch the drop being used on camera).",
                v => NarcoticsConfig.DropDwellMinutes = v);
            BindApply("Distribution", "DropCollectMinutes", 5.0f,
                "In-GAME minutes the DEALER pauses collecting from the dead drop. Shorter than the stash dwell - the pickup doesn't need to be caught on camera, so it keeps stalking from being drawn out.",
                v => NarcoticsConfig.DropCollectMinutes = v);
            BindApply("Distribution", "PotterAfterCollectMinutes", 5.0f,
                "In-GAME minutes the dealer potters (its own free routine) AFTER collecting from the dead drop before meeting a buyer, so the deal happens away from the drop and the game can pick a buyer near a different camera.",
                v => NarcoticsConfig.PotterAfterCollectMinutes = v);

            // --- Arrest / case (a buyer overdoses in public -> enforcer arrest -> the player's case opens) ---
            BindApply("Arrest", "OverdoseDelayMinutes", 5.0f,
                "In-game minutes after a qualifying sale before that buyer overdoses in public. The overdose summons an enforcer who arrests them, which opens (or adds to) your narcotics case.",
                v => NarcoticsConfig.OverdoseDelayMinutes = v);
            BindApply("Arrest", "OverdoseIntervalMinutes", 10.0f,
                "In-game minutes AFTER an arrest before the dealer meets the next buyer (and that buyer later overdoses). Paces the whole sale->overdose->arrest cycle.",
                v => NarcoticsConfig.OverdoseIntervalMinutes = v);
            BindApply("Arrest", "ArrestHoldHours", 12.0f,
                "In-game hours an arrested buyer stays cuffed in place (or until you solve the case), giving you time to find and search them and pin their dose to the case board.",
                v => NarcoticsConfig.ArrestHoldHours = v);
            BindApply("Arrest", "ArrestGuardMinutes", 60.0f,
                "In-game minutes a uniformed officer stands guard over the cuffed suspect before returning to patrol (the suspect stays cuffed for the full hold). Stops a single officer standing frozen the whole time.",
                v => NarcoticsConfig.ArrestGuardMinutes = v);

            // --- Debug (developer tooling) ---
            // NOTE: default ON while the mod is entirely debug-key-driven (no natural operation start/arrest yet).
            // Defaults OFF for release. Developers re-enable it in their own config (an existing cfg value is preserved).
            BindApply("Debug", "EnableDebugKeys", false,
                "Developer tooling. ON: enables the debug hotkeys and the on-screen panels. OFF (default): all inert.",
                v => NarcoticsConfig.EnableDebugKeys = v);

            // --- Debug Keys (rebindable; only act while EnableDebugKeys is ON) ---
            BindApply("Debug Keys", "GodMode", UnityEngine.KeyCode.F7,
                "TESTING: toggle god mode (NPCs/cameras ignore you, no trespass, invincible, echelon access, test cash), so you can freely follow the cook and dealers. Set to None to disable.",
                v => NarcoticsConfig.KeyGodMode = v);
            BindApply("Debug Keys", "TeleportToNextNPC", UnityEngine.KeyCode.F2,
                "TESTING: teleport to the next NPC in the operation (cook -> distributor -> dealers -> back), to verify inventories/positions after watching from the apartment. Set to None to disable.",
                v => NarcoticsConfig.KeyTeleportNPC = v);
            BindApply("Debug Keys", "TeleportToDrop", UnityEngine.KeyCode.F3,
                "TESTING: teleport to the next dead drop (cycles through the dealers' drops) so you can find and search them. Set to None to disable.",
                v => NarcoticsConfig.KeyDropTeleport = v);
            BindApply("Debug Keys", "TeleportCityHall", UnityEngine.KeyCode.Home,
                "TESTING: teleport to City Hall (to collect + submit the case resolution form). Set to None to disable.",
                v => NarcoticsConfig.KeyCityHall = v);
            BindApply("Debug Keys", "OverdoseNearNPC", UnityEngine.KeyCode.F8,
                "TESTING: make the nearest NPC behave erratically (drunk + sick) and flag a public offence, to watch whether witnesses report it and enforcers get summoned to arrest them. Set to None to disable.",
                v => NarcoticsConfig.KeyOverdose = v);
            BindApply("Debug Keys", "ArrestTeleport", UnityEngine.KeyCode.F10,
                "TESTING: teleport to the current arrest / overdose subject so you can watch it. Set to None to disable.",
                v => NarcoticsConfig.KeyArrestTeleport = v);
            BindApply("Debug Keys", "StartOperation", UnityEngine.KeyCode.F5,
                "TESTING: start a drug operation now (pick a cook, site a lab in a vacant address or their home if they live alone, and pin the cook to the lab). Set to None to disable.",
                v => NarcoticsConfig.KeyStartOp = v);
            BindApply("Debug Keys", "StopOperation", UnityEngine.KeyCode.F6,
                "TESTING: stop the active drug operation and release the cook back to their routine. Set to None to disable.",
                v => NarcoticsConfig.KeyStopOp = v);
            BindApply("Debug Keys", "TeleportToLab", UnityEngine.KeyCode.F11,
                "TESTING: teleport to the current operation's lab so you can watch the cook. Set to None to disable.",
                v => NarcoticsConfig.KeyTeleportLab = v);
            BindApply("Debug Keys", "DressLab", UnityEngine.KeyCode.F12,
                "TESTING: dress the current lab with lab-reading furniture (stand in the lab first for reliable placement). Set to None to disable.",
                v => NarcoticsConfig.KeyDressLab = v);
            BindApply("Debug Keys", "LogPanel", UnityEngine.KeyCode.F9,
                "TESTING: toggle the on-screen debug log panel (shows recent mod activity so you don't need the console). Set to None to disable.",
                v => NarcoticsConfig.KeyLogPanel = v);
            BindApply("Debug Keys", "ForceSale", UnityEngine.KeyCode.F4,
                "TESTING: force a dealer holding doses to sell to the nearest passer-by now (dealers also sell automatically on a timer). Set to None to disable.",
                v => NarcoticsConfig.KeyForceSale = v);
            BindApply("Debug Keys", "ResolveFormStep", UnityEngine.KeyCode.PageUp,
                "TESTING: step the open narcotics case's resolution form (1st press = collect the form from City Hall = render the fillable pane; 2nd press = submit). For testing the form without the City Hall tray. Set to None to disable.",
                v => NarcoticsConfig.KeyHandIn = v);
            BindApply("Debug Keys", "FastForward", UnityEngine.KeyCode.End,
                "TESTING: toggle fast-forward (the game's efficient 'simulation' speed, boosted by TimeBoostMultiplier) so operations play out quickly. Set to None to disable.",
                v => NarcoticsConfig.KeyTimeBoost = v);
            BindApply("Debug Keys", "RevealMurder", UnityEngine.KeyCode.Delete,
                "TESTING: dump the current VANILLA murder's answers (killer, where they live, weapon) to the log/F9 panel, so you can cheat-solve a real murder at City Hall and compare its resolve sequence with ours. Set to None to disable.",
                v => NarcoticsConfig.KeyRevealMurder = v);
            BindApply("Debug Keys", "UnlockComputer", UnityEngine.KeyCode.Insert,
                "TESTING: log into the computer you're currently using WITHOUT its password (as the computer's owner), so you can open its surveillance app and check CCTV footage quickly. Sit at the computer, then press the key. Set to None to disable.",
                v => NarcoticsConfig.KeyUnlockComputer = v);
            BindApply("Debug Keys", "SpawnFurnitureHere", UnityEngine.KeyCode.PageDown,
                "TESTING: spawn the lab-equipment furniture clusters into the room you are standing in right now, to check whether furniture placement works in a normal room. Set to None to disable.",
                v => NarcoticsConfig.KeySpawnFurniture = v);
            BindApply("Debug Keys", "DumpResolveQuestions", UnityEngine.KeyCode.Backspace,
                "TESTING: dump every active case's resolve-form questions (and their fields) to the log, so the exact arrest/location wiring of a vanilla murder case can be read. Set to None to disable.",
                v => NarcoticsConfig.KeyDumpResolve = v);
            BindApply("Debug", "TimeBoostMultiplier", 3.0f,
                "Fast-forward multiplier applied on top of the game's fastest built-in speed when fast-forward (End) is on. 1 = just simulation speed. The game may clamp values it can't keep up with.",
                v => NarcoticsConfig.TimeBoostMultiplier = v);

            // Live re-apply: when a knob changes (overlay edit / .cfg reload), push it into its static field.
            Config.SettingChanged += (sender, e) =>
            {
                try
                {
                    var cs = (e as SettingChangedEventArgs)?.ChangedSetting;
                    if (cs != null && _applyByEntry.TryGetValue(cs, out var apply)) apply();
                }
                catch (Exception ex) { Log.LogWarning($"[Narcotics] config live-apply error: {ex.Message}"); }
            };

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(NarcoticsPlugin).Assembly);
            try { BepInEx.Logging.Logger.Listeners.Add(new NarcLogListener()); } catch (Exception e) { Log.LogWarning($"[Narcotics] log panel listener failed: {e.Message}"); }
            Diagnostics.Register();
            Log.LogInfo($"[Narcotics] Patches applied. Narcotics layer = {(NarcoticsConfig.Enable ? "ON" : "OFF")}.");
        }

        private void Banner()
        {
            Log.LogInfo("========================================================");
            Log.LogInfo("==  NARCOTICS MOD  -  v0.3.0 (beta)                    ==");
            Log.LogInfo("==  If you can read this, the mod is LOADED & ACTIVE.  ==");
            Log.LogInfo("========================================================");
        }

        // ---- config helpers -----------------------------------------------

        // Bind a knob, apply it now, and register it for live re-apply when it changes (overlay / .cfg).
        // range = optional AcceptableValueRange (renders a slider + clamps); ui = optional overlay hints.
        private void BindApply<T>(string section, string key, T def, string desc, Action<T> apply,
                                  AcceptableValueBase range = null, ConfigurationManagerAttributes ui = null)
        {
            ConfigDescription cd =
                (range == null && ui == null) ? new ConfigDescription(desc)
              : (ui == null) ? new ConfigDescription(desc, range)
              : new ConfigDescription(desc, range, ui);
            ConfigEntry<T> entry = Config.Bind(section, key, def, cd);
            ConfigEntryBase baseEntry = entry;
            Action applyNow = () =>
            {
                // Float knobs: snap to 2 decimals so the overlay (which renders the raw value with no
                // format control) shows a clean number instead of a long fraction.
                if (baseEntry is ConfigEntry<float> fe)
                {
                    float r = (float)Math.Round(fe.Value, 2, MidpointRounding.AwayFromZero);
                    if (Math.Abs(r - fe.Value) > 0.0005f) { fe.Value = r; return; }
                }
                apply(entry.Value);
            };
            applyNow();
            _applyByEntry[entry] = applyNow;
        }

        private static AcceptableValueRange<float> R01() => new AcceptableValueRange<float>(0f, 1f);         // 0..1 slider
        private static ConfigurationManagerAttributes Order(int order) => new ConfigurationManagerAttributes { Order = order };

        // ---- logging helpers -------------------------------------------------

        internal static string F(float x) => float.IsNaN(x) ? "NaN" : x.ToString("0.00");

        internal static string Name(Human h)
        {
            if (h == null) return "<null>";
            try { return $"{h.citizenName}#{h.humanID}"; }
            catch { return "<name-error>"; }
        }
    }

    // Runtime config, populated by BindApply. Feature modules read these statics.
    internal static class NarcoticsConfig
    {
        public static bool Enable = true;
        public static bool EnableDebugKeys = false;   // release default OFF; developers re-enable in their own config
        public static float HijackChance = 1.0f;     // 0..1 share of vanilla cases to replace with a narcotics case (1.0 = always auto-start as THE case of the day)

        // Distribution network (cook -> 1 distributor -> dead drops -> dealers).
        public static int DealerCount = 1;
        public static int CarryTarget = 1;        // doses per dealer dead drop / dealer target hold (1 = back to the drop every sale)
        public static int CookStashCap = 1;          // doses per BATCH
        public static int CookBatchesPerSession = 6; // doses the cook makes per day (on demand), ~6 sales/arrests per day
        public static float CookBatchGapHours = 4f;  // in-game hours the cook stays in the lab between batches
        public static float CookIntervalHours = 12f; // in-game hours between re-cook sessions (anchored to case init; mirrors the vanilla 12h serial re-strike)
        public static float CookShiftHours = 8f;     // in-game hours the cook STAYS at the lab each session before going home (goes home at shift END regardless of doses made; 6 is a ceiling, not a requirement)
        public static float DealerSellIntervalHours = 2.0f;   // a sale every 2h = 2 sales per 2-dose batch
        public static float BuyerSearchRangeM = 12.0f;
        public static float DealDwellMinutes = 20.0f;      // in-game minutes the pair holds on the meet node for the camera
        public static float DealShiftMinutes = 10.0f;       // in-game minutes into a deal before the ONE shift to a second on-camera spot
        public static float DropDwellMinutes = 3.0f;       // in-game minutes the DISTRIBUTOR pauses stashing at a dead drop (on camera, so the player can catch the drop being used)
        public static float DropCollectMinutes = 5.0f;     // in-game minutes the DEALER pauses collecting from a dead drop (shorter; not needed on camera)
        public static float PotterAfterCollectMinutes = 5.0f;   // in-game minutes the dealer potters after collecting before meeting a buyer

        // Arrest / case.
        public static float OverdoseDelayMinutes = 5.0f;   // minutes after a buy before the buyer overdoses
        public static float OverdoseIntervalMinutes = 10.0f;  // in-game minutes after an arrest before the next sale/overdose
        public static float ArrestHoldHours = 12.0f;       // in-game hours the arrestee stays cuffed (or until solved)
        public static float ArrestGuardMinutes = 60.0f;    // in-game minutes a uniformed officer guards before leaving

        public static UnityEngine.KeyCode KeyDump = UnityEngine.KeyCode.None;      // substrate dump now unbound (dev tool)
        public static UnityEngine.KeyCode KeyCityHall = UnityEngine.KeyCode.Home;  // teleport to City Hall
        public static UnityEngine.KeyCode KeyDropTeleport = UnityEngine.KeyCode.F3;
        public static UnityEngine.KeyCode KeyGodMode = UnityEngine.KeyCode.F7;
        public static UnityEngine.KeyCode KeyOverdose = UnityEngine.KeyCode.F8;
        public static UnityEngine.KeyCode KeyArrestTeleport = UnityEngine.KeyCode.F10;
        public static UnityEngine.KeyCode KeyStartOp = UnityEngine.KeyCode.F5;
        public static UnityEngine.KeyCode KeyStopOp = UnityEngine.KeyCode.F6;
        public static UnityEngine.KeyCode KeyTeleportLab = UnityEngine.KeyCode.F11;
        public static UnityEngine.KeyCode KeyDressLab = UnityEngine.KeyCode.F12;
        public static UnityEngine.KeyCode KeyLogPanel = UnityEngine.KeyCode.F9;
        public static UnityEngine.KeyCode KeyForceSale = UnityEngine.KeyCode.F4;
        public static UnityEngine.KeyCode KeyTimeBoost = UnityEngine.KeyCode.End;
        public static UnityEngine.KeyCode KeyRevealMurder = UnityEngine.KeyCode.Delete;
        public static UnityEngine.KeyCode KeyHandIn = UnityEngine.KeyCode.PageUp;
        public static UnityEngine.KeyCode KeyTeleportNPC = UnityEngine.KeyCode.F2;
        public static UnityEngine.KeyCode KeyUnlockComputer = UnityEngine.KeyCode.Insert;   // log into the computer you're using (skip its password), to check surveillance quickly
        public static UnityEngine.KeyCode KeySpawnFurniture = UnityEngine.KeyCode.PageDown;  // TEST: spawn lab furniture in the room you're standing in
        public static UnityEngine.KeyCode KeyDumpResolve = UnityEngine.KeyCode.Backspace;    // TEST: dump active cases' resolve-form question wiring
        public static float TimeBoostMultiplier = 3.0f;
    }

    // Overlay integration: the BepInEx ConfigurationManager / BepInExConfigManager overlays recognise a
    // settings-tags class purely by NAME ("ConfigurationManagerAttributes") via reflection - no assembly
    // reference needed. We declare only the few fields we use (nullable so unset = the overlay's default).
    public sealed class ConfigurationManagerAttributes
    {
        public int? Order;
        public bool? Browsable;
        public bool? IsAdvanced;
    }
}
