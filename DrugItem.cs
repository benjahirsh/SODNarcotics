using System;
using UnityEngine;

namespace Narcotics
{
    // The re-skinned drug item: clone a vanilla consumable (beta blockers, or another small
    // pill/medical item) and rename it to "Mystery Drug A" WITHOUT mutating the shared vanilla
    // preset (that would rename every beta blocker in the city). Discovery is by name scan
    // because preset asset names live in Unity bundles, not the DLL.
    internal static class DrugItem
    {
        internal static InteractablePreset BasePreset;   // the discovered vanilla base
        internal static InteractablePreset Current;      // the active operation's cloned drug preset
        internal static string CurrentKey;
        internal static string CurrentDisplay;
        internal const string DrugDescription = "A mysterious new substance";   // examine-panel description
        internal static string DescTreeId;   // cached custom DDS document-tree id that renders DrugDescription (see EnsureDescTree)
        private static readonly System.Random _rng = new System.Random();
        private static System.Collections.Generic.List<InteractablePreset> _basePool;   // per-session pool of drug-item bases; one picked per case for photo variety
        // Every drug preset WE created this session (clone pointer -> its display name). The card patches scope by
        // membership here rather than the single Current, so EVERY one of our doses renders its own name + our caption,
        // even when several different drugs exist at once (the test-spawn key) or across cases. Cleared on load.
        private static readonly System.Collections.Generic.Dictionary<IntPtr, string> _drugNames = new System.Collections.Generic.Dictionary<IntPtr, string>();

        // True if this preset is one of our drug clones. NameFor returns its display name (null if not ours).
        internal static bool IsOurDrugPreset(InteractablePreset p)
        {
            try { return p != null && _drugNames.ContainsKey(p.Pointer); } catch { return false; }
        }
        internal static string NameFor(InteractablePreset p)
        {
            try { if (p != null && _drugNames.TryGetValue(p.Pointer, out var n)) return n; } catch { }
            return null;
        }

        // On a game load the cached presets reference a previous session's (destroyed) objects; clear them so a
        // fresh drug is created next operation.
        internal static void ResetOnLoad() { BasePreset = null; Current = null; CurrentKey = null; CurrentDisplay = null; DescTreeId = null; _basePool = null; _drugNames.Clear(); }

        // Register (once per session) a custom DDS document tree whose single always-display block renders the drug's
        // examine DESCRIPTION ("A mysterious new substance"), and return its id. This id is set as our cloned
        // EvidencePreset.ddsDocumentID (CreateDrug), so the EVIDENCE CARD shows our text under the item photo while the
        // card/photo/title stay native. (The earlier approach set it via Interactable.SetDDSOverride, which turned the
        // item into a readable DOCUMENT - a big crumpled page - and regressed the perfect card; that route is removed.)
        // Same block/message/tree pipeline the game + our Motives notes use (Toolbox.allDDS* dictionaries + Strings
        // 'dds.blocks' table). Returns null on failure (the dose then keeps the base description). Session-only; cleared
        // in ResetOnLoad.
        internal static string EnsureDescTree()
        {
            try
            {
                if (!string.IsNullOrEmpty(DescTreeId)) return DescTreeId;
                var tb = Toolbox.Instance; if (tb == null) return null;
                string uid = System.Guid.NewGuid().ToString("N");
                string blockKey = "narc.desc.block." + uid;
                string messageKey = "narc.desc.msg." + uid;
                string treeId = "narc.desc.tree." + uid;

                // 1) block text -> the dds.blocks table + register the block object.
                int lineNo = 1;
                try { var st = Strings.stringTable; if (st != null && st.ContainsKey("dds.blocks")) lineNo = st["dds.blocks"].Count + 1; } catch { }
                try { Strings.LoadIntoDictionary("dds.blocks", lineNo, blockKey, DrugDescription, "", 0, false, true); } catch (Exception e) { Log($"EnsureDescTree LoadIntoDictionary: {e.Message}"); }
                var block = new DDSSaveClasses.DDSBlockSave();
                block.name = blockKey; block.id = blockKey;
                try { block.replacements = new Il2CppSystem.Collections.Generic.List<DDSSaveClasses.DDSReplacement>(); } catch { }
                try { tb.allDDSBlocks[blockKey] = block; } catch { }

                // 2) message pointing at the block (alwaysDisplay).
                var msg = new DDSSaveClasses.DDSMessageSave();
                msg.name = messageKey; msg.id = messageKey;
                msg.blocks = new Il2CppSystem.Collections.Generic.List<DDSSaveClasses.DDSBlockCondition>();
                try { msg.AddBlock(blockKey); } catch { }
                try { var c = msg.blocks[0]; c.alwaysDisplay = true; c.group = 0; try { c.useTraits = false; } catch { } try { c.traits = new Il2CppSystem.Collections.Generic.List<string>(); } catch { } } catch { }
                try { tb.allDDSMessages[messageKey] = msg; } catch { }

                // 3) document tree pointing at the message.
                var tree = new DDSSaveClasses.DDSTreeSave();
                tree.name = treeId; tree.id = treeId;
                try { tree.treeType = DDSSaveClasses.TreeType.document; } catch { }
                tree.messages = new Il2CppSystem.Collections.Generic.List<DDSSaveClasses.DDSMessageSettings>();
                tree.messageRef = new Il2CppSystem.Collections.Generic.Dictionary<string, DDSSaveClasses.DDSMessageSettings>();
                try { tree.document = new DDSSaveClasses.DDSDocument(); } catch { }
                string inst = null; try { inst = tree.AddMessage(messageKey); } catch { }
                try { tree.startingMessage = inst; } catch { }
                // This tree is attached as the EVIDENCE CARD's writeup (EvidencePreset.ddsDocumentID), which renders the
                // block text under the item photo in the card's own style - NOT the full document reader. So we DON'T set
                // reader layout (CrumpledPaper background, page size, paging): that is what the old Interactable.SetDDSOverride
                // route produced (a big crumpled page replacing the card). Keep only a modest font as a harmless hint; the
                // card controls presentation.
                try
                {
                    DDSSaveClasses.DDSMessageSettings ms = null;
                    try { if (tree.messages != null && tree.messages.Count > 0) ms = tree.messages[0]; } catch { }
                    if (ms != null)
                    {
                        try { ms.font = "TruetypewriterPolyglott SDF"; } catch { }
                        try { ms.fontSize = 11f; } catch { }
                        try { if (tree.messageRef != null && !string.IsNullOrEmpty(inst)) tree.messageRef[inst] = ms; } catch { }
                    }
                }
                catch { }
                try { tb.allDDSTrees[treeId] = tree; } catch { }

                DescTreeId = treeId;
                Log($"registered drug description DDS tree ({treeId}).");
                return DescTreeId;
            }
            catch (Exception e) { Log($"EnsureDescTree error: {e.Message}"); return null; }
        }

        // Find a vanilla consumable to base the drug on. Beta blockers preferred; any small
        // pill/medical consumable is an acceptable fallback.
        internal static InteractablePreset FindBasePreset(bool logAll = false)
        {
            try
            {
                var tb = Toolbox.Instance;
                if (tb == null) { Log("FindBasePreset: Toolbox.Instance null"); return null; }
                var dict = tb.objectPresetDictionary;
                if (dict == null) { Log("FindBasePreset: objectPresetDictionary null"); return null; }

                InteractablePreset beta = null, pill = null, med = null;
                int n = 0, matched = 0;
                foreach (InteractablePreset preset in dict.Values)
                {
                    n++;
                    if (preset == null) continue;
                    string nm = SafeName(preset);
                    if (string.IsNullOrEmpty(nm)) continue;
                    string low = nm.ToLowerInvariant();
                    bool drugish = low.Contains("beta") || low.Contains("blocker") || low.Contains("pill")
                                 || low.Contains("medic") || low.Contains("drug") || low.Contains("capsule")
                                 || low.Contains("tablet") || low.Contains("dose") || low.Contains("sedativ")
                                 || low.Contains("painkill") || low.Contains("narcot");
                    if (logAll && drugish) { matched++; Log($"  candidate: '{nm}'  consumableAmount={SafeConsumable(preset)}"); }
                    if (beta == null && (low.Contains("beta") || low.Contains("blocker"))) beta = preset;
                    else if (pill == null && low.Contains("pill")) pill = preset;
                    else if (med == null && (low.Contains("medic") || low.Contains("capsule") || low.Contains("tablet") || low.Contains("sedativ") || low.Contains("painkill"))) med = preset;
                }
                Log($"FindBasePreset: scanned {n} presets, {matched} drug-ish candidates. beta={(beta != null)} pill={(pill != null)} med={(med != null)}");
                return beta ?? pill ?? med;
            }
            catch (Exception e) { Log($"FindBasePreset error: {e.Message}"); return null; }
        }

        // Known-good drug/medical item preset names for PER-CASE PHOTO VARIETY (the user curated these in-game): each is a
        // small examinable item that renders the pill-bottle-style evidence card, so picking a different one per case
        // varies the photo while our GetSummary/GenerateName patches (base-agnostic, keyed on the dose's preset) still
        // drive the caption + title. Matched as a squashed substring so "Dorma-Dix"/"Birth Control Pills" etc. hit
        // regardless of spacing/case/hyphens.
        // Variety pool = the drug items the game's NewGameLocation.PlaceObject can actually spawn into the lab stash.
        // Verified in-game: BetaBlockers, PoisonBottle, BirthControlPills, Syringe PLACE; SleepingPills, Painkillers,
        // InsulinInjector return NULL. PlaceObject is the world-gen CLUTTER placer, so it only seats an item the game
        // also auto-spawns as free-standing world clutter: the placing items have autoPlacement != never (onlyInHomes /
        // always - you find them lying around), whereas the failing three are vending/retail-only medical items whose
        // prefab has no valid world-resting footprint. Not a settable preset field. No workaround - the chain runs normally.
        // POISON DROPPED from the pool: although PoisonBottle places fine, its examine-evidence is an EvidenceRetailItem
        // whose dusted fingerprint is generated by the retail-evidence system independent of the item's writer/owner/
        // density, so our writer=cook fix (Dose.Place) can't stop it showing a stray citizen's print. BetaBlockers dusts
        // the cook's print correctly; BirthControlPills + Syringe are thin (no dustable print surface) so they never show
        // a stray print at all. (Syringe is a weapon base, left attackable per the user.)
        private static readonly string[] DrugBaseTerms = { "betablocker", "birthcontrol", "syringe" };

        // Build the per-session pool of drug-item bases: every preset whose squashed name matches a term AND spawns
        // evidence (so it examines as a card). Logged so the matched set is visible after a run. Empty pool -> PickBase
        // falls back to the single beta-blocker pick.
        internal static System.Collections.Generic.List<InteractablePreset> FindBasePool(bool log = true)
        {
            var pool = new System.Collections.Generic.List<InteractablePreset>();
            try
            {
                var tb = Toolbox.Instance; var dict = tb != null ? tb.objectPresetDictionary : null;
                if (dict == null) return pool;
                foreach (InteractablePreset preset in dict.Values)
                {
                    if (preset == null) continue;
                    string nm = SafeName(preset); if (string.IsNullOrEmpty(nm)) continue;
                    string low = nm.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
                    bool match = false;
                    for (int i = 0; i < DrugBaseTerms.Length; i++) if (low.Contains(DrugBaseTerms[i])) { match = true; break; }
                    if (!match) continue;
                    bool hasEv = false; try { hasEv = preset.spawnEvidence != null; } catch { }
                    if (!hasEv) { if (log) Log($"  base-pool SKIP (no evidence): '{nm}'"); continue; }
                    pool.Add(preset);
                    if (log) Log($"  base-pool: '{nm}'");
                }
                if (log) Log($"FindBasePool: {pool.Count} drug-item base(s) for per-case photo variety.");
            }
            catch (Exception e) { Log($"FindBasePool error: {e.Message}"); }
            return pool;
        }

        // Pick a random drug-item base for this case (photo variety). Falls back to the single beta-blocker pick if the
        // pool is empty (e.g. no names matched). Pool is discovered once per session and cached.
        internal static InteractablePreset PickBase()
        {
            try
            {
                if (_basePool == null) _basePool = FindBasePool();
                if (_basePool != null && _basePool.Count > 0) return _basePool[_rng.Next(_basePool.Count)];
            }
            catch (Exception e) { Log($"PickBase error: {e.Message}"); }
            return FindBasePreset();
        }

        // Clone the base into a uniquely named drug preset. Never mutates the shared base. `forceBase` pins a specific
        // base (used by the test key to cycle bases deterministically); otherwise a random pool pick gives per-case variety.
        // Restore a saved dose without changing the current operation's drug. Loading can call this twice;
        // re-use existing presets instead of creating another Unity clone for the same key.
        internal static InteractablePreset CreateDrug(string presetKey, string displayName, InteractablePreset forceBase = null,
            bool makeCurrent = true, bool persist = true)
        {
            try
            {
                var dict = Toolbox.Instance != null ? Toolbox.Instance.objectPresetDictionary : null;
                if (dict == null) { Log("CreateDrug: objectPresetDictionary not ready"); return null; }
                InteractablePreset existing = null;
                if (dict.TryGetValue(presetKey, out existing) && existing != null)
                {
                    // ResetOnLoad clears the display-name map, so bind it even if this preset already exists.
                    try { Strings.WriteToDictionary("evidence.names", presetKey, DrugDescription, displayName, "", 0, false, ""); } catch { }
                    try { _drugNames[existing.Pointer] = displayName; } catch { }
                    if (makeCurrent) { Current = existing; CurrentKey = presetKey; CurrentDisplay = displayName; BasePreset = forceBase; }
                    return existing;
                }
                var baseP = forceBase ?? PickBase();   // a specific or random drug-item base; random gives per-case photo variety
                if (baseP == null) { Log("CreateDrug: no base preset found (run the substrate dump to see item names)"); return null; }
                if (makeCurrent) BasePreset = baseP;

                var clone = UnityEngine.Object.Instantiate(baseP);
                if (clone == null) { Log("CreateDrug: Instantiate returned null"); return null; }
                UnityEngine.Object.DontDestroyOnLoad(clone);
                try { clone.hideFlags = HideFlags.HideAndDontSave; } catch { }
                try { clone.name = presetKey; } catch { }
                try { clone.presetName = presetKey; } catch (Exception e) { Log($"CreateDrug: set presetName failed: {e.Message}"); }
                // Concealed: NPCs carry the dose in inventory (still searchable + fingerprintable) but do NOT walk
                // around visibly holding it in hand (user decision 2026-10-01). The arrest fires from an enforcer's
                // line-of-sight to a known buyer, not from vanilla contraband detection.
                try { clone.inventoryCarryItem = false; } catch { }
                try { clone.requiredCarryAnimation = false; } catch { }
                // The base (beta blockers etc.) is a RETAIL consumable, which (a) typed the dose as EvidenceRetailItem so
                // its name came from the shop/retail config instead of our evidence.names write (showing "retail item" or
                // an inconsistent cached name), and (b) let the player TAKE/consume it. Strip both: null the retail link so
                // the name resolves through evidence.names (below), and zero the consumable + take flags so it can't be
                // taken or used - it is clue evidence only, frisked/searched but not pocketed or swallowed.
                try { clone.retailItem = null; } catch (Exception e) { Log($"CreateDrug: clear retailItem failed: {e.Message}"); }
                try { clone.consumableAmount = 0f; } catch { }
                try { clone.destroyWhenAllConsumed = false; } catch { }
                try { clone.takeWith = false; } catch { }

                // Declassify any WEAPON base (the Syringe is a MurderWeaponPreset, so it offered "attack/throw" instead of
                // the normal "place/throw"): null the weapon preset so the dose behaves like a regular object, matching the
                // other drug items. (weapon is the only weapon field on InteractablePreset.)
                try { clone.weapon = null; } catch (Exception e) { Log($"CreateDrug: clear weapon failed: {e.Message}"); }
                // Fingerprints: the "random" examine print is the item's WRITER (a citizen the game assigns at spawn), which
                // we set to the cook/holder in Dose.Place / Dose.Create so the dose dusts to the real handler. Also zero the
                // clone's fingerprintDensity as a belt-and-suspenders so no density-driven ambient print is added on top.
                // (Dead ends from the investigation: fingerprintsEnabled=false removes ALL prints incl. the cook's; df/saved/fp
                // are not where the examine print lives; EvidenceRetailItem bases like PoisonBottle ignore all of this, which
                // is why PoisonBottle is excluded from the pool - see DrugBaseTerms.)
                try { clone.fingerprintDensity = 0f; } catch (Exception e) { Log($"CreateDrug: zero fingerprintDensity failed: {e.Message}"); }

                // The card's examine DESCRIPTION (the writeup under the item photo) is substituted per-dose by a Harmony
                // patch on the writeup getter (Patch_DrugItemDescription in Diagnostics.cs), NOT by touching the evidence
                // preset: cloning the EvidencePreset blanked the whole card (name/photo/writeup all resolve off the shared
                // preset's identity + registration, which an Instantiate'd copy loses), and mutating the shared base would
                // change every real beta blocker. So the clone keeps the base's spawnEvidence untouched and the patch swaps
                // only the displayed text for our dose.

                // Register before persisting; a failed registration should not be recorded as a valid drug.
                try { dict[presetKey] = clone; } catch (Exception e) { Log($"CreateDrug: register failed: {e.Message}"); return null; }

                // Item names resolve via Strings 'evidence.names' keyed by presetName; the 'notes' slot (3rd arg) carries
                // the examine description. So the dose reads as the case's drug name + "A mysterious new substance".
                try { Strings.WriteToDictionary("evidence.names", presetKey, DrugDescription, displayName, "", 0, false, ""); }
                catch (Exception e) { Log($"CreateDrug: WriteToDictionary failed: {e.Message}"); }

                if (makeCurrent) { Current = clone; CurrentKey = presetKey; CurrentDisplay = displayName; }
                try { _drugNames[clone.Pointer] = displayName; } catch { }   // register this clone so the card patches recognise its doses + show this name
                // Store the exact vanilla base so reloading does not randomly change this drug's appearance.
                if (persist && presetKey.StartsWith("NarcDrug_", StringComparison.Ordinal))
                    DrugPersistence.Remember(presetKey, displayName, SafeName(baseP));
                Log($"CreateDrug: created '{displayName}' (key={presetKey}) from base '{SafeName(baseP)}'");
                return clone;
            }
            catch (Exception e) { Log($"CreateDrug error: {e}"); return null; }
        }

        // Rename the CURRENT drug preset in place (no new clone) so each operation's doses carry that case's drug name.
        // Rewrites the evidence.names entry keyed by the preset's key; every existing/new dose of this preset then shows
        // the new name. Safe because cases are serial and the previous op's doses are cleaned up on resolve.
        internal static void Rename(string displayName)
        {
            try
            {
                if (Current == null || string.IsNullOrEmpty(CurrentKey)) { CreateDrug("NarcOpDrug", string.IsNullOrEmpty(displayName) ? "Mystery Drug" : displayName); return; }
                if (string.IsNullOrEmpty(displayName)) return;
                try { Strings.WriteToDictionary("evidence.names", CurrentKey, DrugDescription, displayName, "", 0, false, ""); }
                catch (Exception e) { Log($"Rename: WriteToDictionary failed: {e.Message}"); }
                CurrentDisplay = displayName;
                // Preserve the final street name if an operation renames the drug after creation.
                if (BasePreset != null && CurrentKey.StartsWith("NarcDrug_", StringComparison.Ordinal))
                    DrugPersistence.Remember(CurrentKey, displayName, SafeName(BasePreset));
                Log($"Rename: current drug is now '{displayName}' (key={CurrentKey}).");
            }
            catch (Exception e) { Log($"Rename error: {e.Message}"); }
        }

        // Put a dose into a citizen's consumables (what they will later 'use' / sell).
        internal static void GiveConsumable(Human who, InteractablePreset drug)
        {
            try { if (who != null && drug != null) who.AddCurrentConsumable(drug); }
            catch (Exception e) { Log($"GiveConsumable error: {e.Message}"); }
        }

        // How many of the CURRENT operation's drug a citizen is holding (in their consumables).
        internal static int CountIn(Human who)
        {
            int c = 0;
            try
            {
                var list = who != null ? who.currentConsumables : null;
                if (list == null || Current == null) return 0;
                for (int i = 0; i < list.Count; i++)
                {
                    try { var p = list[i]; if (p != null && p.Pointer == Current.Pointer) c++; } catch { }
                }
            }
            catch { }
            return c;
        }

        // Remove one dose of the current drug from a citizen (returns true if one was taken).
        internal static bool TakeOne(Human who)
        {
            try { if (who != null && Current != null && CountIn(who) > 0) { who.RemoveCurrentConsumable(Current); return true; } }
            catch { }
            return false;
        }

        // Spawn a physical, visible dose near an actor via the game's own furniture placement.
        // The raw CreateWorldInteractable path needs room/node context and NREs without it; PlaceObject
        // is the proven primitive (the same one the clue system uses).
        internal static Interactable SpawnDoseAtActor(InteractablePreset drug, Human at, Human belongsTo)
        {
            try
            {
                if (drug == null || at == null) return null;
                var loc = at.currentGameLocation;
                if (loc == null) { Log("SpawnDoseAtActor: actor has no currentGameLocation"); return null; }
                FurnitureLocation furn;
                return loc.PlaceObject(drug, belongsTo, null, null, out furn,
                    passVariable: false,
                    forceSecuritySettings: true,
                    forcedSecurity: 0,
                    forcedOwnership: InteractablePreset.OwnedPlacementRule.both,
                    forcedPriority: 5,
                    placeClosestTo: at.currentNode,
                    ignoreLimits: true);
            }
            catch (Exception e) { Log($"SpawnDoseAtActor error: {e.Message}"); return null; }
        }

        static string SafeName(InteractablePreset p)
        {
            try { if (!string.IsNullOrEmpty(p.presetName)) return p.presetName; } catch { }
            try { return p.name; } catch { return null; }
        }
        static string SafeConsumable(InteractablePreset p) { try { return p.consumableAmount.ToString("0.##"); } catch { return "?"; } }
        static void Log(string m) => NarcoticsPlugin.Log.LogInfo("[Narcotics][item] " + m);
    }
}
