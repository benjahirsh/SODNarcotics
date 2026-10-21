using System;
using UnityEngine;

namespace Narcotics
{
    // The single persistent custom Case the player works. Opened automatically the moment a buyer is restrained
    // after overdosing in public (Arrest.cs). ONE case at a time, like a vanilla murder: a later arrest while a
    // case is still open feeds that case (the player can search the new arrestee too) rather than spawning a
    // duplicate. It is created silently (the direct `new Case(...)` ctor, NOT CreateNewCase, which pops a confirm
    // dialog), selected on the board, then announced exactly like a murder: the big centre banner + the
    // police-scanner blip, so "the player gets the new case notification".
    //
    // Objectives (e.g. an initial "INVESTIGATE THIS NPC") and evidence/fact links (dose prints -> dealer, lab
    // stash -> cook) are Phase 5 and deliberately NOT built here yet - the user wants to test the vanilla
    // resolution/hand-in form against a bare case first.
    internal static class NarcCase
    {
        internal static Case Current;
        internal static int SubjectId = -1;   // humanID of the most recent arrested buyer tied to this case
        // Active "Search <name>" leads keyed by the arrested buyer's humanID, so each arrest has its OWN lead and
        // searching one buyer clears only THAT lead. Lets multiple overdoses accumulate independent search leads on the
        // one case (serial style) instead of collapsing into a single shared "Search" lead.
        private static readonly System.Collections.Generic.Dictionary<int, string> _leads = new System.Collections.Generic.Dictionary<int, string>();
        internal static bool HasLead(int humanID) { try { return _leads.ContainsKey(humanID); } catch { return false; } }

        // The resolve-question labels. ResolveQuestion.GetText looks `name` up as a missions.postings KEY, so we
        // register these verbatim as self-resolving keys (RegisterStrings) - that makes the form rows, the objectives,
        // AND the verdict reveal all show the text, instead of blank.
        internal const string QProducing = "Who is producing the drug?";
        internal const string QLabLocation = "Where is the drug lab?";
        internal const string QCookArrested = "Has the cook been arrested?";
        internal const string QTransporting = "Who is transporting the drug from the lab?";
        internal const string QDealing = "Who is dealing the drug?";

        private static bool _pendingVerdict;   // submit -> short processing wait -> Case.Resolve() runs the verdict
        private static float _pendingVerdictAt;
        private const float ProcessingDelaySec = 5f;
        private static bool _resolvingSelf;    // true only inside OUR TickVerdict Resolve call
        internal static bool ResolvingSelf { get { return _resolvingSelf; } }   // Patch_CaseResolve swallows any Resolve that isn't ours
        private const int SolveSocialCredit = 500;   // social-credit bump on a solved case (Resolve only grants money, never social credit for custom)
        // Pointers of cases we have already SOLVED. The decode proved the double reveal+banner is a SECOND Case.Resolve()
        // on an already-resolved case; the old Current-pointer guard misses it once Current is nulled by tear-down. This
        // latch survives tear-down: any Resolve on a latched pointer is swallowed (Patch_CaseResolve). Only solved cases
        // are latched - a FAILED case reopens to handInCollected and must stay re-resolvable for the retry.
        internal static readonly System.Collections.Generic.HashSet<System.IntPtr> ResolvedPtrs = new System.Collections.Generic.HashSet<System.IntPtr>();

        internal static bool IsOpen
        {
            get { try { return Current != null && Current.Pointer != IntPtr.Zero; } catch { return false; } }
        }

        // The case has actually been RESOLVED (verdict ran, solved). Used to release the cuffed buyer + stand the
        // operation down. NOT merely "submitted": a submitted case is still mid-processing (the verdict - Case.Resolve
        // - runs a few seconds later and may come back UNSOLVED, which must NOT count as solved or we'd end the case
        // on a failed attempt). So we rely on the real isSolved flag + the closable/archived end states.
        internal static bool IsSolved()
        {
            try
            {
                if (!IsOpen) return false;
                try { if (Current.isSolved) return true; } catch { }
                try
                {
                    var st = Current.caseStatus;
                    if (st == Case.CaseStatus.closable || st == Case.CaseStatus.archived)
                        return true;
                }
                catch { }
                try
                {
                    var cpc = CasePanelController.Instance;
                    if (cpc != null && cpc.activeCases != null && !cpc.activeCases.Contains(Current)) return true;   // off the board
                }
                catch { }
            }
            catch { }
            return false;
        }

        // Open the persistent case for an arrested buyer. Idempotent: if a case is already open, just records the
        // latest subject and returns false (no second case, no second banner). Returns true if a NEW case opened.
        internal static bool Open(Human subject)
        {
            try
            {
                int sid = -1; try { if (subject != null) sid = subject.humanID; } catch { }

                if (IsOpen)
                {
                    if (sid != -1) SubjectId = sid;
                    Log($"arrest of {NarcoticsPlugin.Name(subject)} folds into the open case '{SafeName(Current)}' (one case at a time; no new notification).");
                    return false;
                }

                var cpc = CasePanelController.Instance;
                if (cpc == null) { Log("CasePanelController.Instance null - cannot open a case yet (load a game)."); return false; }

                // CaseType.custom (NOT murder). CaseType.murder is a HARD no: MurderController.OnVictimDiscovery
                // enforces exactly ONE active murder case at a time (it FindIndex-es activeCases for any caseType==
                // murder case whose status is not submitted/closable/archived and, if one exists, SKIPS creating the
                // real murder's case AND its banner, which are both built inside CasePanelController.CreateNewCase).
                // Our persistent case would occupy that slot for its whole ~12h life and silently eat every real
                // murder in that window. Custom avoids the gate entirely (and has no murder-resolve path to NPE). We
                // drive objective text (Patch_ObjectiveSetup), the resolve-question rendering (Patch_InputFieldSetup),
                // and the hand-in ourselves.
                string caseTitle = "New Drug on the Street"; try { if (!string.IsNullOrEmpty(Operation.CaseTitle)) caseTitle = Operation.CaseTitle; } catch { }
                Case c = new Case(caseTitle, Case.CaseType.custom, Case.CaseStatus.handInNotCollected);
                bool present = false; try { present = cpc.activeCases != null && cpc.activeCases.Contains(c); } catch { }
                if (!present) { try { cpc.activeCases.Add(c); } catch (Exception e) { Log($"activeCases.Add failed: {e.Message}"); } }
                try { cpc.SetActiveCase(c); } catch (Exception e) { Log($"SetActiveCase failed: {e.Message}"); }
                try { cpc.UpdateCaseButtonsActive(); } catch { }
                try { cpc.UpdateCaseControls(); } catch { }

                Current = c; SubjectId = sid; _pendingVerdict = false; _resolvingSelf = false;
                try { ResolvedPtrs.Clear(); } catch { }   // fresh case: drop any stale solved-pointer latch
                try { _leads.Clear(); } catch { }         // fresh case: drop any stale search leads

                // CaseType.custom never auto-resolves objective text and SetStatus never adds the hand-in machinery
                // for it, so we drive the whole case ourselves:
                //   1) the resolution-form questions (the engine only generates these for murder cases),
                //   2) the "Search the suspect" lead (Patch_ObjectiveSetup copies our entryRef onto the name),
                //   3) the hand-in destination: Case.handIn must hold City Hall's address id or GetClosestHandIn
                //      returns null and BOTH "Plot Route to Hand-in" and the submit objective silently fail. We fill
                //      it, add the "Collect resolution form from City Hall" lead, and ValidationCheck() to make the
                //      submit objective evaluate now. Patch_GetCaseForm + Patch_HandInCase admit our custom case to the
                //      tray's collect (GetCaseForm) + submit (HandInCase) actions, which vanilla gates to murder/
                //      mainStory/retirement only.
                AddResolveQuestions(c, subject);
                AddInvestigateObjective(c, subject);
                PopulateHandIn(c);
                AddCollectObjective(c);
                try { c.ValidationCheck(); } catch (Exception e) { Log($"ValidationCheck threw: {e.Message}"); }
                try
                {
                    var hi = c.GetClosestHandIn();
                    string hn = "?"; if (hi != null) { try { hn = hi.GetName(); } catch { } }
                    Log($"GetClosestHandIn after populate: {(hi != null ? "resolved a tray ('" + hn + "')" : "NULL - no tray resolved from the handIn ids (Plot Route + submit objective will fail)")}");
                }
                catch (Exception e) { Log($"GetClosestHandIn threw: {e.Message}"); }

                // Notify exactly like a murder arrives: the police-scanner line FIRST so it lands WITH the big banner.
                Scanner("[Scanner] Reported Possession");
                try { Diagnostics.Announce("NEW NARCOTICS CASE"); } catch (Exception e) { Log($"banner error: {e.Message}"); }

                int count = -1; try { count = cpc.activeCases != null ? cpc.activeCases.Count : -1; } catch { }
                Log($"OPENED custom case '{SafeName(c)}' for {NarcoticsPlugin.Name(subject)} (activeCases={count}). Open the case board - it is selected, no popup.");
                // Persist the immutable crew, lab and dead-drop identities once there is a real case.
                try { InvestigationPersistence.RecordOpenedCase(); } catch { }
                return true;
            }
            catch (Exception e) { Log($"Open error: {e}"); return false; }
        }

        // The arrest hook (called when an overdosed buyer is restrained). FIRST arrest opens the case (Open adds its
        // own "search the suspect" lead). Each LATER arrest - there is only ever one cook/drug at a time, so it is
        // always the SAME case - adds ANOTHER "Search <buyer>" objective to that case + a scanner blip, with NO new
        // case and NO banner. This mirrors how vanilla serial killers work (verified 2026-10-04): the game keeps one
        // currentActiveCase and surfaces each new victim as another objective via Case.AddObjective, never a 2nd case.
        internal static bool OnSubjectArrested(Human subject)
        {
            try
            {
                if (!IsOpen) return Open(subject);   // first arrest: opens the case + adds the first search lead

                if (subject != null) { try { SubjectId = subject.humanID; } catch { } }
                AddInvestigateObjective(Current, subject);   // another "Search <buyer> at <loc>" lead on the same case
                try { Current.ValidationCheck(); } catch { }
                try { var cpc = CasePanelController.Instance; if (cpc != null) { cpc.SetActiveCase(Current); cpc.UpdateCaseButtonsActive(); cpc.UpdateCaseControls(); } } catch { }
                string nm = CleanName(subject);
                Scanner("[Scanner] Reported Possession - search " + nm);
                Log($"ADDITIONAL ARREST: {nm} (#{(subject != null ? subject.humanID : -1)}) folds into the open case '{SafeName(Current)}' as a new search lead (no new case, serial-style).");
                return false;
            }
            catch (Exception e) { Log($"OnSubjectArrested error: {e.Message}"); return false; }
        }

        // The bottom-left police-scanner feed blip (the per-event notification; distinct from the big banner).
        internal static void Scanner(string message, InterfaceControls.Icon icon = InterfaceControls.Icon.handcuffs, bool scannerSound = true)
        {
            try
            {
                var ic = InterfaceController.Instance;
                if (ic == null) return;
                // The 5th arg (additionalSFX) overrides the message's sound. For an ARREST alert we force the enforcer
                // scanner blip (so every arrest gets it, not just the first). For the case-form COLLECT/SUBMIT banners we
                // pass null so the game plays that notification's OWN (correct) sound - forcing the scanner blip there was
                // overriding the real submission sound.
                AudioEvent sfx = null; if (scannerSound) { try { sfx = AudioControls.Instance.enforcerScannerMsg; } catch { } }
                ic.NewGameMessage(InterfaceController.GameMessageType.notification, 0, message, icon, sfx);
            }
            catch (Exception e) { Log($"Scanner error: {e.Message}"); }
        }

        // Called by Patch_GetCaseForm the moment the player collects the form at the tray, BEFORE SetStatus spawns the
        // objective banners. Fires the vanilla-style "Collected Form: <case>" scanner blip, then the typewriter
        // day/time/location game-header - the same sequence (scanner -> header -> objective banners) vanilla plays
        // for murder/mainStory/retirement collects but skips for our custom case.
        internal static void OnFormCollected()
        {
            // Vanilla sequence: the "Collected Form" scanner blip + the case-title typewriter header fire together,
            // then a second typewriter header with the time/date/location.
            try { Scanner("Collected Form: " + SafeName(Current), InterfaceControls.Icon.resolve, scannerSound: false); } catch (Exception e) { Log($"OnFormCollected scanner error: {e.Message}"); }
            Header(SafeName(Current));          // header 1: the case title (with the scanner)
            Header(TimeLocationString());       // header 2: time, date, location
        }

        // Called by Patch_HandInCase when the player submits the completed form. Fires the vanilla "Case submitted
        // for processing" scanner blip (ui.gamemessage key "case submitted") with the resolve icon.
        internal static void OnFormSubmitted()
        {
            string msg = ""; try { msg = Strings.Get("ui.gamemessage", "case submitted"); } catch { }
            if (string.IsNullOrEmpty(msg)) msg = "Case submitted for processing. Please wait for results.";
            try { Scanner(msg, InterfaceControls.Icon.resolve, scannerSound: false); } catch (Exception e) { Log($"OnFormSubmitted error: {e.Message}"); }
            // SetStatus(submitted) only set each question's isCorrect + repainted; the actual VERDICT (isSolved, the
            // credit reward/penalty via GameplayController.AddMoney, the solved/unsolved display, -> closable) lives in
            // Case.Resolve(). Vanilla reaches it via the "case processing" wait; we run it ourselves after a short
            // processing delay. Resolve() is SAFE for a custom case (its murder derefs are caseType-gated/null-guarded).
            // Each submit arms a fresh verdict - the _resolvingSelf guard is per-call, so a retry after a fail re-resolves
            // cleanly with no stale/duplicate (that was the double-resolve + stuck-processing bug).
            _pendingVerdict = true; _pendingVerdictAt = UnityEngine.Time.time;
        }

        // Called every frame (NarcoticsHotkey.Update). After the processing delay, drive the ONE Case.Resolve() that
        // the engine never calls for a custom case (decode: zero engine callers of Resolve for custom). Resolve() is
        // safe + nearly complete for custom: it plays the answer reveal + the Solved/Unsolved banner, pays the money
        // reward on solve / penalty + reopen-for-retry on fail, and sets closable - all in vanilla ordering. The ONE
        // thing it skips for custom is SOCIAL CREDIT (ExecuteMissionCompleteDisplay bare-returns for caseType 3/4), so
        // we inject only that. A solved case's pointer is latched (ResolvedPtrs) so a stray second Resolve cannot
        // replay the whole sequence (that was the double reveal+banner bug).
        internal static void TickVerdict()
        {
            if (!_pendingVerdict) return;
            try
            {
                if (UnityEngine.Time.time - _pendingVerdictAt < ProcessingDelaySec) return;
                _pendingVerdict = false;
                if (!IsOpen) return;

                GradeSpecialQuestions(true);   // grade the arrest + location rows ourselves (engine's murder-linked checks don't fire for custom); log at the verdict

                // One engine-owned verdict. _resolvingSelf lets THIS call through Patch_CaseResolve; the latch below
                // swallows any later re-entry. Leave each question's reward/penalty INTACT so Resolve pays them (do
                // NOT zero them and do NOT hand-emit money - the engine owns money + ordering).
                _resolvingSelf = true;
                try { Current.Resolve(); } catch (Exception e) { Log($"Resolve threw: {e.Message}"); }
                _resolvingSelf = false;

                bool solved = false; try { solved = Current.isSolved; } catch { }
                if (solved)
                {
                    try { ResolvedPtrs.Add(Current.Pointer); } catch { }   // latch: never re-resolve this solved case
                    // Resolve pays the money reward but grants NO social credit for a custom case - inject it. With
                    // displayMessage=true it shows its own "+N" + any level-up banner + refreshes perks.
                    try { var gc = GameplayController.Instance; if (gc != null) gc.AddSocialCredit(SolveSocialCredit, true, "Narcotics case solved"); } catch (Exception e) { Log($"AddSocialCredit error: {e.Message}"); }
                }
                Log($"verdict: isSolved={solved} (money + banner owned by Case.Resolve; {(solved ? $"+{SolveSocialCredit} social credit injected + pointer latched" : "penalty paid by Resolve, case reopened for retry")}).");
            }
            catch (Exception e) { _pendingVerdict = false; Log($"TickVerdict error: {e.Message}"); }
        }

        // Grade the ARREST and LOCATION rows ourselves right before Resolve. Vanilla's arrestPurp check is tied to the
        // murder case's killer and its location check expects the engine's own evidence linkage - neither fires for a
        // CaseType.custom case, so we set each question's isCorrect from live game state: the arrest ticks when the cook
        // is restrained; the location ticks when the player's answer is the lab's directory index. (Citizen questions
        // the engine already grades correctly, so we leave them.) Logs the inputs so any residual mismatch is visible.
        internal static void GradeSpecialQuestions(bool log = false)
        {
            try
            {
                var c = Current; if (c == null || !IsOpen) return;
                var qs = c.resolveQuestions; if (qs == null) return;
                bool cookCuffed = Operation.CookRestrained();
                string labName = null; try { labName = Operation.LabLoc != null ? Operation.LabLoc.name : null; } catch { }
                // Did the player NAME the cook correctly in the producer row? The arrest row only ticks once the cook is
                // both identified AND in handcuffs (vanilla: the arrest check keys off the named killer).
                bool producerNamed = false;
                for (int i = 0; i < qs.Count; i++) { var pq = qs[i]; if (pq == null) continue; string pn = null; try { pn = pq.name; } catch { } if (pn == QProducing) { try { producerNamed = pq.isCorrect; } catch { } break; } }
                for (int i = 0; i < qs.Count; i++)
                {
                    var q = qs[i]; if (q == null) continue;
                    string nm = null; try { nm = q.name; } catch { }
                    if (nm == QCookArrested)
                    {
                        bool arrested = producerNamed && cookCuffed;
                        // Set BOTH isValid AND isCorrect: the LIVE checkbox painter (InputFieldController.UpdateCheckbox,
                        // resultsMode=false) reads the question's isValid byte (0xAC) as well as isCorrect (0xAD) to pick
                        // the tick sprite; the results screen reads only isCorrect. Setting only isCorrect is why the box
                        // graded correct at the end but never ticked LIVE while filling the form. Then repaint the row -
                        // the engine only auto-repaints a row when its OWN UpdateCorrect fires OnProgressChange, which our
                        // arrest row's never does (no murder wires its naming-question reference), so we drive it.
                        try { q.isValid = arrested; } catch { }
                        try { q.isCorrect = arrested; } catch { }
                        RepaintResolveRow(q);
                        if (log) Log($"[grade] cook arrested: namedCook={producerNamed} restrained={cookCuffed} -> {arrested}");
                    }
                    else if (nm == QLabLocation)
                    {
                        // The player's answer is stored as the location's NAME (e.g. 'Vacant address 1, Zeng Terrace'),
                        // so compare it to the lab's name (case/whitespace-insensitive), not the directory index.
                        string inp = null, inpEv = null; try { inp = q.input; } catch { } try { inpEv = q.inputtedEvidence; } catch { }
                        bool ok = !string.IsNullOrEmpty(labName) &&
                                  ((inp != null && string.Equals(inp.Trim(), labName.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                                   (inpEv != null && string.Equals(inpEv.Trim(), labName.Trim(), StringComparison.OrdinalIgnoreCase)));
                        try { q.isValid = ok; } catch { }   // live checkbox needs isValid too (see arrest row note above)
                        try { q.isCorrect = ok; } catch { }
                        RepaintResolveRow(q);
                        if (log) Log($"[grade] location: input='{inp}' inputEv='{inpEv}' labName='{labName}' -> {ok}");
                    }
                }
            }
            catch (Exception e) { if (log) Log($"GradeSpecialQuestions error: {e.Message}"); }
        }

        // Repaint ONE resolve-form row's checkbox so a correctness change WE made in data shows LIVE. The engine paints
        // a row's checkbox via InputFieldController.UpdateCheckbox (from the question's isValid/isCorrect), but only
        // calls it when the row's own UpdateCorrect succeeds and fires OnProgressChange - which our arrest/location
        // rows never do (no backing murder), so we call UpdateCheckbox ourselves. No-op when the resolve form isn't
        // open (e.g. grading at verdict time). Matches the live field to the question by pointer.
        private static void RepaintResolveRow(Case.ResolveQuestion q)
        {
            try
            {
                if (q == null) return;
                var rc = ResolveController.Instance; if (rc == null) return;
                var fields = rc.spawnedInputFields; if (fields == null) return;
                for (int i = 0; i < fields.Count; i++)
                {
                    var f = fields[i]; if (f == null) continue;
                    Case.ResolveQuestion fq = null; try { fq = f.question; } catch { }
                    if (fq == null) continue;
                    try { if (fq.Pointer != q.Pointer) continue; } catch { continue; }
                    try { f.UpdateCheckbox(); } catch { }
                    return;   // one field per question
                }
            }
            catch { }
        }

        // Register the resolve-question labels as self-resolving keys in missions.postings (key == value), so
        // ResolveQuestion.GetText (which treats `name` as a key) returns the text instead of blank. Deduped: once the
        // key resolves to itself it is already registered (a MISSING key returns the "new objective" fallback), so we
        // never re-write it and the game's string CSV does not accumulate duplicates across sessions.
        internal static void RegisterStrings()
        {
            TryRegisterKey("missions.postings", QProducing);
            TryRegisterKey("missions.postings", QLabLocation);
            TryRegisterKey("missions.postings", QCookArrested);
            TryRegisterKey("missions.postings", QTransporting);
            TryRegisterKey("missions.postings", QDealing);
        }

        private static void TryRegisterKey(string dict, string key)
        {
            try
            {
                string existing = null; try { existing = Strings.Get(dict, key); } catch { }
                if (!string.IsNullOrEmpty(existing) && existing == key) return;   // already a self-resolving key
                Strings.WriteToDictionary(dict, key, "", key, "", 0, false, "");
            }
            catch (Exception e) { Log($"TryRegisterKey({key}) error: {e.Message}"); }
        }

        // Fire one typewriter game-header (InterfaceController GameMessageType.gameHeader - the big centre header that
        // types itself out, distinct from the scanner blip and the case banner). They queue, so two calls play in order.
        private static void Header(string text)
        {
            try { var ic = InterfaceController.Instance; if (ic == null || string.IsNullOrEmpty(text)) return; ic.NewGameMessage(InterfaceController.GameMessageType.gameHeader, 0, text); }
            catch (Exception e) { Log($"Header error: {e.Message}"); }
        }

        // "<time>, <day>, <date>, <location>" for the collect header, matching vanilla (e.g. "4:12AM, Tue, Jan 3rd,
        // City Hall Ground floor lobby"): 12-hour clock + short day + short month + ordinal date + no year + location.
        // The game's own header system hides the date on later same-day headers, so we always build the full form.
        private static string TimeLocationString()
        {
            try
            {
                var sd = SessionData.Instance; if (sd == null) return "";
                float gt = sd.gameTime;
                string time = ""; try { time = sd.GameTimeToClock12String(gt, false); } catch { }
                string date = ""; try { date = sd.LongDateString(gt, true, true, true, true, true, false, false, true); } catch { }   // "Tue, Jan 3rd"
                string loc = null; try { var p = Player.Instance; if (p != null && p.currentGameLocation != null) loc = p.currentGameLocation.name; } catch { }
                var parts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrEmpty(time)) parts.Add(time);
                if (!string.IsNullOrEmpty(date)) parts.Add(date);
                if (!string.IsNullOrEmpty(loc)) parts.Add(loc);
                return string.Join(", ", parts);
            }
            catch { return ""; }
        }

        // Stop tracking the case (e.g. once the player has solved/closed it) without touching the game's Case
        // object, which stays on the board for them.
        // Keep ResolvedPtrs across close: a solved case's pointer stays latched so a stray post-close Resolve is still swallowed.
        internal static void MarkClosed() { Current = null; SubjectId = -1; _pendingVerdict = false; _resolvingSelf = false; try { _leads.Clear(); } catch { } }

        // Clear the "Explore Reported Crime Scene" lead the moment the player engages the arrested suspect - talking
        // to them or searching them is the whole point of that lead, so it is done on contact. Uses Objective.Cancel()
        // (which only removes the HUD pointer + awareness icon; no murder-state side effects), NOT Complete() whose
        // murder branch could deref the backing murder our synthetic case lacks. Idempotent + pointer-safe.
        // Clear the "Search <name>" lead for ONE specific arrested buyer (the player frisked them). Keyed by humanID so
        // searching buyer A never clears buyer B's still-open lead. Uses Objective.Cancel() (HUD pointer + icon only;
        // no murder-state side effects). Idempotent + pointer-safe.
        internal static void ClearLeadFor(int humanID, string via)
        {
            try
            {
                string leadTxt = null;
                if (!_leads.TryGetValue(humanID, out leadTxt) || string.IsNullOrEmpty(leadTxt)) return;
                _leads.Remove(humanID);
                if (!IsOpen) return;
                var list = Current.currentActiveObjectives;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    Objective o = null; try { o = list[i]; } catch { }
                    if (o == null) continue;
                    try { if (o.isComplete || o.isCancelled) continue; } catch { }
                    if (!IsLeadObjective(o, leadTxt)) continue;
                    try { o.Cancel(); } catch (Exception e) { Log($"search-lead Cancel threw: {e.Message}"); }
                    try { CasePanelController.Instance.UpdateCaseControls(); } catch { }
                    Log($"cleared the search lead for #{humanID} ({via}).");
                    return;   // Cancel mutates the list; stop iterating
                }
            }
            catch (Exception e) { Log($"ClearLeadFor error: {e.Message}"); }
        }

        // Does this objective match the SPECIFIC lead text (exact name/entryRef match only - no "Search" substring
        // wildcard, which previously cross-matched every search lead and collapsed multiple arrests into one).
        private static bool IsLeadObjective(Objective o, string leadTxt)
        {
            try
            {
                if (string.IsNullOrEmpty(leadTxt)) return false;
                string nm = null; try { nm = o.name; } catch { }
                if (nm == leadTxt) return true;
                try { var qe = o.queueElement; if (qe != null && qe.entryRef == leadTxt) return true; } catch { }
            }
            catch { }
            return false;
        }

        // The starting objectives, each composed with its FULL display text passed as the entryRef (custom cases do
        // not resolve objective text in the engine - Patch_ObjectiveSetup copies our entryRef onto the name once the
        // Objective is built asynchronously; Patch_ObjectiveSetup copies our entryRef onto the name once the Objective
        // is built): a "Search <name> at <location>" lead that POINTS at the arrested suspect but does NOT auto-complete
        // on arrival (trigger type `nothing`) - only the player actually frisking the suspect clears it (ClearLead,
        // called from the OnSearch patch). The "Collect a case resolution form from City Hall" hand-in objective is
        // added separately by AddCollectObjective (custom cases get no hand-in machinery from SetStatus).
        private static void AddInvestigateObjective(Case c, Human subject)
        {
            try
            {
                if (c == null) return;
                NewNode node = null; try { node = subject != null ? subject.currentNode : null; } catch { }
                NewGameLocation loc = null; try { loc = subject != null ? subject.currentGameLocation : null; } catch { }
                Vector3 pos = default; try { if (node != null) pos = node.position; } catch { }
                string where = null; try { if (loc != null) where = loc.name; } catch { }
                string name = CleanName(subject);

                string leadTxt = string.IsNullOrEmpty(where) ? $"Search {name}" : $"Search {name} at {where}";
                try { int id = subject != null ? subject.humanID : -1; if (id != -1) _leads[id] = leadTxt; } catch { }   // per-buyer lead
                // Trigger type `nothing` = never auto-completes (goToNode would clear it the moment the player arrives);
                // the pointer still routes the player to the suspect via usePointer + pointerPosition.
                var leadTrig = new Objective.ObjectiveTrigger(Objective.ObjectiveTriggerType.nothing, "", false, 0f, null, null, null, node, null, loc, null, "", false, pos);
                try { c.AddObjective(leadTxt, leadTrig, true, pos, InterfaceControls.Icon.lookingGlass, Objective.OnCompleteAction.nothing, 0f, false, "", false, false, null, false, false, false); }
                catch (Exception e) { Log($"search objective failed: {e.Message}"); }

                Log($"added search objective: '{leadTxt}'.");
            }
            catch (Exception e) { Log($"AddInvestigateObjective error: {e.Message}"); }
        }

        // Fill Case.handIn with the case-tray INTERACTABLE ids. handIn is a List<int> that Case.GetClosestHandIn
        // resolves via CityData.savableInteractableDictionary (Dictionary<int,Interactable>) - NOT addressDictionary,
        // so the entries must be Interactable.ids, not NewAddress.ids (that was the bug: all 17 address ids missed and
        // GetClosestHandIn returned null, which killed Plot Route AND the submit objective). CityData.caseTrays is the
        // canonical list of hand-in trays (vanilla's own reception points); GetClosestHandIn returns the nearest to
        // the player, so we add them all and the City Hall tray wins when the player is there.
        private static void PopulateHandIn(Case c)
        {
            try
            {
                if (c == null) return;
                if (c.handIn == null) c.handIn = new Il2CppSystem.Collections.Generic.List<int>();
                try { c.handIn.Clear(); } catch { }
                int added = 0;
                var cd = CityData.Instance; var trays = cd != null ? cd.caseTrays : null;
                if (trays != null)
                {
                    for (int i = 0; i < trays.Count; i++)
                    {
                        var t = trays[i]; if (t == null) continue;
                        int id; try { id = t.id; } catch { continue; }
                        try { c.handIn.Add(id); added++; } catch { }
                    }
                }
                Log($"populated hand-in with {added} case-tray interactable id(s) (total {c.handIn.Count}).");
            }
            catch (Exception e) { Log($"PopulateHandIn error: {e.Message}"); }
        }

        // The "Collect a case resolution form from City Hall" lead (collectHandIn trigger auto-completes it when the
        // player collects the form at the tray). Text via the vanilla missions.postings key so it reads exactly like
        // a real case; Patch_ObjectiveSetup copies our resolved entryRef onto the objective name for the custom case.
        private static void AddCollectObjective(Case c)
        {
            try
            {
                if (c == null) return;
                string txt = ""; try { txt = Strings.Get("missions.postings", "collect hand in"); } catch { }
                if (string.IsNullOrEmpty(txt)) txt = "Collect a case resolution form from City Hall";
                // Anchor a HUD pointer at the City Hall tray (the nearest hand-in tray) so the collect objective shows
                // its resolve icon floating over the form in-world, like the search lead does over the suspect.
                Vector3 pos = default; bool usePtr = false;
                try { var tray = c.GetClosestHandIn(); if (tray != null) { pos = tray.GetWorldPosition(true); usePtr = true; } } catch { }
                var trig = new Objective.ObjectiveTrigger(Objective.ObjectiveTriggerType.collectHandIn, "", false, 0f, null, null, null, null, null, null, null, "", false, pos);
                try { c.AddObjective(txt, trig, usePtr, pos, InterfaceControls.Icon.hand, Objective.OnCompleteAction.nothing, 0f, false, "", false, false, null, false, false, false); }
                catch (Exception e) { Log($"collect objective failed: {e.Message}"); }
                Log($"added collect objective: '{txt}' (pointer={usePtr}).");
            }
            catch (Exception e) { Log($"AddCollectObjective error: {e.Message}"); }
        }

        // Populate the case's resolution-form questions (the engine only generates these for murder/mainStory cases).
        // IMPORTANT: Case.ResolveQuestion.GetText looks `name` up as a missions.postings LOCALIZATION KEY (it is NOT
        // literal display text), so a free-text name renders BLANK in the form row - Patch_InputFieldSetup forces the
        // row label to our literal `name` instead. `displayObjective=false` stops the engine auto-spawning a (blank)
        // objective from this question when the form is collected. `useAlternateName`/`useName` do NOT affect GetText,
        // so we don't bother with them. The correct answer gets wired to the real dealer once rendering is confirmed.
        private static void AddResolveQuestions(Case c, Human subject)
        {
            try
            {
                if (c == null) return;
                if (c.resolveQuestions == null) c.resolveQuestions = new Il2CppSystem.Collections.Generic.List<Case.ResolveQuestion>();
                // PRIMARY (required): identify the COOK - the lab operator who produces the drug, the real target of
                // the bust. SECONDARY (optional bonus): the street DEALER the interrogation trail points to. The
                // correct answer is the citizen's humanID as a string (ResolveQuestion.UpdateCorrect feeds each
                // correctAnswers entry to CityData.GetHuman). Cook from the active operation; dealer from the trail.
                RegisterStrings();   // make the question labels resolvable BEFORE the questions/objectives are built
                int cookId = -1; try { if (Operation.CookHuman != null) cookId = Operation.CookHuman.humanID; } catch { }
                int distId = -1; try { if (Operation.Distributors != null && Operation.Distributors.Count > 0 && Operation.Distributors[0].H != null) distId = Operation.Distributors[0].H.humanID; } catch { }
                int dealerId = -1; try { int bid = subject != null ? subject.humanID : -1; var dl = Operation.DealerFor(bid); if (dl != null) dealerId = dl.humanID; } catch { }
                // The full bust form, in trail order. Producing is the REQUIRED target of the bust; the rest are bonus
                // rows (no penalty) that reward following the chain. Lab location is a LOCATION pick (unscored for now -
                // see AddQuestion). "Has the cook been arrested?" is an arrestPurp row = the vanilla boolean CHECKMARK
                // that ticks when the identified cook is in custody (UpdateCorrect checks the correct citizen is
                // restrained), keyed to the cook.
                // The location answer is the lab's INDEX in CityData.gameLocationDirectory as a string (vanilla stores
                // the killer's home the same way, e.g. "565"), NOT the display name.
                string labId = Operation.LabDirIndex();
                // ORDER MATTERS: the arrestPurp "arrested?" checkmark sits DIRECTLY under the cook-identify row (vanilla
                // puts "arrest the murderer" right under "who killed them" - the arrest row keys off the citizen named
                // in the row above + whether they're in handcuffs). So: produce -> arrested -> location -> transport -> deal.
                AddQuestion(c, QProducing,    required: true,  reward: 300, penalty: 100, correctHumanId: cookId);   // the NAMING question (citizen) - the arrest row below reads its answer
                AddQuestion(c, QCookArrested, required: false, reward: 100, penalty: 0,   inputType: Case.InputType.arrestPurp);   // correctAnswers EMPTY (like vanilla "Arrest the killer"): it checks the person named in the citizen row above is in handcuffs
                AddQuestion(c, QLabLocation,  required: false, reward: 150, penalty: 0,   inputType: Case.InputType.location, correctLocation: labId);   // lab pick, matched by gameLocationDirectory index (like vanilla's "where does the killer live")
                AddQuestion(c, QTransporting, required: false, reward: 150, penalty: 0,   correctHumanId: distId);   // the distributor
                AddQuestion(c, QDealing,      required: false, reward: 150, penalty: 0,   correctHumanId: dealerId);  // the street dealer
                Log($"resolve answers: cook#{cookId}, distributor#{distId}, dealer#{dealerId}; lab location dir-index='{labId}'.");
                Log($"added {c.resolveQuestions.Count} resolve question(s).");
            }
            catch (Exception e) { Log($"AddResolveQuestions error: {e.Message}"); }
        }

        // Add one citizen-identify resolve question. `name` is forced onto the form row by Patch_InputFieldSetup
        // (GetText treats it as a localization key = blank otherwise); displayObjective=false stops the engine
        // auto-spawning a blank objective from it on collect.
        private static void AddQuestion(Case c, string label, bool required, int reward, int penalty, int correctHumanId = -1, Case.InputType inputType = Case.InputType.citizen, string correctLocation = null)
        {
            try
            {
                var q = new Case.ResolveQuestion();
                try { q.name = label; } catch { }
                // Location correct-answer: the lab's gameLocationDirectory INDEX as a string (confirmed from a vanilla
                // murder dump: "where does the killer live" stored correct='565', an index - not the display name).
                if (inputType == Case.InputType.location && !string.IsNullOrEmpty(correctLocation))
                {
                    try { q.correctAnswers = new Il2CppSystem.Collections.Generic.List<string>(); q.correctAnswers.Add(correctLocation); } catch { }
                }
                // The correct answer for a CITIZEN pick: the citizen's humanID as a string (UpdateCorrect matches it
                // against the picked citizen's humanID.ToString()). For a LOCATION pick the correct-answer identifier
                // format is not yet confirmed from the decode, so the lab-location row is left UNSCORED for now (renders
                // + is fillable + submittable; optional so it never penalises) - we wire the grading once we see in-game
                // what the picker returns.
                if ((inputType == Case.InputType.citizen || inputType == Case.InputType.arrestPurp) && correctHumanId >= 0)
                {
                    try { q.correctAnswers = new Il2CppSystem.Collections.Generic.List<string>(); q.correctAnswers.Add(correctHumanId.ToString()); } catch { }
                }
                // displayObjective=true mimics vanilla: on collect, Case.SetStatus spawns one objective per question
                // (each with a banner + a submitCase trigger). Those submit-objectives are what make the City Hall
                // tray offer the HAND-IN action, so without them the form can't be submitted.
                try { q.displayObjective = true; } catch { }
                try { q.inputType = inputType; } catch { }
                try { q.icon = inputType == Case.InputType.location ? InterfaceControls.Icon.location : InterfaceControls.Icon.citizen; } catch { }
                try { q.isOptional = !required; } catch { }
                // Case.Resolve()'s AddMoney reads the INT fields (reward on solve, penalty on a wrong REQUIRED answer),
                // NOT the ranges - setting only rewardRange/penaltyRange left the money at 0. Set both.
                try { q.reward = reward; q.penalty = penalty; } catch { }
                try { q.rewardRange = new Vector2(reward, reward); q.penaltyRange = new Vector2(penalty, penalty); } catch { }
                c.resolveQuestions.Add(q);
            }
            catch (Exception e) { Log($"AddQuestion error: {e.Message}"); }
        }

        // Debug: step the hand-in flow for the open custom case, to TEST the resolution form independently of the
        // City Hall tray (which did nothing in testing). 1st press collects the form (status -> handInCollected,
        // which should make ResolveController render the fillable questions); 2nd press submits (Resolve + submitted).
        internal static void DebugHandInStep()
        {
            try
            {
                if (!IsOpen) { Log("DEBUG hand-in: no open case."); return; }
                var st = Current.caseStatus;
                if (st == Case.CaseStatus.handInNotCollected)
                {
                    try { Current.SetStatus(Case.CaseStatus.handInCollected, false); } catch (Exception e) { Log($"SetStatus(handInCollected) threw: {e.Message}"); }
                    try { var cpc = CasePanelController.Instance; if (cpc != null) { cpc.SetActiveCase(Current); cpc.UpdateCaseControls(); } } catch { }
                    Log("DEBUG hand-in: COLLECTED (status -> handInCollected). Open the case board + Resolve pane - are the questions fillable? Press again to submit.");
                }
                else if (st == Case.CaseStatus.handInCollected)
                {
                    // Route through the SAME path as the City Hall tray (Patch_HandInCase): set submitted + arm the one
                    // TickVerdict Resolve. Do NOT call Resolve() directly here - that was a second verdict source.
                    try { Current.SetStatus(Case.CaseStatus.submitted, true); } catch (Exception e) { Log($"SetStatus(submitted) threw: {e.Message}"); }
                    OnFormSubmitted();
                    Log("DEBUG hand-in: SUBMITTED (status -> submitted; the single verdict runs after the processing delay).");
                }
                else Log($"DEBUG hand-in: case status is {st}; nothing to step.");
            }
            catch (Exception e) { Log($"DebugHandInStep error: {e.Message}"); }
        }

        // True when OUR case is the one currently selected on the case panel. The resolve-form repair patches use this
        // so they only touch OUR render (a real murder's resolve form is left entirely to the game).
        internal static bool IsActiveOnPanel()
        {
            try
            {
                if (!IsOpen) return false;
                var cpc = CasePanelController.Instance;
                var ac = cpc != null ? cpc.activeCase : null;
                return ac != null && ac.Pointer == Current.Pointer;
            }
            catch { return false; }
        }

        // Reconnect to the ACTUAL Case object deserialized by the game. Never create a second Case.
        // Active leads and arrests will need their own persistence work; a saved case's resolution
        // questions remain on this original object.
        internal static void BindLoadedCase(Case saved)
        {
            if (saved == null || IsOpen) return;
            Current = saved;
            SubjectId = -1;
            _pendingVerdict = false;
            _resolvingSelf = false;
            Log($"rebound existing saved case '{SafeName(saved)}' without creating a duplicate.");
        }

        internal static void ResetOnLoad() { Current = null; SubjectId = -1; _pendingVerdict = false; _resolvingSelf = false; try { ResolvedPtrs.Clear(); } catch { } try { _leads.Clear(); } catch { } }

        private static string SafeName(Case c) { try { return c.name; } catch { return "?"; } }

        // A clean, player-facing citizen name (no "#id" suffix, unlike NarcoticsPlugin.Name which is for logs).
        private static string CleanName(Human h)
        {
            try { var n = h != null ? h.citizenName : null; if (!string.IsNullOrEmpty(n)) return n; } catch { }
            return "a suspect";
        }
        static void Log(string m) => NarcoticsPlugin.Log.LogInfo("[Narcotics][case] " + m);
    }
}
