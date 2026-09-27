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
//     Advanced options), exactly like horizun_execute_python. The workshared
//     read-only policy wins over that grant. Decisions: SyncWithCentralRules.
//   * Its preview is an ESTIMATE and says so. Ownership is counted over every
//     collectable element with WorksharingUtils.GetCheckoutStatus; whether the
//     local is current is SAMPLED with GetModelUpdatesStatus, because that API is
//     per (Document, ElementId) and the document-level answer does not exist.
//   * The confirmation token binds that estimate. If ownership, IsModified or the
//     sampled statuses moved between preview and apply, the apply is refused.
//   * After the sync, ownership is re-counted and held against the relinquish
//     choice, and the same sample is re-read: every present element must read
//     CurrentWithCentral. A sync that happened but whose postcondition failed is
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
        private sealed class SyncCensus
        {
            public int? OwnedWorksets;
            public readonly List<long> AllIds = new List<long>();
            public readonly Dictionary<long, ElementId> Ids = new Dictionary<long, ElementId>();
            public readonly List<long> OwnedElements = new List<long>();
            public readonly List<long> Borrowed = new List<long>();
            public int Unreadable;
        }

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

            var clock = Stopwatch.StartNew();
            SyncCensus before = TakeSyncCensus(doc);
            if (before.OwnedWorksets == null || before.Unreadable > 0)
                return SyncRefuse("ownership_census_incomplete",
                    "The ownership of '" + title + "' could not be read in full (worksets readable=" +
                    (before.OwnedWorksets != null) + ", elements without a checkout status=" + before.Unreadable +
                    "). A sync whose relinquish cannot be checked afterwards would be reported unverified, so it " +
                    "is not offered. Nothing ran.");

            List<long> sample = SyncWithCentralRules.Sample(before.AllIds, before.OwnedElements,
                SyncWithCentralRules.SpreadSampleSize, SyncWithCentralRules.OwnedSampleSize);
            Dictionary<long, string> statusBefore = ReadUpdateStatuses(doc, before.Ids, sample);
            bool? modified = SafeModified(doc);
            string documentKey = DocumentGate.IdentityOf(doc, HostVersion(app))?.Fingerprint();
            var sampleCounts = JObject.FromObject(SyncWithCentralRules.Counts(statusBefore.Values));

            var estimate = new JObject
            {
                ["document"] = documentKey,
                ["comment"] = comment,
                ["relinquish"] = SyncWithCentralRules.Name(choice),
                ["compact"] = compact,
                ["owned_worksets"] = before.OwnedWorksets,
                ["owned_elements"] = before.OwnedElements.Count,
                ["borrowed_elements"] = before.Borrowed.Count,
                ["is_modified"] = modified,
                ["sample_status_counts"] = sampleCounts
            };
            string planHash = ConfirmationStore.PlanHash(estimate, SyncWithCentralRules.EstimateFields);

            string centralPath = null;
            try
            {
                ModelPath central = doc.GetWorksharingCentralModelPath();
                if (central != null) centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
            }
            catch { }

            if (dryRun)
            {
                Confirmation issued = DocumentGate.Confirmations.Issue(
                    SyncWithCentralRules.ConfirmationScope, documentKey, planHash);
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
                    ["owned_worksets"] = before.OwnedWorksets,
                    ["owned_elements"] = before.OwnedElements.Count,
                    ["borrowed_elements"] = before.Borrowed.Count,
                    ["borrowed_sample_ids"] = new JArray(before.Borrowed.Take(20)),
                    ["elements_scanned"] = before.AllIds.Count,
                    ["update_status_sample"] = new JObject
                    {
                        ["sample_size"] = sample.Count,
                        ["counts"] = sampleCounts,
                        ["method"] = "WorksharingUtils.GetModelUpdatesStatus per element, on " + sample.Count +
                                     " of " + before.AllIds.Count + " elements (an even spread plus the first owned)."
                    },
                    ["estimate_note"] =
                        "ESTIMATE, not a rehearsal. A sync cannot be rehearsed or rolled back. Ownership counts are " +
                        "exact at this moment; whether the local is current with central is SAMPLED, because the API " +
                        "answers per element and has no document-level status. What other people pushed to the " +
                        "central since your last reload is not visible from here until the sync runs.",
                    ["cannot_be_rolled_back"] = true,
                    ["confirmation_token"] = issued.Token,
                    ["confirmation_expires_utc"] = issued.ExpiresUtc.ToString("u"),
                    ["confirmation_note"] =
                        "Bound to THIS estimate: the same document, relinquish, comment, compact, ownership counts, " +
                        "IsModified and sampled statuses. If any of them moves before the apply, it is refused and " +
                        "nothing is synchronized.",
                    ["elapsed_ms"] = clock.ElapsedMilliseconds
                });
            }

            ConfirmationCheck check = DocumentGate.Confirmations.Validate(
                request.Value<string>("confirmation_token"), SyncWithCentralRules.ConfirmationScope, documentKey, planHash);
            if (!check.Ok)
                return SyncRefuse("confirmation_rejected",
                    "REFUSING TO SYNCHRONIZE '" + title + "': " + check.Message + " The estimate the token was " +
                    "issued for must match the model now. Run operation=sync_with_central with dry_run=true again " +
                    "and confirm the new estimate. Nothing was synchronized.");

            var options = new SynchronizeWithCentralOptions
            {
                Comment = comment,
                Compact = compact,
                SaveLocalBefore = true,
                SaveLocalAfter = true
            };
            options.SetRelinquishOptions(BuildRelinquish(choice));

            string syncError = null;
            try { doc.SynchronizeWithCentral(new TransactWithCentralOptions(), options); }
            catch (Exception ex) { syncError = ex.GetType().Name + ": " + ex.Message; }

            SyncCensus after = TakeSyncCensus(doc);
            var afterSet = new HashSet<long>(after.AllIds);
            SyncVerdict ownership = SyncWithCentralRules.VerifyOwnership(choice,
                before.OwnedWorksets, before.OwnedElements, before.Borrowed,
                after.OwnedWorksets, after.OwnedElements, after.Unreadable, id => afterSet.Contains(id));
            Dictionary<long, string> statusAfter = ReadUpdateStatuses(doc, before.Ids, sample);
            SyncVerdict updates = SyncWithCentralRules.VerifyUpdates(statusAfter);

            bool? verified = ownership.Verified == false || updates.Verified == false ? false
                : (ownership.Verified == true && updates.Verified == true ? (bool?)true : null);

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
                    ["owned_elements_before"] = before.OwnedElements.Count,
                    ["borrowed_elements_before"] = before.Borrowed.Count,
                    ["owned_elements_after"] = after.OwnedElements.Count,
                    ["unreadable_after"] = after.Unreadable,
                    ["unexpectedly_owned_ids"] = new JArray(ownership.UnexpectedlyOwned.Take(50)),
                    ["unexpectedly_released_ids"] = new JArray(ownership.UnexpectedlyReleased.Take(50)),
                    ["problems"] = new JArray(ownership.Problems)
                },
                ["update_status_sample"] = new JObject
                {
                    ["verified"] = updates.Verified,
                    ["sample_size"] = sample.Count,
                    ["counts_before"] = sampleCounts,
                    ["counts_after"] = JObject.FromObject(SyncWithCentralRules.Counts(statusAfter.Values)),
                    ["not_current_ids"] = new JArray(updates.UnexpectedlyOwned.Take(50)),
                    ["problems"] = new JArray(updates.Problems)
                },
                ["is_modified_after"] = SafeModified(doc),
                ["measured_how"] =
                    "Worksets counted by Owner; every collectable element checked with WorksharingUtils." +
                    "GetCheckoutStatus before and after; the same element sample re-read with GetModelUpdatesStatus.",
                ["elapsed_ms"] = clock.ElapsedMilliseconds
            };

            if (syncError != null)
            {
                report["write_started"] = true;
                report["changes_applied"] = JValue.CreateNull();
                return CommandResult.FailWithDetail(
                    "SynchronizeWithCentral threw (" + syncError + "). Whether anything reached the central is not " +
                    "known from the exception; the ownership and status re-read after it is in the detail. Do not " +
                    "retry blindly: preview again first.", report);
            }
            if (verified != true)
            {
                report["write_started"] = true;
                report["changes_applied"] = true;
                return CommandResult.FailWithDetail(
                    "SYNCHRONIZED, but the postcondition " + (verified == false ? "did NOT hold" : "could not be measured") +
                    ": " + string.Join("; ", ownership.Problems.Concat(updates.Problems)) + ". The sync cannot be undone; " +
                    "the detail names what differs.", report);
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
        /// which owned elements are BORROWED (owned while their workset is not). Whether
        /// Revit reports elements inside an owned workset as OwnedByCurrentUser does not
        /// change the split: those land in owned, never in borrowed.
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
                    if (c.Ids.ContainsKey(id)) continue;
                    c.Ids[id] = e.Id;
                    c.AllIds.Add(id);
                    try
                    {
                        if (WorksharingUtils.GetCheckoutStatus(doc, e.Id) != CheckoutStatus.OwnedByCurrentUser) continue;
                        c.OwnedElements.Add(id);
                        int ws = -1;
                        try { ws = e.WorksetId.IntegerValue; } catch { }
                        if (!ownedWorksets.Contains(ws)) c.Borrowed.Add(id);
                    }
                    catch { c.Unreadable++; }
                }
            }
            c.AllIds.Sort();
            return c;
        }

        /// <summary>null = the element no longer exists; "unreadable" = the read threw.</summary>
        private static Dictionary<long, string> ReadUpdateStatuses(Document doc, Dictionary<long, ElementId> ids,
                                                                  List<long> sample)
        {
            var result = new Dictionary<long, string>();
            foreach (long id in sample)
            {
                if (!ids.TryGetValue(id, out ElementId eid)) { result[id] = null; continue; }
                try
                {
                    if (doc.GetElement(eid) == null) { result[id] = null; continue; }
                    result[id] = WorksharingUtils.GetModelUpdatesStatus(doc, eid).ToString();
                }
                catch { result[id] = SyncWithCentralRules.Unreadable; }
            }
            return result;
        }
    }
}
