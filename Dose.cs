using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Narcotics
{
    // A PHYSICAL, searchable dose: a real Interactable sitting in an NPC's inventory, so it shows up when the
    // player searches/frisks them (unlike a currentConsumable, which does not appear on a body search - that
    // was why searching the cook found nothing). Built from the operation's cloned drug preset.
    //
    // We keep our own per-human handle list so we can count, transfer and destroy doses precisely, and we
    // validate each handle against the item's live `inInventory` owner so the counts self-heal if the game
    // moves or deletes an item under us.
    internal static class Dose
    {
        // humanID -> the physical dose Interactables we believe are in that human's inventory.
        private static readonly Dictionary<int, List<Interactable>> _held = new Dictionary<int, List<Interactable>>();

        internal static void ResetOnLoad() { _held.Clear(); }
        // Reconnect to dose Interactables that the GAME has already deserialized. The mod's
        // _held dictionary is purely a managed index, so ResetOnLoad() drops it even while
        // physical items survive in the game's world/inventories. Reindex after the active
        // preset/operation is restored and game save loading has completed, before Tick().
        //
        // This method MUST NOT Create(), SetInInventory(), change fingerprints, or restore
        // missing items by count: the player may have seized/destroyed them before saving.
        // Scoping to the exact active drug key also excludes historical case evidence.
        internal static int ReindexSavedInventory(string activeDrugKey)
        {
            _held.Clear();
            if (string.IsNullOrEmpty(activeDrugKey)) return 0;
            try
            {
                var cd = CityData.Instance;
                var savedItems = cd != null ? cd.savableInteractableDictionary : null;
                if (savedItems == null)
                {
                    Log("[resume] savable interactable dictionary not ready; no inventory handles rebound.");
                    return 0;
                }
                var seenIds = new HashSet<int>();
                var holderIds = new Dictionary<int, List<int>>();
                int matchedDrugItems = 0, rebound = 0;
                foreach (var entry in savedItems)
                {
                    Interactable item = null;
                    try { item = entry.Value; } catch { }
                    if (!Alive(item)) continue;
                    string key = null;
                    try { var p = item.preset; if (p != null) key = p.presetName; } catch { }
                    if (!string.Equals(key, activeDrugKey, StringComparison.Ordinal)) continue;
                    matchedDrugItems++;
                    Human holder = null;
                    try { holder = item.inInventory; } catch { }
                    if (holder == null) continue; // world/stash/previously seized item, not NPC possession
                    int itemId;
                    try { itemId = item.id; } catch { continue; }
                    if (!seenIds.Add(itemId)) continue;
                    Track(holder, item);
                    if (!holderIds.TryGetValue(holder.humanID, out var ids))
                    {
                        ids = new List<int>();
                        holderIds[holder.humanID] = ids;
                    }
                    ids.Add(itemId);
                    rebound++;
                }
                Log($"[resume] found {matchedDrugItems} saved world item(s) for {activeDrugKey}; " +
                    $"rebound {rebound} inventory dose(s) across {_held.Count} holder(s). " +
                    string.Join("; ", holderIds.Select(kv => $"holder={kv.Key}:ids=[{string.Join(",", kv.Value)}]")) +
                    ". No items created or moved.");
                return rebound;
            }
            catch (Exception e)
            {
                Log($"[resume] inventory reindex failed: {e}");
                _held.Clear();
                return 0;
            }
        }

        // How many physical doses `who` is currently holding (validated + self-healing).
        internal static int CountIn(Human who)
        {
            int c = 0;
            try
            {
                if (who == null) return 0;
                int id = who.humanID;
                if (!_held.TryGetValue(id, out var l) || l == null) return 0;
                for (int i = l.Count - 1; i >= 0; i--)
                {
                    if (HeldBy(l[i], id)) c++; else l.RemoveAt(i);
                }
            }
            catch { }
            return c;
        }

        // Create a brand-new physical dose directly in `holder`'s inventory. Returns the Interactable or null.
        //
        // Uses the game's OWN NPC-inventory primitive: CreateWorldInteractable assigns a UNIQUE world id, so each
        // dose gets its own distinct Evidence ("I{id}") and is a properly registered, PICKABLE item; SetInInventory
        // then moves it into the holder and stamps their fingerprint. (The old CreateCitizenInteractable path never
        // assigned an id -> every dose collided on evidence "I0" = whatever already owned that key, e.g. a bullet
        // wound; doses shared one case pin; and the unregistered item vanished when the player tried to take it.)
        internal static Interactable Create(Human holder)
        {
            try
            {
                var preset = DrugItem.Current;
                if (preset == null || holder == null) return null;
                NewNode node = null; try { node = holder.currentNode; } catch { }
                if (node == null) { Log("Create: holder has no currentNode (unstreamed); skipping."); return null; }

                InteractableCreator creator = null;
                try { creator = InteractableCreator.Instance; } catch { }
                if (creator == null) { Log("Create: InteractableCreator.Instance null"); return null; }

                Interactable it = null;
                try { it = creator.CreateWorldInteractable(preset, holder, null, null, node.position, Vector3.zero, null, null); }
                catch (Exception e) { Log($"CreateWorldInteractable threw: {e.Message}"); }
                if (it == null) return null;

                try { it.SetInInventory(holder); } catch (Exception e) { Log($"SetInInventory threw: {e.Message}"); }   // also stamps the holder's fingerprint (the forensic trail); no extra AddNewDynamicFingerprint, which would double the print
                // CreateWorldInteractable assigns a random citizen as the item's WRITER (same stray-print source as PlaceObject
                // on the lab doses). Set it to the holder (the creator) so a frisked/lifted dose shows only real handlers'
                // prints, never a stray unrelated citizen.
                try { it.writer = holder; } catch { }
                try { var ev = it.evidence; if (ev != null) ev.SetWriter(holder); } catch { }
                ClearOwner(it);   // no owner -> taking it (during a search) isn't theft, so bystanders don't attack
                Track(holder, it);
                return it;
            }
            catch (Exception e) { Log($"Create error: {e.Message}"); return null; }
        }

        // Clear the dose's OWNER (belongsTo) so the player taking it during a search is NOT treated as theft -> no
        // bystander-attack. The forensic link is the FINGERPRINT (still stamped) and searchability is the Evidence +
        // inInventory, both independent of belongsTo. (Decoded: GetValidPlayerActionIllegal keys theft off belongsTo.)
        private static void ClearOwner(Interactable it)
        {
            try { if (it != null) it.belongsTo = null; } catch { }
        }

        // Remove every dynamic fingerprint currently on the item. PlaceObject seeds a freshly-spawned clutter item with
        // its own native handler print(s) - on a vacant lab that shows up as a RANDOM citizen's print beside the cook's,
        // muddying the lab-raid evidence. We strip them so we can stamp ONLY the real handler. (RemoveDynamicPrint mutates
        // df, so iterate backwards by index.)
        private static void StripPrints(Interactable it)
        {
            try
            {
                if (it == null) return;
                // 1) dynamic prints (runtime list): remove each, then hard-clear the list as a backstop (RemoveDynamicPrint
                //    may not drop the entry reliably through interop).
                try { var df = it.df; if (df != null) { for (int i = df.Count - 1; i >= 0; i--) { try { var d = df[i]; if (d != null) it.RemoveDynamicPrint(d); } catch { } } try { df.Clear(); } catch { } } } catch { }
                // 2) saved prints (the persisted list the dust renderer also reads) + the fingerprint-count field.
                try { var sp = it.print; if (sp != null) sp.Clear(); } catch { }
                try { it.fp = 0; } catch { }
            }
            catch (Exception e) { Log($"StripPrints error: {e.Message}"); }
        }


        // Place a physical dose into a game location's furniture (a persistent, searchable STASH - e.g. inside the
        // lab). Uses the game's own PlaceObject pipeline (survives streaming + save/load). Not tracked as inventory;
        // the caller keeps the returned handle. Stamps the owner's fingerprint. Returns null if placement failed
        // (e.g. the location's geometry is not streamed yet - retry when the player is near).
        internal static Interactable Place(NewGameLocation loc, NewNode near, Human owner)
        {
            try
            {
                var preset = DrugItem.Current;
                if (preset == null || loc == null) { Log($"Place: null preset or loc (preset={(preset==null?"NULL":"ok")}, loc={(loc==null?"NULL":"ok")})"); return null; }
                string pn = SafePresetName(preset);
                FurnitureLocation furn;
                Interactable it = null;
                try
                {
                    // writer = owner (the cook): PlaceObject attributes the item's fingerprints to the WRITER. Passing null
                    // let the game assign a RANDOM citizen as the handler (and regenerate a fresh one on each placement) -
                    // that was the mystery unknown print. writer=cook makes the placed dose carry the cook's print as the trail.
                    it = loc.PlaceObject(preset, owner, owner, null, out furn,
                        passVariable: false, forceSecuritySettings: true, forcedSecurity: 0,
                        forcedOwnership: InteractablePreset.OwnedPlacementRule.both, forcedPriority: 5,
                        placeClosestTo: near, ignoreLimits: true);
                }
                catch (Exception pe) { Log($"Place: PlaceObject THREW for base-clone '{pn}': {pe.GetType().Name}: {pe.Message}"); return null; }
                if (it == null) { Log($"Place: PlaceObject returned NULL for base-clone '{pn}' at loc '{SafeLocName(loc)}' - this drug item did NOT go into the lab stash."); return null; }
                // The random dust print is the item's WRITER (a citizen the game assigns at spawn) - NOT anything in
                // df/saved/fp (those read clean yet the random still dusts). Set the writer to the cook on both the
                // interactable and its evidence, so the dust attributes that print to the cook instead of a random.
                if (owner != null)
                {
                    try { it.writer = owner; } catch { }
                    try { var ev = it.evidence; if (ev != null) ev.SetWriter(owner); } catch (Exception e) { Log($"Place: SetWriter failed: {e.Message}"); }
                }
                StripPrints(it);   // clear any spawn-baked df/fp junk; the cook's print now comes solely from the writer set above (one clean cook print, no duplicate)
                ClearOwner(it);    // drop ownership so taking it during a search isn't theft
                return it;
            }
            catch (Exception e) { Log($"Place error: {e.Message}"); return null; }
        }

        // ---- searchable trash-bin dead drops ------------------------------------------------------------------
        // A bin's SEARCHABLE contents are contained MetaObjects on the bin container's EvidenceMultiPage - that is
        // the ONLY list the player's search reads. MetaObjects self-register in CityData and live in the save, so
        // this is fully data-side + persistent: it works with the player nowhere near (same pipeline PlaceObject
        // uses, which is why the lab stash works). World items in the furniture's spawned list, loose litter
        // (isTrash), and ActionController.Dispose all do NOT show up on a bin search.

        // The bin's searchable page, building the container interactable data-side if needed. Null = this furniture
        // is not a searchable container (pick a different bin).
        internal static EvidenceMultiPage FindBinPage(FurnitureLocation fl)
        {
            try
            {
                if (fl == null) return null;
                try { if (!fl.createdInteractables) fl.CreateInteractables(); } catch (Exception e) { Log($"CreateInteractables threw: {e.Message}"); }
                for (int pass = 0; pass < 2; pass++)
                {
                    var list = pass == 0 ? fl.integratedInteractables : fl.spawnedInteractables;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var it = list[i]; if (it == null) continue;
                        Evidence ev = null; try { ev = it.evidence; } catch { }
                        if (ev == null) continue;
                        EvidenceMultiPage mp = null; try { mp = ev.TryCast<EvidenceMultiPage>(); } catch { }
                        if (mp != null) return mp;
                    }
                }
            }
            catch (Exception e) { Log($"FindBinPage error: {e.Message}"); }
            return null;
        }

        // True if this furniture is a REAL searchable container (has a container EvidenceMultiPage the player's search
        // reads). Use to validate a dead-drop candidate at siting time so we never pick something that can't hold doses
        // (e.g. a decorative item whose name happens to match a bin keyword).
        internal static bool CanDeposit(FurnitureLocation fl)
        {
            try { return FindBinPage(fl) != null; } catch { return false; }
        }

        // Deposit one dose as searchable trash into the bin (a contained MetaObject on its page), carrying the
        // depositor's authorship. Returns true on success.
        internal static bool DepositToBin(FurnitureLocation fl, Human depositor)
        {
            try
            {
                var preset = DrugItem.Current; if (preset == null) return false;
                var page = FindBinPage(fl); if (page == null) { Log("DepositToBin: furniture is not a searchable container."); return false; }
                MetaObject mo = null;
                try { mo = new MetaObject(preset, depositor, depositor, null, null); }   // ctor auto-registers in CityData
                catch (Exception e) { Log($"new MetaObject threw: {e.Message}"); return false; }
                if (mo == null) return false;
                try { page.AddContainedMetaObjectToNewPage(mo); }
                catch (Exception e) { Log($"AddContainedMetaObjectToNewPage threw: {e.Message}"); return false; }
                return true;
            }
            catch (Exception e) { Log($"DepositToBin error: {e.Message}"); return false; }
        }

        // How many of OUR doses are currently in the bin (authoritative; reads the page).
        internal static int CountInBin(FurnitureLocation fl)
        {
            int c = 0;
            try
            {
                var page = FindBinPage(fl); if (page == null) return 0;
                string key = DrugItem.CurrentKey;
                var pc = page.pageContent; if (pc == null) return 0;
                for (int i = 0; i < pc.Count; i++)
                {
                    var e = pc[i]; if (e == null) continue;
                    int id = 0; try { id = e.meta; } catch { }
                    if (IsOurMeta(id, key)) c++;
                }
            }
            catch { }
            return c;
        }

        // Remove up to `max` of our doses from the searchable bin, creating one physical
        // dose in the collector's inventory for each. The MetaObject is an EVIDENCE SOURCE:
        // deleting it via MetaObject.Remove() leaves a previously discovered I{id} case pin
        // dangling after a save/load, even though the replacement physical dose survives.
        // Detach only the page entry, retaining the registered MetaObject for that evidence.
        // The detached entry is no longer searchable, stock-counted or collectible.
        internal static int CollectFromBin(FurnitureLocation fl, Human collector, int max)
        {
            int got = 0;
            try
            {
                if (collector == null || max <= 0) return 0;
                var page = FindBinPage(fl); if (page == null) return 0;
                string key = DrugItem.CurrentKey;
                var pc = page.pageContent; if (pc == null) return 0;
                for (int i = pc.Count - 1; i >= 0 && got < max; i--)
                {
                    var e = pc[i]; if (e == null) continue;
                    int id = 0; try { id = e.meta; } catch { }
                    if (!IsOurMeta(id, key)) continue;

                    // Mint BEFORE removing the bin's content: an unstreamed collector (or
                    // failed CreateWorldInteractable) must not consume a saved stash dose.
                    var created = Create(collector);
                    if (created == null) { Log($"[evidence] collection deferred: could not create inventory dose for meta={id}."); break; }
                    if (!HeldBy(created, collector.humanID))
                    {
                        RollBackFailedBinCollection(collector, created);
                        Log($"[evidence] collection deferred: created dose not held by collector, meta={id}.");
                        break;
                    }
                    try { pc.RemoveAt(i); }
                    catch (Exception e2)
                    {
                        RollBackFailedBinCollection(collector, created);
                        Log($"[evidence] collection deferred: removing page entry meta={id} failed: {e2.Message}");
                        break;
                    }
                    // Do NOT call FindMetaObject(id).Remove() here. The registered meta is
                    // a historical evidence source, not another physical/stocked dose.
                    got++;
                    Log($"[evidence] retired searchable bin entry meta={id}; evidence source retained; physical dose={created.id}.");
                }
            }
            catch (Exception e) { Log($"CollectFromBin error: {e.Message}"); }
            return got;
        }

        // Undo only the newly created inventory item if we couldn't detach the source
        // page entry. No previous dose or discovered evidence should be touched.
        private static void RollBackFailedBinCollection(Human collector, Interactable created)
        {
            try
            {
                if (collector != null && _held.TryGetValue(collector.humanID, out var list))
                    list.Remove(created);
            }
            catch { }
            try { if (created != null) created.SafeDelete(true); } catch { }
        }

        // Clear searchable stock on operation teardown. Preserve the underlying meta
        // evidence sources for any pin made before the case was resolved/stopped.
        internal static int ClearBin(FurnitureLocation fl)
        {
            int n = 0;
            try
            {
                var page = FindBinPage(fl); if (page == null) return 0;
                string key = DrugItem.CurrentKey;
                var pc = page.pageContent; if (pc == null) return 0;
                for (int i = pc.Count - 1; i >= 0; i--)
                {
                    var e = pc[i]; if (e == null) continue;
                    int id = 0; try { id = e.meta; } catch { }
                    if (!IsOurMeta(id, key)) continue;
                    try { pc.RemoveAt(i); n++; }
                    catch (Exception e2) { Log($"[evidence] failed clearing bin entry meta={id}: {e2.Message}"); break; }
                }
            }
            catch (Exception e) { Log($"ClearBin error: {e.Message}"); }
            return n;
        }

        private static bool IsOurMeta(int id, string key)
        {
            if (id == 0 || string.IsNullOrEmpty(key)) return false;
            try { var m = CityData.Instance.FindMetaObject(id); return m != null && m.preset == key; } catch { return false; }
        }

        // Move one dose from a holder's inventory to a loose spot at a node (a DEAD DROP). Reuses the same item, so
        // its accumulated fingerprints are preserved. Returns the handle now sitting at the node, or null.
        internal static Interactable Drop(Human from, NewNode node)
        {
            try
            {
                if (from == null || node == null) return null;
                var it = TakeHandle(from);
                if (it == null) return null;
                try { it.SetAsNotInventory(node); } catch (Exception e) { Log($"Drop SetAsNotInventory threw: {e.Message}"); }
                return it;
            }
            catch (Exception e) { Log($"Drop error: {e.Message}"); return null; }
        }

        // Move a world/furniture dose (from a stash or dead drop) into a collector's inventory. Adds it to inventory
        // tracking so CountIn(to) includes it, and stamps the collector's fingerprint (via SetInInventory). Returns
        // true on success.
        internal static bool Pickup(Interactable it, Human to)
        {
            try
            {
                if (to == null || !Alive(it)) return false;
                try { it.SetInInventory(to); } catch (Exception e) { Log($"Pickup SetInInventory threw: {e.Message}"); return false; }
                ClearOwner(it);   // in case SetInInventory re-stamped ownership; keep it un-owned (not theft to take)
                bool ok = false; try { var inv = it.inInventory; ok = inv != null && inv.humanID == to.humanID; } catch { }
                if (ok) Track(to, it);
                return ok;
            }
            catch { return false; }
        }

        // Is a world/dropped handle still valid (not destroyed)?
        internal static bool Alive(Interactable it)
        {
            try { return it != null && it.Pointer != IntPtr.Zero; } catch { return false; }
        }

        // Create n physical doses in a holder's inventory. Returns how many were created.
        internal static int CreateMany(Human holder, int n)
        {
            int made = 0;
            for (int i = 0; i < n; i++) { if (Create(holder) != null) made++; }
            return made;
        }

        // Move one existing dose from `from` to `to` (stays a physical, friskable item on the new holder).
        internal static bool Transfer(Human from, Human to)
        {
            try
            {
                if (from == null || to == null) return false;
                var it = TakeHandle(from);
                if (it == null) return false;
                try { it.SetInInventory(to); } catch (Exception e) { Log($"Transfer SetInInventory threw: {e.Message}"); }
                ClearOwner(it);   // the buyer holds it but doesn't OWN it -> searching them + taking it isn't theft
                Track(to, it);
                return true;
            }
            catch (Exception e) { Log($"Transfer error: {e.Message}"); return false; }
        }

        // Move up to n doses from -> to; returns how many moved.
        internal static int TransferMany(Human from, Human to, int n)
        {
            int moved = 0;
            for (int i = 0; i < n; i++) { if (!Transfer(from, to)) break; moved++; }
            return moved;
        }

        // Destroy one dose held by `who` (e.g. consumed / deposited into an abstract stash). True if one went.
        internal static bool Destroy(Human who)
        {
            try
            {
                var it = TakeHandle(who);
                if (it == null) return false;
                try { it.SafeDelete(true); } catch (Exception e) { Log($"Destroy SafeDelete threw: {e.Message}"); }
                return true;
            }
            catch { return false; }
        }

        internal static int DestroyMany(Human who, int n)
        {
            int gone = 0;
            for (int i = 0; i < n; i++) { if (!Destroy(who)) break; gone++; }
            return gone;
        }

        // Destroy every tracked dose (operation teardown). Best-effort.
        internal static void DestroyAll()
        {
            try
            {
                foreach (var kv in _held)
                {
                    var l = kv.Value; if (l == null) continue;
                    for (int i = 0; i < l.Count; i++) { try { var it = l[i]; if (it != null) it.SafeDelete(true); } catch { } }
                }
            }
            catch { }
            _held.Clear();
        }

        // Pop and return a valid dose handle currently held by `who`, or null.
        private static Interactable TakeHandle(Human who)
        {
            try
            {
                if (who == null) return null;
                int id = who.humanID;
                if (!_held.TryGetValue(id, out var l) || l == null) return null;
                for (int i = l.Count - 1; i >= 0; i--)
                {
                    var it = l[i];
                    l.RemoveAt(i);
                    if (HeldBy(it, id)) return it;   // else it was stale; keep popping
                }
            }
            catch { }
            return null;
        }

        private static void Track(Human who, Interactable it)
        {
            try
            {
                if (who == null || it == null) return;
                int id = who.humanID;
                if (!_held.TryGetValue(id, out var l) || l == null) { l = new List<Interactable>(); _held[id] = l; }
                l.Add(it);
            }
            catch { }
        }

        // Is this interactable alive and in the given human's inventory?
        private static bool HeldBy(Interactable it, int humanID)
        {
            try
            {
                if (it == null || it.Pointer == IntPtr.Zero) return false;
                var inv = it.inInventory;
                return inv != null && inv.humanID == humanID;
            }
            catch { return false; }
        }

        private static string SafePresetName(InteractablePreset p) { try { if (!string.IsNullOrEmpty(p.presetName)) return p.presetName; } catch { } try { return p.name; } catch { return "?"; } }
        private static string SafeLocName(NewGameLocation l) { try { return l.name; } catch { return "?"; } }
        static void Log(string m) => NarcoticsPlugin.Log.LogInfo("[Narcotics][dose] " + m);
    }
}
