using System;
using UnityEngine;
using Il2CppInterop.Runtime.Injection;

namespace Narcotics
{
    // Phase 0 in-game smoke tests, driven by debug hotkeys. These de-risk the reds/yellows and
    // reveal the runtime strings (item/furniture names) we cannot read from the DLL, before the
    // engine hardcodes anything. All behind [Debug] EnableDebugKeys.
    internal static class Diagnostics
    {
        internal static void Register()
        {
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<NarcoticsHotkey>();
                var go = new GameObject("Narcotics_Debug");
                GameObject.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<NarcoticsHotkey>();
                NarcoticsPlugin.Log.LogInfo($"[Narcotics] Debug hotkeys (need [Debug] EnableDebugKeys): {NarcoticsConfig.KeyTeleportNPC}=teleport to next NPC, {NarcoticsConfig.KeyDropTeleport}=teleport to next dead drop, {NarcoticsConfig.KeyForceSale}=force a sale, {NarcoticsConfig.KeyStartOp}=start op, {NarcoticsConfig.KeyStopOp}=stop op, {NarcoticsConfig.KeyGodMode}=god mode, {NarcoticsConfig.KeyOverdose}=overdose nearest NPC, {NarcoticsConfig.KeyArrestTeleport}=teleport to subject, {NarcoticsConfig.KeyLogPanel}=log panel, {NarcoticsConfig.KeyTeleportLab}=to lab, {NarcoticsConfig.KeyDressLab}=dress lab, {NarcoticsConfig.KeyTimeBoost}=fast-forward, {NarcoticsConfig.KeyUnlockComputer}=log into the computer you're using (skip password), {NarcoticsConfig.KeySpawnFurniture}=spawn lab furniture in your room (test), {NarcoticsConfig.KeyDump}=substrate dump.");
            }
            catch (Exception e) { NarcoticsPlugin.Log.LogWarning($"[Narcotics] hotkey register failed: {e.Message}"); }
        }

        // Read-only: dump population counts, drug-ish item presets, and lab-reading furniture clusters.
        internal static void DumpSubstrate()
        {
            var log = NarcoticsPlugin.Log;
            log.LogInfo("[Narcotics] ===== SUBSTRATE DUMP =====");
            try
            {
                var cd = CityData.Instance;
                if (cd != null)
                {
                    int homeless = -1, cits = -1, addrs = -1, homed = -1;
                    try { homeless = cd.homelessDirectory != null ? cd.homelessDirectory.Count : -1; } catch { }
                    try { cits = cd.citizenDirectory != null ? cd.citizenDirectory.Count : -1; } catch { }
                    try { homed = cd.homedDirectory != null ? cd.homedDirectory.Count : -1; } catch { }
                    try { addrs = cd.addressDirectory != null ? cd.addressDirectory.Count : -1; } catch { }
                    log.LogInfo($"[Narcotics]  homeless={homeless}  homed={homed}  citizens={cits}  addresses={addrs}");
                }
                else log.LogInfo("[Narcotics]  CityData.Instance null (load a game first)");
            }
            catch (Exception e) { log.LogWarning($"[Narcotics]  city dump error: {e.Message}"); }

            log.LogInfo("[Narcotics]  --- drug-ish item presets (name scan) ---");
            DrugItem.FindBasePreset(logAll: true);

            log.LogInfo("[Narcotics]  --- lab-reading furniture clusters (name scan) ---");
            try
            {
                var tb = Toolbox.Instance;
                if (tb != null && tb.allFurnitureClusters != null)
                {
                    int n = 0, hits = 0;
                    foreach (var fc in tb.allFurnitureClusters)
                    {
                        n++;
                        string nm = null; try { nm = fc != null ? fc.name : null; } catch { }
                        if (string.IsNullOrEmpty(nm)) continue;
                        string low = nm.ToLowerInvariant();
                        if (low.Contains("lab") || low.Contains("chem") || low.Contains("kitchen") || low.Contains("industr")
                          || low.Contains("medic") || low.Contains("counter") || low.Contains("workbench") || low.Contains("bench")
                          || low.Contains("equip") || low.Contains("machine") || low.Contains("cook") || low.Contains("science"))
                        { hits++; log.LogInfo($"[Narcotics]    cluster: '{nm}'"); }
                    }
                    log.LogInfo($"[Narcotics]  furniture clusters: {n} total, {hits} lab-ish");
                }
                else log.LogInfo("[Narcotics]  Toolbox.allFurnitureClusters null");
            }
            catch (Exception e) { log.LogWarning($"[Narcotics]  furniture dump error: {e.Message}"); }
            log.LogInfo("[Narcotics] ===== END DUMP =====");
        }

        // Prove the drug item AND the searchable physical dose: clone + rename, then put a real, friskable dose
        // into the player's inventory (open your inventory to confirm "Mystery Drug A" is there). Physical doses
        // are what a body search reveals - the same primitive the chain now uses to carry doses on NPCs, so if
        // this shows on the player it will show when you frisk a dealer (use F4 during an operation to stock them).
        internal static void DrugSmokeTest()
        {
            var log = NarcoticsPlugin.Log;
            log.LogInfo("[Narcotics] ===== DRUG SMOKE TEST =====");
            try
            {
                var drug = DrugItem.CreateDrug("NarcMysteryDrugA", "Mystery Drug A");
                if (drug == null) { log.LogWarning("[Narcotics]  drug create FAILED (see [item] logs; run the substrate dump for item names)"); return; }
                var player = Player.Instance;
                if (player != null)
                {
                    var it = Dose.Create(player);
                    log.LogInfo($"[Narcotics]  created a physical dose in your inventory: {(it != null ? "OK" : "FAILED")} (you now hold x{Dose.CountIn(player)}). Open your inventory to confirm 'Mystery Drug A'.");
                    try
                    {
                        var world = DrugItem.SpawnDoseAtActor(drug, player, null);
                        log.LogInfo($"[Narcotics]  also placed a loose dose near you: name='{(world != null ? SafeItemName(world) : "<null>")}'.");
                    }
                    catch (Exception e) { log.LogWarning($"[Narcotics]  dose placement error: {e.Message}"); }
                }
                else log.LogInfo("[Narcotics]  Player.Instance null");
            }
            catch (Exception e) { log.LogWarning($"[Narcotics]  drug smoke test error: {e}"); }
            log.LogInfo("[Narcotics] ===== END DRUG SMOKE TEST =====");
        }

        // Prove the case host WITHOUT the player-facing popup: construct a Case directly
        // (CreateNewCase routes CaseType.custom through a confirm dialog; the ctor does not).
        internal static void CaseSmokeTest()
        {
            var log = NarcoticsPlugin.Log;
            log.LogInfo("[Narcotics] ===== CASE SMOKE TEST (direct construct, no popup) =====");
            try
            {
                var cpc = CasePanelController.Instance;
                if (cpc == null) { log.LogWarning("[Narcotics]  CasePanelController.Instance null (load a game)"); return; }

                int before = -1; try { before = cpc.activeCases != null ? cpc.activeCases.Count : -1; } catch { }
                Case c = new Case("New Drug on the Street", Case.CaseType.custom, Case.CaseStatus.handInNotCollected);
                int afterCtor = -1; try { afterCtor = cpc.activeCases != null ? cpc.activeCases.Count : -1; } catch { }
                bool present = false; try { present = cpc.activeCases != null && cpc.activeCases.Contains(c); } catch { }
                log.LogInfo($"[Narcotics]  activeCases before={before} afterCtor={afterCtor} ctorSelfRegistered={present}");

                if (!present)
                {
                    try { cpc.activeCases.Add(c); log.LogInfo("[Narcotics]  manually added case to activeCases"); }
                    catch (Exception e) { log.LogWarning($"[Narcotics]  activeCases.Add failed: {e.Message}"); }
                }
                try { cpc.SetActiveCase(c); } catch (Exception e) { log.LogWarning($"[Narcotics]  SetActiveCase failed: {e.Message}"); }
                try { cpc.UpdateCaseButtonsActive(); } catch (Exception e) { log.LogWarning($"[Narcotics]  UpdateCaseButtonsActive failed: {e.Message}"); }
                try { cpc.UpdateCaseControls(); } catch (Exception e) { log.LogWarning($"[Narcotics]  UpdateCaseControls failed: {e.Message}"); }

                int final = -1; try { final = cpc.activeCases != null ? cpc.activeCases.Count : -1; } catch { }
                log.LogInfo($"[Narcotics]  built custom case type={SafeType(c)} status={SafeStatus(c)}; activeCases now {final}. Check the board (should appear with NO popup).");

                // The dramatic "new case" announcement, matching how murders/kidnappings arrive:
                // the big centre headline banner + the new-murder-case sting.
                Announce("NEW NARCOTICS CASE");
            }
            catch (Exception e) { log.LogWarning($"[Narcotics]  case smoke test error: {e}"); }
            log.LogInfo("[Narcotics] ===== END CASE SMOKE TEST =====");
        }

        // Fire the big on-screen "NEW ... CASE" banner (InterfaceController.NewMurderCaseDisplay ->
        // DisplayMissionEndText). Its main text is a FIXED Strings key ("New Murder Case"), so to show our own
        // wording we temporarily override that key, fire the banner, and restore the vanilla text a few seconds
        // later (via TickBannerRestore) so real murder banners are unaffected.
        private static float _bannerRestoreAt = -1f;

        internal static void Announce(string message)
        {
            var log = NarcoticsPlugin.Log;
            try
            {
                var ic = InterfaceController.Instance;
                if (ic == null) { log.LogWarning("[Narcotics]  Announce: InterfaceController.Instance null"); return; }
                // The banner reads its text via a DEFERRED Strings.Get (after this call returns), so the override
                // must stay armed past the call. ONE-SHOT: the Strings.Get patch clears the override the instant it
                // substitutes, so ONLY our banner's read is caught and a vanilla murder banner moments later is not.
                // A 2s safety timer (TickBannerRestore) clears it if that read never arrives.
                Patch_Strings_Get.BannerOverride = message;
                _bannerRestoreAt = UnityEngine.Time.time + 2f;
                ic.NewMurderCaseDisplay();
                log.LogInfo($"[Narcotics]  fired big banner with text '{message}'");
            }
            catch (Exception e) { Patch_Strings_Get.BannerOverride = null; log.LogWarning($"[Narcotics]  Announce error: {e}"); }
        }

        // Clear the banner Strings.Get override a couple of seconds after firing, so vanilla lookups are untouched.
        internal static void TickBannerRestore()
        {
            if (_bannerRestoreAt < 0f) return;
            try { if (UnityEngine.Time.time < _bannerRestoreAt) return; Patch_Strings_Get.BannerOverride = null; }
            catch { }
            finally { _bannerRestoreAt = -1f; }
        }

        // DEBUG CHEAT: dump the current VANILLA murder's resolve answers (killer, where they live, weapon, scene), so
        // you can solve a real murder at City Hall and watch the vanilla success sequence to compare with ours.
        // Dump every ACTIVE vanilla case (murder / sniper / KIDNAP) with its NPC pair + their home and work addresses,
        // so a vanilla kidnapping can be inspected. Iterates MurderController.activeMurders (not just GetCurrentMurder),
        // flags our own narcotics shell so it isn't mistaken for a real case, and prints the kidnap-specific fields.
        internal static void RevealMurder()
        {
            var log = NarcoticsPlugin.Log;
            try
            {
                var mc = MurderController.Instance;
                if (mc == null) { log.LogInfo("[Narcotics][case] no MurderController (load a game first)."); return; }
                var list = mc.activeMurders;
                int n = 0; try { n = list != null ? list.Count : 0; } catch { }
                MurderController.Murder cur = null; try { cur = mc.GetCurrentMurder(); } catch { }
                log.LogInfo($"[Narcotics][case] ===== ACTIVE CASES (activeMurders={n}; narcoticsCaseActive={Operation.Active}) =====");
                if (n == 0) { log.LogInfo("[Narcotics][case]  none right now - no active murder/sniper/kidnapping (wait for one, or it was solved)."); return; }
                for (int i = 0; i < n; i++)
                {
                    MurderController.Murder m = null; try { m = list[i]; } catch { }
                    if (m == null) continue;

                    string type = "?"; try { var p = m.preset; if (p != null) type = p.caseType.ToString(); } catch { }
                    string state = "?"; try { state = m.state.ToString(); } catch { }
                    bool isCur = false; try { isCur = cur != null && m.Pointer == cur.Pointer; } catch { }
                    log.LogInfo($"[Narcotics][case] --- case {i}: {type.ToUpperInvariant()}  state={state}{(isCur ? "  [current]" : "")} ---");

                    Human killer = null, victim = null; try { killer = m.murderer; } catch { } try { victim = m.victim; } catch { }
                    log.LogInfo($"[Narcotics][case]   {(type == "kidnap" ? "KIDNAPPER" : "PERPETRATOR")}: {WhoAddr(killer)}");
                    log.LogInfo($"[Narcotics][case]   VICTIM:     {WhoAddr(victim)}");

                    string scene = "?"; try { if (m.location != null) scene = m.location.name; } catch { }
                    log.LogInfo($"[Narcotics][case]   scene/held-at (Murder.location): {scene}");
                    if (type == "kidnap")
                    {
                        bool kp = false; int vs = -1, rs = -1, ad = -1, ra = -1; bool vss = false;
                        try { kp = m.kidnapKillPhase; } catch { }
                        try { vs = m.victimSiteID; } catch { } try { vss = m.victimSiteIsStreet; } catch { }
                        try { rs = m.ransomSiteID; } catch { } try { ra = m.ransomAmount; } catch { } try { ad = m.addressID; } catch { }
                        log.LogInfo($"[Narcotics][case]   KIDNAP: kidnapKillPhase={kp}  victimSiteID={vs}(street={vss})->{LocNameById(vs)}  ransomSiteID={rs}->{LocNameById(rs)}  ransom={ra}  addressID={ad}->{LocNameById(ad)}");
                    }
                }
                log.LogInfo("[Narcotics][case] ===== END =====");
            }
            catch (Exception e) { log.LogWarning($"[Narcotics][case] reveal error: {e.Message}"); }
        }

        // "<name> #<id>  home: <address>  work: <company> @ <address>" - the NPC + both their addresses.
        private static string WhoAddr(Human h)
        {
            if (h == null) return "<none>";
            string nm = "?"; int id = -1; bool dead = false;
            try { nm = h.citizenName; } catch { } try { id = h.humanID; } catch { } try { dead = h.isDead; } catch { }
            string home = "?"; try { if (h.home != null) home = h.home.name; } catch { }
            string work = "-";
            try { var j = h.job; if (j != null) { var emp = j.employer; if (emp != null) { string cn = "?", ca = "?"; try { cn = emp.name; } catch { } try { if (emp.address != null) ca = emp.address.name; } catch { } work = $"{cn} @ {ca}"; } } } catch { }
            return $"{nm} #{id}{(dead ? " (DEAD)" : "")}  home: {home}  work: {work}";
        }

        // Best-effort: resolve a Murder site/address id to a location name via the game's directories. The kidnap site
        // ids may index the gameLocationDirectory; guard every access so a bad id just prints "?" rather than crashing.
        private static string LocNameById(int id)
        {
            try
            {
                if (id < 0) return "-";
                var cd = CityData.Instance; if (cd == null) return "?";
                try { var gld = cd.gameLocationDirectory; if (gld != null && id < gld.Count) { var gl = gld[id]; if (gl != null) return gl.name; } } catch { }
                try { var ad = cd.addressDirectory; if (ad != null && id < ad.Count) { var a = ad[id]; if (a != null) return a.name; } } catch { }
                return "?";
            }
            catch { return "?"; }
        }

        // Teleport the player to City Hall (where the case resolution form is collected + submitted). Ported from
        // SODMotives: scans the gameLocationDirectory for a "City Hall" location with a reachable node.
        internal static void TeleportToCityHall()
        {
            var log = NarcoticsPlugin.Log;
            try
            {
                var cd = CityData.Instance; var player = Player.Instance;
                if (cd == null || player == null) { log.LogInfo("[Narcotics][cityhall] no city/player."); return; }
                var dir = cd.gameLocationDirectory;
                if (dir == null) { log.LogInfo("[Narcotics][cityhall] no location directory."); return; }
                for (int i = 0; i < dir.Count; i++)
                {
                    var loc = dir[i]; if (loc == null) continue;
                    string n = null; try { n = loc.name; } catch { }
                    if (string.IsNullOrEmpty(n) || n.IndexOf("City Hall", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    NewNode node = null;
                    try { node = player.FindSafeTeleport(loc, false, true); } catch { }
                    if (node == null) { try { node = loc.anchorNode; } catch { } }
                    if (node == null) { try { var ns = loc.nodes; if (ns != null && ns.Count > 0) node = ns[0]; } catch { } }
                    if (node == null) continue;
                    player.Teleport(node, null, true, false, true);
                    log.LogInfo($"[Narcotics][cityhall] teleported to {n}.");
                    return;
                }
                log.LogInfo("[Narcotics][cityhall] found City Hall but no reachable node.");
            }
            catch (Exception e) { log.LogWarning($"[Narcotics][cityhall] teleport error: {e.Message}"); }
        }

        // Teleport the player to the active operation's lab.
        internal static void TeleportToLab()
        {
            var log = NarcoticsPlugin.Log;
            try
            {
                if (!Operation.Active || Operation.LabLoc == null) { log.LogWarning("[Narcotics]  no active lab (press Start first)"); return; }
                var player = Player.Instance; if (player == null) return;
                NewNode node = null; try { node = player.FindSafeTeleport(Operation.LabLoc, false, true); } catch { }
                if (node == null) { log.LogWarning("[Narcotics]  no safe teleport node at the lab"); return; }
                player.Teleport(node, null, true, false, true);
                log.LogInfo($"[Narcotics]  teleported to lab {Cook.LName(Operation.LabLoc)}");
                Operation.Status();
            }
            catch (Exception e) { log.LogWarning($"[Narcotics]  TeleportToLab error: {e.Message}"); }
        }

        // Teleport to the next NPC in the operation (cook -> distributor -> dealers -> back to cook), so you can
        // verify inventories/positions after watching from the apartment. Logs who + their doses + distance to
        // their current target.
        private static int _npcCycle;
        // Drop the player RIGHT ONTO an NPC (their exact world position), not merely into their location - a street
        // node is too vague to find someone. Teleports to their node first (so streaming/pathing is correct), then
        // snaps the player's transform onto the NPC. Returns false if it couldn't place them.
        // TESTING: log into the computer the player is currently using WITHOUT its password, so surveillance footage
        // can be checked quickly. Finds the player-controlled ComputerController and logs it in as the computer's owner
        // (the account that already exists on it), which is what the password gate would otherwise do. Single-player
        // debug convenience only.
        internal static void UnlockActiveComputer()
        {
            var log = NarcoticsPlugin.Log;
            try
            {
                var comps = UnityEngine.Object.FindObjectsOfType<ComputerController>();
                if (comps == null || comps.Length == 0) { log.LogWarning("[Narcotics]  no computer in the loaded scene (sit at one first)."); return; }
                int done = 0;
                for (int i = 0; i < comps.Length; i++)
                {
                    var cc = comps[i]; if (cc == null) continue;
                    bool pc = false; try { pc = cc.playerControlled; } catch { }
                    if (!pc) continue;   // only the computer the player is actually using
                    Human owner = null;
                    try { owner = cc.ic != null ? cc.ic.belongsTo : null; } catch { }
                    if (owner == null) { try { owner = Player.Instance; } catch { } }   // fallback: log in as the player
                    if (owner == null) { log.LogWarning("[Narcotics]  computer has no owner account to log in as."); continue; }
                    try { cc.SetLoggedIn(owner); done++; log.LogInfo($"[Narcotics]  logged into the computer as {NarcoticsPlugin.Name(owner)} - password bypassed. Open the Surveillance app."); }
                    catch (Exception e) { log.LogWarning($"[Narcotics]  SetLoggedIn failed: {e.Message}"); }
                }
                if (done == 0) log.LogWarning("[Narcotics]  you aren't using a computer right now - sit at one (the login screen), then press the key.");
            }
            catch (Exception e) { log.LogWarning($"[Narcotics]  UnlockActiveComputer error: {e.Message}"); }
        }

        // TESTING: dump every active case's resolve-form questions (type + each question's fields) so we can see EXACTLY
        // how vanilla wires the "arrest the suspect" checkmark (its reference to the naming question) and the location
        // answer. Run a VANILLA murder (set HijackChance=0 for a run) then press the key while that case is on the board.
        internal static void DumpResolveQuestions()
        {
            var log = NarcoticsPlugin.Log;
            try
            {
                var cpc = CasePanelController.Instance; var cases = cpc != null ? cpc.activeCases : null;
                if (cases == null || cases.Count == 0) { log.LogWarning("[Narcotics]  no active cases to dump"); return; }
                for (int i = 0; i < cases.Count; i++)
                {
                    var c = cases[i]; if (c == null) continue;
                    string ct = "?"; try { ct = c.caseType.ToString(); } catch { }
                    string cn = "?"; try { cn = c.name; } catch { }
                    var qs = c.resolveQuestions;
                    log.LogInfo($"[Narcotics][dump] CASE '{cn}' type={ct} questions={(qs != null ? qs.Count : 0)}");
                    if (qs == null) continue;
                    for (int q = 0; q < qs.Count; q++)
                    {
                        var rq = qs[q]; if (rq == null) continue;
                        string name = "?", it = "?", corr = "", inp = "", inpEv = "";
                        try { name = rq.name; } catch { }
                        try { it = rq.inputType.ToString(); } catch { }
                        try { if (rq.correctAnswers != null) { for (int a = 0; a < rq.correctAnswers.Count; a++) corr += (a > 0 ? "|" : "") + rq.correctAnswers[a]; } } catch { }
                        try { inp = rq.input; } catch { }
                        try { inpEv = rq.inputtedEvidence; } catch { }
                        bool opt = false; try { opt = rq.isOptional; } catch { }
                        log.LogInfo($"[Narcotics][dump]   q{q}: input={it} opt={opt} name='{name}' correct='{corr}' input='{inp}' inputEv='{inpEv}'");
                    }
                }
            }
            catch (Exception e) { log.LogWarning($"[Narcotics]  DumpResolveQuestions error: {e.Message}"); }
        }

        internal static bool TeleportOnto(Human target)
        {
            try
            {
                var player = Player.Instance;
                if (player == null || target == null) return false;
                NewNode node = null; try { node = target.currentNode; } catch { }
                if (node == null) { try { node = player.FindSafeTeleport(target.currentGameLocation, false, true); } catch { } }
                if (node == null) return false;
                // Let the game's own Teleport GROUND the player on the node's floor. We do NOT copy a position onto the
                // transform afterwards: copying the NPC's transform put the player up on a chair (sitting fall), and
                // copying node.position sank the player through the floor while the NPC was walking (its currentNode is
                // briefly a threshold/stair node whose stored position sits below the floor collider). Teleport(node)
                // handles both postures correctly on its own.
                player.Teleport(node, null, true, false, true);
                return true;
            }
            catch { return false; }
        }

        internal static void TeleportToNextNPC()
        {
            var log = NarcoticsPlugin.Log;
            try
            {
                if (!Operation.Active) { log.LogWarning("[Narcotics]  no active operation (press Start first)"); return; }
                var npcs = Operation.InvolvedNPCs();
                if (npcs == null || npcs.Count == 0) { log.LogWarning("[Narcotics]  no operation NPCs to teleport to"); return; }
                if (_npcCycle >= npcs.Count) _npcCycle = 0;
                var h = npcs[_npcCycle];
                _npcCycle = (_npcCycle + 1) % npcs.Count;
                if (h == null) return;
                if (!TeleportOnto(h)) { log.LogWarning($"[Narcotics]  could not teleport to {NarcoticsPlugin.Name(h)}"); return; }
                log.LogInfo($"[Narcotics]  teleported onto {Operation.RoleOf(h)} {NarcoticsPlugin.Name(h)}  doses x{Dose.CountIn(h)}");
                Inspect(h, "teleport");
            }
            catch (Exception e) { log.LogWarning($"[Narcotics]  TeleportToNextNPC error: {e.Message}"); }
        }

        // Teleport to the next dead drop (cycles through the dealers' drops), so the player can find + search them.
        // Logs the container name and how many doses are in it.
        private static int _dropCycle;
        internal static void TeleportToNextDrop()
        {
            var log = NarcoticsPlugin.Log;
            try
            {
                if (!Operation.Active) { log.LogWarning("[Narcotics]  no active operation (press Start first)"); return; }
                var drops = new System.Collections.Generic.List<Operation.Dealer>();
                var dealers = Operation.Dealers;
                for (int i = 0; i < dealers.Count; i++) { var dl = dealers[i]; if (dl != null && dl.Drop != null) drops.Add(dl); }
                if (drops.Count == 0) { log.LogWarning("[Narcotics]  no dead drops sited yet (they are placed when the distributor first runs)"); return; }
                if (_dropCycle >= drops.Count) _dropCycle = 0;
                var d = drops[_dropCycle];
                _dropCycle = (_dropCycle + 1) % drops.Count;
                var player = Player.Instance;
                if (player == null) return;
                player.Teleport(d.Drop, null, true, false, true);
                int stash = Operation.DropCount(d);
                string cont = "loose spot"; try { if (d.Container != null && d.Container.furniture != null) cont = d.Container.furniture.name; } catch { }
                log.LogInfo($"[Narcotics]  teleported to {NarcoticsPlugin.Name(d.H)}'s dead drop ('{cont}') - holds x{stash} dose(s). Search it.");
            }
            catch (Exception e) { log.LogWarning($"[Narcotics]  TeleportToNextDrop error: {e.Message}"); }
        }


        // Dress the active lab with furniture (stand in the lab first for reliable placement).
        internal static void DressLabNow()
        {
            var log = NarcoticsPlugin.Log;
            if (!Operation.Active || Operation.LabLoc == null) { log.LogWarning("[Narcotics]  no active lab to dress"); return; }
            int n = LabSiting.DressLab(Operation.LabLoc).Count;
            log.LogInfo($"[Narcotics]  DressLab placed {n} cluster(s) (stand in the lab if 0).");
        }

        // TESTING: spawn one set of lab-equipment clusters into the room the PLAYER is standing in right now (e.g. your
        // apartment), to check whether furniture-cluster placement works at all in a normal furnished room. If it works
        // here but not in the vacant lab shell, the shell's missing furniture-placement geometry is the problem.
        internal static void TestSpawnFurnitureHere()
        {
            var log = NarcoticsPlugin.Log;
            try
            {
                var p = Player.Instance; if (p == null) { log.LogWarning("[Narcotics]  no player"); return; }
                NewNode n = null; try { n = p.currentNode; } catch { }
                NewRoom room = null; try { room = n != null ? n.room : null; } catch { }
                if (room == null) { log.LogWarning("[Narcotics]  no current room - stand inside a room and retry"); return; }
                int placed = LabSiting.DressRoom(room, true).Count;
                log.LogInfo($"[Narcotics]  TEST furniture spawn: placed {placed} cluster(s) in the room you're standing in. Look around (see [lab] log lines for per-cluster detail).");
            }
            catch (Exception e) { log.LogWarning($"[Narcotics]  TestSpawnFurnitureHere error: {e.Message}"); }
        }

        // Dump a citizen's identity + operation role + drug inventory to the log panel (fired on talk).
        internal static void Inspect(Human h, string via)
        {
            try
            {
                if (h == null) return;
                int id = -1; try { id = h.humanID; } catch { }
                bool homeless = false; try { homeless = h.isHomeless; } catch { }
                NarcoticsPlugin.Log.LogInfo($"[Narcotics][inspect:{via}] {NarcoticsPlugin.Name(h)} id={id} homeless={homeless} role={Operation.RoleOf(h)} drugDoses={Dose.CountIn(h)} consumables=[{ConsumablesSummary(h)}] sightings=[{SightingsSummary(h)}]");
            }
            catch (Exception e) { NarcoticsPlugin.Log.LogWarning($"[Narcotics]  inspect error: {e.Message}"); }
        }

        // Who this citizen remembers seeing (their vanilla lastSightings), so we can verify a buyer "saw" the dealer
        // and that the interrogation trail has the data it needs.
        private static string SightingsSummary(Human h)
        {
            try
            {
                var ls = h.lastSightings;
                if (ls == null) return "none";
                int count = 0; try { count = ls.Count; } catch { }
                if (count == 0) return "0";
                // Only call out operation members (cook/distributor/dealers) and dump their sighting flags, so we
                // can see whether the arrested buyer actually "saw" the dealer and whether it reads as notable.
                var hits = new System.Collections.Generic.List<string>();
                try
                {
                    foreach (var key in ls.Keys)
                    {
                        if (key == null) continue;
                        string role = Operation.RoleOf(key);
                        if (role == "-") continue;
                        string flags = "?";
                        try { var s = ls[key]; if (s != null) { float now = 0f; try { now = SessionData.Instance.gameTime; } catch { } flags = $"poi={s.poi} run={s.run} mov={s.mov} ageH={(now - s.time):0.00}"; } } catch { }
                        hits.Add($"{role} {NarcoticsPlugin.Name(key)}[{flags}]");
                    }
                }
                catch { }
                return hits.Count == 0 ? $"{count} total, 0 op-members" : $"{count} total | " + string.Join(" | ", hits);
            }
            catch (Exception e) { return "err:" + e.Message; }
        }

        private static string ConsumablesSummary(Human h)
        {
            try
            {
                var list = h.currentConsumables;
                if (list == null || list.Count == 0) return "";
                var parts = new System.Collections.Generic.List<string>();
                int max = Math.Min(list.Count, 8);
                for (int i = 0; i < max; i++) { try { var p = list[i]; if (p != null) parts.Add(SafePresetName(p)); } catch { } }
                if (list.Count > max) parts.Add($"+{list.Count - max}");
                return string.Join(", ", parts);
            }
            catch { return "?"; }
        }

        private static string SafePresetName(InteractablePreset p)
        {
            try { if (!string.IsNullOrEmpty(p.presetName)) return p.presetName; } catch { }
            try { return p.name; } catch { return "?"; }
        }

        static string SafeItemName(Interactable it) { try { return it.GetName(); } catch { return "?"; } }
        static string SafeType(Case c) { try { return c.caseType.ToString(); } catch { return "?"; } }
        static string SafeStatus(Case c) { try { return c.caseStatus.ToString(); } catch { return "?"; } }
    }

    // Injected MonoBehaviour: polls the debug hotkeys.
    public class NarcoticsHotkey : MonoBehaviour
    {
        public NarcoticsHotkey(IntPtr ptr) : base(ptr) { }

        void Update()
        {
            try
            {
                InvestigationPersistence.Tick(); // reattach saved case/operation BEFORE any auto-start
                Operation.TickAutoStart();   // proactive auto-start when HijackChance=1.0 (doesn't wait for a vanilla case)
                Operation.TickPendingStrip();   // finish a deferred lab-furniture strip once the old lab is back in view; cheap no-op otherwise
                Operation.Tick();   // real operation logic (cook pin re-assert); cheap no-op when inactive
                Arrest.Tick();      // drives a staged arrest test; cheap no-op when inactive
                NarcCase.TickVerdict();   // after submit + the processing delay, run the one Case.Resolve() (the verdict)
                Diagnostics.TickBannerRestore();   // restore vanilla banner text after our override window
                if (!NarcoticsConfig.EnableDebugKeys) return;
                if (Input.GetKeyDown(NarcoticsConfig.KeyCityHall)) Diagnostics.TeleportToCityHall();
                if (Input.GetKeyDown(NarcoticsConfig.KeyOverdose)) Arrest.Overdose();
                if (Input.GetKeyDown(NarcoticsConfig.KeyArrestTeleport)) Arrest.TeleportToPair();
                if (Input.GetKeyDown(NarcoticsConfig.KeyStartOp)) Operation.Start();
                if (Input.GetKeyDown(NarcoticsConfig.KeyStopOp)) Operation.Stop();
                if (Input.GetKeyDown(NarcoticsConfig.KeyTeleportLab)) Diagnostics.TeleportToLab();
                if (Input.GetKeyDown(NarcoticsConfig.KeyDressLab)) Diagnostics.DressLabNow();
                if (Input.GetKeyDown(NarcoticsConfig.KeyLogPanel)) LogPanel.Show = !LogPanel.Show;
                if (Input.GetKeyDown(NarcoticsConfig.KeyForceSale)) Operation.ForceSaleNow();
                if (Input.GetKeyDown(NarcoticsConfig.KeyGodMode)) GodMode.Toggle();
                if (GodMode.On) GodMode.Apply();
                if (Input.GetKeyDown(NarcoticsConfig.KeyHandIn)) NarcCase.DebugHandInStep();
                if (Input.GetKeyDown(NarcoticsConfig.KeyRevealMurder)) Diagnostics.RevealMurder();
                if (Input.GetKeyDown(NarcoticsConfig.KeyTimeBoost)) FastForward.Toggle();
                FastForward.Apply();
                if (Input.GetKeyDown(NarcoticsConfig.KeyTeleportNPC)) Diagnostics.TeleportToNextNPC();
                if (Input.GetKeyDown(NarcoticsConfig.KeyDropTeleport)) Diagnostics.TeleportToNextDrop();
                if (Input.GetKeyDown(NarcoticsConfig.KeyUnlockComputer)) Diagnostics.UnlockActiveComputer();
                if (Input.GetKeyDown(NarcoticsConfig.KeySpawnFurniture)) Diagnostics.TestSpawnFurnitureHere();
                if (Input.GetKeyDown(NarcoticsConfig.KeyDumpResolve)) Diagnostics.DumpResolveQuestions();
            }
            catch (Exception e) { NarcoticsPlugin.Log.LogWarning($"[Narcotics] hotkey update error: {e.Message}"); }
        }

        private static GUIStyle _panelStyle;

        // Mimics the Motives mod's F9 overlay: a bare GUIStyle (not derived from GUI.skin.label, which caused the
        // cramped/cut lines), fontSize 14, lineH 20, a dark translucent box behind it.
        void OnGUI()
        {
            try
            {
                if (!NarcoticsConfig.EnableDebugKeys) return;
                if (_panelStyle == null) _panelStyle = new GUIStyle { fontSize = 14, wordWrap = false, richText = false };
                _panelStyle.normal.textColor = Color.white;

                // Left: scrolling debug log.
                if (LogPanel.Show)
                {
                    var lines = LogPanel.Lines;
                    int count = lines.Count;
                    float lineH = 20f, w = 940f;
                    float h = 40f + lineH * (count + 1);
                    GUI.color = new Color(0f, 0f, 0f, 0.8f);
                    GUI.Box(new Rect(8f, 8f, w, h), GUIContent.none);
                    GUI.color = Color.white;
                    GUI.Label(new Rect(18f, 14f, w - 20f, lineH), "NARCOTICS debug log (F9 to hide)", _panelStyle);
                    for (int i = 0; i < count; i++)
                        GUI.Label(new Rect(18f, 14f + lineH * (i + 1.4f), w - 20f, lineH), lines[i], _panelStyle);
                }

                // Right: live operation roster (cook + every tier + their physical dose counts). Toggles WITH the F9 log
                // panel (LogPanel.Show) so both appear/hide together.
                if (LogPanel.Show && Operation.Active)
                {
                    var rl = Operation.RosterLines();
                    float rlineH = 20f, rw = 460f, rx = UnityEngine.Screen.width - rw - 8f;
                    float rh = 34f + rl.Count * rlineH;
                    GUI.color = new Color(0f, 0f, 0f, 0.8f);
                    GUI.Box(new Rect(rx, 8f, rw, rh), GUIContent.none);
                    GUI.color = Color.white;
                    GUI.Label(new Rect(rx + 10f, 14f, rw - 14f, rlineH), "NARCOTICS operation", _panelStyle);
                    for (int i = 0; i < rl.Count; i++)
                        GUI.Label(new Rect(rx + 10f, 14f + rlineH * (i + 1f), rw - 14f, rlineH), rl[i], _panelStyle);
                }
            }
            catch { }
        }
    }

    // Substitutes the big-banner's fixed text (Strings.Get("ui.gamemessage","New Murder Case"/"New Kidnapping
    // Case")) with our own, but ONLY while our banner is firing (BannerOverride != null). When it is null every
    // vanilla lookup passes through untouched, so real murder banners are unaffected.
    [HarmonyLib.HarmonyPatch(typeof(Strings), nameof(Strings.Get))]
    internal static class Patch_Strings_Get
    {
        internal static string BannerOverride;

        static void Postfix(string dictionary, string key, ref string __result)
        {
            try
            {
                // Banner headline swap, ONLY while our banner is firing (armed + cleared synchronously in Announce).
                if (BannerOverride == null) return;
                if (dictionary != "ui.gamemessage") return;
                if (key == "New Murder Case" || key == "New Kidnapping Case")
                {
                    NarcoticsPlugin.Log.LogInfo($"[Narcotics][banner-get] substituting '{key}' -> '{BannerOverride}'");
                    __result = BannerOverride;
                    BannerOverride = null;   // one-shot: only OUR banner's read; a real murder banner later is untouched
                }
            }
            catch { }
        }
    }

    // Diagnostic: the objective text runs entryRef through Strings.ComposeText (not Strings.Get). Log how our
    // "Investigate ..." text is composed, so we can confirm the path + whether a plain sentence passes through.
    [HarmonyLib.HarmonyPatch(typeof(Strings), nameof(Strings.ComposeText))]
    internal static class Patch_ComposeText
    {
        static void Postfix(string input, ref string __result)
        {
            try
            {
                if (string.IsNullOrEmpty(input)) return;
                if (input.Contains("Crime Scene") || input.Contains("crime scene") || input.Contains("Hand In")
                    || input.Contains("hand in") || input.Contains("Explore") || input.Contains("Collect a case"))
                    NarcoticsPlugin.Log.LogInfo($"[Narcotics][compose] in='{input}' out='{__result}'");
            }
            catch { }
        }
    }

    // Drug item NAME safety net. With the clone's retail link nulled (DrugItem.CreateDrug), the dose's name resolves
    // through evidence.names[presetName] (what we write = the case's drug name), cached per-dose and consistent between
    // the hover tooltip and the examine panel. This postfix only steps in if that resolution comes back BROKEN - empty,
    // the generic "retail item", or the raw preset key - and fills the current operation's drug name. A good per-dose
    // name is left untouched, so a dose keeps ITS case's drug name (no live-override mismatch across cases).
    [HarmonyLib.HarmonyPatch(typeof(Interactable), nameof(Interactable.GetName))]
    internal static class Patch_DrugItemName
    {
        static void Postfix(Interactable __instance, ref string __result)
        {
            try
            {
                if (__instance == null) return;
                InteractablePreset p = null; try { p = __instance.preset; } catch { }
                if (p == null || !DrugItem.IsOurDrugPreset(p)) return;   // one of OUR drug clones?
                string nm = DrugItem.NameFor(p);   // that clone's own drug name
                if (!string.IsNullOrEmpty(nm) && __result != nm) __result = nm;   // force it on every GetName surface (hover tooltip / world name)
            }
            catch { }
        }
    }

    // Drug dose DESCRIPTION override. The examine card's writeup (the blurb under the item photo, on the SUMMARY view)
    // is the evidence SUMMARY. For a retail-item base (beta blockers etc.) the dose's evidence is EvidenceRetailItem,
    // which OVERRIDES GetSummary; a non-retail base uses the base Evidence.GetSummary. We postfix BOTH and substitute our
    // text ("A mysterious new substance") for OUR dose only - scoped by the evidence's interactablePreset matching the
    // current drug clone - so real beta blockers (same shared EvidencePreset) are untouched. This replaced the earlier
    // broken routes: Interactable.SetDDSOverride turned the dose into a readable DOCUMENT (big crumpled page, "Retail
    // Item" title), and cloning/repointing the EvidencePreset.ddsDocumentID blanked the whole card.
    internal static class DrugDesc
    {
        internal static bool IsOurDose(Evidence ev)
        {
            try { InteractablePreset p = ev != null ? ev.interactablePreset : null; return DrugItem.IsOurDrugPreset(p); }
            catch { return false; }
        }

        // The display name for THIS dose's drug (each of our presets carries its own), so several different drugs can be
        // on screen at once (the test-spawn key) and each shows its own name.
        internal static string NameFor(Evidence ev)
        {
            try { InteractablePreset p = ev != null ? ev.interactablePreset : null; return DrugItem.NameFor(p); }
            catch { return null; }
        }
    }

    // Drug dose NAME on the examine-window TITLE. For a retail base the window title is the EVIDENCE name produced by
    // EvidenceRetailItem.GenerateName (the generic "Retail Item" discovery name) - NOT Interactable.GetName - so the
    // GetName patch (which fixes the hover tooltip) never reaches the window title. Override GenerateName for OUR dose to
    // the case's drug name; base Evidence.GenerateName covers any non-retail future base. Scoped by interactablePreset so
    // real retail items are untouched.
    [HarmonyLib.HarmonyPatch(typeof(EvidenceRetailItem), nameof(EvidenceRetailItem.GenerateName))]
    internal static class Patch_DrugItemEvName
    {
        static void Postfix(EvidenceRetailItem __instance, ref string __result)
        {
            try { var nm = DrugDesc.NameFor(__instance); if (!string.IsNullOrEmpty(nm)) __result = nm; } catch { }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(Evidence), nameof(Evidence.GenerateName))]
    internal static class Patch_DrugItemEvName_Base
    {
        static void Postfix(Evidence __instance, ref string __result)
        {
            try { var nm = DrugDesc.NameFor(__instance); if (!string.IsNullOrEmpty(nm)) __result = nm; } catch { }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(EvidenceRetailItem), nameof(EvidenceRetailItem.GetSummary))]
    internal static class Patch_DrugItemDescription
    {
        static void Postfix(EvidenceRetailItem __instance, ref string __result)
        {
            try { if (DrugDesc.IsOurDose(__instance)) __result = DrugItem.DrugDescription; } catch { }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(Evidence), nameof(Evidence.GetSummary))]
    internal static class Patch_DrugItemDescription_Base
    {
        static void Postfix(Evidence __instance, ref string __result)
        {
            try { if (DrugDesc.IsOurDose(__instance)) __result = DrugItem.DrugDescription; } catch { }
        }
    }

    // Talk-to-inspect: when the player opens a dialog with a citizen, dump their identity + role + drug inventory
    // to the log panel, so drug transfers can be verified by talking to NPCs. Behind EnableDebugKeys.
    [HarmonyLib.HarmonyPatch(typeof(InteractionController), nameof(InteractionController.SetDialog))]
    internal static class Patch_InspectOnTalk
    {
        static void Postfix(bool val, Interactable newTalkingTo)
        {
            try
            {
                if (!NarcoticsConfig.EnableDebugKeys || !val || newTalkingTo == null) return;
                var actor = newTalkingTo.isActor;
                if (actor == null) return;
                var h = actor.TryCast<Human>();
                if (h != null) Diagnostics.Inspect(h, "talk");
            }
            catch { }
        }
    }

    // Detect when the player searches (frisks) the current arrestee, so the arrest holds them until then.
    [HarmonyLib.HarmonyPatch(typeof(Player), nameof(Player.OnSearch))]
    internal static class Patch_OnSearch
    {
        static void Postfix(Interactable newSearchItem)
        {
            try
            {
                if (newSearchItem == null) return;
                var actor = newSearchItem.isActor;
                if (actor == null) return;
                var h = actor.TryCast<Human>();
                if (h == null) return;
                Arrest.NotifySearched(h);   // matches the searched human against the active + all posted arrestees
                // Frisking an arrested buyer clears THAT buyer's search lead (keyed by humanID, so each overdose's lead
                // is independent - searching one never clears another's). Works even after the hold ends while the case is open.
                try { if (NarcCase.IsOpen && NarcCase.HasLead(h.humanID)) NarcCase.ClearLeadFor(h.humanID, "search"); } catch { }
            }
            catch { }
        }
    }

    // Reset mod state whenever a game starts or loads (fires on both). Fixes the "F5 fails after exit + reload"
    // bug: without this, static state (Active flag, cached drug presets) from the previous session lingers.
    [HarmonyLib.HarmonyPatch(typeof(MurderController), nameof(MurderController.OnStartGame))]
    internal static class Patch_OnStartGame_Reset
    {
        static void Postfix()
        {
            try
            {
                Operation.ResetOnLoad();
                Arrest.ResetOnLoad();
                NarcCase.ResetOnLoad();
                DrugItem.ResetOnLoad();
                Dose.ResetOnLoad();
                Herd.ReleaseAll();
                // The prefix restores presets before evidence loading; after ResetOnLoad, rebind their names.
                // This restores item identity only, not the active cook/dealer/operation state.
                DrugPersistence.RestorePresets();
                InvestigationPersistence.AfterGameStarted();
                NarcoticsPlugin.Log.LogInfo("[Narcotics] game started/loaded - operation state reset; saved investigations will be reattached before another operation starts.");
            }
            catch { }
        }
    }

    // CaseType.custom never resolves objective text: the Objective ctor only runs the missions.postings text lookup
    // for mainStory/murder/sideJob/retirement case types, so our objectives keep the default "New Objective" name.
    // We pass each objective's FULLY-RESOLVED text as its entryRef and copy it onto the name HERE - Objective.Setup
    // runs at the END of the (async) Objective ctor, when the object is fully built and already in its list. Hard
    // pointer-guard to OUR case so vanilla murder/side-job objectives are never renamed; wrapped so a bad frame
    // never throws into the game's own Setup.
    [HarmonyLib.HarmonyPatch(typeof(Objective), nameof(Objective.Setup))]
    internal static class Patch_ObjectiveSetup
    {
        static void Postfix(Objective __instance)
        {
            try
            {
                var cur = NarcCase.Current; if (cur == null || __instance == null) return;
                Case tc = null; try { tc = __instance.thisCase; } catch { }
                if (tc == null) return;
                try { if (tc.Pointer != cur.Pointer) return; } catch { return; }
                var qe = __instance.queueElement; if (qe == null) return;
                string entry = null; try { entry = qe.entryRef; } catch { }
                string nm = null; try { nm = __instance.name; } catch { }
                if (string.IsNullOrEmpty(entry))
                {
                    // Some engine objectives (the custom case's "case processing" one) carry NO entryRef, so they stay
                    // "New Objective". Label the blank objective by case status: once submitted, it is "Case processing...".
                    try
                    {
                        bool ph = string.IsNullOrEmpty(nm) || nm.Equals("New Objective", StringComparison.OrdinalIgnoreCase);
                        var st = cur.caseStatus;
                        if (ph && (st == Case.CaseStatus.submitted || st == Case.CaseStatus.closable))
                        {
                            string proc = null; try { proc = Strings.Get("missions.postings", "case processing"); } catch { }
                            if (string.IsNullOrEmpty(proc) || proc.Equals("New Objective", StringComparison.OrdinalIgnoreCase)) proc = "Case processing...";
                            try { __instance.name = proc; } catch { }
                            try { CasePanelController.Instance.UpdateCaseControls(); } catch { }
                        }
                    }
                    catch { }
                    return;
                }
                // Our objectives' entryRef is EITHER our own free text (search lead, collect) OR a real missions.postings
                // KEY the engine auto-added (e.g. "submit case" -> "Submit resolution form to City Hall", "case
                // processing" -> "Case processing..."). Compute the right display text: if entryRef resolves as a key,
                // use the resolved value; else it is free text, use it verbatim.
                string resolved = null; try { resolved = Strings.Get("missions.postings", entry); } catch { }
                // CRITICAL: Strings.Get returns the "New Objective" PLACEHOLDER for a missing key, so a free-text
                // entryRef (our questions / leads) looks "resolved" to that placeholder. Treat a resolution as real
                // only if it is non-empty, different from the key, AND not that placeholder - otherwise our free-text
                // objectives get clobbered back to "New Objective" (the regression).
                bool resolvedReal = !string.IsNullOrEmpty(resolved) && resolved != entry
                                    && !resolved.Equals("New Objective", StringComparison.OrdinalIgnoreCase);
                string finalText = resolvedReal ? resolved : entry;
                // Only act when the current name is WRONG (placeholder, empty, or the raw un-resolved entryRef); never
                // clobber a name the ctor already resolved correctly.
                bool nameWrong = string.IsNullOrEmpty(nm) || nm.Equals("New Objective", StringComparison.OrdinalIgnoreCase) || nm == entry;
                if (!nameWrong || nm == finalText) return;
                try { __instance.name = finalText; } catch { }
                try { CasePanelController.Instance.UpdateCaseControls(); } catch { }
            }
            catch { }
        }
    }

    // The City Hall tray's two case actions are caseType-gated to murder/mainStory/retirement, so a custom case can
    // neither COLLECT (ActionController.GetCaseForm: status 0->1) nor SUBMIT (ActionController.HandInCase: status
    // 1->2). We admit OUR custom case to both. GetCaseForm -> SetStatus(handInCollected) renders the fillable form
    // (the renderer is gated on STATUS, not caseType). HandInCase -> SetStatus(submitted) scores + closes it; we do
    // NOT call Case.Resolve() (its murder branch derefs a murder we don't have, and submit never uses it - the tray
    // submit is just SetStatus(submitted)). Both return true (run vanilla) for every non-custom case so real cases
    // are untouched.
    [HarmonyLib.HarmonyPatch(typeof(ActionController), nameof(ActionController.GetCaseForm))]
    internal static class Patch_GetCaseForm
    {
        static bool Prefix(Interactable what, NewNode where, Actor who)
        {
            try
            {
                var cpc = CasePanelController.Instance;
                var ac = cpc != null ? cpc.activeCase : null;
                if (ac == null) return true;
                try { if (ac.caseType != Case.CaseType.custom) return true; } catch { return true; }
                if (ac.caseStatus == Case.CaseStatus.handInNotCollected)
                {
                    try { NarcCase.OnFormCollected(); } catch { }   // scanner blip + typewriter header FIRST
                    try { ac.SetStatus(Case.CaseStatus.handInCollected, false); } catch { }   // then the per-question objective banners
                    NarcoticsPlugin.Log.LogInfo("[Narcotics][case] tray: collected the resolution form (fill it in, then hand in to submit).");
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(ActionController), nameof(ActionController.HandInCase))]
    internal static class Patch_HandInCase
    {
        static bool Prefix(Interactable what, NewNode where, Actor who)
        {
            try
            {
                var cpc = CasePanelController.Instance;
                var ac = cpc != null ? cpc.activeCase : null;
                if (ac == null) return true;
                try { if (ac.caseType != Case.CaseType.custom) return true; } catch { return true; }
                if (ac.caseStatus == Case.CaseStatus.handInCollected)
                {
                    try { ac.SetStatus(Case.CaseStatus.submitted, true); } catch { }
                    try { NarcCase.OnFormSubmitted(); } catch { }   // "Case submitted for processing" scanner blip
                    NarcoticsPlugin.Log.LogInfo("[Narcotics][case] tray: submitted the resolution form.");
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    // Case.SetStatus runs murder-coupled calls (MurderController.UpdateCorrectResolveAnswers) on the hand-in
    // transitions REGARDLESS of caseType, so our custom case's collect (handInNotCollected->handInCollected) and
    // submit (->submitted) still invoke it with no backing MurderController.Murder -> NPE. Swallow it: a Finalizer
    // returning null only takes effect WHEN there is an exception, so real murders (which never throw here) are
    // unaffected; only our synthetic case's NPE is absorbed.
    [HarmonyLib.HarmonyPatch(typeof(MurderController), nameof(MurderController.UpdateCorrectResolveAnswers))]
    internal static class Patch_UpdateCorrectResolveAnswers
    {
        static Exception Finalizer(Exception __exception)
        {
            if (__exception != null) NarcoticsPlugin.Log.LogInfo("[Narcotics][case] absorbed UpdateCorrectResolveAnswers NPE (synthetic case, no backing murder).");
            return null;
        }
    }

    // Decode (2026-10-04): NO engine code resolves a custom case (zero symbol-callers of Case.Resolve; SetStatus and
    // the case-processing objective schedule none), so the mod drives the ONE Resolve itself (TickVerdict, flagged
    // _resolvingSelf). The reported double reveal+banner is therefore a SECOND Resolve() landing on the SAME case:
    //   (a) while it is our active case but not our call -> block by Current.Pointer + !ResolvingSelf;
    //   (b) AFTER it solved and tear-down nulled Current -> block by the ResolvedPtrs latch (survives Current==null,
    //       which is exactly where the old Current-only guard let the second banner through).
    // A FAILED case is never latched (it reopens for a retry and must stay re-resolvable). The Finalizer absorbs any
    // residual NPE for our case (real cases' exceptions propagate).
    [HarmonyLib.HarmonyPatch(typeof(Case), nameof(Case.Resolve))]
    internal static class Patch_CaseResolve
    {
        static bool Prefix(Case __instance)
        {
            try
            {
                if (__instance == null) return true;
                var ptr = __instance.Pointer;
                if (NarcCase.ResolvedPtrs.Contains(ptr))   // (b) already solved -> never replay the sequence
                {
                    NarcoticsPlugin.Log.LogInfo("[Narcotics][case] swallowed a re-Resolve of an already-solved case (double-banner guard).");
                    // Debug: capture WHO fired the second Resolve, so we can root-cause (band-aid -> known cause).
                    if (NarcoticsConfig.EnableDebugKeys)
                    {
                        try { NarcoticsPlugin.Log.LogInfo("[Narcotics][case][resolve-caller]\n" + new System.Diagnostics.StackTrace(1, false)); } catch { }
                    }
                    return false;
                }
                var cur = NarcCase.Current;
                if (cur != null && ptr == cur.Pointer && !NarcCase.ResolvingSelf)   // (a) our case, not our call
                {
                    NarcoticsPlugin.Log.LogInfo("[Narcotics][case] swallowed a non-mod Case.Resolve (we drive the verdict ourselves).");
                    return false;
                }
            }
            catch { }
            return true;
        }
        static Exception Finalizer(Case __instance, Exception __exception)
        {
            try
            {
                if (__exception == null || __instance == null) return __exception;
                var ptr = __instance.Pointer;
                var cur = NarcCase.Current;
                if (NarcCase.ResolvedPtrs.Contains(ptr) || (cur != null && ptr == cur.Pointer))
                {
                    NarcoticsPlugin.Log.LogInfo("[Narcotics][case] absorbed Case.Resolve NPE (synthetic case, no backing murder).");
                    return null;   // swallow for our case
                }
            }
            catch { }
            return __exception;   // let real cases' exceptions propagate
        }
    }

    // ===== Resolution-form repair (murder-flagged case with NO backing MurderController.Murder) =====================
    // The native murder resolution form renders our case, but with no backing Murder the murder-validation path NPEs
    // partway through ResolveController.UpdateResolveFields, ABORTING before the layout pass (SetPageSize). That left
    // the window showing its design-time placeholders ("[Validate Text]", oversized text, the "ABC" tab) and a blank
    // question row. These patches, all SCOPED to our active case (NarcCase.IsActiveOnPanel), repair it:
    //   - Patch_InputFieldSetup forces the row label to our literal text (GetText treats ResolveQuestion.name as a
    //     missions.postings KEY, so free text renders blank);
    //   - the Finalizers swallow the murder-data NPE at the CALLEE level (ValidationCheck / ValidationUpdate / the
    //     murder-question display) so UpdateResolveFields runs through to SetPageSize and the window lays out +
    //     validates natively. They also LOG the exception, so any residual breakage names its exact source next test.
    // Finalizers only act on an actual exception, so real murders (which never throw here) are unaffected.

    [HarmonyLib.HarmonyPatch(typeof(InputFieldController), nameof(InputFieldController.Setup))]
    internal static class Patch_InputFieldSetup
    {
        static void Postfix(InputFieldController __instance, Case.ResolveQuestion newQuestion, Case newCase)
        {
            try
            {
                var cur = NarcCase.Current; if (cur == null || newCase == null || newQuestion == null) return;
                if (newCase.Pointer != cur.Pointer) return;
                string lbl = null; try { lbl = newQuestion.name; } catch { }
                if (string.IsNullOrEmpty(lbl)) return;
                try { __instance.titleText.text = lbl; } catch { }   // our literal question, not a blank key lookup
            }
            catch { }
        }
        static Exception Finalizer(Exception __exception)
        {
            if (__exception != null && NarcCase.IsActiveOnPanel()) { NarcoticsPlugin.Log.LogInfo($"[Narcotics][case] absorbed InputFieldController.Setup: {__exception.GetType().Name}: {__exception.Message}"); return null; }
            return __exception;
        }
    }

    // The engine re-evaluates a resolve question (UpdateCorrect) whenever the player SELECTS/changes an answer. We ride
    // that event to re-grade our arrest + location rows (the engine's own murder-linked checks don't fire for a custom
    // case), so the arrest checkmark ticks the moment the player names the cook (and the cook is in custody) - exactly
    // when vanilla's does - without us polling every frame.
    [HarmonyLib.HarmonyPatch(typeof(Case.ResolveQuestion), nameof(Case.ResolveQuestion.UpdateCorrect))]
    internal static class Patch_ResolveUpdateCorrect
    {
        static void Postfix(Case forCase)
        {
            try
            {
                var cur = NarcCase.Current;
                if (cur != null && forCase != null && forCase.Pointer == cur.Pointer) NarcCase.GradeSpecialQuestions(false);
            }
            catch { }
        }
    }

    // Case.ResolveQuestion.GetText treats the question's `name` as a missions.postings localization KEY and returns
    // blank for our free-text questions - that is why the post-processing results screen showed the first question
    // blank and the second as just "(Optional)". Return our literal name (plus the "(Optional)" suffix the engine
    // adds for optional questions) for OUR case's questions, which fixes every GetText surface at the source.
    [HarmonyLib.HarmonyPatch(typeof(Case.ResolveQuestion), nameof(Case.ResolveQuestion.GetText))]
    internal static class Patch_ResolveQuestionGetText
    {
        static void Postfix(Case.ResolveQuestion __instance, Case belongsToCase, ref string __result)
        {
            try
            {
                var cur = NarcCase.Current; if (cur == null || belongsToCase == null || __instance == null) return;
                if (belongsToCase.Pointer != cur.Pointer) return;
                string nm = null; try { nm = __instance.name; } catch { }
                if (string.IsNullOrEmpty(nm)) return;
                bool opt = false; try { opt = __instance.isOptional; } catch { }
                __result = opt ? nm + " (Optional)" : nm;
            }
            catch { }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(Case), nameof(Case.ValidationCheck))]
    internal static class Patch_Case_ValidationCheck
    {
        static Exception Finalizer(Case __instance, Exception __exception, ref bool __result)
        {
            try
            {
                if (__exception == null) return null;
                var cur = NarcCase.Current;
                if (cur != null && __instance != null && __instance.Pointer == cur.Pointer)
                {
                    NarcoticsPlugin.Log.LogInfo($"[Narcotics][case] absorbed Case.ValidationCheck: {__exception.GetType().Name}: {__exception.Message}");
                    __result = false; return null;   // treat as "not yet valid"; lets UpdateResolveFields continue to layout
                }
            }
            catch { }
            return __exception;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(ResolveController), nameof(ResolveController.ValidationUpdate))]
    internal static class Patch_ValidationUpdate
    {
        static Exception Finalizer(Exception __exception)
        {
            if (__exception != null && NarcCase.IsActiveOnPanel()) { NarcoticsPlugin.Log.LogInfo($"[Narcotics][case] absorbed ResolveController.ValidationUpdate: {__exception.GetType().Name}: {__exception.Message}"); return null; }
            return __exception;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(ResolveController), nameof(ResolveController.UpdateResolveFields))]
    internal static class Patch_UpdateResolveFields
    {
        static Exception Finalizer(Exception __exception)
        {
            if (__exception != null && NarcCase.IsActiveOnPanel()) { NarcoticsPlugin.Log.LogInfo($"[Narcotics][case] absorbed ResolveController.UpdateResolveFields: {__exception.GetType().Name}: {__exception.Message}"); return null; }
            return __exception;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(CasePanelController), nameof(CasePanelController.DisplayCorrectMurderQuestions))]
    internal static class Patch_DisplayCorrectMurderQuestions
    {
        static Exception Finalizer(Exception __exception)
        {
            if (__exception != null && NarcCase.IsActiveOnPanel()) { NarcoticsPlugin.Log.LogInfo($"[Narcotics][case] absorbed DisplayCorrectMurderQuestions: {__exception.GetType().Name}: {__exception.Message}"); return null; }
            return __exception;
        }
    }
}
