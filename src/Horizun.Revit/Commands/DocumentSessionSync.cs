// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// horizun_document_session operation=sync_with_central.
//
// The only write in this bridge that can be neither rehearsed nor rolled back:
// Document.SynchronizeWithCentral publishes the local's changes into the central
// and reloads everyone else's, and the relinquish hands ownership back. So:
//
//   * It is OFF unless the machine owner turned it on in Revit (the ribbon's
//     Advanced options), exactly like horizun_execute_python - and a Python script
//     that calls SynchronizeWithCentral is refused while it is off. The workshared
//     read-only policy wins over that grant. Decisions: SyncWithCentralRules.
//   * Its preview is an ESTIMATE and says so. Document.HasAllChangesFromCentral()
//     asks the central whether the local is up to date; ownership
//     (WorksharingUtils.GetCheckoutStatus over every collectable element) and a
//     SAMPLE of GetModelUpdatesStatus are the session's cached view.
//   * The confirmation token binds the request as its plan hash and the estimate as
//     its model fingerprint: a model that moved is refused naming what moved.
//   * Revit waits forever for a locked central by default; this call gives up at
//     once, because nobody is at the keyboard to read an answer that arrives later.
//   * After the sync, everything measured before is looked up again by UniqueId
//     (an ElementId may change across a sync), ownership is held against the
//     relinquish choice, the sample is re-read, HasAllChangesFromCentral() must be
//     true and a save must be witnessed. A sync that returned but did not verify is
//     reported as exactly that - it happened, and it did not verify.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

using Horizun.Revit.Core;
using BridgeSettings = Horizun.Revit.Core.Settings;

namespace Horizun.Revit.Commands
{
    public partial class DocumentSessionCommand
    {
        /// <summary>
        /// Ownership keyed so it survives the sync: ids order the sample and are shown to the
        /// caller, UniqueIds are what gets compared after SynchronizeWithCentral.
        /// </summary>
        private sealed class SyncCensus
        {
            public int? OwnedWorksets;
            public readonly List<long> AllIds = new List<long>();
            public readonly Dictionary<long, string> UidById = new Dictionary<long, string>();
            public readonly Dictionary<string, long> IdByUid = new Dictionary<string, long>(StringComparer.Ordinal);
            public readonly List<long> OwnedIds = new List<long>();
            public readonly List<long> BorrowedIds = new List<long>();
            public int Unreadable;

            public List<string> Uids(IEnumerable<long> ids) =>
                ids.Where(UidById.ContainsKey).Select(id => UidById[id]).ToList();
        }

        /// <summary>
        /// Revit's default when the central is locked is to wait and retry endlessly
        /// (TransactWithCentralOptions.SetLockCallback). Unattended, that would hold Revit's
        /// UI thread and this bridge's one-command queue with nobody left to read the
        /// outcome, so the bridge gives up at once and reports the central as locked.
        /// </summary>
        private sealed class GiveUpWhenCentralLocked : ICentralLockedCallback
        {
            public bool ShouldWaitForLockAvailability() => false;
        }

        // The preview's model fields by token, so a refused apply can name what moved.
        // Bounded: tokens are single-use and expire in minutes.
        private static readonly object SyncEstimatesLock = new object();
        private static readonly Dictionary<string, Dictionary<string, string>> SyncEstimates =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private static readonly Queue<string> SyncEstimateOrder = new Queue<string>();

        private static CommandResult SyncRefuse(string code, string message, bool writeStarted = false)
        {
            return CommandResult.FailWithDetail(message, new JObject
            {
                ["code"] = code, ["operation"] = "sync_with_central",
                ["write_started"] = writeStarted, ["changes_applied"] = false,
                ["transaction_status"] = "not_started"
            });
        }

        private static CommandResult SyncWithCentral(UIApplication app, JObject request)
        {
            // A sync is the least reversible call here, so an omitted dry_run is a preview.
            bool dryRun = request.Value<bool?>("dry_run") ?? true;
            if (!SyncWithCentralRules.TryParseRelinquish(request.Value<string>("relinquish"), out SyncRelinquish choice))
                return SyncRefuse("invalid_relinquish", "relinquish must be all, keep_borrowed or none. Nothing ran.");
            string comment = request.Value<string>("comment") ?? "";
            bool compact = request.Value<bool?>("compact") ?? false;

            string pickError = PickDocument(app, request, out Document doc, requireExplicitTarget: true);
            if (pickError != null) return SyncRefuse("target_not_resolved", pickError);
            string title = SafeTitle(doc) ?? SafePath(doc) ?? "the target document";

            bool? detached = null;
            try { detached = doc.IsDetached; } catch { }
            SyncRefusal shape = SyncWithCentralRules.DocumentRefusal(SafeWorkshared(doc), detached);
            if (shape != null) return SyncRefuse(shape.Code, "'" + title + "' " + shape.Message);

            // Unreadable settings fall CLOSED: protected, and not granted.
            bool protectedShared, ownerGranted;
            try { protectedShared = BridgeSettings.ForceReadOnlyOnWorkshared; } catch { protectedShared = true; }
            try { ownerGranted = BridgeSettings.SyncWithCentralOwnerEnabled; } catch { ownerGranted = false; }
            string settingsPath;
            try { settingsPath = BridgeSettings.Path(); } catch { settingsPath = null; }
            SyncRefusal auth = SyncWithCentralRules.AuthorisationRefusal(ownerGranted, protectedShared, settingsPath);
            if (auth != null) return SyncRefuse(auth.Code, auth.Message);

            string centralPath = null;
            bool? hasCentral = null;
            try
            {
                ModelPath central = doc.GetWorksharingCentralModelPath();
                hasCentral = central != null;
                if (central != null) centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
            }
            catch { }
            bool? readOnlyNow = null, transactionOpen = null;
            try { readOnlyNow = doc.IsReadOnly; } catch { }
            try { transactionOpen = doc.IsModifiable; } catch { }
            SyncRefusal pre = SyncWithCentralRules.PreconditionRefusal(readOnlyNow, transactionOpen, hasCentral);
            if (pre != null) return SyncRefuse(pre.Code, "'" + title + "' " + pre.Message);

            var clock = Stopwatch.StartNew();
            SyncCensus before = TakeSyncCensus(doc);
            if (before.OwnedWorksets == null || before.Unreadable > 0)
                return SyncRefuse("ownership_census_incomplete",
                    "The ownership of '" + title + "' could not be read in full (worksets readable=" +
                    (before.OwnedWorksets != null) + ", elements without a checkout status or UniqueId=" + before.Unreadable +
                    "). A sync whose relinquish cannot be checked afterwards would be reported unverified, so it " +
                    "is not offered. Nothing ran.");

            List<long> sampleIds = SyncWithCentralRules.Sample(before.AllIds, before.BorrowedIds, before.OwnedIds,
                SyncWithCentralRules.SpreadSampleSize, SyncWithCentralRules.OwnedSampleSize);
            List<string> sample = before.Uids(sampleIds);
            Dictionary<string, string> statusBefore = ReadUpdateStatuses(doc, sample);
            bool? modified = SafeModified(doc);
            bool? hasAll = HasAllChanges(doc, out string hasAllError);
            string documentKey = DocumentGate.IdentityOf(doc, HostVersion(app))?.Fingerprint();
            var sampleCounts = JObject.FromObject(SyncWithCentralRules.Counts(statusBefore.Values));

            var estimate = new JObject
            {
                ["document"] = documentKey,
                ["comment"] = comment,
                ["relinquish"] = SyncWithCentralRules.Name(choice),
                ["compact"] = compact,
                ["owned_worksets"] = before.OwnedWorksets,
                ["owned_elements"] = before.OwnedIds.Count,
                ["borrowed_elements"] = before.BorrowedIds.Count,
                ["is_modified"] = modified,
                ["has_all_changes_from_central"] = hasAll,
                ["sample_status_counts"] = sampleCounts
            };
            string planHash = ConfirmationStore.PlanHash(estimate, SyncWithCentralRules.RequestFields);
            string modelFingerprint = ConfirmationStore.PlanHash(estimate, SyncWithCentralRules.ModelFields);
            Dictionary<string, string> modelView = ModelView(estimate);

            if (dryRun)
            {
                Confirmation issued = DocumentGate.Confirmations.Issue(
                    SyncWithCentralRules.ConfirmationScope, documentKey, planHash, elementFingerprint: modelFingerprint);
                RememberEstimate(issued.Token, modelView);
                return CommandResult.Ok(new JObject
                {
                    ["operation"] = "sync_with_central",
                    ["dry_run"] = true,
                    ["preview_kind"] = "estimate",
                    ["target_document"] = title,
                    ["central"] = centralPath,
                    ["relinquish"] = SyncWithCentralRules.Name(choice),
                    ["comment"] = comment,
                    ["compact"] = compact,
                    ["is_modified"] = modified,
                    ["has_all_changes_from_central"] = hasAll,
                    ["has_all_changes_error"] = hasAllError,
                    ["owned_worksets"] = before.OwnedWorksets,
                    ["owned_elements"] = before.OwnedIds.Count,
                    ["borrowed_elements"] = before.BorrowedIds.Count,
                    ["borrowed_sample_ids"] = new JArray(before.BorrowedIds.OrderByDescending(x => x).Take(20)),
                    ["elements_scanned"] = before.AllIds.Count,
                    ["update_status_sample"] = new JObject
                    {
                        ["sample_size"] = sample.Count,
                        ["counts"] = sampleCounts,
                        ["method"] = "WorksharingUtils.GetModelUpdatesStatus per element (a local cache that cannot see " +
                                     "what others pushed), on " + sample.Count + " of " + before.AllIds.Count +
                                     " elements: an even spread plus the borrowed, then the newest owned."
                    },
                    ["estimate_note"] =
                        "ESTIMATE, not a rehearsal. A sync cannot be rehearsed or rolled back. " +
                        "has_all_changes_from_central is Document.HasAllChangesFromCentral(), which asks the central " +
                        "whether this local is up to date (null + has_all_changes_error when it could not). Ownership " +
                        "counts and the sampled update statuses are this session's CACHED view (WorksharingUtils " +
                        "reads a local cache), not the central's.",
                    ["cannot_be_rolled_back"] = true,
                    ["confirmation_token"] = issued.Token,
                    ["confirmation_expires_utc"] = issued.ExpiresUtc.ToString("u"),
                    ["confirmation_note"] =
                        "Bound to THIS request (document, relinquish, comment, compact) and THIS estimate (ownership " +
                        "counts, IsModified, has_all_changes_from_central, sampled statuses). If the estimate moves " +
                        "before the apply, it is refused naming what moved, and nothing is synchronized.",
                    ["elapsed_ms"] = clock.ElapsedMilliseconds
                });
            }

            string token = request.Value<string>("confirmation_token");
            ConfirmationCheck check = DocumentGate.Confirmations.Validate(
                token, SyncWithCentralRules.ConfirmationScope, documentKey, planHash, modelFingerprint,
                SyncWithCentralRules.DescribeEstimateDrift(RecallEstimate(token), modelView));
            if (!check.Ok)
                return SyncRefuse("confirmation_rejected",
                    "REFUSING TO SYNCHRONIZE '" + title + "': " + check.Message + " Run operation=sync_with_central " +
                    "with dry_run=true again and confirm the new estimate. Nothing was synchronized.");

            var options = new SynchronizeWithCentralOptions
            {
                Comment = comment,
                Compact = compact,
                SaveLocalBefore = true,
                SaveLocalAfter = true
            };
            options.SetRelinquishOptions(BuildRelinquish(choice));
            var transact = new TransactWithCentralOptions();
            transact.SetLockCallback(new GiveUpWhenCentralLocked());

            DateTime? centralWriteBefore = CentralWriteTime(centralPath);
            string syncError = null;
            SyncFailureKind failure = SyncFailureKind.Unknown;
            try { doc.SynchronizeWithCentral(transact, options); }
            catch (Exception ex)
            {
                syncError = ex.GetType().Name + ": " + ex.Message;
                failure = SyncWithCentralRules.ClassifyFailure(ex.GetType().FullName);
            }
            if (syncError != null && failure == SyncFailureKind.NotStarted)
                return SyncRefuse("sync_precondition_failed",
                    "SynchronizeWithCentral refused '" + title + "' before doing anything (" + syncError + "): that " +
                    "exception is one of its documented preconditions (read-only, open transaction or edit mode, no " +
                    "central, local not owned by this user or read-only). Nothing was synchronized.");

            SyncCensus after = TakeSyncCensus(doc);
            SyncVerdict ownership = SyncWithCentralRules.VerifyOwnership(choice,
                before.OwnedWorksets, before.Uids(before.OwnedIds), before.Uids(before.BorrowedIds),
                after.OwnedWorksets, after.Uids(after.OwnedIds), after.Unreadable, uid => ElementExists(doc, uid));
            Dictionary<string, string> statusAfter = ReadUpdateStatuses(doc, sample);
            SyncVerdict updates = SyncWithCentralRules.VerifyUpdates(statusAfter);
            bool? modifiedAfter = SafeModified(doc);
            bool? hasAllAfter = HasAllChanges(doc, out string hasAllAfterError);
            DateTime? centralWriteAfter = CentralWriteTime(centralPath);
            bool? centralAdvanced = centralWriteBefore == null || centralWriteAfter == null
                ? (bool?)null : centralWriteAfter.Value > centralWriteBefore.Value;
            var overallProblems = new List<string>();
            bool? verified = SyncWithCentralRules.OverallVerdict(syncError != null, ownership.Verified, updates.Verified,
                hasAllAfter, modifiedAfter, centralAdvanced, overallProblems);

            var report = new JObject
            {
                ["operation"] = "sync_with_central",
                ["dry_run"] = false,
                ["target_document"] = title,
                ["central"] = centralPath,
                ["relinquish"] = SyncWithCentralRules.Name(choice),
                ["api_error"] = syncError,
                ["sync_verified"] = verified,
                ["ownership"] = new JObject
                {
                    ["verified"] = ownership.Verified,
                    ["owned_worksets_before"] = before.OwnedWorksets,
                    ["owned_worksets_after"] = after.OwnedWorksets,
                    ["owned_elements_before"] = before.OwnedIds.Count,
                    ["borrowed_elements_before"] = before.BorrowedIds.Count,
                    ["owned_elements_after"] = after.OwnedIds.Count,
                    ["unreadable_after"] = after.Unreadable,
                    ["unexpectedly_owned"] = Display(ownership.UnexpectedlyOwned, after, before),
                    ["unexpectedly_released"] = Display(ownership.UnexpectedlyReleased, after, before),
                    ["problems"] = new JArray(ownership.Problems)
                },
                ["update_status_sample"] = new JObject
                {
                    ["verified"] = updates.Verified,
                    ["sample_size"] = sample.Count,
                    ["counts_before"] = sampleCounts,
                    ["counts_after"] = JObject.FromObject(SyncWithCentralRules.Counts(statusAfter.Values)),
                    ["not_current"] = Display(updates.UnexpectedlyOwned, after, before),
                    ["problems"] = new JArray(updates.Problems)
                },
                ["has_all_changes_from_central_after"] = hasAllAfter,
                ["has_all_changes_error_after"] = hasAllAfterError,
                ["is_modified_after"] = modifiedAfter,
                ["central_file_written"] = centralAdvanced,
                ["problems"] = new JArray(overallProblems),
                ["measured_how"] =
                    "Elements tracked by UniqueId (an ElementId may change across a sync; element_id values are for " +
                    "display). Worksets counted by Owner; every collectable element checked with " +
                    "WorksharingUtils.GetCheckoutStatus before and after; the same sample re-read with " +
                    "GetModelUpdatesStatus; Document.HasAllChangesFromCentral() after; the save witnessed by " +
                    "IsModified=false after SaveLocalAfter or, for a file-based central, its write time advancing.",
                ["elapsed_ms"] = clock.ElapsedMilliseconds
            };

            if (syncError != null)
            {
                bool locked = failure == SyncFailureKind.CentralLocked;
                report["code"] = locked ? "central_locked" : "sync_failed_state_unknown";
                report["write_started"] = true;
                report["changes_applied"] = JValue.CreateNull();
                return CommandResult.FailWithDetail(locked
                    ? "The central of '" + title + "' is locked by another client (" + syncError + "). This call " +
                      "does not wait for a lock, so Revit cancelled the synchronize. The local may already have been " +
                      "saved or reloaded before the lock was needed; the re-read is in the detail. Preview again once " +
                      "the central is free."
                    : "SynchronizeWithCentral threw (" + syncError + "). Whether anything reached the central is not " +
                      "known from the exception, and the local may have been saved or reloaded; the ownership and " +
                      "status re-read after it is in the detail. Do not retry blindly: preview again first.", report);
            }
            if (verified != true)
            {
                report["write_started"] = true;
                report["changes_applied"] = true;
                return CommandResult.FailWithDetail(
                    "SynchronizeWithCentral RETURNED (Revit reports it synchronized), but the postcondition " +
                    (verified == false ? "did NOT hold" : "could not be measured") + ": " +
                    string.Join("; ", ownership.Problems.Concat(updates.Problems).Concat(overallProblems)) +
                    ". The sync cannot be undone; the detail names what differs.", report);
            }
            return CommandResult.Ok(report);
        }

        private static RelinquishOptions BuildRelinquish(SyncRelinquish choice)
        {
            if (choice == SyncRelinquish.All) return new RelinquishOptions(true);
            bool worksets = choice == SyncRelinquish.KeepBorrowed;
            return new RelinquishOptions(false)
            {
                StandardWorksets = worksets,
                ViewWorksets = worksets,
                FamilyWorksets = worksets,
                UserWorksets = worksets,
                CheckedOutElements = false
            };
        }

        /// <summary>
        /// Worksets owned by this user, every collectable element's checkout status, and
        /// which owned elements are BORROWED (owned while their workset is not). Elements
        /// inside an owned workset read as owned too (a non-borrowed element's owner is its
        /// workset's owner): those land in owned, never in borrowed. An element whose
        /// UniqueId cannot be read cannot be followed across the sync, so it is unreadable.
        /// </summary>
        private static SyncCensus TakeSyncCensus(Document doc)
        {
            var c = new SyncCensus();
            string user = null;
            try { user = doc.Application.Username; } catch { }
            var ownedWorksets = new HashSet<int>();
            try
            {
                foreach (Workset w in new FilteredWorksetCollector(doc))
                    if (w != null && user != null && string.Equals(w.Owner, user, StringComparison.OrdinalIgnoreCase))
                        ownedWorksets.Add(w.Id.IntegerValue);
                c.OwnedWorksets = user == null ? (int?)null : ownedWorksets.Count;
            }
            catch { c.OwnedWorksets = null; }

            foreach (bool types in new[] { false, true })
            {
                FilteredElementCollector collector;
                try
                {
                    collector = new FilteredElementCollector(doc);
                    collector = types ? collector.WhereElementIsElementType() : collector.WhereElementIsNotElementType();
                }
                catch { c.Unreadable++; continue; }
                foreach (Element e in collector)
                {
                    if (e == null) continue;
                    long id = Rid.Value(e.Id);
                    if (c.UidById.ContainsKey(id)) continue;
                    string uid = null;
                    try { uid = e.UniqueId; } catch { }
                    if (string.IsNullOrEmpty(uid)) { c.Unreadable++; continue; }
                    c.UidById[id] = uid;
                    c.IdByUid[uid] = id;
                    c.AllIds.Add(id);
                    try
                    {
                        if (WorksharingUtils.GetCheckoutStatus(doc, e.Id) != CheckoutStatus.OwnedByCurrentUser) continue;
                        c.OwnedIds.Add(id);
                        int ws = -1;
                        try { ws = e.WorksetId.IntegerValue; } catch { }
                        if (!ownedWorksets.Contains(ws)) c.BorrowedIds.Add(id);
                    }
                    catch { c.Unreadable++; }
                }
            }
            c.AllIds.Sort();
            return c;
        }

        /// <summary>By UniqueId. null = the element no longer exists; "unreadable" = the read threw.</summary>
        private static Dictionary<string, string> ReadUpdateStatuses(Document doc, List<string> sample)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string uid in sample)
            {
                try
                {
                    Element e = doc.GetElement(uid);
                    result[uid] = e == null ? null : WorksharingUtils.GetModelUpdatesStatus(doc, e.Id).ToString();
                }
                catch { result[uid] = SyncWithCentralRules.Unreadable; }
            }
            return result;
        }

        /// <summary>A lookup that throws counts as present: an unreadable element is never dropped from "expected".</summary>
        private static bool ElementExists(Document doc, string uid)
        {
            try { return doc.GetElement(uid) != null; }
            catch { return true; }
        }

        private static bool? HasAllChanges(Document doc, out string error)
        {
            error = null;
            try { return doc.HasAllChangesFromCentral(); }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return null; }
        }

        /// <summary>A file-based central's last write time; null for a server or cloud central, or when unreadable.</summary>
        private static DateTime? CentralWriteTime(string centralPath)
        {
            try
            {
                return !string.IsNullOrEmpty(centralPath) && System.IO.File.Exists(centralPath)
                    ? System.IO.File.GetLastWriteTimeUtc(centralPath) : (DateTime?)null;
            }
            catch { return null; }
        }

        private static JArray Display(IEnumerable<string> uids, SyncCensus after, SyncCensus before)
        {
            var list = new JArray();
            foreach (string uid in uids.Take(50))
            {
                long id;
                JToken shown = after.IdByUid.TryGetValue(uid, out id) || before.IdByUid.TryGetValue(uid, out id)
                    ? (JToken)id : JValue.CreateNull();
                list.Add(new JObject { ["unique_id"] = uid, ["element_id"] = shown });
            }
            return list;
        }

        private static Dictionary<string, string> ModelView(JObject estimate)
        {
            var view = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string field in SyncWithCentralRules.ModelFields)
                view[field] = estimate[field]?.ToString(Newtonsoft.Json.Formatting.None);
            return view;
        }

        private static void RememberEstimate(string token, Dictionary<string, string> view)
        {
            if (string.IsNullOrEmpty(token)) return;
            lock (SyncEstimatesLock)
            {
                SyncEstimates[token] = view;
                SyncEstimateOrder.Enqueue(token);
                while (SyncEstimateOrder.Count > 32) SyncEstimates.Remove(SyncEstimateOrder.Dequeue());
            }
        }

        private static Dictionary<string, string> RecallEstimate(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            lock (SyncEstimatesLock)
                return SyncEstimates.TryGetValue(token, out Dictionary<string, string> view) ? view : null;
        }
    }
}
