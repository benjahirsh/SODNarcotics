using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;

namespace Narcotics
{
    // Experimental second-stage persistence. Recipes for the case's crew and their fixed dead drops
    // are stored separately from DrugPersistence's global preset registry. Only immutable identity
    // is persisted: the loaded game's own case, item pages and citizen objects remain authoritative.
    internal sealed class SavedDealerSite
    {
        public int HumanId { get; set; }
        public int DistributorIndex { get; set; }
        public string FurnitureName { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    internal sealed class SavedInvestigation
    {
        public string DrugKey { get; set; }
        public string DrugName { get; set; }
        public string BasePresetName { get; set; }
        public string CaseTitle { get; set; }
        public int CookId { get; set; }
        public int LabDirectoryIndex { get; set; }
        public string LabName { get; set; }
        public float StartedAtGameHours { get; set; }
        public List<int> DistributorIds { get; set; } = new List<int>();
        public List<SavedDealerSite> Dealers { get; set; } = new List<SavedDealerSite>();
        // Optional per-save cook snapshots. Missing/null is valid for legacy records.
        public List<SavedCookSession> CookSessions { get; set; } = new List<SavedCookSession>();
    }

    internal static class InvestigationPersistence
    {
        private static readonly string PathOnDisk = Path.Combine(Paths.ConfigPath, "com.opi.narcotics.investigations.json");
        private static readonly Dictionary<string, SavedInvestigation> Saved = new Dictionary<string, SavedInvestigation>();
        private static bool _read;
        private static bool _restoredThisLoad;
        private static bool _preCasePending;
        // LoadSaveState and OnStartGame are not guaranteed to precede the first murder
        // hijack/update in a fixed order. Reserve a validated pre-case operation during
        // the native load prefix, before any operation can be started.
        private static bool _nativeSaveLoadPending;
        private static bool _loading;
        private static float _nextRetry;
        private static string _lastWarning;
        private static string _loadSaveIdentity;
        private static long _loadSaveLastWriteUtcTicks;
        private static float _loadedGameHours = float.NaN;

        internal static void BeginLoad(string saveIdentity, float loadedGameHours)
        {
            _loading = true;
            _nativeSaveLoadPending = true;
            _restoredThisLoad = false;
            _preCasePending = false;
            _nextRetry = 0f;
            _lastWarning = null;
            _loadSaveIdentity = saveIdentity;
            _loadedGameHours = loadedGameHours;
            _loadSaveLastWriteUtcTicks = 0;
            try
            {
                if (!string.IsNullOrWhiteSpace(_loadSaveIdentity) && File.Exists(_loadSaveIdentity))
                    _loadSaveLastWriteUtcTicks = File.GetLastWriteTimeUtc(_loadSaveIdentity).Ticks;
            }
            catch { }
            // IMPORTANT: this happens in LoadSaveState Prefix. The game's murder hijack
            // may fire before our Update/Tick runs. A pre-case save has no game Case for
            // HasUnresolvedCase() to find, so the flag MUST already be set here.
            ReservePreCaseDuringLoad();
        }

        internal static void AfterGameStarted()
        {
            _loading = false;
            _restoredThisLoad = false;
            _nextRetry = 0f;
            if (_nativeSaveLoadPending)
            {
                // Old implementation cleared _preCasePending here, exposing a window
                // for the murder hijack to replace the saved operation before Tick.
                ReservePreCaseDuringLoad();
            }
            else
            {
                // Fresh city, not a native save load: do not carry a reservation over.
                _preCasePending = false;
                _loadSaveIdentity = null;
                _loadedGameHours = float.NaN;
                _loadSaveLastWriteUtcTicks = 0;
            }
            _nativeSaveLoadPending = false;
        }

        private static void ReadOnce()
        {
            if (_read) return;
            _read = true;
            try
            {
                if (!File.Exists(PathOnDisk)) return;
                var records = JsonSerializer.Deserialize<List<SavedInvestigation>>(File.ReadAllText(PathOnDisk));
                if (records == null) return;
                foreach (var r in records)
                {
                    if (r == null || string.IsNullOrEmpty(r.DrugKey) ||
                        !r.DrugKey.StartsWith("NarcDrug_", StringComparison.Ordinal) ||
                        string.IsNullOrEmpty(r.CaseTitle)) continue;
                    Saved[r.DrugKey] = r;
                }
                Log($"loaded {Saved.Count} investigation identity record(s).");
            }
            catch (Exception e) { Warn("reading investigation identities failed: " + e.Message); }
        }

        // Once the case opens, update its identity while keeping per-save snapshots
        // captured during the pre-case phase of this SAME operation.
        internal static void RecordOpenedCase()
        {
            try
            {
                if (!Operation.Active || !NarcCase.IsOpen) return;
                var record = Operation.CaptureIdentity();
                if (record == null || string.IsNullOrEmpty(record.DrugKey))
                {
                    Warn("unable to capture full crew/lab/dead-drop identity; the case cannot be resumed by this prototype.");
                    return;
                }
                ReadOnce();
                if (Saved.TryGetValue(record.DrugKey, out var previous) && previous != null &&
                    SameOperation(previous, record) && previous.CookSessions != null)
                    record.CookSessions = previous.CookSessions;
                Saved[record.DrugKey] = record;
                WriteSavedRecords();
                Log($"recorded case '{record.CaseTitle}' (key={record.DrugKey}, crew={record.DistributorIds.Count}+{record.Dealers.Count}).");
            }
            catch (Exception e) { Warn("recording investigation identity failed: " + e); }
        }

        private static void WriteSavedRecords()
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOnDisk));
            var json = JsonSerializer.Serialize(new List<SavedInvestigation>(Saved.Values),
                new JsonSerializerOptions { WriteIndented = true });
            string temporary = PathOnDisk + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, PathOnDisk, true);
        }

        // This is called only AFTER the native asynchronous game save completed.
        // The identity was captured alongside the native state, before the save task
        // finished. A pre-case save is therefore just as safe as a post-case save.
        internal static void CommitCookSession(SavedCookSession snapshot, SavedInvestigation capturedIdentity)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.SaveIdentity) ||
                snapshot.SaveFileLastWriteUtcTicks <= 0) return;
            ReadOnce();
            if (capturedIdentity == null && snapshot.HasCaseOpenState && snapshot.CaseWasOpen)
                Saved.TryGetValue(snapshot.DrugKey ?? string.Empty, out capturedIdentity);
            if (capturedIdentity == null || capturedIdentity.DrugKey != snapshot.DrugKey ||
                capturedIdentity.CaseTitle != snapshot.CaseTitle ||
                capturedIdentity.CookId != snapshot.CookHumanId ||
                capturedIdentity.LabDirectoryIndex != snapshot.LabDirectoryIndex)
            {
                Warn("[cook-save] skipped: missing or mismatched atomic save identity.");
                return;
            }
            if (!Saved.TryGetValue(snapshot.DrugKey, out var record) || record == null ||
                !SameOperation(record, capturedIdentity))
            {
                // A pre-case operation has no investigation record yet. Do not write
                // it when Start() fires: only an actual successful game save commits it.
                record = capturedIdentity;
                Saved[snapshot.DrugKey] = record;
            }
            if (record.CookSessions == null) record.CookSessions = new List<SavedCookSession>();
            for (int i = record.CookSessions.Count - 1; i >= 0; i--)
                if (SamePath(record.CookSessions[i]?.SaveIdentity, snapshot.SaveIdentity)) record.CookSessions.RemoveAt(i);
            record.CookSessions.Add(snapshot);
            WriteSavedRecords();
            float remaining = snapshot.CookActive
                ? (snapshot.CookSessionStartGameHours >= 0f ? snapshot.CookSessionStartGameHours + snapshot.CookShiftHours - snapshot.CapturedAtGameHours : -1f)
                : snapshot.NextCookAtGameHours - snapshot.CapturedAtGameHours;
            Log($"[cook-save] committed phase={(snapshot.HasCaseOpenState && !snapshot.CaseWasOpen ? "PRE-CASE" : "case-open")} " +
                $"active={snapshot.CookActive} started={snapshot.CookSessionStartGameHours:0.###} " +
                $"batchesLeft={snapshot.CookBatchesLeft} savedGT={snapshot.CapturedAtGameHours:0.###} " +
                $"remaining={remaining:0.###} save={System.IO.Path.GetFileName(snapshot.SaveIdentity)}.");
        }

        private static bool SameOperation(SavedInvestigation a, SavedInvestigation b)
        {
            return a != null && b != null && a.DrugKey == b.DrugKey &&
                a.CaseTitle == b.CaseTitle && a.CookId == b.CookId &&
                a.LabDirectoryIndex == b.LabDirectoryIndex && a.LabName == b.LabName &&
                Math.Abs(a.StartedAtGameHours - b.StartedAtGameHours) <= 0.05f;
        }

        internal static SavedCookSession FindCookSession(SavedInvestigation saved, out string reason, out SavedCookSession diagnostic)
        {
            reason = null;
            diagnostic = null;
            ReadOnce();
            if (saved == null) { reason = "investigation identity missing"; return null; }
            // During an in-game (hot) load, MainMenuController.selectedSave may be null even
            // though the native LoadSaveState(StateSaveData) hook has the correct game time.
            // Never guess a slot by case title or "most recent": accept only ONE unchanged
            // save file whose recorded snapshot also matches the loaded game's identity/time.
            // Cold loads keep the stricter, existing selected-path validation below.
            if (string.IsNullOrWhiteSpace(_loadSaveIdentity))
                return FindUniqueHotLoadSnapshot(saved, out reason, out diagnostic);
            if (saved.CookSessions == null || saved.CookSessions.Count == 0) { reason = "legacy save has no cook snapshot"; return null; }
            if (_loadSaveLastWriteUtcTicks == 0) { reason = "selected save file timestamp unavailable"; return null; }
            const float HoursTolerance = 0.05f;
            for (int i = saved.CookSessions.Count - 1; i >= 0; i--)
            {
                var candidate = saved.CookSessions[i];
                if (candidate == null || !SamePath(candidate.SaveIdentity, _loadSaveIdentity)) continue;
                diagnostic = candidate;
                if (candidate.SaveFileLastWriteUtcTicks != _loadSaveLastWriteUtcTicks)
                { reason = "selected save file timestamp differs from captured save"; continue; }
                if (candidate.SchemaVersion != 1) { reason = "unsupported cook snapshot version"; return null; }
                if (candidate.DrugKey != saved.DrugKey || candidate.CaseTitle != saved.CaseTitle ||
                    candidate.CookHumanId != saved.CookId || candidate.LabDirectoryIndex != saved.LabDirectoryIndex)
                { reason = "operation identity mismatch"; return null; }
                if (!Finite(candidate.CapturedAtGameHours) || !Finite(_loadedGameHours) ||
                    Math.Abs(candidate.CapturedAtGameHours - _loadedGameHours) > HoursTolerance)
                { reason = $"save game-time mismatch (snapshot={candidate.CapturedAtGameHours:0.###}, loaded={_loadedGameHours:0.###})"; return null; }
                return candidate;
            }
            reason = "no snapshot matches selected save path and file timestamp";
            return null;
        }

        // Fail-closed recovery for a game-initiated hot load that doesn't expose selectedSave.
        // It does NOT repair/recreate items, change a snapshot, or select the newest save.
        // Each candidate must belong to this exact operation, still exist at its original
        // full path with its recorded last-write tick, and match the loaded game's time.
        // If more than one candidate is possible, restore none rather than mix save slots.
        private static SavedCookSession FindUniqueHotLoadSnapshot(
            SavedInvestigation saved, out string reason, out SavedCookSession diagnostic)
        {
            diagnostic = null;
            reason = "selected save identity unavailable; no uniquely matching saved snapshot";
            if (saved.CookSessions == null || saved.CookSessions.Count == 0 || !Finite(_loadedGameHours))
                return null;

            const float HoursTolerance = 0.05f;
            SavedCookSession match = null;
            for (int i = 0; i < saved.CookSessions.Count; i++)
            {
                var candidate = saved.CookSessions[i];
                if (candidate == null || candidate.SchemaVersion != 1 ||
                    candidate.DrugKey != saved.DrugKey || candidate.CaseTitle != saved.CaseTitle ||
                    candidate.CookHumanId != saved.CookId || candidate.LabDirectoryIndex != saved.LabDirectoryIndex ||
                    !Finite(candidate.CapturedAtGameHours) ||
                    Math.Abs(candidate.CapturedAtGameHours - _loadedGameHours) > HoursTolerance ||
                    candidate.SaveFileLastWriteUtcTicks <= 0 || string.IsNullOrWhiteSpace(candidate.SaveIdentity))
                    continue;

                try
                {
                    // Only previously recorded absolute paths are eligible. Never resolve a
                    // relative filename against the working directory during hot loading.
                    if (!System.IO.Path.IsPathRooted(candidate.SaveIdentity) || !File.Exists(candidate.SaveIdentity) ||
                        File.GetLastWriteTimeUtc(candidate.SaveIdentity).Ticks != candidate.SaveFileLastWriteUtcTicks)
                        continue;
                }
                catch { continue; }

                if (match != null)
                {
                    reason = "selected save identity unavailable; multiple snapshots match game time and file timestamps";
                    return null;
                }
                match = candidate;
            }

            if (match == null) return null;
            diagnostic = match;
            reason = null;
            Log($"[cook-resume] hot-load selected path unavailable; uniquely matched {System.IO.Path.GetFileName(match.SaveIdentity)} " +
                $"by operation identity, game time and unchanged save-file timestamp.");
            return match;
        }

        // Without an existing game Case, select ONLY a snapshot explicitly saved
        // before the first case. FindCookSession enforces exact path+timestamp+time,
        // or uniquely matched unchanged hot-load files when the path is unavailable.
        // An ambiguous or overwritten slot is NEVER restored.
        private static SavedInvestigation FindSavedPreCaseOperation(out SavedCookSession snapshot)
        {
            snapshot = null;
            ReadOnce();
            SavedInvestigation matched = null;
            foreach (var record in Saved.Values)
            {
                if (record?.CookSessions == null || record.CookSessions.Count == 0) continue;
                var candidate = FindCookSession(record, out _, out _);
                if (candidate == null || !candidate.HasCaseOpenState || candidate.CaseWasOpen) continue;
                if (matched != null) // not enough information to distinguish two cities/slots
                {
                    WarnOnce("multiple pre-case operation snapshots match this load; refusing to guess.");
                    return null;
                }
                matched = record;
                snapshot = candidate;
            }
            return matched;
        }

        // A saved pre-case operation must be protected BEFORE the game's own murder
        // scheduling can fire, not only once NarcoticsHotkey.Update calls Tick().
        // Keep the existing exact save path/timestamp/game-time matching. If the
        // selected slot and game time identify a pre-case snapshot but its timestamp
        // has changed, do NOT restore that stale snapshot; block replacement and
        // log why, rather than silently starting a different criminal crew.
        private static void ReservePreCaseDuringLoad()
        {
            if (!Finite(_loadedGameHours))
            {
                Log($"[pre-case-load] cannot reserve: loaded game time unavailable; selected={System.IO.Path.GetFileName(_loadSaveIdentity) ?? "(unknown)"}.");
                return;
            }
            var match = FindSavedPreCaseOperation(out var matchedSnapshot);
            if (match != null)
            {
                _preCasePending = true;
                Log($"[pre-case-load] RESERVED '{match.CaseTitle}' (key={match.DrugKey}), " +
                    $"save={System.IO.Path.GetFileName(matchedSnapshot.SaveIdentity)}, " +
                    $"loadedGT={_loadedGameHours:0.###}. Holding murder hijack until restore.");
                return;
            }

            // Diagnostic, fail-closed protection for a known slot with changed
            // timestamp. Never rebind a different slot or use time alone to restore.
            // When selectedSave is unavailable the existing unique hot-load
            // validation above remains authoritative.
            ReadOnce();
            foreach (var record in Saved.Values)
            {
                if (record?.CookSessions == null) continue;
                foreach (var state in record.CookSessions)
                {
                    if (state == null || !state.HasCaseOpenState || state.CaseWasOpen ||
                        !Finite(state.CapturedAtGameHours) ||
                        Math.Abs(state.CapturedAtGameHours - _loadedGameHours) > 0.05f ||
                        !SamePath(state.SaveIdentity, _loadSaveIdentity)) continue;
                    var checkedState = FindCookSession(record, out var reason, out _);
                    if (checkedState != null) continue; // exact match handled above
                    _preCasePending = true;
                    WarnOnce($"[pre-case-load] BLOCKED replacement for matched pre-case slot " +
                        $"'{System.IO.Path.GetFileName(state.SaveIdentity)}', gameTime={_loadedGameHours:0.###}: " +
                        $"{reason ?? "snapshot validation failed"}. Original operation NOT restored; no new one will start.");
                    return;
                }
            }
            Log($"[pre-case-load] no validated pre-case snapshot for selected=" +
                $"{System.IO.Path.GetFileName(_loadSaveIdentity) ?? "(unavailable)"}, " +
                $"loadedGT={_loadedGameHours:0.###}, fileTicks={_loadSaveLastWriteUtcTicks}.");
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try { return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // Recognize OUR case by its five custom resolution questions, not merely CaseType.custom:
        // other mods can use that case type too. We only bind unsolved cases.
        private static Case FindSavedCase()
        {
            try
            {
                var cpc = CasePanelController.Instance;
                var cases = cpc != null ? cpc.activeCases : null;
                if (cases == null) return null;
                for (int i = 0; i < cases.Count; i++)
                {
                    var c = cases[i]; if (c == null) continue;
                    try
                    {
                        if (c.caseType != Case.CaseType.custom || c.isSolved ||
                            c.caseStatus == Case.CaseStatus.closable || c.caseStatus == Case.CaseStatus.archived) continue;
                        bool producer = false, transporter = false, location = false;
                        var qs = c.resolveQuestions;
                        if (qs == null) continue;
                        for (int j = 0; j < qs.Count; j++)
                        {
                            var q = qs[j]; if (q == null) continue;
                            if (q.name == NarcCase.QProducing) producer = true;
                            if (q.name == NarcCase.QTransporting) transporter = true;
                            if (q.name == NarcCase.QLabLocation) location = true;
                        }
                        if (producer && transporter && location) return c;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static string CorrectAnswer(Case c, string label)
        {
            try
            {
                var qs = c.resolveQuestions;
                if (qs == null) return null;
                for (int i = 0; i < qs.Count; i++)
                {
                    var q = qs[i];
                    if (q != null && q.name == label && q.correctAnswers != null && q.correctAnswers.Count > 0)
                        return q.correctAnswers[0];
                }
            }
            catch { }
            return null;
        }

        // Do not select a stale snapshot from a different city or a previous case with the same
        // procedural title. Compare the game's persisted cook/distributor/lab answers before binding.
        private static SavedInvestigation Match(Case c)
        {
            ReadOnce();
            string cook = CorrectAnswer(c, NarcCase.QProducing);
            string distributor = CorrectAnswer(c, NarcCase.QTransporting);
            string lab = CorrectAnswer(c, NarcCase.QLabLocation);
            if (string.IsNullOrEmpty(cook) || string.IsNullOrEmpty(distributor) || string.IsNullOrEmpty(lab)) return null;
            foreach (var s in Saved.Values)
            {
                try
                {
                    if (s.CaseTitle != c.name || cook != s.CookId.ToString() ||
                        lab != s.LabDirectoryIndex.ToString() || s.DistributorIds == null ||
                        s.DistributorIds.Count == 0 || distributor != s.DistributorIds[0].ToString()) continue;
                    // Verify the lab's name as well as directory index (indexes repeat between cities).
                    var cd = CityData.Instance;
                    var dir = cd != null ? cd.gameLocationDirectory : null;
                    if (dir == null || s.LabDirectoryIndex < 0 || s.LabDirectoryIndex >= dir.Count) continue;
                    var loc = dir[s.LabDirectoryIndex];
                    if (loc == null || loc.name != s.LabName) continue;
                    return s;
                }
                catch { }
            }
            return null;
        }

        // Called from the game tick BEFORE TickAutoStart. If a saved case exists, NEVER start a
        // fresh drug operation over it, even when its sidecar identity is missing or unusable.
        // This protects player progress rather than quietly replacing Mirage with Drift.
        internal static bool HasUnresolvedCase()
        {
            return FindSavedCase() != null || _preCasePending;
        }

        internal static bool Loading => _loading;

        internal static void Tick()
        {
            CookSessionPersistence.PollCompletedSaves();
            if (_loading || Operation.Active || _restoredThisLoad) return;
            try
            {
                if (UnityEngine.Time.time < _nextRetry) return;
                _nextRetry = UnityEngine.Time.time + 3f;
                var c = FindSavedCase();
                if (c == null)
                {
                    var preCaseIdentity = FindSavedPreCaseOperation(out var preCaseSnapshot);
                    if (preCaseIdentity == null) return;
                    _preCasePending = true; // block bootstrap and murder while retrying
                    if (!Operation.RestoreIdentity(preCaseIdentity))
                    {
                        WarnOnce("valid pre-case save found but crew/lab/bin reconstruction is incomplete; not starting a replacement operation.");
                        return;
                    }
                    _restoredThisLoad = true;
                    _preCasePending = false;
                    Log($"RESUMED PRE-CASE '{preCaseIdentity.CaseTitle}' (key={preCaseIdentity.DrugKey}) " +
                        $"from {System.IO.Path.GetFileName(preCaseSnapshot.SaveIdentity)}; no game case created.");
                    return;
                }
                NarcCase.BindLoadedCase(c);
                var snapshot = Match(c);
                if (snapshot == null)
                {
                    WarnOnce("saved Narcotics case found but no matching operation record; blocking new operations to avoid overwriting this investigation.");
                    return;
                }
                if (!Operation.RestoreIdentity(snapshot))
                {
                    WarnOnce("operation reconstruction is incomplete; not starting a replacement operation.");
                    return;
                }
                _restoredThisLoad = true;
                Log($"RESUMED '{snapshot.CaseTitle}' (key={snapshot.DrugKey}); existing game case preserved; no duplicate operation started.");
            }
            catch (Exception e) { WarnOnce("restoration attempt failed: " + e.Message); }
        }

        private static void WarnOnce(string message)
        {
            if (_lastWarning == message) return;
            _lastWarning = message;
            Warn(message);
        }

        private static void Log(string msg) => NarcoticsPlugin.Log.LogInfo("[Narcotics][investigation-save] " + msg);
        private static void Warn(string msg) => NarcoticsPlugin.Log.LogWarning("[Narcotics][investigation-save] " + msg);
    }
}
