using System;
using System.Collections.Generic;
using UnityEngine;

namespace Narcotics
{
    // The single active drug operation, modelled as a once-per-day pipeline the player can tail hop by hop:
    //
    //   COOK  --(daily)-->  goes to the lab, cooks a batch, stashes it INSIDE the lab, leaves
    //   DISTRIBUTOR  --(daily, after the cook)-->  collects the lab stash, then runs to a DEAD DROP near each
    //                                              dealer and leaves a couple of doses
    //   DEALERS  -->  collect from their nearby dead drop
    //
    // The cook is NOT pinned at the lab: they only commute to/from the lab to cook (the tell), then resume their
    // normal routine. Doses are physical, searchable Interactables (Dose.cs); the lab stash uses the game's
    // PlaceObject pipeline (persistent), dead drops reuse the carried item (SetAsNotInventory, so fingerprints
    // travel with it), and pickups are scripted (herd the collector to the spot, then SetInInventory).
    //
    // Exactly ONE actor is driven at a time (a single "job"), so movement is sane and tailable.
    internal static class Operation
    {
        internal sealed class Dist { public Human H; public bool Collected; }
        internal sealed class Dealer { public Human H; public int DistIndex; public FurnitureLocation Container; public NewNode Drop; public NewGameLocation DropLoc; public IntPtr DropCam; public readonly List<Interactable> Stash = new List<Interactable>(); public int BinDoses; public float NextSell; }   // DropCam = the camera Interactable pointer that films the dead drop, so the DEAL can pick a DIFFERENT camera. NextSell = game-hours gate: the dealer potters freely until then, then sells one dose to the nearest passer-by at a filmed node.

        internal static bool Active;
        internal static Human CookHuman;
        internal static NewAddress Lab;
        internal static string DrugName = "Mystery";              // this operation's drug street-name (NarcNames); doses + stash share it
        internal static string CaseTitle = "New Drug on the Street";   // the case headline built from DrugName
        internal static readonly List<Dist> Distributors = new List<Dist>();
        internal static readonly List<Dealer> Dealers = new List<Dealer>();
        internal static readonly List<Human> Buyers = new List<Human>();      // passers-by who bought a dose (for the consume/arrest phase)
        internal static readonly List<Interactable> LabStash = new List<Interactable>();

        private enum Job { None, CookSession, DistCollect, DistDrop, DealerCollect, SellToBuyer }
        private static Job _job;
        private static Human _mover;
        private static Dist _jobDist;
        private static Dealer _jobDealer;
        private static Human _jobBuyer;         // the passer-by walking over to buy (SellToBuyer)
        private static NewNode _meetNode;       // the public spot the dealer waits at during a sale
        private static NewNode _buyerMeetNode;  // an adjacent (still on-camera) node the buyer stands on, so the pair aren't stacked on one node
        private static IntPtr _meetCam;          // the camera filming the meet node; the buyer's node must be on this SAME camera so both are on one feed
        private static float _nextDealShift;     // game hours; during a deal, shuffle the pair to a new on-camera spot every ~5 min so they don't block a doorway
        private static float _dwellUntil;       // GAME hours; the visible pause at a dead drop (stash/collect), DropDwellMinutes long
        private static float _dealDwellEndGT;   // GAME hours; the deal holds the pair on the camera node until this (DealDwellMinutes)
        private static float _deadline;         // real-time seconds; abort a stuck job
        private static float _reactWaitUntil;   // real-time seconds; a collect/drop job releases its mover while it reacts to a tail, but gives up waiting at this point
        private static bool _labDressed;         // true once the lab has been dressed with equipment (needs the lab geometry streamed, i.e. the player visited)
        private static float _nextDressTry;      // real-time seconds; throttle the auto-dress attempts until one succeeds
        private static int _labDressTries;       // failed dress attempts; capped so an un-furnishable shell doesn't spam
        private static readonly List<FurnitureClusterLocation> _labFurniture = new List<FurnitureClusterLocation>();   // equipment we added, reverted when the case ends
        // Deferred furniture strip: DeleteCluster only works while the lab GEOMETRY is streamed (same requirement as
        // placement), but a case usually resolves at City Hall with the lab unstreamed, so the strip at Stop() can't
        // reach it. We queue the clusters + the lab here and finish the strip (TickPendingStrip) the next time that lab
        // is streamed (player enters it). The leftover furniture is out of sight until then, so nothing shows wrong.
        private static readonly List<FurnitureClusterLocation> _pendingStrip = new List<FurnitureClusterLocation>();
        private static NewGameLocation _pendingStripLoc;
        // Cook now runs a PASSIVE multi-batch lab session (TickCook), NOT a single-mover job: it stays pinned in the
        // lab and makes CookBatchesPerSession batches CookBatchGapHours apart, so the player can stake out the lab and
        // the distributor's second run comes a reliable ~4h later (not the old ~12h gap). Sessions repeat every
        // CookIntervalHours (measured from when the cook leaves).
        private static float _nextCookTime;     // game hours; when the next SESSION starts
        private static float _cookSessionStart;  // game hours; when the current lab session started (the shift ends CookShiftHours after this)
        private static float _opStartGT;        // game hours when THIS case initialised; re-cook sessions anchor to it (opStart + N*CookIntervalHours)
        private static bool _cookActive;        // a lab session is in progress (cook staying)
        private static int _cookBatchesLeft;    // batches still to make this session
        private static int _cookBatchLimit;     // ceiling captured when the current session was initialized
        private static float _cookShiftHours;   // stable per-operation shift duration
        private static float _cookIntervalHours; // stable per-operation cadence
        private static float _nextSellScan;     // real-time seconds; throttle the passer-by scan
        private static bool _forceSale;         // F4: make a dealer sell now (bypass the interval + scan throttle)
        private static float _nextAutoStartTry; // real-time seconds; throttle the proactive auto-start retry
        private static float _stalledSchedulerLoggedForEndGT = -1f; // one fallback diagnostic per resolved operation
        private static float _lastOpEndGT;      // game hours when the last op ended; the next case waits to the next 12h grid point after it
        private static NewNode _targetNode;     // where the active mover is headed (for the distance readout)
        private static NewNode _labNode;        // cached stable node inside the lab (see LabNode)
        private static NewNode _cookPotter;     // the lab node the cook is currently pottering to (rotated, so they pace while cooking)
        private static float _cookNextPotter;   // real-time seconds; when to pick a new cook potter node
        // Trace resumed in-flight navigation without affecting inventories or world objects.
        private static bool _resumeCookTravelTrace;
        private static float _resumeCookTravelLogAfter;
        private static bool _labDosesPlaced;    // discoverable doses seeded in the lab yet (so the player finds a stash, not just 1)
        private static readonly List<Interactable> _labDiscoveryDoses = new List<Interactable>();   // extra lab doses for the player to find - NOT part of the pull chain (kept out of LabStash so cadence is unchanged)
        private static readonly List<SavedLabDiscoveryDose> _labDiscoveryOriginalPlaces = new List<SavedLabDiscoveryDose>();
        private static bool _labDiscoveryNeedsExactRestore;
        // The exact-placement pass restores Interactable metadata; after lab streaming,
        // native Unity visuals sometimes still need to be instantiated from that SAME object.
        private static bool _labDiscoveryVisualPassPending;

        // Overdose cadence: a buyer overdoses a few minutes after a purchase, triggering the enforcer arrest. The
        // FIRST overdose opens the player's case; later ones add a new "search" lead to the SAME case (serial-style,
        // since there is only ever one cook/drug at a time). Rate-limited to one every OverdoseIntervalHours.
        private static int _odBuyerId = -1;      // humanID of the buyer currently scheduled to overdose; -1 = none pending
        private static float _odAtHours;         // game hours at which the scheduled overdose fires
        private static float _odNextEligible;    // game hours; the earliest the NEXT overdose may be scheduled (interval gate)
        private static readonly List<int> _odQueue = new List<int>();   // every buyer awaiting their overdose (FIFO), drained >= OverdoseIntervalHours apart

        // Each buyer's dealer, so we can keep the buyer's memory of that dealer alive. SoD evicts old sightings
        // (Human.sightingMemoryLimit) as the buyer sees new people, so a one-time record is forgotten over time -
        // we re-assert it periodically so interrogation ("seen anything unusual") keeps surfacing the dealer.
        private static readonly Dictionary<int, Human> _buyerDealer = new Dictionary<int, Human>();
        // Each buyer's DEAL NODE (the on-camera spot where they bought), so reinforcement re-anchors the sighting there
        // instead of letting it drift to the dealer's live position. This is the location the witness reports.
        private static readonly Dictionary<int, NewNode> _buyerSpot = new Dictionary<int, NewNode>();

        // The dealer who sold to a given buyer (humanID). Used to wire the resolution form's "who is dealing" answer.
        internal static Human DealerFor(int buyerId)
        {
            try { if (_buyerDealer.TryGetValue(buyerId, out var h)) return h; } catch { }
            return null;
        }
        private static float _nextSightingReinforce;   // real-time seconds; throttle the re-assert

        private static readonly System.Random _rng = new System.Random();

        private const float RangeM = 2.0f;
        private const float JobTimeoutSec = 200f;

        // ------------------------------------------------------------------ lifecycle

        internal static bool Start()
        {
            try
            {
                if (Active) { Log("an operation is already active; stop it first."); return false; }
                if (InvestigationPersistence.Loading || InvestigationPersistence.HasUnresolvedCase())
                {
                    Log("saved narcotics investigation exists or is loading; refusing to replace its operation.");
                    return false;
                }
                Human cook = PickCook();
                if (cook == null) { Log("could not find a suitable cook."); return false; }
                NewAddress lab = LabSiting.PickLab(cook, out _);
                if (lab == null) { Log($"could not site a lab for {NarcoticsPlugin.Name(cook)} (no vacant unit?)."); return false; }
                // Flavour: pick this operation's drug street-name + case headline (e.g. "Velvet" -> "Velvet on the
                // Street"). Create a FRESH preset with a UNIQUE key each op (not a rename of a shared one): renaming the
                // shared evidence.names entry didn't reliably update, so case 2's doses showed case 1's name while the
                // case title (which reads the live DrugName) was correct. A unique preset per op guarantees each case's
                // doses carry that case's name; leftover doses from a prior case keep their own (correct) name.
                DrugName = NarcNames.PickDrug();
                CaseTitle = NarcNames.CaseTitle(DrugName);
                DrugItem.CreateDrug("NarcDrug_" + System.Guid.NewGuid().ToString("N").Substring(0, 8), DrugName);
                Log($"operation drug = '{DrugName}'; case title = '{CaseTitle}'.");

                CookHuman = cook; Lab = lab; Active = true;
                _labDressed = false; _nextDressTry = 0f; _labDressTries = 0; _labFurniture.Clear(); _labDosesPlaced = false; _labDiscoveryDoses.Clear(); _labDiscoveryOriginalPlaces.Clear(); _labDiscoveryNeedsExactRestore = false; _labDiscoveryVisualPassPending = false; _cookPotter = null; _cookNextPotter = 0f;   // fresh lab dressing/doses/cook-potter for this op
                ResetJob();
                _opStartGT = Herd.NowHours();      // case init: re-cook sessions anchor to this (opStart + N*interval)
                _cookIntervalHours = Math.Max(0.1f, NarcoticsConfig.CookIntervalHours);
                _cookShiftHours = Math.Max(0.1f, NarcoticsConfig.CookShiftHours);
                _cookBatchLimit = Math.Max(1, NarcoticsConfig.CookBatchesPerSession);
                _nextCookTime = Herd.NowHours();   // cook heads to the lab right away on the first day
                LabStash.Clear();

                var used = new HashSet<int>(); try { used.Add(cook.humanID); } catch { }
                Distributors.Clear();
                foreach (var h in SelectCitizens(1, used)) Distributors.Add(new Dist { H = h });   // always exactly one
                Dealers.Clear();
                int deal = Math.Max(1, NarcoticsConfig.DealerCount);
                // Dead drop = a SEARCHABLE CONTAINER that a security camera genuinely films (so the distributor + dealer
                // handoffs land on CCTV for the player to follow). ANY searchable container works - street bin, postbox,
                // etc. - the type doesn't matter, only that the player can rummage it (Dose.CanDeposit) and a human at it
                // is actually recorded. Rank by how CENTRED in a camera's frame the bin sits (CameraFrameScore: smaller
                // = closer to the camera eye = reliably on film), NOT just "within range" - an edge-of-range bin reads as
                // covered but never makes footage. Off-camera bins are a last-resort fallback. No loose ground drops.
                var containers = Containers.SiteContainersNearLab(lab, Math.Max(deal * 6, 24));
                var picks = Containers.PickNearestHomeless(lab, deal, used);
                if (picks.Count == 0) picks = SelectHomeless(deal, used);   // fallback if proximity pick found none

                try { Log($"[cctv] humanCaptureRange={GameplayControls.Instance.humanCaptureRange:0.0}m captureRange={GameplayControls.Instance.captureRange:0.0}m (a bin must be within humanCaptureRange of a camera eye to film a human at it)."); } catch { }

                // Rank by DISTANCE TO THE LAB (nearest first), gated by CCTV framing - the user wants the NEAREST bin that
                // is genuinely in a camera's frame, so the drop tracks the lab (a different lab = a different nearest bin,
                // not the same globally-best-framed bin every time). The pool from SiteContainersNearLab is already sorted
                // nearest-lab-first, so the pool INDEX is the distance order. Tiers: well-framed-on-camera (0) <
                // marginally-on-camera (1) < off-camera (2); nearest-first within each tier.
                const float WellFramed = 45f;   // off-axis degrees (+ far-cusp penalty) under which a bin films reliably
                var frame = new float[containers.Count];      // frame score (lower = more centred); -1 = off camera
                var frameAng = new float[containers.Count];    // off-axis degrees, for the log
                var frameEye = new float[containers.Count];    // eye distance, for the log
                var order = new List<int>();
                for (int i = 0; i < containers.Count; i++)
                {
                    var fl = containers[i];
                    NewNode bn = fl != null ? Containers.ContainerNode(fl) : null;
                    IntPtr _c; float ang = -1f, eye = -1f;
                    frame[i] = (fl != null && bn != null) ? CameraFrameScore(bn, IntPtr.Zero, out _c, out ang, out eye) : -1f;
                    frameAng[i] = (frame[i] >= 0f) ? ang : -1f; frameEye[i] = (frame[i] >= 0f) ? eye : -1f;
                    order.Add(i);
                }
                order.Sort((a, b) =>
                {
                    int ta = frame[a] < 0f ? 2 : (frame[a] <= WellFramed ? 0 : 1);
                    int tb = frame[b] < 0f ? 2 : (frame[b] <= WellFramed ? 0 : 1);
                    if (ta != tb) return ta - tb;      // well-framed on-camera, then marginal, then off-camera
                    return a.CompareTo(b);             // NEAREST-to-lab first within a tier (pool is nearest-lab-sorted)
                });
                // Variety: shuffle among the NEAREST bins that are ON CAMERA AT ALL (well-framed OR marginal, frame >= 0),
                // not just the well-framed tier - so when a lab's single best-framed bin keeps winning, the other filmed
                // bins around it (the user saw 3 ignored bins next to the chosen one) get picked too. Still prefers on-camera
                // over off-camera (off-camera bins sort last and aren't in this front window). Up to 6 for a decent spread.
                int onCamFront = 0; for (; onCamFront < order.Count; onCamFront++) { if (frame[order[onCamFront]] < 0f) break; }
                int shuffN = Math.Min(6, onCamFront);
                for (int i = 0; i < shuffN; i++) { int j = _rng.Next(shuffN); int tmp = order[i]; order[i] = order[j]; order[j] = tmp; }
                var ranked = new List<FurnitureLocation>();
                int onCam = 0; for (int i = 0; i < order.Count; i++) { int idx = order[i]; if (containers[idx] == null) continue; ranked.Add(containers[idx]); if (frame[idx] >= 0f) onCam++; }
                Log($"dead-drop candidates: {onCam} on CCTV, {ranked.Count - onCam} off-camera (pool {containers.Count}).");
                for (int i = 0; i < order.Count && i < 8; i++) { int idx = order[i]; if (containers[idx] == null) continue; Log($"  bin '{BinName(containers[idx])}' {(frame[idx] >= 0f ? $"ON CAMERA off-axis={frameAng[idx]:0}deg eye={frameEye[idx]:0.0}m" : "off-camera")}."); }

                int bi = 0;
                for (int k = 0; k < picks.Count; k++)
                {
                    var dl = new Dealer { H = picks[k], DistIndex = 0 };
                    FurnitureLocation bin = null;
                    while (bi < ranked.Count)   // take the best-ranked candidate that is a real searchable container
                    {
                        var cand = ranked[bi++]; if (cand == null) continue;
                        if (!Dose.CanDeposit(cand)) { Log($"  reject dead drop '{BinName(cand)}' - not a searchable container."); continue; }
                        bin = cand; break;
                    }
                    if (bin != null)
                    {
                        dl.Container = bin;
                        dl.Drop = Containers.ContainerNode(bin);
                        try { dl.DropLoc = dl.Drop != null ? dl.Drop.gameLocation : null; } catch { }
                        IntPtr dropCam = IntPtr.Zero; float dAng = -1f, dEye = -1f;
                        float dScore = dl.Drop != null ? CameraFrameScore(dl.Drop, IntPtr.Zero, out dropCam, out dAng, out dEye) : -1f;
                        dl.DropCam = dropCam;   // remember the drop's camera so the deal can use a different one
                        bool trash = Containers.IsTrashBin(bin);
                        Log($"dealer {NarcoticsPlugin.Name(dl.H)} dead drop = '{BinName(bin)}' ({(trash ? "trash bin" : "container")}, {(dScore >= 0f ? $"on CCTV, off-axis={dAng:0}deg eye={dEye:0.0}m" : "OFF-CAMERA - no filmable bin near the lab")}).");
                    }
                    else
                    {
                        Log($"dealer {NarcoticsPlugin.Name(dl.H)} has NO searchable container near the lab; supply chain cannot stage a drop (check [sites] log).");
                    }
                    Dealers.Add(dl);
                }

                int withContainers = 0; for (int k = 0; k < Dealers.Count; k++) if (Dealers[k].Container != null) withContainers++;
                Log($"STARTED: cook={NarcoticsPlugin.Name(cook)} job='{JobName(cook)}' lab={Cook.LName(lab)}. Chain: cook -> 1 distributor -> {Dealers.Count} dealer(s) ({withContainers} with a real container drop). Cook cooks {NarcoticsConfig.CookStashCap}/session, {NarcoticsConfig.CarryTarget} per dealer.");
                // Probe (debug): if the go-goals have disableActionInsertions=true, that confirms WHY a continuously
                // pinned NPC never fires the vanilla tail reaction (the inserted spook action is refused). Informs
                // whether we could ever keep a hard pin AND reactions (only if these are false).
                if (NarcoticsConfig.EnableDebugKeys)
                {
                    try { var rc = RoutineControls.Instance; Log($"[probe] toGoWalk.disableActionInsertions={rc.toGoWalkGoal.disableActionInsertions} toGo.disableActionInsertions={rc.toGoGoal.disableActionInsertions}"); }
                    catch (Exception e) { Log($"[probe] disableActionInsertions read threw: {e.Message}"); }
                }
                // NOTE: we no longer suppress the vanilla murder loop here. Suppressing paused the scheduler and it
                // fired its backed-up murder the instant we restored it on resolve = a narcotics case immediately after
                // the last one. Instead the hijack CANCELS any murder that tries to spawn while an op is active (so no
                // parallel vanilla case), and the game keeps its own schedule - the next narcotics case then arrives at
                // the vanilla cadence. EnsureVanillaLoopOnLoad still repairs a persisted-off loop.
                return true;
            }
            catch (Exception e) { Log($"Start error: {e}"); return false; }
        }

        // Make the narcotics operation THE active case by stopping the vanilla proc-gen murder loop, so no separate
        // vanilla murder runs alongside ours. Decoded (see [[sod-narc-case-hijack]]): procGenLoopActive is the master
        // gate; with it off the loop schedules nothing. Restored in Stop(). We do NOT null currentMurderer/currentVictim
        // here (that happens on restore, off the Tick call path) to avoid a null-deref inside the game's own Tick.
        private static void SuppressVanillaMurderLoop()
        {
            try
            {
                var mc = MurderController.Instance;
                if (mc == null) return;
                // Diagnostic: is a vanilla murder/case ALREADY active in parallel? (activeMurders>0 => yes, and its
                // investigation/enforcers would run alongside ours = a likely source of "enforcers before any deal".
                // activeMurders==0 => the enforcers the player saw are ambient city crime, not us.)
                try
                {
                    int am = -1, im = -1; try { am = mc.activeMurders != null ? mc.activeMurders.Count : -1; } catch { } try { im = mc.inactiveMurders != null ? mc.inactiveMurders.Count : -1; } catch { }
                    string cac = "null"; try { var c = mc.currentActiveCase; if (c != null) cac = $"{c.name}/type={c.caseType}"; } catch { }
                    string cm = "null"; try { if (mc.currentMurderer != null) cm = NarcoticsPlugin.Name(mc.currentMurderer); } catch { }
                    string cv = "null"; try { if (mc.currentVictim != null) cv = NarcoticsPlugin.Name(mc.currentVictim); } catch { }
                    Log($"[murder-state] activeMurders={am} inactiveMurders={im} currentActiveCase={cac} currentMurderer={cm} currentVictim={cv} (activeMurders>0 = a vanilla case is running in parallel).");
                }
                catch { }
                try { mc.murderRoutineActive = false; } catch { }
                mc.SetProcGenKillerLoop(false);
                Log("vanilla murder loop suppressed (narcotics is the active case).");
            }
            catch (Exception e) { Log($"SuppressVanillaMurderLoop threw: {e.Message}"); }
        }

        // Re-enable the vanilla proc-gen murder loop when our op ends, and clear the stale picked pair so the loop
        // rolls a FRESH vanilla case next (PickNewMurderer overwrites these anyway, but clearing is tidy + safe here,
        // since Stop() runs off the game's Tick call path).
        private static void RestoreVanillaMurderLoop()
        {
            try
            {
                var mc = MurderController.Instance;
                if (mc == null) return;
                try { mc.currentMurderer = null; mc.currentVictim = null; mc.currentVictimSite = null; } catch { }
                try { mc.murderRoutineActive = false; } catch { }
                mc.SetProcGenKillerLoop(true);
                try { mc.SetUpdateEnabled(true); } catch { }
                Log("vanilla murder loop restored.");
            }
            catch (Exception e) { Log($"RestoreVanillaMurderLoop threw: {e.Message}"); }
        }

        // The next 12h GRID boundary (anchored to case init: opStart + N*CookIntervalHours) strictly after `afterHours`.
        // The cook's re-cook AND the next-case gap (InCaseGap) both land on this one grid, so the whole narcotics rhythm
        // is a single coherent 12h cadence: a cook event every 12h (the same cook while the case runs, a new cook + new
        // case once it is solved), and the quiet after a case is "whatever time is left in the current 12h cycle".
        private static float NextGridPoint(float afterHours)
        {
            float iv = Math.Max(0.1f, _cookIntervalHours > 0f ? _cookIntervalHours : NarcoticsConfig.CookIntervalHours);
            float next = _opStartGT + iv;
            for (int guard = 0; next <= afterHours && guard < 100000; guard++) next += iv;
            return next;
        }

        // Finish a DEFERRED furniture strip (queued in Stop) once the player is in the old lab (its geometry streamed),
        // so leftover equipment from a resolved case is removed when it actually comes into view. Runs every frame.
        internal static void TickPendingStrip()
        {
            try
            {
                if (_pendingStrip.Count == 0) return;
                if (_pendingStripLoc == null || !PlayerInLocation(_pendingStripLoc)) return;
                int rm = LabSiting.StripFurniture(_pendingStrip);
                _pendingStrip.Clear(); _pendingStripLoc = null;
                if (rm > 0) Log($"deferred strip: reverted {rm} leftover lab equipment cluster(s) now that the lab is in view.");
            }
            catch (Exception e) { Log($"TickPendingStrip error: {e.Message}"); }
        }

        internal static void Stop(bool preserveSolvedCaseEvidence = false)
        {
            try
            {
                RestoreVanillaMurderLoop();        // let the game resume its own cases once ours ends
                try { Arrest.Stop(); } catch { }   // release any cuffed suspect tied to this operation
                try { int rm = LabSiting.StripFurniture(_labFurniture); if (rm > 0) Log($"reverted {rm} lab equipment cluster(s) - the unit is a bare vacant shell again."); } catch { }
                // Queue the same clusters for a DEFERRED re-strip: DeleteCluster needs the lab streamed, which it usually
                // isn't at resolve (player at City Hall), so finish stripping when the player is next in that lab.
                try { _pendingStrip.Clear(); for (int i = 0; i < _labFurniture.Count; i++) { var fc = _labFurniture[i]; if (fc != null) _pendingStrip.Add(fc); } _pendingStripLoc = Lab; } catch { }
                _labFurniture.Clear();
                if (!Active) { Log("no active operation."); return; }
                _lastOpEndGT = Herd.NowHours();     // the next proactive auto-start waits ~CookIntervalHours after this
                if (_mover != null) Herd.Release(_mover);
                Herd.ReleaseAll();
                // The solved case keeps its evidence cards. Their image lookup depends on the source interactables, so
                // leave those objects in the save while still clearing all operation state in ClearState().
                if (!preserveSolvedCaseEvidence)
                {
                    Dose.DestroyAll();
                    DestroyWorldDoses();
                }
                Log($"STOPPED (cook {NarcoticsPlugin.Name(CookHuman)} + crew released, doses {(preserveSolvedCaseEvidence ? "retained for solved-case evidence" : "cleared")}).");
            }
            catch (Exception e) { Log($"Stop error: {e.Message}"); }
            finally { ClearState(); }
        }

        // Snapshot immutable identities, NOT engine objects or Il2Cpp pointers. Live stock/delivery
        // state is owned by the loaded game and deliberately not recreated by spawning new items.
        internal static SavedInvestigation CaptureIdentity()
        {
            try
            {
                if (!Active || CookHuman == null || Lab == null || string.IsNullOrEmpty(DrugItem.CurrentKey)) return null;
                int labIndex;
                if (!int.TryParse(LabDirIndex(), out labIndex)) return null;
                var record = new SavedInvestigation
                {
                    DrugKey = DrugItem.CurrentKey,
                    DrugName = DrugName,
                    CaseTitle = CaseTitle,
                    BasePresetName = DrugItem.BasePreset != null ? DrugItem.BasePreset.presetName : null,
                    CookId = CookHuman.humanID,
                    LabDirectoryIndex = labIndex,
                    LabName = Lab.name,
                    StartedAtGameHours = _opStartGT
                };
                for (int i = 0; i < Distributors.Count; i++)
                {
                    var dist = Distributors[i];
                    if (dist == null || dist.H == null) return null;
                    record.DistributorIds.Add(dist.H.humanID);
                }
                for (int i = 0; i < Dealers.Count; i++)
                {
                    var d = Dealers[i]; if (d == null || d.H == null || d.Container == null) return null;
                    var node = Containers.ContainerNode(d.Container);
                    if (node == null) return null;
                    var position = node.position;
                    record.Dealers.Add(new SavedDealerSite {
                        HumanId = d.H.humanID,
                        DistributorIndex = d.DistIndex,
                        FurnitureName = d.Container.furniture != null ? d.Container.furniture.name : null,
                        X = position.x, Y = position.y, Z = position.z
                    });
                }
                if (record.DistributorIds.Count == 0 || record.Dealers.Count == 0 ||
                    string.IsNullOrEmpty(record.BasePresetName)) return null;
                return record;
            }
            catch (Exception e) { Log($"CaptureIdentity failed: {e.Message}"); return null; }
        }

        private static Human ResolveSavedHuman(int humanId)
        {
            try
            {
                var cd = CityData.Instance;
                var citizens = cd != null ? cd.citizenDirectory : null;
                if (citizens == null) return null;
                for (int i = 0; i < citizens.Count; i++)
                {
                    var person = citizens[i];
                    if (person != null && person.humanID == humanId) return person;
                }
            }
            catch { }
            return null;
        }

        // Rebinds stable operation identities and saved world state without replaying in-flight jobs,
        // which could duplicate a deposit, overdose, or sale after loading.
        internal static bool RestoreIdentity(SavedInvestigation saved)
        {
            try
            {
                if (Active || saved == null || saved.DistributorIds == null || saved.Dealers == null ||
                    saved.DistributorIds.Count == 0 || saved.Dealers.Count == 0) return false;
                var city = CityData.Instance;
                var locations = city != null ? city.gameLocationDirectory : null;
                if (locations == null || saved.LabDirectoryIndex < 0 ||
                    saved.LabDirectoryIndex >= locations.Count) return false;
                var location = locations[saved.LabDirectoryIndex];
                var lab = location != null ? location.TryCast<NewAddress>() : null;
                if (lab == null || lab.name != saved.LabName) return false;
                var cook = ResolveSavedHuman(saved.CookId);
                if (cook == null) return false;

                var restoredDist = new List<Dist>();
                for (int i = 0; i < saved.DistributorIds.Count; i++)
                {
                    var h = ResolveSavedHuman(saved.DistributorIds[i]);
                    if (h == null) return false;
                    restoredDist.Add(new Dist { H = h });
                }

                // Locate the SAME physical containers, not randomly picked new ones. Saved world
                // positions + furniture preset names disambiguate nearby dumpsters of the same type.
                var candidates = Containers.SiteContainersNearLab(lab,
                    Math.Max(24, saved.Dealers.Count * 12));
                var restoredDealers = new List<Dealer>();
                var used = new HashSet<IntPtr>();
                for (int i = 0; i < saved.Dealers.Count; i++)
                {
                    var sd = saved.Dealers[i];
                    var h = ResolveSavedHuman(sd.HumanId);
                    if (h == null || sd.DistributorIndex < 0 || sd.DistributorIndex >= restoredDist.Count) return false;
                    FurnitureLocation bin = null;
                    NewNode drop = null;
                    float best = 0.25f; // <= 0.5m error in stable world-node coordinates
                    for (int k = 0; k < candidates.Count; k++)
                    {
                        var c = candidates[k]; if (c == null || used.Contains(c.Pointer)) continue;
                        var n = Containers.ContainerNode(c); if (n == null) continue;
                        string furniture = c.furniture != null ? c.furniture.name : null;
                        if (furniture != sd.FurnitureName) continue;
                        var p = n.position;
                        float dx = p.x - sd.X, dy = p.y - sd.Y, dz = p.z - sd.Z;
                        float dist2 = dx * dx + dy * dy + dz * dz;
                        if (dist2 < best) { best = dist2; bin = c; drop = n; }
                    }
                    if (bin == null || !Dose.CanDeposit(bin))
                    {
                        Log($"[resume] cannot uniquely reconnect {NarcoticsPlugin.Name(h)}'s original dead drop; will retry.");
                        return false;
                    }
                    used.Add(bin.Pointer);
                    var dealer = new Dealer { H = h, DistIndex = sd.DistributorIndex,
                        Container = bin, Drop = drop, DropLoc = drop.gameLocation,
                        NextSell = Herd.NowHours() + Math.Max(0.1f, NarcoticsConfig.DealerSellIntervalHours) };
                    IntPtr camera = IntPtr.Zero; float angle, eye;
                    CameraFrameScore(drop, IntPtr.Zero, out camera, out angle, out eye);
                    dealer.DropCam = camera;
                    restoredDealers.Add(dealer);
                }

                var presets = Toolbox.Instance != null ? Toolbox.Instance.objectPresetDictionary : null;
                InteractablePreset basePreset = null;
                if (presets == null || !presets.TryGetValue(saved.BasePresetName, out basePreset) || basePreset == null)
                    return false;
                var activeDrug = DrugItem.CreateDrug(saved.DrugKey, saved.DrugName, basePreset,
                    makeCurrent: true, persist: false);
                if (activeDrug == null) return false;

                // All required references validated above: only now commit the new runtime operation.
                CookHuman = cook;
                Lab = lab;
                DrugName = saved.DrugName;
                CaseTitle = saved.CaseTitle;
                Distributors.Clear(); Distributors.AddRange(restoredDist);
                Dealers.Clear(); Dealers.AddRange(restoredDealers);
                Buyers.Clear(); LabStash.Clear();
                ResetJob();
                _opStartGT = saved.StartedAtGameHours;
                if (_opStartGT <= 0f || _opStartGT > Herd.NowHours()) _opStartGT = Herd.NowHours();
                _cookIntervalHours = Math.Max(0.1f, NarcoticsConfig.CookIntervalHours);
                _cookShiftHours = Math.Max(0.1f, NarcoticsConfig.CookShiftHours);
                _cookBatchLimit = Math.Max(1, NarcoticsConfig.CookBatchesPerSession);
                _nextCookTime = NextGridPoint(Herd.NowHours()); // legacy fallback until a matching per-save snapshot is found
                _cookActive = false; _cookSessionStart = -1f; _cookBatchesLeft = 0;
                _odBuyerId = -1; _odQueue.Clear(); _odNextEligible = Herd.NowHours();
                _labDressed = true;        // avoid creating duplicate lab equipment
                _labDosesPlaced = true;    // legacy default: never duplicate evidence on an old save
                _labDiscoveryDoses.Clear();
                _labDiscoveryOriginalPlaces.Clear();
                _labDiscoveryNeedsExactRestore = false;
                _labDiscoveryVisualPassPending = false;
                // Rebuild only our managed index of existing active-drug items held by humans.
                // The game world is authoritative; never recreate doses to match a saved count.
                int reboundInventory = Dose.ReindexSavedInventory(saved.DrugKey);
                foreach (var dealer in Dealers)
                {
                    if (dealer != null && dealer.Container != null)
                        dealer.BinDoses = Dose.CountInBin(dealer.Container);
                }
                _lastOpEndGT = 0f;
                Active = true;
                var cookSnapshot = InvestigationPersistence.FindCookSession(saved, out var cookSkipReason, out var rejectedCookSnapshot);
                if (cookSnapshot == null)
                {
                    var rejected = rejectedCookSnapshot;
                    LogCookResumeSkipped(cookSkipReason ?? "matching snapshot unavailable", rejected != null ? rejected.CapturedAtGameHours : Herd.NowHours(), rejected != null && rejected.CookActive, rejected != null ? rejected.CookSessionStartGameHours : -1f, rejected != null ? rejected.CookBatchesLeft : 0, rejected != null ? rejected.CookShiftHours : 0f, rejected != null ? rejected.NextCookAtGameHours : -1f);
                }
                else
                {
                    if (!RestoreCookSession(cookSnapshot, out cookSkipReason))
                        LogCookResumeSkipped(cookSkipReason ?? "snapshot validation failed", cookSnapshot.CapturedAtGameHours, cookSnapshot.CookActive, cookSnapshot.CookSessionStartGameHours, cookSnapshot.CookBatchesLeft, cookSnapshot.CookShiftHours, cookSnapshot.NextCookAtGameHours);
                    // Independently rebind saved-world references from the same validated save.
                    // This only fills managed lists. No interactable is created, moved or deleted.
                    RestoreWorldTracking(cookSnapshot);
                    RestoreCrewProgress(cookSnapshot);
                    RestoreLabDiscoveryTracking(cookSnapshot);
                    RestoreLabStage(cookSnapshot);
                    RestoreOverdoseTracking(cookSnapshot);
                }
                Log($"[resume] recovered {DrugName} (key={saved.DrugKey}), cook {NarcoticsPlugin.Name(cook)}, " +
                    $"{Distributors.Count} distributor(s), {Dealers.Count} original dead drop(s). " +
                    $"rebound {reboundInventory} held dose(s); bins resynced from saved pages. " +
                    "Transient jobs reset; saved cook and overdose state restored where available.");
                return true;
            }
            catch (Exception e) { Log($"RestoreIdentity failed: {e}"); return false; }
        }

        internal static void ResetOnLoad() { ClearState(); ResetFilmSpots(); _lastOpEndGT = 0f; _nextAutoStartTry = 0f; EnsureVanillaLoopOnLoad(); }

        // On every load/new-game: log the murder-loop state and, if the proc-gen loop is OFF (a prior op suppressed it
        // via SetProcGenKillerLoop(false) and the save persisted that), RE-ENABLE it. Otherwise the loop stays off, the
        // game never spawns a case, the hijack never fires, and narcotics never auto-starts - a deadlock, since our op
        // state was just cleared so nothing is around to restore it. We only flip procGenLoopActive (+ update enabled);
        // we do NOT touch currentMurderer/murderRoutineActive, so a legitimately-active vanilla murder is undisturbed.
        private static void EnsureVanillaLoopOnLoad()
        {
            try
            {
                var mc = MurderController.Instance;
                if (mc == null) { Log("[load] no MurderController yet."); return; }
                bool loop = true, routine = false; int am = -1;
                try { loop = mc.procGenLoopActive; } catch { }
                try { routine = mc.murderRoutineActive; } catch { }
                try { am = mc.activeMurders != null ? mc.activeMurders.Count : -1; } catch { }
                Log($"[load] murder loop: procGenLoopActive={loop} murderRoutineActive={routine} activeMurders={am} (HijackChance={NarcoticsConfig.HijackChance:0.##}).");
                if (!loop)
                {
                    try { mc.SetProcGenKillerLoop(true); } catch { }
                    try { mc.SetUpdateEnabled(true); } catch { }
                    Log("[load] proc-gen murder loop was OFF (leftover suppression) - RE-ENABLED so a case can spawn and the hijack can fire.");
                }
            }
            catch (Exception e) { Log($"EnsureVanillaLoopOnLoad error: {e.Message}"); }
        }

        private static void ClearState()
        {
            Active = false; CookHuman = null; Lab = null; _labNode = null;
            Distributors.Clear(); Dealers.Clear(); Buyers.Clear(); LabStash.Clear();
            _forceSale = false; _nextSellScan = 0f;
            _odBuyerId = -1; _odAtHours = 0f; _odNextEligible = 0f; _odQueue.Clear();
            _resumeCookTravelTrace = false; _resumeCookTravelLogAfter = 0f;
            _cookActive = false; _cookSessionStart = -1f; _cookBatchesLeft = 0; _cookBatchLimit = 0; _nextCookTime = 0f; _cookShiftHours = 0f; _cookIntervalHours = 0f; _labDressed = false; _labDressTries = 0; _labFurniture.Clear(); _labDosesPlaced = false; _labDiscoveryDoses.Clear(); _labDiscoveryOriginalPlaces.Clear(); _labDiscoveryNeedsExactRestore = false; _labDiscoveryVisualPassPending = false; _cookPotter = null; _cookNextPotter = 0f;
            _buyerDealer.Clear(); _buyerSpot.Clear(); _nextSightingReinforce = 0f;
            ResetJob();
            try { Herd.ReleaseAll(); } catch { }
        }

        private static void ResetJob() { _job = Job.None; _mover = null; _jobDist = null; _jobDealer = null; _jobBuyer = null; _meetNode = null; _dwellUntil = 0f; _dealDwellEndGT = 0f; }

        // ------------------------------------------------------------------ tick

        // Proactive auto-start (runs every frame, even when no op is active). When HijackChance is effectively 1.0
        // (narcotics should ALWAYS be the active case), start an op shortly after a game is in play rather than waiting
        // for the game to spawn a proc-gen murder for the hijack to take over (which may never happen on a given run).
        // For HijackChance < 1 we do NOT proactively start - the hijack replaces that FRACTION of real vanilla cases.
        // Retries every 10s until Start() succeeds (the city must be ready: citizens + a vacant unit). After an op ends,
        // the next auto-start waits ~CookIntervalHours so cases don't run back-to-back.
        // The game's OWN murder interval (MurderController.pauseBetweenMurders), used as the gap between narcotics cases
        // so they space like vanilla murders. Clamped to a sane game-hours range in case the raw unit differs; the raw
        // value is logged once so it can be calibrated.
        private static bool _loggedMurderInterval;
        internal static float GameMurderIntervalHours()
        {
            float v = 24f;
            try { var mc = MurderController.Instance; if (mc != null) v = mc.pauseBetweenMurders; } catch { }
            if (!_loggedMurderInterval) { _loggedMurderInterval = true; Log($"[cadence] game pauseBetweenMurders raw={v:0.##}; using it (clamped 8-96) as the inter-case gap in game-hours."); }
            if (v < 8f) v = 8f; else if (v > 96f) v = 96f;
            return v;
        }

        // True while we're still before the next 12h GRID point since the last case ended - the hijack won't start a new
        // narcotics case yet, so the next case lands on the 12h cook rhythm (a cook event every CookIntervalHours: the
        // same cook continues while a case runs, a new cook + new case once it is solved). "Whatever time is left in the
        // current 12h cycle", not a fresh 12h from the solve moment. Never applies before the first case.
        internal static bool InCaseGap()
        {
            try { return _lastOpEndGT > 0.01f && Herd.NowHours() < NextGridPoint(_lastOpEndGT); }
            catch { return false; }
        }

        internal static void TickAutoStart()
        {
            try
            {
                if (Active || !NarcoticsConfig.Enable) return;
                if (InvestigationPersistence.Loading || InvestigationPersistence.HasUnresolvedCase()) return;
                if (NarcoticsConfig.HijackChance < 0.999f) return;   // share mode -> rely on the hijack, not a proactive start
                if (Time.time < _nextAutoStartTry) return;
                _nextAutoStartTry = Time.time + 10f;                 // retry cadence until the city is ready + Start succeeds
                var sd = SessionData.Instance; if (sd == null) return;   // no game loaded yet
                bool playing = true; try { playing = sd.play; } catch { }
                if (!playing) return;                                // paused / still on the load screen
                // Only BOOTSTRAP proactively. The first case needs it (on a fresh game the vanilla murder loop can be
                // off, so the hijack never fires). AFTER a case has run, we restored the vanilla loop in Stop(), so the
                // game schedules its next murder at its OWN cadence and the hijack turns THAT into a narcotics case -
                // don't proactively restart, or cases run back-to-back. Fall through to a proactive restart (after a
                // breather) only if the loop is somehow still off = a safety net so the chain never dies.
                if (_lastOpEndGT > 0.01f)
                {
                    if (InCaseGap()) return;   // still within the game's murder interval since the last case
                    bool loopOn = false; try { var mc = MurderController.Instance; loopOn = mc != null && mc.procGenLoopActive; } catch { }
                    if (loopOn)
                    {
                        // Normally the next ExecuteNewMurder callback replaces the vanilla case. Some scheduler mods
                        // can leave the loop enabled but never issue that callback (the observed ConfigurableMurderCooldown
                        // case). After one expected murder interval plus a short grace, recover only if the game has no
                        // active murder left. This avoids leaving narcotics-only runs waiting forever while preserving
                        // a legitimate vanilla case if one is already underway.
                        float now = Herd.NowHours();
                        float fallbackAt = Math.Max(NextGridPoint(_lastOpEndGT), _lastOpEndGT + GameMurderIntervalHours() + 6f);
                        if (now < fallbackAt) return;
                        int activeMurders = -1;
                        try { var mc = MurderController.Instance; activeMurders = mc != null && mc.activeMurders != null ? mc.activeMurders.Count : -1; } catch { }
                        if (activeMurders != 0) return;
                        if (Math.Abs(_stalledSchedulerLoggedForEndGT - _lastOpEndGT) > 0.01f)
                        {
                            _stalledSchedulerLoggedForEndGT = _lastOpEndGT;
                            Log($"[auto] vanilla murder callback stalled; no active murders {now - _lastOpEndGT:0.0}h after case end (fallback threshold {fallbackAt - _lastOpEndGT:0.0}h). Starting the next narcotics case.");
                        }
                    }
                    // Loop off, or its callback stayed absent past the bounded fallback: start/retry below.
                }
                if (Start()) Log("[auto] narcotics auto-started (bootstrap; subsequent cases follow the vanilla murder cadence via the hijack).");
            }
            catch (Exception e) { Log($"TickAutoStart error: {e.Message}"); }
        }

        internal static void Tick()
        {
            if (!Active || CookHuman == null || Lab == null) return;
            try
            {
                TickOverdose();                        // fire a scheduled buyer overdose -> arrest -> case
                TickDressLab();                        // place lab equipment once the lab geometry streams (player visits)
                RestoreExactLabDiscoveryPlacements();  // reconnect original furniture slots, NEVER choose a new spot
                RestoreLabDiscoveryVisuals();            // native visual for the SAME saved Interactable, at exact saved transform
                TickLabDoses();                        // seed a few discoverable doses in the lab (evidence, not supply)
                TickReinforceSightings();              // keep buyers' dealer sightings fresh so interrogation works
                if (TickCaseResolution()) return;      // player solved the case -> stand down + wipe this tick
                TickCook();                            // passive: cook stays in the lab making both batches
                if (_job == Job.None) PickJob();
                if (_job != Job.None) RunJob();
                // When idle (between the collect + sell jobs) the dealer is left FULLY FREE - no pin, no forced spot - so
                // it runs its own vanilla homeless routine (potters, leans, sits, reacts to tails) until the next job.
            }
            catch (Exception e) { Log($"Tick error: {e.Message}"); }
        }

        // ---- BAKED CCTV "film spots" (perf) -----------------------------------------------------------------------
        // Every (covered node, camera) pair where a human on the node is genuinely recorded: the node is a key in the
        // camera's coveredNodes AND within humanCaptureRange of its eye (cvp). Cameras + coveredNodes are STATIC (baked
        // at city-gen), so we compute this ONCE and reuse it, instead of re-enumerating the Il2Cpp coveredNodes
        // dictionaries on every buyer-selection - that nested interop enumeration per candidate buyer was the per-sale
        // lag spike the user saw. Rebuilt on load. Decode: [[sod-narc-npc-apis]].
        private struct FilmSpot { public NewNode Node; public Vector3 Pos; public IntPtr Cam; public float Eye; public float OffAxis; public bool Outside; }
        private static readonly List<FilmSpot> _filmSpots = new List<FilmSpot>();
        private static readonly System.Collections.Generic.Dictionary<IntPtr, System.Collections.Generic.List<int>> _spotsByNode = new System.Collections.Generic.Dictionary<IntPtr, System.Collections.Generic.List<int>>();
        private static bool _filmSpotsBuilt;
        private static float _filmHcr;

        internal static void ResetFilmSpots() { _filmSpots.Clear(); _spotsByNode.Clear(); _filmSpotsBuilt = false; }

        // One frame score for a spot: off-axis angle (centre-ness) + a penalty for sitting at the far cusp of the range.
        private static float SpotScore(float eye, float offAxis) => offAxis + (eye > 0.8f * _filmHcr ? 90f : 0f);

        // Bake the film spots once (lazy, on first use). Leaves _filmSpotsBuilt false (retries) if nothing is available
        // yet (range unreadable / no cameras streamed), so a later call rebuilds when the data is there.
        private static void EnsureFilmSpots()
        {
            if (_filmSpotsBuilt) return;
            try
            {
                float hcr = 0f; try { hcr = GameplayControls.Instance.humanCaptureRange; } catch { }
                if (hcr <= 0.1f) return;
                float hcrSq = hcr * hcr;
                var cd = CityData.Instance; var dir = cd != null ? cd.surveillanceDirectory : null;
                if (dir == null) return;
                _filmSpots.Clear(); _spotsByNode.Clear(); _filmHcr = hcr;
                for (int i = 0; i < dir.Count; i++)
                {
                    SceneRecorder rec = null; try { rec = dir[i]; } catch { }
                    if (rec == null) continue;
                    Interactable cam = null; try { cam = rec.interactable; } catch { }
                    if (cam == null) continue;
                    IntPtr cp; try { cp = cam.Pointer; } catch { cp = IntPtr.Zero; }
                    bool camOutside = true; try { var cnode = cam.node; if (cnode != null) camOutside = cnode.isOutside; } catch { }   // STREET (outside) vs BUILDING (inside) camera
                    Vector3 eye; try { eye = cam.cvp; } catch { continue; }
                    Vector3 aim; try { aim = cam.cve; } catch { aim = default; }   // forward from aim Euler (ZXY: fwd = (cosP*sinY, -sinP, cosP*cosY))
                    double pitch = aim.x * Math.PI / 180.0, yaw = aim.y * Math.PI / 180.0;
                    double fx = Math.Cos(pitch) * Math.Sin(yaw), fy = -Math.Sin(pitch), fz = Math.Cos(pitch) * Math.Cos(yaw);
                    var cn = rec.coveredNodes;
                    if (cn == null || cn.Count == 0) { try { rec.RefreshCoveredArea(); cn = rec.coveredNodes; } catch { } }
                    if (cn == null) continue;
                    try
                    {
                        foreach (var kv in cn)
                        {
                            var n = kv.Key; if (n == null) continue;
                            bool outside = false; try { outside = n.isOutside; } catch { }
                            if (outside != camOutside) continue;   // a STREET camera must film a street node, a BUILDING camera an indoor node - drops "filmed through a glass door" cross-environment spots
                            Vector3 np; try { np = n.position; } catch { continue; }
                            float ex = np.x - eye.x, ey = np.y - eye.y, ez = np.z - eye.z; float eyeSq = ex * ex + ey * ey + ez * ez;
                            if (eyeSq > hcrSq) continue;   // beyond humanCaptureRange -> not recorded
                            float d = (float)Math.Sqrt(eyeSq); double inv = d > 0.0001 ? 1.0 / d : 0.0;
                            double dot = (ex * inv) * fx + (ey * inv) * fy + (ez * inv) * fz; if (dot > 1.0) dot = 1.0; else if (dot < -1.0) dot = -1.0;
                            float off = (float)(Math.Acos(dot) * 180.0 / Math.PI);
                            IntPtr nptr; try { nptr = n.Pointer; } catch { continue; }
                            int idx = _filmSpots.Count;
                            _filmSpots.Add(new FilmSpot { Node = n, Pos = np, Cam = cp, Eye = d, OffAxis = off, Outside = outside });
                            System.Collections.Generic.List<int> lst; if (!_spotsByNode.TryGetValue(nptr, out lst)) { lst = new System.Collections.Generic.List<int>(); _spotsByNode[nptr] = lst; } lst.Add(idx);
                        }
                    }
                    catch { }
                }
                if (_filmSpots.Count > 0) { _filmSpotsBuilt = true; Log($"[cctv] baked {_filmSpots.Count} film spot(s) over {_spotsByNode.Count} node(s) (cached; cameras are static)."); }
            }
            catch (Exception e) { Log($"EnsureFilmSpots error: {e.Message}"); }
        }

        // Among the baked film spots within [minM,maxM] of `near`, the one MOST CENTRED in a camera's frame (lowest
        // frame score), skipping `excludeCam` and (if outsideOnly) interior nodes. Null if none. Cheap: a flat C# scan.
        private static NewNode NearestCameraVisibleNode(Vector3 near, float minM, float maxM, IntPtr excludeCam = default, bool outsideOnly = false)
        {
            EnsureFilmSpots();
            NewNode best = null; float bestScore = float.MaxValue; float maxSq = maxM * maxM; float minSq = minM * minM;
            try
            {
                for (int i = 0; i < _filmSpots.Count; i++)
                {
                    var s = _filmSpots[i];
                    if (excludeCam != IntPtr.Zero && s.Cam == excludeCam) continue;   // the deal must be a DIFFERENT camera than the drop
                    if (outsideOnly && !s.Outside) continue;                          // street-level deals only
                    float nx = s.Pos.x - near.x, ny = s.Pos.y - near.y, nz = s.Pos.z - near.z; float nearSq = nx * nx + ny * ny + nz * nz;
                    if (nearSq < minSq || nearSq > maxSq) continue;
                    float score = SpotScore(s.Eye, s.OffAxis);
                    if (score < bestScore) { bestScore = score; best = s.Node; }
                }
            }
            catch (Exception e) { Log($"NearestCameraVisibleNode error: {e.Message}"); }
            return best;
        }

        private static float CameraFrameScore(NewNode node) { IntPtr cam; float a, e; return CameraFrameScore(node, IntPtr.Zero, out cam, out a, out e); }

        // The best (lowest) frame score among cameras that film `node` - off-axis angle + far-cusp penalty; lower = more
        // in the MIDDLE of the frame. Outputs the chosen camera, off-axis angle and eye distance; -1f if no camera films
        // it. `excludeCam` skips one camera (so the deal uses a DIFFERENT one than the drop). O(1)-ish via the node index.
        private static float CameraFrameScore(NewNode node, IntPtr excludeCam, out IntPtr camPtr, out float offAxisDeg, out float eyeDist)
        {
            camPtr = IntPtr.Zero; offAxisDeg = 999f; eyeDist = -1f;
            if (node == null) return -1f;
            EnsureFilmSpots();
            float best = -1f;
            try
            {
                IntPtr want; try { want = node.Pointer; } catch { return -1f; }
                System.Collections.Generic.List<int> idxs; if (!_spotsByNode.TryGetValue(want, out idxs) || idxs == null) return -1f;
                for (int k = 0; k < idxs.Count; k++)
                {
                    var s = _filmSpots[idxs[k]];
                    if (excludeCam != IntPtr.Zero && s.Cam == excludeCam) continue;
                    float score = SpotScore(s.Eye, s.OffAxis);
                    if (best < 0f || score < best) { best = score; camPtr = s.Cam; offAxisDeg = s.OffAxis; eyeDist = s.Eye; }
                }
            }
            catch (Exception e) { Log($"CameraFrameScore error: {e.Message}"); }
            return best;
        }

        // True if some camera genuinely films a human standing on `node` (covered + within humanCaptureRange of cvp).
        private static bool IsNodeCameraVisible(NewNode node) => CameraFrameScore(node) >= 0f;

        private static string BinName(FurnitureLocation fl)
        {
            try { return fl != null && fl.furniture != null ? fl.furniture.name : "?"; } catch { return "?"; }
        }

        // A node for the BUYER during a deal: a walkable neighbour of the meet node, PREFERRING one that is filmed by
        // the SAME camera as the meet node (`meetCam`), so the dealer and buyer are both on that one camera's feed (not
        // split across two). Falls back to a neighbour on any camera, then any walkable neighbour, then the meet node.
        private static NewNode BuyerNodeNear(NewNode meet, IntPtr meetCam)
        {
            if (meet == null) return meet;
            NewNode sameCam = null, anyCam = null, walk = null;
            try
            {
                IntPtr meetPtr; try { meetPtr = meet.Pointer; } catch { meetPtr = IntPtr.Zero; }
                var acc = meet.accessToOtherNodes; if (acc == null) return meet;
                foreach (var kv in acc)
                {
                    var nb = kv.Key; var a = kv.Value; if (nb == null || a == null) continue;
                    bool w = false; try { w = a.walkingAccess; } catch { }
                    if (!w) continue;
                    bool bad = false; try { bad = nb.isObstacle || nb.isInaccessable || nb.noAccess || !nb.isConnected; } catch { }
                    if (bad) continue;
                    IntPtr p; try { p = nb.Pointer; } catch { continue; }
                    if (p == meetPtr) continue;
                    if (walk == null) walk = nb;
                    System.Collections.Generic.List<int> idxs;
                    if (_spotsByNode.TryGetValue(p, out idxs) && idxs != null)
                    {
                        if (anyCam == null) anyCam = nb;
                        if (meetCam != IntPtr.Zero) { for (int j = 0; j < idxs.Count; j++) { if (_filmSpots[idxs[j]].Cam == meetCam) { sameCam = nb; break; } } }
                        if (sameCam != null) break;
                    }
                }
            }
            catch { }
            return sameCam ?? anyCam ?? walk ?? meet;
        }

        // A DIFFERENT on-camera node within `maxM` of `center`, filmed by `cam` (or any camera if cam is Zero), chosen
        // at random for variety - used to shuffle a deal around the camera's view so the pair don't block one spot.
        private static NewNode NearbySameCamSpot(NewNode center, IntPtr cam, float maxM)
        {
            if (center == null) return null;
            try
            {
                Vector3 c; try { c = center.position; } catch { return null; }
                IntPtr cp; try { cp = center.Pointer; } catch { cp = IntPtr.Zero; }
                float maxSq = maxM * maxM;
                var cand = new List<NewNode>();
                for (int i = 0; i < _filmSpots.Count; i++)
                {
                    var s = _filmSpots[i];
                    if (cam != IntPtr.Zero && s.Cam != cam) continue;
                    float dx = s.Pos.x - c.x, dy = s.Pos.y - c.y, dz = s.Pos.z - c.z; if (dx * dx + dy * dy + dz * dz > maxSq) continue;
                    IntPtr p; try { p = s.Node != null ? s.Node.Pointer : IntPtr.Zero; } catch { continue; }
                    if (p == cp || p == IntPtr.Zero) continue;
                    cand.Add(s.Node);
                }
                if (cand.Count == 0) return null;
                return cand[_rng.Next(cand.Count)];
            }
            catch { return null; }
        }

        // Dress the lab with equipment (Machinery/workbench/science clusters) as soon as its geometry is streamed. The
        // furniture can only be placed when the lab's rooms are loaded, which happens when the PLAYER is near the lab,
        // so we retry on a throttle until a placement succeeds (then stop). This is what makes the lab visibly read as
        // a drug lab when the player tails the cook to it - no F12 needed.
        private static void TickDressLab()
        {
            if (_labDressed || Lab == null) return;
            // Only attempt while the PLAYER is in the lab - the room's furniture-placement geometry (and the cluster
            // solver) only works when the player is physically there (that's why PageDown worked but the remote retries
            // didn't). This also stops the away-from-lab log spam entirely.
            if (!PlayerInLab()) return;
            if (Time.time < _nextDressTry) return;
            _nextDressTry = Time.time + 2f;
            try
            {
                var placed = LabSiting.DressLab(Lab);
                if (placed.Count > 0) { _labFurniture.AddRange(placed); _labDressed = true; Log($"lab dressed with {placed.Count} equipment cluster(s)."); return; }
                if (++_labDressTries >= 20) { _labDressed = true; Log("gave up dressing the lab after 20 in-lab attempts (no room could host a cluster)."); }
            }
            catch (Exception e) { Log($"TickDressLab error: {e.Message}"); }
        }

        private const int LabDiscoveryDoses = 4;   // extra doses scattered in the lab for the player to find

        // Seed a few searchable doses around the lab so the player discovers a STASH, not just the single in-flight dose
        // the pull chain allows. These are deliberately NOT added to LabStash, so the distributor never ferries them and
        // the supply cadence is unchanged - they're pure evidence (cook's fingerprint, not theft). Placed once the lab
        // geometry is streamed (player present); cleaned up with the rest on resolve.
        private static void TickLabDoses()
        {
            if (_labDosesPlaced || Lab == null || CookHuman == null) return;
            if (!PlayerInLab()) return;
            try
            {
                int placed = 0;
                for (int i = 0; i < LabDiscoveryDoses; i++)
                {
                    NewNode at = LabPotterNode(LabNode(CookHuman));
                    var h = Dose.Place(Lab, at, CookHuman);
                    if (h != null) { _labDiscoveryDoses.Add(h); placed++; }
                }
                _labDosesPlaced = true;
                if (placed > 0) Log($"seeded {placed} discoverable dose(s) around the lab (evidence; not part of the supply chain).");
            }
            catch (Exception e) { Log($"TickLabDoses error: {e.Message}"); }
        }

        // Arm the vanilla "close doors behind me when I leave this location" behaviour for an op NPC + the lab, so the
        // cook/distributor shut the lab door on their way out like any normal citizen (instead of us force-closing it).
        private static void ArmLabDoorClose(Human h)
        {
            try { if (h != null && Lab != null) { var ai = h.ai; if (ai != null) ai.CloseDoorsNormallyAfterLeavingGamelocation(Lab); } } catch { }
        }

        // True when the player is inside the lab location (its geometry is streamed, so furniture placement can work).
        private static bool PlayerInLab() { return PlayerInLocation(Lab); }

        // True when the player is inside `loc` (its geometry is streamed). Used for lab dressing + the deferred strip.
        private static bool PlayerInLocation(NewGameLocation loc)
        {
            try
            {
                var p = Player.Instance; if (p == null || loc == null) return false;
                NewGameLocation gl = null; try { gl = p.currentGameLocation; } catch { }
                if (gl == null) { try { var n = p.currentNode; if (n != null) gl = n.gameLocation; } catch { } }
                if (gl == null) return false;
                try { return gl.Pointer == loc.Pointer; } catch { return false; }
            }
            catch { return false; }
        }

        // The cook's DEMAND-PULL lab day (runs every tick, independent of the single-mover job system). The cook walks to
        // the lab and STAYS there, cooking one dose at a time: a dose is made ONLY when the whole pipeline is DRY
        // (PipelineDry: nothing in the lab / with the distributor / in the drop / on the dealer - i.e. the dealer ran
        // out). So the chain is cook 1 -> distributor ferries -> dealer sells -> (dry) -> cook 2 -> ... up to
        // CookBatchesPerSession doses, then the cook goes home for the day (next day scheduled CookIntervalHours out).
        private static void TickCook()
        {
            try
            {
                if (CookHuman == null || Lab == null) return;
                // Do not re-pin a cook already in an arrest flow.
                try { if (Arrest.IsArrestee(CookHuman.humanID)) { TraceResumedCookTravel("cook-in-arrest", null); Herd.Release(CookHuman); return; } } catch { }
                // End an expired restored shift before navigation or batch production.
                if (FinishCookShiftIfExpired(Herd.NowHours())) return;
                // If the player has ARRESTED the cook (restrained), stop driving them to the lab - they shouldn't keep
                // trying to cook while cuffed. Release the pin and let the arrest play out.
                try { var cai = CookHuman.ai; if (cai != null && cai.restrained) { TraceResumedCookTravel("cook-restrained", null); try { Herd.Release(CookHuman); } catch { } return; } } catch { }
                if (!_cookActive)
                {
                    if (Herd.NowHours() < _nextCookTime) return;   // between shifts - the cook lives its normal life
                    _cookActive = true;
                    _cookSessionStart = -1f;   // the shift clock starts when the cook ARRIVES at the lab (set below), NOT at dispatch, so the walk over doesn't eat into the stakeout window
                    _cookShiftHours = Math.Max(0.1f, NarcoticsConfig.CookShiftHours);
                    _cookBatchLimit = Math.Max(1, NarcoticsConfig.CookBatchesPerSession);
                    _cookBatchesLeft = _cookBatchLimit;   // CEILING for this shift (not a requirement)
                    Log($"cook heading to the lab for a {_cookShiftHours:0.#}h shift: up to {_cookBatchesLeft} dose(s) ON DEMAND (made only when the dealer runs dry); the shift clock starts when they arrive.");
                }

                // If the cook is reacting to a tail (confront/flee), GET OUT OF THE WAY: release the lab pin so the vanilla
                // reaction plays cleanly, instead of the pin fighting it (that caused the cook to repeat the warning bark
                // on the spot). It re-pins and walks back to the lab once calm; batch timing is game-hours based, so a
                // brief interruption is harmless.
                if (Herd.IsReacting(CookHuman)) { TraceResumedCookTravel("cook-reacting", null); try { Herd.Release(CookHuman); } catch { } return; }

                var node = LabNode(CookHuman);
                if (node == null)
                {
                    TraceResumedCookTravel("lab-node-unavailable", null);
                    return; // never teleport a cook or guess a substitute location
                }
                // While the cook is en route, the target must remain STABLE. Rotating
                // _cookPotter every 6-12 real seconds previously replaced the AI goal
                // before doors/pathfinding could complete. After first arrival, the
                // ordinary in-lab pottering cadence resumes.
                if (_cookPotter == null || (_cookSessionStart >= 0f && Time.time >= _cookNextPotter))
                {
                    _cookPotter = LabPotterNode(node) ?? node;
                    _cookNextPotter = Time.time + 6f + (float)_rng.NextDouble() * 6f;
                }
                _targetNode = _cookPotter;
                ArmLabDoorClose(CookHuman);                                    // vanilla: cook shuts the lab door on their way out
                if (!Herd.ToNode(CookHuman, _cookPotter, Lab, 1.2f))
                {
                    TraceResumedCookTravel("en-route", _cookPotter);
                    return;
                }
                try { Anim.Locking(CookHuman); } catch { }                     // hands-busy "cooking" gesture

                // First tick the cook actually REACHES the lab this shift, start the CookShiftHours clock HERE (not at
                // dispatch), so the full shift is spent AT the lab as a reliable stakeout window instead of partly on the
                // walk over. Set once; later ticks skip it. (Placed before the dose-placement return below so arrival is
                // always recorded on the first in-lab tick.)
                if (_cookSessionStart < 0f)
                {
                    _cookSessionStart = Herd.NowHours();
                    _resumeCookTravelTrace = false;
                    Log($"cook arrived at the lab; {_cookShiftHours:0.#}h shift starts now.");
                }

                // Place any doses still in hand from the batch just made (spans a few ticks).
                if (Dose.CountIn(CookHuman) > 0)
                {
                    var h = Dose.Place(Lab, node, CookHuman);
                    if (h != null) { LabStash.Add(h); Dose.Destroy(CookHuman); }
                    return;
                }

                // Cook the next dose when the DEALER has run dry (no doses in any dealer's hand or drop) AND the lab stash
                // is empty (the previous dose has been ferried out, so we pace one-at-a-time and don't pile up at the lab).
                // Keyed to the DEALER - not the whole pipeline - so a dose still in transit (with the distributor) can't
                // stall the restock; that was the day-2 bug (dealer empty but a dose sat in transit, so PipelineDry never
                // went true and the cook never cooked). Demand-pull pacing is preserved via the lab-stash-empty gate.
                // Guard immediately before production so an expired shift cannot gain a reload-time batch.
                if (FinishCookShiftIfExpired(Herd.NowHours())) return;
                bool makeNext = _cookBatchesLeft > 0 && DealerDry() && LabStashCount() <= 0;
                if (makeNext)
                {
                    Dose.CreateMany(CookHuman, Math.Max(1, NarcoticsConfig.CookStashCap));
                    _cookBatchesLeft--;
                    int total = Math.Max(1, _cookBatchLimit);
                    Log($"cook made dose {total - _cookBatchesLeft}/{total}{(_cookBatchesLeft > 0 ? " - next after the dealer runs out again" : " (last of the day)")}.");
                    return;   // next ticks place the dose
                }

                // A valid shift remains pinned to the lab until its saved deadline.
            }
            catch (Exception e) { Log($"TickCook error: {e.Message}"); }
        }

        internal static SavedCookSession CaptureCookSession()
        {
            try
            {
                if (!Active || CookHuman == null || Lab == null || string.IsNullOrEmpty(DrugItem.CurrentKey)) return null;
                if (!int.TryParse(LabDirIndex(), out int labIndex)) return null;
                var snapshot = new SavedCookSession
                {
                    DrugKey = DrugItem.CurrentKey,
                    CaseTitle = CaseTitle,
                    CookHumanId = CookHuman.humanID,
                    LabDirectoryIndex = labIndex,
                    CapturedAtGameHours = Herd.NowHours(),
                    OperationStartedAtGameHours = _opStartGT,
                    CookActive = _cookActive,
                    CookSessionStartGameHours = _cookSessionStart,
                    CookShiftHours = Math.Max(0.1f, _cookShiftHours > 0f ? _cookShiftHours : NarcoticsConfig.CookShiftHours),
                    CookBatchesLeft = Math.Max(0, _cookBatchesLeft),
                    CookBatchLimit = Math.Max(1, _cookBatchLimit > 0 ? _cookBatchLimit : NarcoticsConfig.CookBatchesPerSession),
                    NextCookAtGameHours = _nextCookTime,
                    CookIntervalHours = Math.Max(0.1f, _cookIntervalHours > 0f ? _cookIntervalHours : NarcoticsConfig.CookIntervalHours)
                };

                // Capture only references already tracked in this live operation.
                // Buyers are sales history; an item may have been seized since their purchase.
                var buyerIds = new HashSet<int>();
                for (int i = 0; i < Buyers.Count; i++)
                {
                    try
                    {
                        var buyer = Buyers[i];
                        if (buyer != null && buyer.humanID >= 0 && buyerIds.Add(buyer.humanID))
                            snapshot.BuyerHumanIds.Add(buyer.humanID);
                    }
                    catch { }
                }
                var supplyIds = new HashSet<int>();
                for (int i = 0; i < LabStash.Count; i++)
                {
                    try
                    {
                        var item = LabStash[i];
                        if (IsWorldLabSupply(item, snapshot.DrugKey) && supplyIds.Add(item.id))
                            snapshot.LabStashInteractableIds.Add(item.id);
                    }
                    catch { }
                }
                // Preserve the first overdose even if the operation has not opened a case.
                // Captured before native SaveState serialization begins, so the saved
                // operation timeline matches the native game save.
                snapshot.HasLabStageTracking = true;
                snapshot.LabWasDressed = _labDressed;
                snapshot.LabDosesWereSeeded = _labDosesPlaced;
                snapshot.HasCaseOpenState = true;
                snapshot.CaseWasOpen = NarcCase.IsOpen;
                snapshot.HasOverdoseTracking = true;
                snapshot.PendingOverdoseBuyerId = _odBuyerId;
                snapshot.PendingOverdoseAtGameHours = _odAtHours;
                snapshot.NextOverdoseEligibleAtGameHours = _odNextEligible;
                snapshot.QueuedOverdoseBuyerIds.AddRange(_odQueue);
                if (!snapshot.CaseWasOpen && Arrest.Active && Arrest.Subject != null)
                {
                    int arrestId = SafeId(Arrest.Subject);
                    if (arrestId >= 0 && buyerIds.Contains(arrestId))
                        snapshot.PreCaseApproachingBuyerId = arrestId;
                }
                snapshot.HasCrewProgressTracking = true;
                foreach (var dist in Distributors)
                    if (dist != null && dist.H != null && dist.H.humanID >= 0)
                        snapshot.DistributorProgress.Add(new SavedDistributorProgress { HumanId = dist.H.humanID, Collected = dist.Collected });
                snapshot.HasDealerCooldownTracking = true;
                foreach (var dealer in Dealers)
                    if (dealer != null && dealer.H != null && dealer.H.humanID >= 0 && FiniteCookValue(dealer.NextSell) && dealer.NextSell >= 0f)
                        snapshot.DealerCooldowns.Add(new SavedDealerCooldown { HumanId = dealer.H.humanID, NextSellAtGameHours = dealer.NextSell });
                Log($"[crew-save] distributors={snapshot.DistributorProgress.Count} dealerCooldowns={snapshot.DealerCooldowns.Count}.");
                snapshot.HasWorldTracking = true;
                snapshot.HasLabDiscoveryTracking = true;
                snapshot.LabDiscoveryWasSeeded = _labDosesPlaced;
                var evidenceIds = new HashSet<int>();
                for (int i = 0; i < _labDiscoveryDoses.Count; i++)
                {
                    try
                    {
                        var item = _labDiscoveryDoses[i];
                        if (!Dose.Alive(item) || item.id <= 0 || item.preset == null ||
                            !string.Equals(item.preset.presetName, snapshot.DrugKey, StringComparison.Ordinal) ||
                            !evidenceIds.Add(item.id)) continue;
                        SavedLabDiscoveryDose savedPlace = null;
                        // On resaving a restored operation, keep its ORIGINAL pre-load placement
                        // (not a possibly unstreamed or temporarily detached current wrapper).
                        for (int j = 0; j < _labDiscoveryOriginalPlaces.Count; j++)
                            if (_labDiscoveryOriginalPlaces[j].InteractableId == item.id)
                            { savedPlace = _labDiscoveryOriginalPlaces[j]; break; }
                        if (savedPlace == null) savedPlace = CaptureLabDiscoveryPlace(item);
                        if (savedPlace != null) snapshot.LabDiscoveryDoses.Add(savedPlace);
                    }
                    catch (Exception e) { Log($"[lab-evidence-save] item skipped: {e.Message}"); }
                }
                Log($"[lab-evidence-save] seeded={snapshot.LabDiscoveryWasSeeded} originalPlaces={snapshot.LabDiscoveryDoses.Count} " +
                    $"ids=[{string.Join(",", snapshot.LabDiscoveryDoses.ConvertAll(p => p.InteractableId))}]; exact positions and furniture signatures captured.");
                Log($"[world-save] captured buyers={snapshot.BuyerHumanIds.Count} labStash={snapshot.LabStashInteractableIds.Count} (IDs only).");
                return snapshot;
            }
            catch (Exception e) { Log($"[cook-save] state capture failed: {e.Message}"); return null; }
        }

        // Restore these non-authoritative flags only for the save-matched snapshot.
        // Physical stock remains authoritative; the timer controls when existing stock may sell.
        private static void RestoreCrewProgress(SavedCookSession saved)
        {
            if (saved == null) return;
            int distributorsRestored = 0, cooldownsRestored = 0;
            if (saved.HasCrewProgressTracking && saved.DistributorProgress != null && saved.DistributorProgress.Count <= Distributors.Count)
            {
                var seen = new HashSet<int>();
                foreach (var progress in saved.DistributorProgress)
                {
                    if (progress == null || progress.HumanId < 0 || !seen.Add(progress.HumanId)) continue;
                    var dist = Distributors.Find(d => d != null && d.H != null && d.H.humanID == progress.HumanId);
                    if (dist == null) continue;
                    dist.Collected = progress.Collected;
                    distributorsRestored++;
                }
            }
            if (saved.HasDealerCooldownTracking && saved.DealerCooldowns != null && saved.DealerCooldowns.Count <= Dealers.Count)
            {
                var seen = new HashSet<int>();
                foreach (var cooldown in saved.DealerCooldowns)
                {
                    if (cooldown == null || cooldown.HumanId < 0 || !seen.Add(cooldown.HumanId) ||
                        !FiniteCookValue(cooldown.NextSellAtGameHours) || cooldown.NextSellAtGameHours < 0f) continue;
                    var dealer = Dealers.Find(d => d != null && d.H != null && d.H.humanID == cooldown.HumanId);
                    if (dealer == null) continue;
                    dealer.NextSell = cooldown.NextSellAtGameHours;
                    cooldownsRestored++;
                }
            }
            Log($"[crew-resume] distributorProgress={distributorsRestored}/{Distributors.Count} dealerCooldowns={cooldownsRestored}/{Dealers.Count}; physical stock unchanged.");
        }

        // An operation saved before the player ever visited the lab must still
        // dress that lab and seed the four evidence doses when geometry streams.
        private static void RestoreLabStage(SavedCookSession saved)
        {
            if (saved == null || !saved.HasLabStageTracking) return;
            if (!saved.LabWasDressed)
            {
                _labDressed = false;
                _nextDressTry = 0f;
                _labDressTries = 0;
                _labFurniture.Clear();
            }
            if (!saved.LabDosesWereSeeded) _labDosesPlaced = false;
            Log($"[lab-stage-resume] dressed={_labDressed} evidenceSeeded={_labDosesPlaced}; " +
                "pre-first-visit saves may finish dressing on lab entry.");
        }

        // Buyer identity has already been rebound from this precise native save.
        // Overdoses remain scheduled for their original game-time, never replay sales.
        private static void RestoreOverdoseTracking(SavedCookSession saved)
        {
            if (saved == null || !saved.HasOverdoseTracking) return;
            try
            {
                _odBuyerId = -1;
                _odAtHours = 0f;
                _odQueue.Clear();
                float now = Herd.NowHours();
                _odNextEligible = !float.IsNaN(saved.NextOverdoseEligibleAtGameHours) &&
                    !float.IsInfinity(saved.NextOverdoseEligibleAtGameHours)
                    ? saved.NextOverdoseEligibleAtGameHours : now;
                var known = new HashSet<int>();
                foreach (var buyer in Buyers)
                    if (buyer != null) known.Add(buyer.humanID);
                int due = saved.PendingOverdoseBuyerId;
                if (known.Contains(due) && !float.IsNaN(saved.PendingOverdoseAtGameHours) &&
                    !float.IsInfinity(saved.PendingOverdoseAtGameHours))
                {
                    _odBuyerId = due;
                    _odAtHours = saved.PendingOverdoseAtGameHours;
                }
                else if (!saved.CaseWasOpen && known.Contains(saved.PreCaseApproachingBuyerId))
                {
                    // A first overdose already happened but its enforcer hadn't cuffed
                    // the buyer at save time. Retire the dead arrest machine and resume
                    // the same buyer's incident once; never manufacture a new sale/dose.
                    _odBuyerId = saved.PreCaseApproachingBuyerId;
                    _odAtHours = now + 0.01f;
                }
                var unique = new HashSet<int>();
                foreach (int id in saved.QueuedOverdoseBuyerIds ?? new List<int>())
                    if (id != _odBuyerId && known.Contains(id) && unique.Add(id)) _odQueue.Add(id);
                Log($"[od-resume] pending={_odBuyerId} at={_odAtHours:0.###} " +
                    $"queued={_odQueue.Count} eligible={_odNextEligible:0.###} caseAtSave={saved.CaseWasOpen}; no new items or arrests created.");
            }
            catch (Exception e) { Log($"[od-resume] failed: {e.Message}"); }
        }

        // Identifies ONLY an already-saved, still-unheld world item from this operation.
        // Reject a known different location; allow missing node information when the lab
        // geometry has not streamed, since the save-matched item ID remains authoritative.
        private static bool IsWorldLabSupply(Interactable item, string drugKey)
        {
            try
            {
                if (!Dose.Alive(item) || item.id < 0 || item.inInventory != null) return false;
                var preset = item.preset;
                if (preset == null || !string.Equals(preset.presetName, drugKey, StringComparison.Ordinal)) return false;
                var node = item.node;
                var loc = node != null ? node.gameLocation : null;
                if (loc != null && Lab != null && loc.Pointer != Lab.Pointer) return false;
                return true;
            }
            catch { return false; }
        }

        private static void RestoreWorldTracking(SavedCookSession snapshot)
        {
            if (snapshot == null || !snapshot.HasWorldTracking) return;  // legacy cook snapshots
            try
            {
                var buyerIds = snapshot.BuyerHumanIds;
                var stashIds = snapshot.LabStashInteractableIds;
                if (buyerIds == null || stashIds == null || buyerIds.Count > 1024 || stashIds.Count > 128)
                {
                    Log("[world-resume] skipped: missing or oversized snapshot lists.");
                    return;
                }
                int restoredBuyers = 0, restoredStash = 0;
                var seenBuyers = new HashSet<int>();
                for (int i = 0; i < buyerIds.Count; i++)
                {
                    int id = buyerIds[i];
                    if (id < 0 || !seenBuyers.Add(id)) continue;
                    var buyer = ResolveSavedHuman(id);
                    if (buyer == null) continue;
                    Buyers.Add(buyer);
                    restoredBuyers++;
                }

                var wanted = new HashSet<int>(stashIds);
                if (wanted.Count > 0)
                {
                    var city = CityData.Instance;
                    var items = city != null ? city.savableInteractableDictionary : null;
                    if (items != null)
                    {
                        foreach (var entry in items)
                        {
                            if (wanted.Count == 0) break;
                            Interactable item = null;
                            try { item = entry.Value; } catch { }
                            if (!Dose.Alive(item)) continue;
                            int id;
                            try { id = item.id; } catch { continue; }
                            if (!wanted.Contains(id) || !IsWorldLabSupply(item, snapshot.DrugKey)) continue;
                            LabStash.Add(item);
                            wanted.Remove(id);
                            restoredStash++;
                        }
                    }
                }
                Log($"[world-resume] buyers={restoredBuyers}/{buyerIds.Count} labStash={restoredStash}/{stashIds.Count}; " +
                    $"missingOrInvalidStash={wanted.Count}; no items created or moved.");
            }
            catch (Exception e) { Log($"[world-resume] failed: {e.Message}"); }
        }

        // Game PlaceObject chooses a precise furniture sub-object, not merely a nearby
        // nav node. Save the item's native world transform AND its parent furniture's
        // exact identity/slot index, so evidence never migrates to another cabinet.
        private static SavedLabDiscoveryDose CaptureLabDiscoveryPlace(Interactable item)
        {
            try
            {
                var pos = item.wPos;
                var rot = item.wEuler;
                var record = new SavedLabDiscoveryDose
                {
                    InteractableId = item.id,
                    X = pos.x, Y = pos.y, Z = pos.z,
                    RotX = rot.x, RotY = rot.y, RotZ = rot.z
                };
                var parent = item.furnitureParent;
                if (parent != null && parent.furniture != null)
                {
                    var at = Containers.ContainerNode(parent);
                    if (at != null)
                    {
                        var p = at.position;
                        record.FurnitureName = parent.furniture.name;
                        record.FurnitureX = p.x; record.FurnitureY = p.y; record.FurnitureZ = p.z;
                        var spawned = parent.spawnedInteractables;
                        if (spawned != null)
                            for (int i = 0; i < spawned.Count; i++)
                                if (spawned[i] != null && spawned[i].id == item.id)
                                { record.FurnitureListIndex = i; break; }
                        if (record.FurnitureListIndex < 0)
                        {
                            var integrated = parent.integratedInteractables;
                            if (integrated != null)
                                for (int i = 0; i < integrated.Count; i++)
                                    if (integrated[i] != null && integrated[i].id == item.id)
                                    { record.FurnitureListIndex = i; record.IntegratedList = true; break; }
                        }
                    }
                }
                return record;
            }
            catch (Exception e) { Log($"[lab-evidence-save] position capture failed: {e.Message}"); return null; }
        }

        private static void RestoreLabDiscoveryTracking(SavedCookSession snapshot)
        {
            if (snapshot == null || !snapshot.HasLabDiscoveryTracking) return;
            try
            {
                var records = snapshot.LabDiscoveryDoses;
                if (records == null || records.Count > LabDiscoveryDoses)
                { Log("[lab-evidence-resume] invalid snapshot; refusing to repair or spawn drugs."); return; }
                var wanted = new HashSet<int>();
                for (int i = 0; i < records.Count; i++)
                {
                    var r = records[i];
                    if (r == null || r.InteractableId <= 0 || !wanted.Add(r.InteractableId) ||
                        float.IsNaN(r.X) || float.IsInfinity(r.X) ||
                        float.IsNaN(r.Y) || float.IsInfinity(r.Y) ||
                        float.IsNaN(r.Z) || float.IsInfinity(r.Z))
                    { Log("[lab-evidence-resume] invalid or duplicate evidence placement; leaving world unchanged."); return; }
                }
                _labDiscoveryDoses.Clear();
                _labDiscoveryOriginalPlaces.Clear();
                _labDiscoveryOriginalPlaces.AddRange(records);
                _labDosesPlaced = snapshot.LabDiscoveryWasSeeded;
                var city = CityData.Instance;
                var items = city != null ? city.savableInteractableDictionary : null;
                if (items != null)
                    foreach (var entry in items)
                    {
                        var item = entry.Value;
                        if (!Dose.Alive(item)) continue;
                        int id;
                        try { id = item.id; } catch { continue; }
                        if (!wanted.Contains(id)) continue;
                        try
                        {
                            if (item.preset == null || !string.Equals(item.preset.presetName, snapshot.DrugKey, StringComparison.Ordinal)) continue;
                            _labDiscoveryDoses.Add(item);
                            wanted.Remove(id);
                        }
                        catch { }
                    }
                _labDiscoveryNeedsExactRestore = _labDiscoveryDoses.Count > 0;
                _labDiscoveryVisualPassPending = _labDiscoveryNeedsExactRestore;
                // Try before Unity streams the lab's furniture, not only after entry.
                // Keep the post-stream pass queued for geometry initially unavailable.
                if (_labDiscoveryNeedsExactRestore) RestoreExactLabDiscoveryPlacements(early: true);
                Log($"[lab-evidence-resume] originalPlaces={records.Count} found={_labDiscoveryDoses.Count} missing={wanted.Count}; " +
                    "will reconnect SAME items to their exact furniture, no replacements.");
            }
            catch (Exception e) { Log($"[lab-evidence-resume] failed: {e.Message}"); }
        }

        // Resolve the SAME furnishing by preset + node position, exactly as dealer
        // dead drops are reconnected. Unlike the old candidate patch, NEVER choose a
        // random LabPotterNode, use SetAsNotInventory, or create new Interactables.
        private static FurnitureLocation FindOriginalLabFurniture(SavedLabDiscoveryDose record)
        {
            if (Lab == null || record == null || string.IsNullOrEmpty(record.FurnitureName)) return null;
            FurnitureLocation found = null;
            var seen = new HashSet<IntPtr>();
            var candidates = new List<FurnitureLocation>();
            var rooms = Lab.rooms;
            if (rooms != null)
                for (int i = 0; i < rooms.Count; i++)
                {
                    var room = rooms[i];
                    var locations = room != null ? room.individualFurniture : null;
                    if (locations != null)
                        for (int j = 0; j < locations.Count; j++) candidates.Add(locations[j]);
                }
            var nodes = Lab.nodes;
            if (nodes != null)
                for (int i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    var locations = node != null ? node.individualFurniture : null;
                    if (locations != null)
                        for (int j = 0; j < locations.Count; j++) candidates.Add(locations[j]);
                }
            for (int j = 0; j < candidates.Count; j++)
            {
                var f = candidates[j];
                if (f == null || !seen.Add(f.Pointer) || f.furniture == null ||
                    f.furniture.name != record.FurnitureName) continue;
                var node = Containers.ContainerNode(f);
                if (node == null) continue;
                var pos = node.position;
                float dx = pos.x - record.FurnitureX, dy = pos.y - record.FurnitureY, dz = pos.z - record.FurnitureZ;
                if (dx * dx + dy * dy + dz * dz > 0.0025f) continue; // <=5cm: same original fixture
                if (found != null && found.Pointer != f.Pointer) return null; // ambiguous, do not guess
                found = f;
            }
            return found;
        }

        // Runs once *after* the lab has streamed. Only repair the original furniture
        // reference/list entry and native transform of EXISTING objects. If we cannot
        // identify the same piece of furniture, log and leave the world unchanged.
        private static void RestoreExactLabDiscoveryPlacements(bool early = false)
        {
            if (!_labDiscoveryNeedsExactRestore || (!early && !PlayerInLab())) return;
            if (!early) _labDiscoveryNeedsExactRestore = false;
            int intact = 0, repaired = 0, skipped = 0;
            for (int i = 0; i < _labDiscoveryDoses.Count; i++)
            {
                var item = _labDiscoveryDoses[i];
                try
                {
                    if (!Dose.Alive(item) || item.inInventory != null) { skipped++; continue; }
                    SavedLabDiscoveryDose r = null;
                    for (int j = 0; j < _labDiscoveryOriginalPlaces.Count; j++)
                        if (_labDiscoveryOriginalPlaces[j].InteractableId == item.id)
                        { r = _labDiscoveryOriginalPlaces[j]; break; }
                    if (r == null) { skipped++; continue; }
                    var node = item.node;
                    if (node != null && node.gameLocation != null && node.gameLocation.Pointer != Lab.Pointer)
                    { skipped++; Log($"[lab-evidence] id={item.id} moved outside lab; unchanged."); continue; }
                    var original = FindOriginalLabFurniture(r);
                    if (original == null)
                    { skipped++; Log($"[lab-evidence] id={item.id} original furniture unresolved; unchanged."); continue; }
                    // A wrapper can rehydrate with a different pointer to the SAME fixture;
                    // compare its permanent signature before treating it as moved.
                    var parent = item.furnitureParent;
                    if (parent != null && parent.Pointer != original.Pointer)
                    {
                        var parentNode = Containers.ContainerNode(parent);
                        var parentName = parent.furniture != null ? parent.furniture.name : null;
                        if (parentNode == null || parentName != r.FurnitureName ||
                            Vector3.Distance(parentNode.position, new Vector3(r.FurnitureX, r.FurnitureY, r.FurnitureZ)) > 0.05f)
                        { skipped++; Log($"[lab-evidence] id={item.id} now belongs to another fixture; unchanged."); continue; }
                    }
                    var list = r.IntegratedList ? original.integratedInteractables : original.spawnedInteractables;
                    if (list == null)
                    { skipped++; Log($"[lab-evidence] id={item.id} original furniture list unavailable; unchanged."); continue; }
                    bool registered = false;
                    for (int k = 0; k < list.Count; k++)
                        if (list[k] != null && list[k].id == item.id) { registered = true; break; }
                    if (parent != null && parent.Pointer == original.Pointer && registered)
                    { intact++; continue; } // native scene placement is authoritative
                    if (parent == null || parent.Pointer != original.Pointer) item.furnitureParent = original;
                    // Reuse exactly the saved object; put it back at its previous list index,
                    // not a newly chosen slot or node. World transform stays unchanged.
                    if (!registered)
                    {
                        int index = r.FurnitureListIndex;
                        if (index < 0 || index > list.Count) index = list.Count;
                        list.Insert(index, item);
                    }
                    item.wPos = new Vector3(r.X, r.Y, r.Z);
                    item.wEuler = new Vector3(r.RotX, r.RotY, r.RotZ);
                    repaired++;
                    Log($"[lab-evidence] id={item.id} reattached to ORIGINAL {r.FurnitureName} " +
                        $"at ({r.X:0.###},{r.Y:0.###},{r.Z:0.###}); original evidence identity intact.");
                }
                catch (Exception e) { skipped++; Log($"[lab-evidence] exact attachment error: {e.Message}"); }
            }
            Log($"[lab-evidence] exact furniture {(early ? "early" : "in-lab")} check intact={intact} repaired={repaired} skipped={skipped}; " +
                "no substitute items, relocation, or new supply.");
        }

        // A reattached Interactable can still lack its Unity GameObject after cold load.
        // Look for the game's world visual WITHOUT treating evidence registry membership
        // or FurnitureLocation list membership as proof that a renderer exists.
        // Reflection keeps this compatible with alternate IL2CPP interop wrapper shapes.
        private static UnityEngine.Object ExistingLabDiscoveryVisual(Interactable item, out string state)
        {
            state = "visual-member-not-found";
            if (item == null) return null;
            var flags = System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            foreach (var memberName in new[] { "controller", "spawnedObject", "spawnedObj", "worldObject" })
            {
                try
                {
                    object value = null;
                    var field = typeof(Interactable).GetField(memberName, flags);
                    if (field != null) value = field.GetValue(item);
                    else
                    {
                        var property = typeof(Interactable).GetProperty(memberName, flags);
                        if (property == null || property.GetIndexParameters().Length > 0) continue;
                        value = property.GetValue(item);
                    }
                    if (value is UnityEngine.Object unityObject)
                    {
                        state = memberName + (unityObject != null ? ":live" : ":missing");
                        if (unityObject != null) return unityObject;
                    }
                    else state = memberName + (value == null ? ":null" : ":non-unity");
                }
                catch (Exception e) { state = memberName + ":inspect-failed:" + e.Message; }
            }
            return null;
        }

        // This runs ONCE for the four tracked starter evidence items, after the lab is
        // streamed and after exact furniture reattachment. SpawnObject is the game's
        // native visual-instantiation method on the ALREADY-SAVED Interactable; it is
        // not Toolbox.CreateInteractable or PlaceObject and does not assign a new ID.
        // The native renderer may be inactive while a closed cabinet hides its contents:
        // do not SetActive(true), re-home to a convenient node, or create a substitute.
        private static void RestoreLabDiscoveryVisuals()
        {
            if (!_labDiscoveryVisualPassPending || !PlayerInLab()) return;
            // Never run continuously: retries could create duplicate visual GameObjects.
            _labDiscoveryVisualPassPending = false;
            int alreadyLive = 0, spawned = 0, failed = 0, skipped = 0;
            for (int i = 0; i < _labDiscoveryDoses.Count; i++)
            {
                var item = _labDiscoveryDoses[i];
                try
                {
                    if (!Dose.Alive(item) || item.inInventory != null) { skipped++; continue; }
                    SavedLabDiscoveryDose saved = null;
                    for (int j = 0; j < _labDiscoveryOriginalPlaces.Count; j++)
                        if (_labDiscoveryOriginalPlaces[j].InteractableId == item.id)
                        { saved = _labDiscoveryOriginalPlaces[j]; break; }
                    if (saved == null) { skipped++; continue; }
                    var original = FindOriginalLabFurniture(saved);
                    if (original == null || item.furnitureParent == null ||
                        original.Pointer != item.furnitureParent.Pointer)
                    { skipped++; Log($"[lab-evidence-visual] id={item.id} exact original furniture not attached; no spawn."); continue; }
                    var at = item.node;
                    if (at != null && at.gameLocation != null && at.gameLocation.Pointer != Lab.Pointer)
                    { skipped++; Log($"[lab-evidence-visual] id={item.id} moved outside lab; no spawn."); continue; }
                    var visual = ExistingLabDiscoveryVisual(item, out var before);
                    if (visual != null)
                    {
                        alreadyLive++;
                        Log($"[lab-evidence-visual] id={item.id} existing Unity object ({before}); left unchanged.");
                        continue;
                    }

                    int originalId = item.id;
                    // The authoritative coordinates are the EXACT original placement,
                    // not the player's location or an arbitrary lab node.
                    item.wPos = new Vector3(saved.X, saved.Y, saved.Z);
                    item.wEuler = new Vector3(saved.RotX, saved.RotY, saved.RotZ);
                    bool nativeFlag = false;
                    item.SpawnObject(out nativeFlag);
                    var afterVisual = ExistingLabDiscoveryVisual(item, out var after);
                    if (item.id != originalId)
                    {
                        failed++;
                        Log($"[lab-evidence-visual] ERROR: original ID changed {originalId}->{item.id} during SpawnObject; investigate.");
                    }
                    else if (afterVisual != null)
                    {
                        // Native spawning normally reads wPos/wEuler. Force the SAME
                        // original world transform on the newly materialized visual.
                        Transform transform = null;
                        if (afterVisual is Component component) transform = component.transform;
                        else if (afterVisual is GameObject gameObject) transform = gameObject.transform;
                        if (transform != null)
                        {
                            transform.position = new Vector3(saved.X, saved.Y, saved.Z);
                            transform.eulerAngles = new Vector3(saved.RotX, saved.RotY, saved.RotZ);
                        }
                        spawned++;
                        Log($"[lab-evidence-visual] id={item.id} native visual spawned; before={before}, after={after}, " +
                            $"position=({saved.X:0.###},{saved.Y:0.###},{saved.Z:0.###}) ORIGINAL fixture={saved.FurnitureName}.");
                    }
                    else
                    {
                        failed++;
                        Log($"[lab-evidence-visual] id={item.id} native SpawnObject returned but no live visual detected; " +
                            $"before={before}, after={after}, nativeFlag={nativeFlag}. Original item unchanged.");
                    }
                }
                catch (Exception e)
                {
                    failed++;
                    Log($"[lab-evidence-visual] failed to materialize id={(item != null ? item.id : -1)}: {e.Message}");
                }
            }
            Log($"[lab-evidence-visual] in-lab one-shot pass: existing={alreadyLive} spawned={spawned} " +
                $"failed={failed} skipped={skipped}; same IDs/furniture/original transforms; zero substitute drugs.");
        }

        private static bool RestoreCookSession(SavedCookSession state, out string reason)
        {
            reason = null;
            float now = Herd.NowHours();
            if (state == null) { reason = "snapshot missing"; return false; }
            if (!FiniteCookValue(state.CapturedAtGameHours) || !FiniteCookValue(state.OperationStartedAtGameHours) ||
                !FiniteCookValue(state.CookSessionStartGameHours) || !FiniteCookValue(state.CookShiftHours) ||
                !FiniteCookValue(state.NextCookAtGameHours) || !FiniteCookValue(state.CookIntervalHours))
            { reason = "non-finite game-time value"; return false; }
            if (state.CookHumanId != (CookHuman != null ? CookHuman.humanID : -1) || state.DrugKey != DrugItem.CurrentKey || state.CaseTitle != CaseTitle)
            { reason = "restored operation identity mismatch"; return false; }
            if (Math.Abs(state.OperationStartedAtGameHours - _opStartGT) > 0.05f || Math.Abs(now - state.CapturedAtGameHours) > 0.05f)
            { reason = $"loaded game time or operation start mismatch (now={now:0.###})"; return false; }
            if (state.CookShiftHours < 0.1f || state.CookShiftHours > 48f || state.CookIntervalHours < 0.1f || state.CookIntervalHours > 168f ||
                state.CookBatchLimit < 1 || state.CookBatchLimit > 64 || state.CookBatchesLeft < 0 || state.CookBatchesLeft > state.CookBatchLimit ||
                state.CookSessionStartGameHours < -1f || state.CookSessionStartGameHours > state.CapturedAtGameHours + 0.05f || state.NextCookAtGameHours < 0f)
            { reason = "cook snapshot values outside safe bounds"; return false; }

            _opStartGT = state.OperationStartedAtGameHours;
            _cookIntervalHours = state.CookIntervalHours;
            _cookShiftHours = state.CookShiftHours;
            _cookBatchLimit = state.CookBatchLimit;
            _nextCookTime = state.NextCookAtGameHours;
            _cookActive = state.CookActive;
            _cookSessionStart = state.CookSessionStartGameHours;
            _cookBatchesLeft = state.CookBatchesLeft;
            // The AI goal is deliberately transient. TickCook rebuilds a safe pin using live nodes and Herd.ToNode.
            _labNode = null; _cookPotter = null; _cookNextPotter = 0f; _targetNode = null;
            _resumeCookTravelTrace = _cookActive && _cookSessionStart < 0f;
            _resumeCookTravelLogAfter = 0f;

            if (_cookActive && _cookSessionStart >= 0f && now >= _cookSessionStart + _cookShiftHours)
            {
                FinishCookShiftIfExpired(now);
                LogCookResumeState(state, now, "saved shift expired; ended once and advanced to the next cadence point");
                return true;
            }

            float remaining = _cookActive && _cookSessionStart >= 0f
                ? Math.Max(0f, _cookSessionStart + _cookShiftHours - now)
                : (_cookActive ? -1f : _nextCookTime - now);
            string resumeReason = _cookActive ? (_cookSessionStart < 0f ? "continue travel; shift clock starts on arrival" : "resume original shift")
                : "between shifts; preserve scheduled start";
            LogCookResumeState(state, now, resumeReason, remaining);
            return true;
        }

        // Diagnostic-only heartbeat for a saved cook who was travelling when saved.
        // No item creation, movement, timing changes or AI goal modifications here.
        private static void TraceResumedCookTravel(string stage, NewNode target)
        {
            if (!_resumeCookTravelTrace || Time.time < _resumeCookTravelLogAfter) return;
            _resumeCookTravelLogAfter = Time.time + 15f;
            try
            {
                var current = CookHuman != null ? CookHuman.currentNode : null;
                var ai = CookHuman != null ? CookHuman.ai : null;
                bool hasGoal = false;
                try { hasGoal = ai != null && ai.currentGoal != null; } catch { }
                float distance = -1f;
                try { if (current != null && target != null) distance = Vector3.Distance(current.position, target.position); } catch { }
                Log($"[cook-resume-travel] stage={stage} cook={NarcoticsPlugin.Name(CookHuman)} " +
                    $"gameTime={Herd.NowHours():0.###} target={(target != null ? "present" : "missing")} " +
                    $"currentNode={(current != null ? "present" : "missing")} ai={(ai != null ? "present" : "missing")} " +
                    $"goal={(hasGoal ? "present" : "missing")} distanceM={distance:0.##}; waiting for ORIGINAL cook to reach ORIGINAL lab.");
            }
            catch (Exception e) { Log($"[cook-resume-travel] diagnostics unavailable: {e.Message}"); }
        }

        private static bool FinishCookShiftIfExpired(float nowHours)
        {
            if (!_cookActive || _cookSessionStart < 0f || nowHours < _cookSessionStart + Math.Max(0.1f, _cookShiftHours)) return false;
            int madeThis = Math.Max(0, _cookBatchLimit - _cookBatchesLeft);
            _cookActive = false;
            _cookBatchesLeft = 0;
            _cookSessionStart = -1f;
            _nextCookTime = NextGridPoint(nowHours);
            try { if (CookHuman != null) Herd.Release(CookHuman); } catch { }
            Log($"cook shift ended once (made {madeThis} dose(s)); next legitimate shift at {_nextCookTime:0.###} GT.");
            return true;
        }

        private static bool FiniteCookValue(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static void LogCookResumeSkipped(string reason, float savedAt, bool active = false, float started = -1f, int batchesLeft = 0, float shiftHours = 0f, float nextCookAt = -1f)
        {
            float remaining = active && started >= 0f && shiftHours > 0f ? started + shiftHours - Herd.NowHours() : (!active && nextCookAt >= 0f ? nextCookAt - Herd.NowHours() : -1f);
            Log($"[cook-resume] skipped active={active} started={started:0.###} batchesLeft={batchesLeft} savedGT={savedAt:0.###} remaining={remaining:0.###} reason={reason}.");
        }

        private static void LogCookResumeState(SavedCookSession state, float now, string reason, float? remaining = null)
        {
            float remainingHours = remaining ?? (state.CookActive && state.CookSessionStartGameHours >= 0f
                ? Math.Max(0f, state.CookSessionStartGameHours + state.CookShiftHours - now)
                : (state.CookActive ? -1f : state.NextCookAtGameHours - now));
            Log($"[cook-resume] active={state.CookActive} started={state.CookSessionStartGameHours:0.###} batchesLeft={state.CookBatchesLeft} " +
                $"savedGT={state.CapturedAtGameHours:0.###} remaining={remainingHours:0.###} reason={reason}.");
        }

        // True when NO dose exists anywhere in the pipeline: not in the cook's hand, not in the lab stash, not carried by
        // a distributor, not in any dead drop, and not on any dealer. The cook uses this as its pull trigger (make the
        // next dose only when the dealer has run out and everything downstream is drained).
        private static bool PipelineDry()
        {
            try
            {
                if (CookHuman != null && Dose.CountIn(CookHuman) > 0) return false;   // a just-made dose still in hand
                if (LabStashCount() > 0) return false;
                for (int i = 0; i < Distributors.Count; i++) { var d = Distributors[i]; if (d != null && d.H != null && Dose.CountIn(d.H) > 0) return false; }
                for (int k = 0; k < Dealers.Count; k++)
                {
                    var dl = Dealers[k]; if (dl == null) continue;
                    if (dl.H != null && Dose.CountIn(dl.H) > 0) return false;
                    if (DealerStashCount(dl) > 0) return false;
                }
            }
            catch { return false; }   // on any doubt, DON'T cook (avoids over-producing)
            return true;
        }

        // The DEALER(S) have run dry: no doses in any dealer's hand OR their drop. The cook's restock trigger - unlike
        // PipelineDry it ignores the lab stash + distributor (in-transit doses), so a dose stuck in transit can't stall
        // the cook when the dealer is actually out (the cook's make-condition pairs this with a lab-stash-empty check so
        // it still paces one dose at a time). Returns false (don't cook) on any error, to avoid over-producing.
        private static bool DealerDry()
        {
            try
            {
                if (Dealers.Count == 0) return false;
                for (int k = 0; k < Dealers.Count; k++)
                {
                    var dl = Dealers[k]; if (dl == null) continue;
                    if (dl.H != null && Dose.CountIn(dl.H) > 0) return false;
                    if (DealerStashCount(dl) > 0) return false;
                }
            }
            catch { return false; }
            return true;
        }

        private static void PickJob()
        {
            // (The cook is no longer a job - TickCook runs its passive multi-batch lab session independently.)

            // 1) the distributor DROPS whatever it's carrying at its dealer's dead drop (it only ever carries when it
            //    collected for an empty drop, so just deposit it).
            for (int i = 0; i < Distributors.Count; i++)
            {
                var d = Distributors[i];
                if (d.H == null || !Alive(d.H) || Dose.CountIn(d.H) <= 0) continue;
                for (int k = 0; k < Dealers.Count; k++)
                {
                    var dl = Dealers[k];
                    if (dl.DistIndex != i || dl.H == null || !Alive(dl.H)) continue;
                    BeginDrop(d, dl); return;
                }
            }

            // 2) a dealer that is EMPTY on doses collects from its dead drop when the drop HAS doses. (Prioritised above
            //    the distributor's next lab run so the dealer tops up promptly.)
            for (int k = 0; k < Dealers.Count; k++)
            {
                var dl = Dealers[k];
                if (dl.H == null || !Alive(dl.H)) continue;
                if (Dose.CountIn(dl.H) <= 0 && DealerStashCount(dl) > 0) { BeginDealer(dl); return; }
            }

            // 3) a dealer holding stock SELLS when due. The deal MEETING LOCATION is a genuinely CCTV-FILMABLE node near
            //    the dealer when one exists (a covered node within humanCaptureRange of a camera eye - the real "on film"
            //    test), else the dealer's own node (no stall). The buyer is summoned to the meet node and the dealer goes
            //    there. If nobody is within range, nothing happens - the dealer keeps pottering, retry shortly.
            if (_forceSale || Time.time >= _nextSellScan)
            {
                _nextSellScan = Time.time + 2f;   // throttle the citizen scan
                for (int k = 0; k < Dealers.Count; k++)
                {
                    var dl = Dealers[k];
                    if (dl.H == null || !Alive(dl.H) || Dose.CountIn(dl.H) <= 0) continue;
                    if (!_forceSale && Herd.NowHours() < dl.NextSell) continue;
                    if (Herd.IsReacting(dl.H)) continue;   // don't pull a spooked dealer into a deal; let the tail reaction clear first
                    NewNode here = null; try { here = dl.H.currentNode; } catch { }
                    if (here == null) continue;
                    float summon = _forceSale ? 400f : 150f;
                    // PREFERRED: the buyer standing closest to a DIFFERENT (outdoor) camera than the drop - meet there, so
                    // the deal is a street deal on a SECOND camera, sited where a buyer already is (not summoned to an
                    // indoor top-floor camera near the dealer). Fallbacks: a different-camera node near the dealer + the
                    // nearest buyer; then any camera; then the dealer's own node off camera.
                    NewNode meet = null; string how;
                    Human buyer = PickBuyerForCameraDeal(here.position, dl.DropCam, summon, out meet);
                    if (buyer != null && meet != null) { how = "a buyer near a DIFFERENT camera (street-level)"; }
                    else
                    {
                        NewNode cam = NearestCameraVisibleNode(here.position, 0f, 120f, dl.DropCam);
                        bool diffCam = cam != null;
                        if (cam == null) cam = NearestCameraVisibleNode(here.position, 0f, 120f);   // any filmed node
                        meet = cam != null ? cam : here;
                        buyer = PickBuyerNear(meet.position, summon);
                        if (buyer == null && cam != null) { meet = here; cam = null; buyer = PickBuyerNear(here.position, summon); }   // camera node too quiet -> deal at the dealer (off camera)
                        how = cam != null ? (diffCam ? "a different-camera node near the dealer" : "a camera node near the dealer (same as drop - no other near)") : "its own node (no camera near)";
                    }
                    if (buyer == null) continue;   // nobody around right now - keep pottering, retry shortly
                    // Remember which camera films the meet node, so the BUYER is placed on an adjacent node covered by
                    // the SAME camera - otherwise the pair can land on two different cameras and only one is on footage.
                    IntPtr mcam = IntPtr.Zero; try { if (meet != null) { float a, e; CameraFrameScore(meet, IntPtr.Zero, out mcam, out a, out e); } } catch { }
                    _meetCam = mcam;
                    Log($"SALE: {NarcoticsPlugin.Name(dl.H)} will meet {NarcoticsPlugin.Name(buyer)} at {how}.");
                    BeginSell(dl, buyer, meet);
                    _forceSale = false;
                    return;
                }
                _forceSale = false;
            }

            // 4) the distributor FERRIES: when a dealer's dead drop is EMPTY and the lab has doses, collect from the lab
            //    (step 1 then drops it at the drop). Only when the distributor itself is empty. LAST, so a waiting dealer
            //    collects first.
            if (LabStashCount() > 0)
                for (int i = 0; i < Distributors.Count; i++)
                {
                    var d = Distributors[i];
                    if (d.H == null || !Alive(d.H) || Dose.CountIn(d.H) > 0) continue;   // only when the distributor is empty-handed
                    for (int k = 0; k < Dealers.Count; k++)
                    {
                        var dl = Dealers[k];
                        if (dl.DistIndex != i || dl.H == null || !Alive(dl.H)) continue;
                        if (DealerStashCount(dl) <= 0) { BeginDist(Job.DistCollect, d); return; }   // drop empty + lab has doses -> ferry
                    }
                }
        }

        private static void RunJob()
        {
            try
            {
                if (Time.time > _deadline) { Log($"job {_job} timed out; will retry."); EndJob(); return; }

                // REACTION GUARD. If the job's mover is reacting to a tail (confront/flee/investigate), stop herding it -
                // our priority pin otherwise fights the vanilla reaction and traps the NPC in the flailing "under attack"
                // loop the user saw. Release the pin and let the reaction resolve. A sale is ABANDONED outright (a spooked
                // dealer won't deal; it re-picks a fresh observable buyer when calm). A collect/drop is held open for a
                // short grace window (the lab/drop is fixed, so resuming once calm is fine), then abandoned + retried.
                if (_mover != null && Herd.IsReacting(_mover))
                {
                    try { Herd.Release(_mover); } catch { }
                    if (_job == Job.SellToBuyer)
                    {
                        try { if (_jobBuyer != null) Herd.Release(_jobBuyer); } catch { }
                        Log($"{NarcoticsPlugin.Name(_mover)} spooked mid-deal; abandoning the sale until calm.");
                        EndJob();
                        return;
                    }
                    if (_reactWaitUntil <= 0f) _reactWaitUntil = Time.time + 60f;
                    if (Time.time >= _reactWaitUntil) { Log($"{NarcoticsPlugin.Name(_mover)} stayed spooked too long; abandoning {_job} (will retry)."); EndJob(); return; }
                    _deadline = Math.Max(_deadline, Time.time + 10f);   // don't time out purely because it's reacting
                    return;
                }
                _reactWaitUntil = 0f;

                int carry = Math.Max(1, NarcoticsConfig.CarryTarget);

                switch (_job)
                {
                    case Job.DistCollect:
                    {
                        if (_jobDist == null || _jobDist.H == null) { EndJob(); break; }
                        var node = LabNode(_jobDist.H);
                        if (node == null) { EndJob(); break; }
                        _targetNode = node;
                        ArmLabDoorClose(_jobDist.H);   // vanilla: distributor shuts the lab door on their way out
                        if (!Herd.ToNodeSoft(_jobDist.H, node, Lab, RangeM)) break;   // soft: distributor reacts to a tail while walking to the lab
                        PruneLabStash();
                        if (LabStash.Count == 0) { _jobDist.Collected = true; Log($"{NarcoticsPlugin.Name(_jobDist.H)} collected the lab stash (holding {Dose.CountIn(_jobDist.H)}); heading out on the drops."); EndJob(); break; }
                        var it = LabStash[LabStash.Count - 1];
                        LabStash.RemoveAt(LabStash.Count - 1);
                        Dose.Pickup(it, _jobDist.H);
                        break;
                    }

                    case Job.DistDrop:
                    {
                        if (_jobDist == null || _jobDist.H == null || _jobDealer == null || _jobDealer.H == null) { EndJob(); break; }
                        EnsureDrop(_jobDealer);
                        if (_jobDealer.Drop == null) { EndJob(); break; }
                        _targetNode = _jobDealer.Drop;
                        if (!Herd.ToNodeSoft(_jobDist.H, _jobDealer.Drop, _jobDealer.DropLoc, RangeM)) break;   // soft: distributor reacts to a tail while running drops
                        // Brief visible pause AT the drop while the distributor stashes into the bin (enough to be caught
                        // on CCTV), playing the open-container gesture. The drop is always a real bin now; the guard only
                        // skips the gesture in the rare case no bin could be sited near the lab.
                        bool distBin = _jobDealer.Container != null;
                        if (_dwellUntil <= 0f) { _dwellUntil = Herd.NowHours() + Math.Max(0.001f, NarcoticsConfig.DropDwellMinutes / 60f); if (distBin) { try { Anim.Locking(_jobDist.H); } catch { } FaceBin(_jobDist.H, _jobDealer); } Log($"DROP: {NarcoticsPlugin.Name(_jobDist.H)} stashing in {NarcoticsPlugin.Name(_jobDealer.H)}'s dead drop - {NarcoticsConfig.DropDwellMinutes:0} min."); break; }
                        _deadline = Math.Max(_deadline, Time.time + 30f);   // a game-time dwell can outlast the real-time job timeout
                        if (Herd.NowHours() < _dwellUntil) { if (distBin) { try { Anim.Locking(_jobDist.H); } catch { } FaceBin(_jobDist.H, _jobDealer); } break; }   // hold the gesture (bins only) + keep facing the bin
                        try { Anim.Rest(_jobDist.H); } catch { }   // end the gesture (harmless if none)
                        int placed = 0;
                        while (placed < carry && Dose.CountIn(_jobDist.H) > 0)
                        {
                            if (_jobDealer.Container != null)
                            {
                                // Put the dose into the bin as searchable contained trash (data-side, persistent).
                                if (!Dose.DepositToBin(_jobDealer.Container, _jobDist.H)) break;
                                Dose.Destroy(_jobDist.H);            // the carried dose "moved" into the bin
                            }
                            else
                            {
                                var h = Dose.Drop(_jobDist.H, _jobDealer.Drop);   // fallback: loose at the node
                                if (h == null) break;
                                _jobDealer.Stash.Add(h);
                            }
                            placed++;
                        }
                        if (_jobDealer.Container != null) _jobDealer.BinDoses = Dose.CountInBin(_jobDealer.Container);   // resync to the LIVE bin (also catches doses the PLAYER took)
                        Log($"DEAD DROP: {NarcoticsPlugin.Name(_jobDist.H)} left x{placed} in {NarcoticsPlugin.Name(_jobDealer.H)}'s {(_jobDealer.Container != null ? "bin" : "spot")} (drop holds {DealerStashCount(_jobDealer)}).");
                        EndJob();
                        break;
                    }

                    case Job.DealerCollect:
                    {
                        if (_jobDealer == null || _jobDealer.H == null) { EndJob(); break; }
                        EnsureDrop(_jobDealer);
                        if (_jobDealer.Drop == null) { EndJob(); break; }
                        _targetNode = _jobDealer.Drop;
                        if (!Herd.ToNodeSoft(_jobDealer.H, _jobDealer.Drop, _jobDealer.DropLoc, RangeM)) break;   // soft: dealer reacts to a tail while walking to its drop
                        // Brief visible pause AT the drop while the dealer collects from the bin, playing the open-container
                        // gesture. The drop is always a real bin now; the guard only skips the gesture if no bin was sited.
                        bool dlrBin = _jobDealer.Container != null;
                        if (_dwellUntil <= 0f) { _dwellUntil = Herd.NowHours() + Math.Max(0.001f, NarcoticsConfig.DropCollectMinutes / 60f); if (dlrBin) { try { Anim.Locking(_jobDealer.H); } catch { } FaceBin(_jobDealer.H, _jobDealer); } Log($"COLLECT: {NarcoticsPlugin.Name(_jobDealer.H)} collecting from their dead drop - {NarcoticsConfig.DropCollectMinutes:0} min."); break; }
                        _deadline = Math.Max(_deadline, Time.time + 30f);   // a game-time dwell can outlast the real-time job timeout
                        if (Herd.NowHours() < _dwellUntil) { if (dlrBin) { try { Anim.Locking(_jobDealer.H); } catch { } FaceBin(_jobDealer.H, _jobDealer); } break; }   // hold the gesture (bins only) + keep facing the bin
                        try { Anim.Rest(_jobDealer.H); } catch { }   // end the gesture (harmless if none)
                        int got;
                        if (_jobDealer.Container != null)
                        {
                            got = Dose.CollectFromBin(_jobDealer.Container, _jobDealer.H, carry);
                            _jobDealer.BinDoses = Dose.CountInBin(_jobDealer.Container);   // resync to the LIVE bin: if the PLAYER took a dose, the drop now reads empty (triggers a refill) instead of looping on a phantom count
                        }
                        else
                        {
                            got = 0;
                            PruneDealerStash(_jobDealer);
                            for (int i = _jobDealer.Stash.Count - 1; i >= 0; i--)
                            {
                                var it = _jobDealer.Stash[i]; _jobDealer.Stash.RemoveAt(i);
                                if (Dose.Pickup(it, _jobDealer.H)) got++;
                            }
                        }
                        // Potter for PotterAfterCollectMinutes AFTER collecting before the next deal, so the dealer walks
                        // away from the drop and the deal happens elsewhere (then we pick a buyer near a DIFFERENT camera).
                        // Take the LATER of this and the arrest-paced interval (OnArrestMade), so a restock mid-interval
                        // still waits out the arrest cooldown, while the very first collect only waits the potter.
                        float potterUntil = Herd.NowHours() + Math.Max(0.001f, NarcoticsConfig.PotterAfterCollectMinutes / 60f);
                        if (potterUntil > _jobDealer.NextSell) _jobDealer.NextSell = potterUntil;
                        Log($"COLLECT: {NarcoticsPlugin.Name(_jobDealer.H)} picked up x{got} from their dead drop (holding {Dose.CountIn(_jobDealer.H)}); pottering ~{NarcoticsConfig.PotterAfterCollectMinutes:0}min before dealing.");
                        EndJob();
                        break;
                    }

                    case Job.SellToBuyer:
                    {
                        if (_jobDealer == null || _jobDealer.H == null || !Alive(_jobDealer.H) || _jobBuyer == null || !Alive(_jobBuyer)) { EndJob(); break; }
                        if (Dose.CountIn(_jobDealer.H) <= 0) { EndJob(); break; }       // nothing left to sell
                        if (_meetNode == null) { try { _meetNode = _jobDealer.H.currentNode; } catch { } }
                        if (_meetNode == null) { EndJob(); break; }

                        NewGameLocation meetLoc = null; try { meetLoc = _meetNode.gameLocation; } catch { }
                        if (_buyerMeetNode == null) _buyerMeetNode = BuyerNodeNear(_meetNode, _meetCam);   // buyer stands BESIDE the dealer on the SAME camera, not on the same node
                        NewGameLocation buyerLoc = null; try { buyerLoc = _buyerMeetNode != null ? _buyerMeetNode.gameLocation : meetLoc; } catch { buyerLoc = meetLoc; }
                        _targetNode = _meetNode;
                        if (_dealDwellEndGT <= 0f)
                        {
                            // APPROACH: dealer soft (reacts to a tail en route; the reaction guard aborts if it spooks),
                            // buyer hard. Start the dwell once both are in position.
                            bool dealerHere = Herd.ToNodeSoft(_jobDealer.H, _meetNode, meetLoc, RangeM);
                            bool buyerHere = Herd.ToNode(_jobBuyer, _buyerMeetNode, buyerLoc, RangeM);
                            if (!dealerHere || !buyerHere) break;
                            _dealDwellEndGT = Herd.NowHours() + Math.Max(0.01f, NarcoticsConfig.DealDwellMinutes / 60f);   // GAME-time dwell
                            Log($"DEALING: {NarcoticsPlugin.Name(_jobDealer.H)} meeting buyer {NarcoticsPlugin.Name(_jobBuyer)} - holding ~{NarcoticsConfig.DealDwellMinutes:0}min for the camera...");
                            break;
                        }
                        // ONCE, DealShiftMinutes into the dwell, shuffle the pair to a different on-camera spot nearby (same
                        // camera) so they spend the first half at one spot and the rest at a second (off any doorway), without
                        // moving so often that the camera keeps missing them (moving twice lost the capture). One move only.
                        if (_nextDealShift <= 0f) _nextDealShift = Herd.NowHours() + Math.Max(0.01f, NarcoticsConfig.DealShiftMinutes / 60f);
                        else if (_nextDealShift < float.MaxValue && Herd.NowHours() >= _nextDealShift)
                        {
                            NewNode shifted = NearbySameCamSpot(_meetNode, _meetCam, 10f);
                            if (shifted != null)
                            {
                                _meetNode = shifted; _targetNode = _meetNode;
                                _buyerMeetNode = BuyerNodeNear(_meetNode, _meetCam);
                                try { meetLoc = _meetNode.gameLocation; } catch { }
                                try { buyerLoc = _buyerMeetNode != null ? _buyerMeetNode.gameLocation : meetLoc; } catch { buyerLoc = meetLoc; }
                                Log($"DEALING: {NarcoticsPlugin.Name(_jobDealer.H)} + buyer shift once to a new on-camera spot (off any doorway).");
                            }
                            _nextDealShift = float.MaxValue;   // only once
                        }
                        // DWELL: lock each tightly ONTO their node so their currentNode IS a covered node (the CCTV capture
                        // gate records an actor only when its currentNode is a covered node within humanCaptureRange). The
                        // dealer on the meet node, the buyer on the adjacent (also on-camera) node - beside, not stacked.
                        Herd.ToNode(_jobDealer.H, _meetNode, meetLoc, 0.6f);
                        Herd.ToNode(_jobBuyer, _buyerMeetNode, buyerLoc, 0.6f);
                        _deadline = Math.Max(_deadline, Time.time + 30f);   // a game-time dwell can outlast the real-time job timeout; keep it alive
                        if (Herd.NowHours() < _dealDwellEndGT) break;   // hold the deal on-camera for the whole (game-time) dwell

                        bool sold = Dose.Transfer(_jobDealer.H, _jobBuyer);   // move the real item -> carries dealer + buyer prints
                        if (sold && !HasBuyer(SafeId(_jobBuyer))) Buyers.Add(_jobBuyer);
                        if (sold) MaybeScheduleOverdose(_jobBuyer);           // every buyer ODs later (staggered)
                        // Feed the vanilla witness system: the buyer genuinely "saw" the dealer, so interrogating
                        // the arrested buyer ("seen anything unusual?") can surface the dealer + location. A calm
                        // sale records a MUNDANE sighting that is lost among dozens of normal ones, so we also mark
                        // that sighting as notable (point-of-interest / odd behaviour) to stand out as "unusual".
                        if (sold) { try { int bid = SafeId(_jobBuyer); _jobBuyer.UpdateLastSighting(_jobDealer.H, false, 0); FlagSightingNotable(_jobBuyer, _jobDealer.H, _meetNode); _buyerDealer[bid] = _jobDealer.H; _buyerSpot[bid] = _meetNode; } catch { } }
                        _jobDealer.NextSell = Herd.NowHours() + 2f;   // block re-selling until the resulting arrest; OnArrestMade then sets the real interval (arrest + OverdoseIntervalMinutes). 2h is just a safety if that buyer is never arrested.
                        Log($"SOLD: {NarcoticsPlugin.Name(_jobDealer.H)} -> buyer {NarcoticsPlugin.Name(_jobBuyer)} x{(sold ? 1 : 0)} (buyer holds {Dose.CountIn(_jobBuyer)}; {Buyers.Count} buyer(s) total).");
                        EndJob();
                        break;
                    }

                    default: EndJob(); break;
                }
            }
            catch (Exception e) { Log($"RunJob error: {e.Message}"); EndJob(); }
        }

        private static void Begin(Job j, Human mover) { _job = j; _mover = mover; _jobDist = null; _jobDealer = null; _jobBuyer = null; _deadline = Time.time + JobTimeoutSec; }
        private static void BeginDist(Job j, Dist d) { _job = j; _mover = d.H; _jobDist = d; _jobDealer = null; _jobBuyer = null; _deadline = Time.time + JobTimeoutSec; }
        // Turn an NPC to LOOK AT their dead-drop bin during the stash/collect dwell, so the open-container gesture reads
        // as them reaching into the bin instead of standing 90 degrees off it. Re-asserted every dwell tick (the AI's own
        // look logic drifts them back otherwise). No-op without a real bin (loose-spot fallback) or before it's streamed.
        private static void FaceBin(Human h, Dealer dl)
        {
            try
            {
                if (h == null || dl == null || dl.Container == null) return;
                var ai = h.ai; if (ai == null) return;
                Vector3 p; try { p = dl.Container.GetWorldAveragePosition(); } catch { return; }
                try { ai.SetFacingPosition(p); } catch { }
            }
            catch { }
        }

        private static void BeginDrop(Dist d, Dealer dl) { _job = Job.DistDrop; _mover = d.H; _jobDist = d; _jobDealer = dl; _jobBuyer = null; _deadline = Time.time + JobTimeoutSec; }
        private static void BeginDealer(Dealer dl) { _job = Job.DealerCollect; _mover = dl.H; _jobDist = null; _jobDealer = dl; _jobBuyer = null; _deadline = Time.time + JobTimeoutSec; }
        private static void BeginSell(Dealer dl, Human buyer, NewNode saleNode) { _job = Job.SellToBuyer; _mover = dl.H; _jobDist = null; _jobDealer = dl; _jobBuyer = buyer; _meetNode = saleNode; _buyerMeetNode = null; _nextDealShift = 0f; _dwellUntil = 0f; _dealDwellEndGT = 0f; _reactWaitUntil = 0f; _deadline = Time.time + JobTimeoutSec; }   // saleNode = the dealer's own node; the buyer walks to an adjacent node
        private static void EndJob()
        {
            try { if (_mover != null) Herd.Release(_mover); } catch { }
            try { if (_job == Job.SellToBuyer) { if (_jobDealer != null && _jobDealer.H != null) Herd.Release(_jobDealer.H); if (_jobBuyer != null) Herd.Release(_jobBuyer); } } catch { }
            _job = Job.None; _mover = null; _jobDist = null; _jobDealer = null; _jobBuyer = null; _meetNode = null; _buyerMeetNode = null; _nextDealShift = 0f; _dwellUntil = 0f; _dealDwellEndGT = 0f; _targetNode = null; _reactWaitUntil = 0f;
        }

        private static bool HasBuyer(int id) { try { for (int i = 0; i < Buyers.Count; i++) { var b = Buyers[i]; if (b != null && b.humanID == id) return true; } } catch { } return false; }

        private static bool IsOperationMember(int id)
        {
            try
            {
                if (CookHuman != null && CookHuman.humanID == id) return true;
                for (int i = 0; i < Distributors.Count; i++) { var d = Distributors[i]; if (d.H != null && d.H.humanID == id) return true; }
                for (int k = 0; k < Dealers.Count; k++) { var dl = Dealers[k]; if (dl.H != null && dl.H.humanID == id) return true; }
            }
            catch { }
            return false;
        }

        // Is this citizen one of the game's enforcers (any on the roster)? We never sell to a cop - the "dealer sold to
        // the arresting officer standing at the scene" bug.
        private static bool IsEnforcer(int id)
        {
            try
            {
                var gc = GameplayController.Instance; var list = gc != null ? gc.enforcers : null;
                if (list == null) return false;
                for (int i = 0; i < list.Count; i++) { var e = list[i]; if (e != null && e.humanID == id) return true; }
            }
            catch { }
            return false;
        }

        // Nearest citizen within range of the dealer who isn't the player or part of the operation - a random
        // passer-by to sell to.
        private static Human PickPasserBy(Human dealer)
        {
            try
            {
                NewNode dn = null; try { dn = dealer.currentNode; } catch { }
                if (dn == null) return null;
                var pos = dn.position;
                float range = Math.Max(2f, NarcoticsConfig.BuyerSearchRangeM);
                var cd = CityData.Instance; var cits = cd != null ? cd.citizenDirectory : null; if (cits == null) return null;
                int playerId = PlayerId();
                Human best = null; float bestD = float.MaxValue;
                for (int i = 0; i < cits.Count; i++)
                {
                    Human h = null; try { h = cits[i]; } catch { }
                    if (h == null || !Alive(h)) continue;
                    int id; try { id = h.humanID; } catch { continue; }
                    if (id == playerId || IsOperationMember(id)) continue;
                    if (HasBuyer(id)) continue;   // one dose per buyer - never re-sell to someone who already bought
                    if (IsEnforcer(id) || Arrest.IsArrestee(id)) continue;   // never sell to a cop or to someone being/already arrested
                    NewNode hn = null; try { hn = h.currentNode; } catch { }
                    if (hn == null) continue;
                    float d; try { d = Vector3.Distance(pos, hn.position); } catch { continue; }
                    if (d <= range && d < bestD) { bestD = d; best = h; }
                }
                return best;
            }
            catch { return null; }
        }

        // Nearest valid passer-by to `pos` within `maxRange` (not the player / an op member / a prior buyer / a cop /
        // an arrestee). Used to find someone near the dealer to sell to. One cheap citizen-directory pass.
        private static Human PickBuyerNear(Vector3 pos, float maxRange)
        {
            try
            {
                var cd = CityData.Instance; var cits = cd != null ? cd.citizenDirectory : null; if (cits == null) return null;
                int playerId = PlayerId();
                float maxSq = maxRange * maxRange;
                Human best = null; float bestSq = float.MaxValue;
                for (int i = 0; i < cits.Count; i++)
                {
                    Human h = null; try { h = cits[i]; } catch { }
                    if (h == null || !Alive(h)) continue;
                    int id; try { id = h.humanID; } catch { continue; }
                    if (id == playerId || IsOperationMember(id) || HasBuyer(id)) continue;   // one dose per buyer (never re-sell)
                    if (IsEnforcer(id) || Arrest.IsArrestee(id)) continue;   // never summon a cop or an arrestee as a buyer
                    NewNode hn = null; try { hn = h.currentNode; } catch { }
                    if (hn == null) continue;
                    float dsq; try { var p = hn.position; float dx = p.x - pos.x, dy = p.y - pos.y, dz = p.z - pos.z; dsq = dx * dx + dy * dy + dz * dz; } catch { continue; }
                    if (dsq <= maxSq && dsq < bestSq) { bestSq = dsq; best = h; }
                }
                return best;
            }
            catch { return null; }
        }

        // Pick the buyer (within `summon` of the dealer) who stands CLOSEST to a CCTV camera that is NOT the drop's
        // camera, and return that camera node as the meet location (out `meetNode`). This keeps the deal and the drop
        // on two DIFFERENT cameras AND sites the deal where a buyer already is - near a street camera - instead of
        // summoning a buyer to a camera node near the dealer (which could be an indoor top-floor one). Prefers OUTDOOR
        // camera nodes. Returns null if no nearby buyer is near a different camera; the caller then falls back.
        private static Human PickBuyerForCameraDeal(Vector3 dealerPos, IntPtr dropCam, float summon, out NewNode meetNode)
        {
            meetNode = null;
            Human bestBuyer = null; NewNode bestNode = null; float bestScore = float.MaxValue; int bestTier = 99;
            try
            {
                EnsureFilmSpots();
                var cd = CityData.Instance; var cits = cd != null ? cd.citizenDirectory : null; if (cits == null) return null;
                int playerId = PlayerId();
                float summonSq = summon * summon;
                const float camSearchM = 30f; float camSearchSq = camSearchM * camSearchM;   // how far a buyer may stand from the different camera
                for (int i = 0; i < cits.Count; i++)
                {
                    Human h = null; try { h = cits[i]; } catch { }
                    if (h == null || !Alive(h)) continue;
                    int id; try { id = h.humanID; } catch { continue; }
                    if (id == playerId || IsOperationMember(id) || HasBuyer(id)) continue;   // one dose per buyer
                    if (IsEnforcer(id) || Arrest.IsArrestee(id)) continue;                   // never a cop or an arrestee
                    NewNode hn = null; try { hn = h.currentNode; } catch { }
                    if (hn == null) continue;
                    Vector3 hp; try { hp = hn.position; } catch { continue; }
                    { float dx = hp.x - dealerPos.x, dy = hp.y - dealerPos.y, dz = hp.z - dealerPos.z; if (dx * dx + dy * dy + dz * dz > summonSq) continue; }   // within summon of the dealer
                    // ONE pass over the baked spots: nearest DIFFERENT-camera spot to THIS buyer, outdoor preferred
                    // (tier 0) over interior (tier 1). No Il2Cpp enumeration here - just cached float math.
                    int tier = 99; NewNode n = null; float bToN = float.MaxValue;
                    for (int s = 0; s < _filmSpots.Count; s++)
                    {
                        var sp = _filmSpots[s];
                        if (sp.Cam == dropCam) continue;   // must be a different camera than the drop
                        float dx = sp.Pos.x - hp.x, dy = sp.Pos.y - hp.y, dz = sp.Pos.z - hp.z; float dsq = dx * dx + dy * dy + dz * dz;
                        if (dsq > camSearchSq) continue;
                        int t = sp.Outside ? 0 : 1;
                        if (t < tier || (t == tier && dsq < bToN)) { tier = t; bToN = dsq; n = sp.Node; }
                    }
                    if (n == null) continue;   // this buyer isn't near a different camera
                    if (tier < bestTier || (tier == bestTier && bToN < bestScore)) { bestTier = tier; bestScore = bToN; bestBuyer = h; bestNode = n; }   // buyer closest to a different (outdoor) camera
                }
            }
            catch (Exception e) { Log($"PickBuyerForCameraDeal error: {e.Message}"); }
            meetNode = bestNode;
            return bestBuyer;
        }


        // Debug (F4): force a dealer who is holding doses to sell to the nearest passer-by right now (bypasses the
        // sell interval + scan throttle), so the dealer->buyer deal can be watched on demand.
        internal static void ForceSaleNow()
        {
            if (!Active) { Log("no active operation."); return; }
            _forceSale = true; _nextSellScan = 0f;
            int ready = 0; for (int k = 0; k < Dealers.Count; k++) { var dl = Dealers[k]; if (dl.H != null && Dose.CountIn(dl.H) > 0) ready++; }
            Log($"F4: forcing a sale now ({ready} dealer(s) currently holding doses). The dealer will deal to the nearest passer-by.");
        }

        // ------------------------------------------------------------------ daily overdose -> arrest -> case

        // On a sale, QUEUE that buyer for an overdose. Every buyer who buys will eventually overdose (one dose each,
        // since PickPasserBy no longer re-sells). TickOverdose drains the queue no faster than OverdoseIntervalHours.
        private static void MaybeScheduleOverdose(Human buyer)
        {
            try
            {
                if (buyer == null) return;
                int id = SafeId(buyer); if (id == -1) return;
                if (id == _odBuyerId || _odQueue.Contains(id)) return;   // already pending / already queued
                _odQueue.Add(id);
            }
            catch { }
        }

        // Drains the overdose queue: fires a pending overdose when its time comes, else schedules the next queued buyer
        // - but never closer than OverdoseIntervalHours apart, so arrests stagger. Every buyer gets their turn.
        private static void TickOverdose()
        {
            try
            {
                // 1) a buyer is scheduled - fire when their moment arrives.
                if (_odBuyerId != -1)
                {
                    if (Herd.NowHours() < _odAtHours) return;
                    // One physical arrest at a time (Arrest is a single state machine). If one is mid-flight, DEFER a
                    // little rather than dropping it, so the incident isn't lost - it fires once free.
                    if (Arrest.Active) { _odAtHours = Herd.NowHours() + 0.02f; return; }
                    int fireId = _odBuyerId; _odBuyerId = -1;
                    var b = FindBuyerById(fireId);
                    if (b == null || !Alive(b)) { Log("scheduled overdose buyer is gone; skipping."); return; }
                    Log($"OVERDOSE: {NarcoticsPlugin.Name(b)} overdoses in public.");
                    // openCase=true -> OnSubjectArrested opens the case on the FIRST arrest, or adds a new search lead
                    // to the SAME case on later arrests (one cook/drug at a time; serial-killer style).
                    Arrest.Begin(b, true);
                    return;
                }

                // 2) nothing scheduled - pull the next queued buyer once the interval has elapsed SINCE THE LAST ARREST
                //    (OnArrestMade sets _odNextEligible = arrest time + OverdoseIntervalHours). Don't pull while an arrest
                //    is still approaching (Arrest.Active) - wait until it's cuffed, then OverdoseIntervalHours more.
                if (_odQueue.Count == 0) return;
                if (Arrest.Active) return;                      // an arrest is in flight; let it finish first
                if (Herd.NowHours() < _odNextEligible) return;  // wait OverdoseIntervalHours after the last arrest
                int id = _odQueue[0]; _odQueue.RemoveAt(0);
                var buyer = FindBuyerById(id);
                if (buyer == null || !Alive(buyer)) return;   // dead/gone - drop and let the next tick take the following one
                _odBuyerId = id;
                _odAtHours = Herd.NowHours() + Math.Max(0.1f, NarcoticsConfig.OverdoseDelayMinutes) / 60f;
                Log($"OVERDOSE SCHEDULED: {NarcoticsPlugin.Name(buyer)} in ~{NarcoticsConfig.OverdoseDelayMinutes:0} in-game min ({_odQueue.Count} more queued).");
            }
            catch (Exception e) { Log($"TickOverdose error: {e.Message}"); }
        }

        // Called by Arrest when a subject is actually cuffed (EnterDetain). Starts the interval clock FROM THE ARREST
        // (user: "N in-game minutes from the moment the last overdose was arrested") for BOTH the next overdose AND the
        // dealer's next sale - so the dealer meets a new buyer, and that buyer later overdoses, OverdoseIntervalMinutes
        // after THIS arrest.
        internal static void OnArrestMade()
        {
            try
            {
                float t = Herd.NowHours() + Math.Max(0.02f, NarcoticsConfig.OverdoseIntervalMinutes / 60f);
                _odNextEligible = t;
                for (int k = 0; k < Dealers.Count; k++) { var dl = Dealers[k]; if (dl != null) dl.NextSell = t; }
            }
            catch { }
        }

        private static Human FindBuyerById(int id)
        {
            try { for (int i = 0; i < Buyers.Count; i++) { var b = Buyers[i]; if (b != null && SafeId(b) == id) return b; } } catch { }
            return null;
        }

        // Periodically re-assert each buyer's sighting of their dealer so it stays notable + recent (not evicted) for
        // interrogation. CRITICAL: we do NOT call UpdateLastSighting here - that re-snapshots the dealer's LIVE position,
        // which made the recorded "where I saw them" drift to wherever the dealer wandered (off-camera). Instead
        // FlagSightingNotable refreshes only the EXISTING entry's recency + notability, LEAVING its node (the deal spot
        // the sale captured) intact, so the witness keeps reporting the real deal location.
        private static void TickReinforceSightings()
        {
            try
            {
                if (_buyerDealer.Count == 0) return;
                if (NarcCase.IsSolved()) return;   // case solved -> stop reinforcing; the investigation is over
                if (Time.time < _nextSightingReinforce) return;
                _nextSightingReinforce = Time.time + 5f;   // keep the dealer the newest/sole-notable sighting between the buyer seeing new people
                var ids = new List<int>(_buyerDealer.Keys);
                for (int i = 0; i < ids.Count; i++)
                {
                    int bid = ids[i];
                    Human dealer = null; try { dealer = _buyerDealer[bid]; } catch { }
                    if (dealer == null || !Alive(dealer)) continue;
                    var buyer = FindBuyerById(bid);
                    if (buyer == null || !Alive(buyer)) continue;
                    NewNode spot = null; try { _buyerSpot.TryGetValue(bid, out spot); } catch { }
                    FlagSightingNotable(buyer, dealer, spot);   // refresh recency/notability + re-anchor location; NO position re-snapshot
                }
            }
            catch (Exception e) { Log($"TickReinforceSightings error: {e.Message}"); }
        }

        // Make `seen` (the dealer) the DOMINANT "suspicious" sighting in `witness`'s memory so the vanilla "did you see
        // anyone suspicious?" answer names the dealer on the FIRST ask. We make it the lone poi/run-flagged entry AND
        // strictly the newest (recency is the engine's only ordering key; eviction removes the oldest-time entry), and
        // age every other entry just behind it.
        //
        // We deliberately do NOT touch `s.node`. Decode (2026-10-07): the witness's "I saw them at <X>" location is
        // composed from `Sighting.node`, which is a node-GRID coord the game resolves via PathFinder - NOT a world
        // position. UpdateLastSighting (called at the sale, when the dealer is locked on the meet node) already stored a
        // resolvable node; an earlier attempt to overwrite it with `anchorNode.position` (world meters) made the lookup
        // fail, so the witness reported appearance + time but no LOCATION. `anchorNode` is now unused (kept for the
        // signature); the deal location rides along in the node UpdateLastSighting captured.
        private static void FlagSightingNotable(Human witness, Human seen, NewNode anchorNode)
        {
            try
            {
                var ls = witness != null ? witness.lastSightings : null;
                if (ls == null || seen == null) return;
                int sid = SafeId(seen);
                float now = Herd.NowHours();   // = SessionData.gameTime, the units Sighting.time uses

                // Snapshot keys first (don't enumerate the dict while touching it).
                var keys = new List<Human>();
                try { foreach (var key in ls.Keys) if (key != null) keys.Add(key); } catch { }

                for (int i = 0; i < keys.Count; i++)
                {
                    var key = keys[i];
                    try
                    {
                        var s = ls[key];
                        if (s == null) continue;
                        if (SafeId(key) == sid)
                        {
                            s.poi = true; s.run = true; s.time = now;   // the lone notable, and newest
                            // DO NOT touch s.node. It is a node-GRID coord the game resolves via PathFinder to name the
                            // location; UpdateLastSighting at the sale already stored the dealer's real currentNode (the
                            // meet node it's locked on), which resolves. Overwriting it with a world position broke that
                            // lookup, so the witness gave appearance+time but no WHERE. Leaving it keeps the deal location.
                        }
                        else { s.poi = false; s.run = false; if (s.time >= now) s.time = now - 0.001f; } // keep it strictly behind the dealer
                    }
                    catch { }
                }
            }
            catch { }
        }

        // When the player solves/closes the case (vanilla resolution), stand the operation down: release the cuffed
        // buyer and wipe every drug object (lab stash, bins, all inventories). "Wipe them all once solved." Stop()
        // does the dose wipe (DestroyAll + DestroyWorldDoses). Returns true if it stood down this tick.
        private static bool TickCaseResolution()
        {
            try
            {
                if (!NarcCase.IsOpen || !NarcCase.IsSolved()) return false;
                Log("case solved/closed - standing the operation down and wiping all drug objects.");
                Human cookToJail = CookHuman;   // capture before Stop() nulls it
                try { Arrest.Stop(); } catch { }
                NarcCase.MarkClosed();
                Stop(preserveSolvedCaseEvidence: true);   // stop the operation but retain evidence image sources for the solved case
                // Remove the cook from the world (jailed), like an arrested murderer disappears once the case is solved.
                try { if (cookToJail != null) { cookToJail.RemoveFromWorld(true); Log($"cook {NarcoticsPlugin.Name(cookToJail)} removed from the world (jailed) on resolution."); } }
                catch (Exception e) { Log($"cook removal failed: {e.Message}"); }
                return true;
            }
            catch (Exception e) { Log($"TickCaseResolution error: {e.Message}"); return false; }
        }

        // ------------------------------------------------------------------ helpers

        // A single STABLE node inside the lab, cached and reused for everyone who walks there. Critical: computing
        // a fresh FindSafeTeleport each tick returned a shifting node, so Herd kept recreating the goal every frame
        // and the NPC never finished opening a door (the "stuck leaving their apartment" jam). Motives pinned to a
        // node computed ONCE - same idea here.
        private static NewNode LabNode(Human mover)
        {
            try { if (_labNode != null && _labNode.Pointer != IntPtr.Zero) return _labNode; } catch { _labNode = null; }
            NewNode n = null;
            try { n = mover.FindSafeTeleport(Lab, false, true); } catch { }
            if (n != null) { _labNode = n; return n; }
            try { return Lab.anchorNode; } catch { }   // transient fallback until the lab streams; not cached
            return null;
        }

        // A random walkable node inside the lab for the cook to potter to (so they pace around while cooking instead of
        // standing frozen on one node). Falls back to `center` if the lab's nodes aren't available.
        private static NewNode LabPotterNode(NewNode center)
        {
            try
            {
                if (Lab == null) return center;
                var nodes = Lab.nodes; if (nodes == null || nodes.Count == 0) return center;
                // Nodes our placed lab equipment sits on - the cook must NOT pace onto these or it clips/sticks inside the
                // machinery. Collect each cluster's anchor + every furniture piece in it.
                var furnNodes = new HashSet<IntPtr>();
                try
                {
                    for (int i = 0; i < _labFurniture.Count; i++)
                    {
                        var fc = _labFurniture[i]; if (fc == null) continue;
                        try { var an = fc.anchorNode; if (an != null) furnNodes.Add(an.Pointer); } catch { }
                        try { var cl = fc.clusterList; if (cl != null) for (int j = 0; j < cl.Count; j++) { var fl = cl[j]; if (fl == null) continue; try { var fan = fl.anchorNode; if (fan != null) furnNodes.Add(fan.Pointer); } catch { } } } catch { }
                    }
                }
                catch { }
                IntPtr curPtr = IntPtr.Zero; try { if (_cookPotter != null) curPtr = _cookPotter.Pointer; } catch { }
                var cand = new List<NewNode>();
                for (int i = 0; i < nodes.Count; i++)
                {
                    var n = nodes[i]; if (n == null) continue;
                    bool bad = false; try { bad = n.isObstacle || n.isInaccessable || n.noAccess || !n.isConnected; } catch { }
                    if (bad) continue;
                    IntPtr p; try { p = n.Pointer; } catch { continue; }
                    if (furnNodes.Contains(p)) continue;   // don't stand inside the equipment
                    if (p == curPtr) continue;             // pick a DIFFERENT node than the current one so the cook actually paces
                    cand.Add(n);
                }
                if (cand.Count == 0) return center;
                return cand[_rng.Next(cand.Count)];
            }
            catch { return center; }
        }

        private static void EnsureDrop(Dealer dl)
        {
            try
            {
                if (dl == null || dl.Drop != null) return;
                NewGameLocation loc = null; NewNode node = null;
                try { loc = dl.H.currentGameLocation; } catch { }
                try { if (loc != null) node = dl.H.FindSafeTeleport(loc, false, true); } catch { }
                if (node == null) { try { node = dl.H.currentNode; } catch { } }
                dl.Drop = node; dl.DropLoc = loc;
            }
            catch { }
        }

        private static int LabStashCount() { PruneLabStash(); return LabStash.Count; }
        private static void PruneLabStash() { try { for (int i = LabStash.Count - 1; i >= 0; i--) if (!Dose.Alive(LabStash[i])) LabStash.RemoveAt(i); } catch { } }
        private static int DealerStashCount(Dealer dl) { if (dl == null) return 0; if (dl.Container != null) return dl.BinDoses; PruneDealerStash(dl); return dl.Stash.Count; }

        // Authoritative drop count (reads the bin page for container drops); for the F3 teleport verification.
        internal static int DropCount(Dealer dl) { if (dl == null) return 0; if (dl.Container != null) return Dose.CountInBin(dl.Container); PruneDealerStash(dl); return dl.Stash.Count; }
        private static void PruneDealerStash(Dealer dl) { try { for (int i = dl.Stash.Count - 1; i >= 0; i--) if (!Dose.Alive(dl.Stash[i])) dl.Stash.RemoveAt(i); } catch { } }

        private static void DestroyWorldDoses()
        {
            try { for (int i = 0; i < LabStash.Count; i++) { try { var it = LabStash[i]; if (it != null) it.SafeDelete(true); } catch { } } } catch { }
            LabStash.Clear();
            try { for (int i = 0; i < _labDiscoveryDoses.Count; i++) { try { var it = _labDiscoveryDoses[i]; if (it != null) it.SafeDelete(true); } catch { } } } catch { }
            _labDiscoveryDoses.Clear();
            try
            {
                for (int k = 0; k < Dealers.Count; k++)
                {
                    var dl = Dealers[k];
                    if (dl.Container != null) { try { Dose.ClearBin(dl.Container); } catch { } dl.BinDoses = 0; }
                    for (int i = 0; i < dl.Stash.Count; i++) { try { var it = dl.Stash[i]; if (it != null) it.SafeDelete(true); } catch { } }
                    dl.Stash.Clear();
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------ queries / display

        internal static string RoleOf(Human h)
        {
            try
            {
                if (!Active || h == null) return "-";
                if (CookHuman != null && h.humanID == CookHuman.humanID) return "COOK";
                for (int i = 0; i < Distributors.Count; i++) { var d = Distributors[i]; if (d.H != null && d.H.humanID == h.humanID) return "DISTRIBUTOR"; }
                for (int k = 0; k < Dealers.Count; k++) { var dl = Dealers[k]; if (dl.H != null && dl.H.humanID == h.humanID) return "DEALER"; }
                for (int b = 0; b < Buyers.Count; b++) { var by = Buyers[b]; if (by != null && by.humanID == h.humanID) return "BUYER"; }
            }
            catch { }
            return "-";
        }

        internal static string StateLabel
        {
            get
            {
                try
                {
                    if (!Active) return "inactive";
                    if (_job != Job.None && _mover != null) return $"{_job} {NarcoticsPlugin.Name(_mover)}";
                    return "waiting";
                }
                catch { return "?"; }
            }
        }

        internal static List<string> RosterLines()
        {
            var lines = new List<string>();
            try
            {
                if (!Active) return lines;
                var labNode = LabAnchor();
                lines.Add($"COOK        {NarcoticsPlugin.Name(CookHuman)}   x{Dose.CountIn(CookHuman)}{LineDist(CookHuman, labNode)}   [{StateLabel}]");
                lines.Add($"LAB STASH   x{LabStashCount()}");
                for (int i = 0; i < Distributors.Count; i++)
                {
                    var d = Distributors[i]; if (d.H == null) continue;
                    lines.Add($"DISTRIBUTOR {NarcoticsPlugin.Name(d.H)}   x{Dose.CountIn(d.H)}{(d.Collected ? " (done)" : "")}{LineDist(d.H, null)}{Mark(d.H)}");
                }
                for (int k = 0; k < Dealers.Count; k++)
                {
                    var dl = Dealers[k]; if (dl.H == null) continue;
                    lines.Add($"DEALER      {NarcoticsPlugin.Name(dl.H)}   x{Dose.CountIn(dl.H)}   drop:{DealerStashCount(dl)}{LineDist(dl.H, dl.Drop)}{Mark(dl.H)}");
                }
                if (Buyers.Count > 0)
                {
                    lines.Add($"BUYERS      x{Buyers.Count} (hold doses)");
                    for (int b = 0; b < Buyers.Count && b < 6; b++) { var by = Buyers[b]; if (by == null) continue; lines.Add($"  buyer     {NarcoticsPlugin.Name(by)}   x{Dose.CountIn(by)}{Mark(by)}"); }
                }
            }
            catch { }
            return lines;
        }

        private static string Mark(Human h) { try { if (_mover != null && h != null && _mover.humanID == h.humanID) return "  <= " + _job; } catch { } return ""; }

        // Distance readout for a roster line: distance to the mover's live target if this is the active mover,
        // else to a role-relevant fallback node (cook->lab, dealer->their drop). Shown so the player can gauge
        // progress from the apartment without tailing.
        private static string LineDist(Human h, NewNode fallback)
        {
            try
            {
                NewNode n = (_mover != null && h != null && _mover.humanID == h.humanID) ? _targetNode : fallback;
                float d = DistM(h, n);
                return (n != null && d >= 0f) ? $"   {d:0}m" : "";
            }
            catch { return ""; }
        }

        private static float DistM(Human h, NewNode node)
        {
            try { var cn = h != null ? h.currentNode : null; if (cn != null && node != null) return Vector3.Distance(cn.position, node.position); } catch { }
            return -1f;
        }

        private static NewNode LabAnchor() { try { return Lab != null ? Lab.anchorNode : null; } catch { return null; } }

        // Everyone involved in the operation, for the debug "teleport to next NPC" cycle.
        internal static List<Human> InvolvedNPCs()
        {
            var l = new List<Human>();
            try
            {
                if (CookHuman != null) l.Add(CookHuman);
                for (int i = 0; i < Distributors.Count; i++) if (Distributors[i].H != null) l.Add(Distributors[i].H);
                for (int k = 0; k < Dealers.Count; k++) if (Dealers[k].H != null) l.Add(Dealers[k].H);
            }
            catch { }
            return l;
        }

        internal static void Status()
        {
            if (!Active) { Log("STATUS: no active operation."); return; }
            Log($"STATUS: cook={NarcoticsPlugin.Name(CookHuman)} lab={Cook.LName(Lab)} cookDoses={Dose.CountIn(CookHuman)} labStash={LabStashCount()} job={_job} dists={Distributors.Count} dealers={Dealers.Count}");
        }

        internal static NewGameLocation LabLoc => Lab;

        // The lab's index in CityData.gameLocationDirectory, as a string - the identifier the resolve-form location
        // question matches against (vanilla stores the killer-home the same way). Null if not found.
        internal static string LabDirIndex()
        {
            try
            {
                var dir = CityData.Instance != null ? CityData.Instance.gameLocationDirectory : null;
                if (dir == null || Lab == null) return null;
                IntPtr want = IntPtr.Zero; try { want = Lab.Pointer; } catch { }
                for (int i = 0; i < dir.Count; i++) { var gl = dir[i]; if (gl != null) { IntPtr p = IntPtr.Zero; try { p = gl.Pointer; } catch { } if (p == want) return i.ToString(); } }
            }
            catch { }
            return null;
        }

        // Is the cook currently restrained (the player has arrested them)? Used to grade the "arrest" resolve row.
        internal static bool CookRestrained()
        {
            try { var ck = CookHuman; return ck != null && ck.ai != null && ck.ai.restrained; } catch { return false; }
        }

        // ------------------------------------------------------------------ staffing

        private static Human PickCook()
        {
            try
            {
                var cd = CityData.Instance;
                var cits = cd != null ? cd.citizenDirectory : null;
                if (cits == null) return null;
                int playerId = PlayerId();
                var pool = new List<Human>();
                for (int i = 0; i < cits.Count; i++)
                {
                    Human h = null; try { h = cits[i]; } catch { }
                    if (h == null) continue;
                    try { if (h.humanID == playerId) continue; } catch { }
                    try { if (h.isHomeless) continue; } catch { }
                    try { if (h.home == null) continue; } catch { continue; }
                    try { if (h.job == null) continue; } catch { }
                    try { if (IsEnforcer(h.humanID)) continue; } catch { }   // never an enforcer (they'd be unarrestable + it reads wrong; vanilla avoids them too)
                    if (!Alive(h)) continue;
                    pool.Add(h);
                }
                Log($"cook pool: {pool.Count} housed, employed citizens.");
                if (pool.Count == 0) return null;
                return pool[_rng.Next(pool.Count)];
            }
            catch (Exception e) { Log($"PickCook error: {e.Message}"); return null; }
        }

        private static List<Human> SelectHomeless(int n, HashSet<int> used)
        {
            var result = new List<Human>();
            try
            {
                var cd = CityData.Instance;
                var homeless = cd != null ? cd.homelessDirectory : null;
                if (homeless == null) { Log("no homelessDirectory; can't pick dealers."); return result; }
                var pool = new List<Human>();
                for (int i = 0; i < homeless.Count; i++)
                {
                    Human h = null; try { h = homeless[i]; } catch { }
                    if (h == null || !Alive(h)) continue;
                    if (used.Contains(SafeId(h))) continue;
                    pool.Add(h);
                }
                Log($"homeless pool: {pool.Count} available.");
                Draw(pool, n, used, result);
            }
            catch (Exception e) { Log($"SelectHomeless error: {e.Message}"); }
            return result;
        }

        private static List<Human> SelectCitizens(int n, HashSet<int> used)
        {
            var result = new List<Human>();
            try
            {
                var cd = CityData.Instance;
                var cits = cd != null ? cd.citizenDirectory : null;
                if (cits == null) return result;
                int playerId = PlayerId();
                var pool = new List<Human>();
                for (int i = 0; i < cits.Count; i++)
                {
                    Human h = null; try { h = cits[i]; } catch { }
                    if (h == null || !Alive(h)) continue;
                    int id = SafeId(h);
                    if (id == playerId || used.Contains(id)) continue;
                    try { if (h.isHomeless) continue; } catch { }
                    if (IsEnforcer(id)) continue;   // keep enforcers out of the operation crew
                    pool.Add(h);
                }
                Log($"citizen pool: {pool.Count} available.");
                Draw(pool, n, used, result);
            }
            catch (Exception e) { Log($"SelectCitizens error: {e.Message}"); }
            return result;
        }

        private static void Draw(List<Human> pool, int n, HashSet<int> used, List<Human> result)
        {
            for (int k = 0; k < n && pool.Count > 0; k++)
            {
                int idx = _rng.Next(pool.Count);
                var h = pool[idx]; pool.RemoveAt(idx);
                result.Add(h);
                try { used.Add(h.humanID); } catch { }
            }
        }

        // ------------------------------------------------------------------ misc

        private static int PlayerId() { try { if (Player.Instance != null) return Player.Instance.humanID; } catch { } return -1; }
        private static int SafeId(Human h) { try { return h.humanID; } catch { return -1; } }
        private static bool Alive(Human h) { if (h == null) return false; try { return !h.isDead; } catch { return true; } }
        private static string JobName(Human h) { try { return h.job != null ? h.job.name : "-"; } catch { return "?"; } }
        static void Log(string m) => NarcoticsPlugin.Log.LogInfo("[Narcotics][op] " + m);
    }
}
