// -----------------------------------------------------------------------------
// Horizun Revit MCP - reading a .bcfzip back. Original Horizun code.
//
// G14 of the 2026-09-14 competitive inventory, second half. The export half was
// already here (CoordinationCommand.ExportBcf, BCF 2.1, re-read and hashed) - the
// inventory said otherwise and was wrong, which is recorded in the campaign's
// BACKLOG. What was genuinely missing is the return trip: a coordinator opens the
// exported file in BIMcollab or Solibri, writes comments and statuses into it, and
// sends it back. Until now that file could only be read by a human.
//
// THE HARD PART IS NOT THE ZIP (Core's BcfMarkupReader does that, Revit-free and
// tested without a model). It is deciding what a returned topic MEANS about a
// finding this model measured, and the rule here is deliberately conservative:
//
//   A RETURNED TOPIC NEVER RESOLVES A FINDING. resolved_by_model is detection's
//   verdict and nothing else may assert it - an external tool saying "Closed"
//   means a person decided, not that the geometry moved. So a closed topic maps
//   to closed_by_decision, which is exactly what it is, and the comment that
//   accompanied it is preserved so the decision has its reason attached.
//
//   A TOPIC THIS LEDGER DID NOT MINT IS NOT INVENTED INTO IT BY ASSERTION. Someone
//   else's BCF, or a topic raised by hand in another tool, is not matched by the
//   guid this ledger mints for its own exports (BcfTopicGuid) - so instead of just
//   reporting it, CoordinationImportBcfExternal.cs resolves its own viewpoint
//   Components against the model and RE-DETECTS the pair, exactly like
//   import_navisworks does beside it. Only a REPRODUCED pair becomes a finding;
//   one that cannot be traced to two elements is reported not_traceable, never
//   invented.
//
//   THE MATCH IS BY THE GUID WE MINTED. BcfTopicGuid is a deterministic function
//   of the finding id, so a topic that came from this ledger matches exactly.
//   Matching on the title instead would be matching on a string a coordinator is
//   free to edit, and the first person who tidied a title would silently detach
//   their comments from the finding.
//
// AND IT IS STILL A DRY RUN FIRST. The ledger is bridge state rather than model
// state, so there is no Revit transaction - but importing somebody else's file is
// exactly the moment to show what WOULD change before changing it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CoordinationCommand
    {
        /// <summary>
        /// Fold a .bcfzip back into this document's ledger - both topics this ledger
        /// exported (status/comments only) and topics from any other tool (resolved and
        /// re-detected against the model; see CoordinationImportBcfExternal.cs).
        /// </summary>
        private static CommandResult Import(Document doc, JObject request, string ledgerPath)
        {
            string path = request.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return CommandResult.Fail("path is required: the .bcfzip to read.");
            if (!File.Exists(path))
                return CommandResult.Fail("no file at '" + path + "'. Nothing was read.");

            List<BcfTopic> topics;
            string readError;
            if (!BcfMarkupReader.TryReadTopics(path, out topics, out readError))
                return CommandResult.Fail(readError);

            if (topics.Count == 0)
                return CommandResult.Fail(
                    "'" + path + "' is a readable zip with no BCF topic in it. A file with no markup.bcf entry " +
                    "is not a BCF, and reporting zero imported topics would read as 'nothing had changed'.");

            string documentTitle;
            Dictionary<string, CoordinationFinding> findings = CoordinationLedger.Load(ledgerPath, out documentTitle);

            // Index by the guid this ledger MINTS for each finding. A coordinator may
            // rename a topic freely; the guid is the only thing that survives that.
            var byGuid = new Dictionary<string, CoordinationFinding>(StringComparer.OrdinalIgnoreCase);
            foreach (CoordinationFinding f in findings.Values)
                byGuid[CoordinationRules.BcfTopicGuid(f.Id)] = f;

            string nowUtc = DateTime.UtcNow.ToString("o");
            var planned = new JArray();
            var unmatched = new JArray();
            var conflicts = new JArray();

            string onConflict = (request.Value<string>("on_conflict") ?? "report").ToLowerInvariant();
            if (onConflict != "report" && onConflict != "prefer_external" && onConflict != "prefer_local")
                return CommandResult.Fail(
                    "on_conflict must be 'report' (default: apply nothing where both sides moved, and say " +
                    "so), 'prefer_external' (take the file's word) or 'prefer_local' (keep this ledger's). " +
                    "There is no safe default beyond reporting: which side is right depends on what " +
                    "happened, and nothing here knows that.");
            var refused = new JArray();
            var changes = new List<Change>();
            var unmatchedTopics = new List<BcfTopic>();

            foreach (BcfTopic topic in topics)
            {
                CoordinationFinding finding;
                if (!byGuid.TryGetValue(topic.Guid, out finding))
                {
                    unmatchedTopics.Add(topic);
                    unmatched.Add(new JObject
                    {
                        ["guid"] = topic.Guid,
                        ["title"] = topic.Title,
                        ["status"] = topic.Status,
                        ["comments"] = topic.Comments.Count,
                        ["means"] = "no finding in this document's ledger has that topic guid - it is not one " +
                                    "this ledger exported. Resolved and re-detected against its own viewpoint " +
                                    "components below (external_reproduced/external_not_traceable/" +
                                    "external_not_reproduced); NOT invented into a finding merely for being named here."
                    });
                    continue;
                }

                string wantedStatus = BcfMarkupReader.MapStatus(topic.Status);
                var change = new Change { Finding = finding, Topic = topic };

                if (wantedStatus != null && wantedStatus != finding.Status)
                {
                    // BOTH SIDES MOVED? A coordinator's week-old "Closed" overwriting a
                    // re-detection that re-opened the issue yesterday leaves the ledger saying
                    // Closed about a clash that is still in the model. Two dates that were both
                    // already recorded and never compared.
                    string localAt = finding.UpdatedUtc;
                    string externalAt = BcfMarkupReader.LastExternalChange(topic);
                    bool conflict = localAt != null && externalAt != null &&
                                    string.CompareOrdinal(localAt, externalAt) > 0;

                    string why;
                    if (!CoordinationRules.CanTransition(finding.Status, wantedStatus, out why))
                        refused.Add(new JObject
                        {
                            ["finding_id"] = finding.Id,
                            ["guid"] = topic.Guid,
                            ["from"] = finding.Status,
                            ["to"] = wantedStatus,
                            ["reason"] = why
                        });
                    else if (conflict && onConflict == "report")
                        conflicts.Add(new JObject
                        {
                            ["finding_id"] = finding.Id,
                            ["guid"] = topic.Guid,
                            ["local_status"] = finding.Status,
                            ["local_changed_utc"] = localAt,
                            ["external_status"] = wantedStatus,
                            ["external_changed_utc"] = externalAt,
                            ["means"] =
                                "this issue changed on BOTH sides since the file was sent out, and the " +
                                "status was NOT applied. The local change is the newer one. Applying the " +
                                "external status would leave the ledger saying '" + wantedStatus + "' " +
                                "about a finding this model re-measured at " + localAt + ". Send " +
                                "on_conflict='prefer_external' to take the file's word, or " +
                                "'prefer_local' to keep this ledger's - neither is a default, because " +
                                "which is right depends on what happened and nothing here knows that."
                        });
                    else if (conflict && onConflict == "prefer_local")
                        conflicts.Add(new JObject
                        {
                            ["finding_id"] = finding.Id,
                            ["guid"] = topic.Guid,
                            ["local_status"] = finding.Status,
                            ["external_status"] = wantedStatus,
                            ["resolution"] = "kept the local status, as on_conflict asked"
                        });
                    else
                        change.NewStatus = wantedStatus;
                }

                // Comments this ledger has not seen. Compared by their own text and date,
                // because re-importing the same file must not double every comment - a
                // coordinator sends the file back more than once.
                foreach (BcfComment comment in topic.Comments)
                    if (!BcfMarkupReader.AlreadyRecorded(finding, comment))
                        change.NewComments.Add(comment);

                if (change.NewStatus == null && change.NewComments.Count == 0) continue;

                changes.Add(change);
                planned.Add(new JObject
                {
                    ["finding_id"] = finding.Id,
                    ["guid"] = topic.Guid,
                    ["status_from"] = finding.Status,
                    ["status_to"] = change.NewStatus == null ? (JToken)JValue.CreateNull() : change.NewStatus,
                    ["comments_to_add"] = change.NewComments.Count
                });
            }

            // ---- topics THIS ledger does not recognize: resolve their own viewpoint
            // components against the model and re-detect, exactly like import_navisworks. ----
            JArray extNotTraceable, extReproduced, extNotReproduced;
            List<CoordinationDetected> extDetected;
            Dictionary<string, BcfTopic> extTopicByFindingId;
            List<string> extLinksUnloaded;
            string extMatchRule;
            ResolveExternalBcfTopics(doc, unmatchedTopics, out extNotTraceable, out extReproduced,
                out extNotReproduced, out extDetected, out extTopicByFindingId, out extLinksUnloaded, out extMatchRule);

            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            var summary = new JObject
            {
                ["document"] = doc.Title,
                ["path"] = path,
                ["topics_in_file"] = topics.Count,
                ["matched"] = topics.Count - unmatched.Count,
                ["planned"] = planned,
                ["unmatched"] = unmatched,
                ["conflicts"] = conflicts,
                ["on_conflict"] = onConflict,
                ["conflict_means"] =
                    "a CONFLICT is an issue that changed on BOTH sides since the file was sent out - the " +
                    "topic carries a newer external change and this ledger carries a newer local one. " +
                    "Under the default 'report' the status is NOT applied: a coordinator's week-old " +
                    "'Closed' overwriting yesterday's re-detection leaves the ledger saying Closed about " +
                    "a clash that is still in the model.",
                ["refused_transitions"] = refused,
                ["external_reproduced"] = extReproduced,
                ["external_not_reproduced"] = extNotReproduced,
                ["external_not_traceable"] = extNotTraceable,
                ["external_links_not_loaded"] = new JArray(extLinksUnloaded),
                ["external_match_rule"] = extMatchRule,
                ["means"] =
                    "A returned topic that matches one of THIS ledger's own exports NEVER sets " +
                    "resolved_by_model: that status is detection's verdict, and an external tool saying " +
                    "'Closed' means a person decided, which is closed_by_decision (see 'planned'/'unmatched' " +
                    "above). A topic from ANY OTHER tool is resolved by its own viewpoint components and " +
                    "RE-DETECTED (external_reproduced/external_not_reproduced/external_not_traceable): only a " +
                    "reproduced pair becomes a finding, origin 'bcf', runComplete=false always - it never " +
                    "resolves a finding by itself either."
            };

            if (dry)
            {
                summary["dry_run"] = true;
                summary["would_change"] = changes.Count;
                summary["external_would_record"] = extDetected.Count;
                return CommandResult.Ok(summary);
            }

            foreach (Change change in changes)
            {
                if (change.NewStatus != null)
                {
                    CoordinationRules.AppendEvent(change.Finding, "status",
                        "imported from BCF '" + Path.GetFileName(path) + "': " +
                        change.Finding.Status + " -> " + change.NewStatus, nowUtc);
                    change.Finding.Status = change.NewStatus;
                    change.Finding.UpdatedUtc = nowUtc;
                }
                foreach (BcfComment comment in change.NewComments)
                {
                    CoordinationRules.AppendEvent(change.Finding, "comment",
                        BcfMarkupReader.ImportedCommentText(comment), nowUtc);
                    change.Finding.UpdatedUtc = nowUtc;
                }
            }

            // runComplete is ALWAYS false: a spot-check over named topics is not a complete
            // detection run over a category scope, and must never resolve anything.
            CoordinationRules.Merge(findings, extDetected, nowUtc, runComplete: false, scopeKey: "bcf");
            foreach (KeyValuePair<string, BcfTopic> kv in extTopicByFindingId)
            {
                CoordinationFinding finding;
                if (!findings.TryGetValue(kv.Key, out finding)) continue; // Merge just added or refreshed it
                BcfTopic topic = kv.Value;

                string wantedStatus = BcfMarkupReader.MapStatus(topic.Status);
                if (wantedStatus != null && wantedStatus != finding.Status)
                {
                    string why;
                    if (CoordinationRules.CanTransition(finding.Status, wantedStatus, out why))
                    {
                        // Same conflict rule as a matched topic: a brand-new finding (UpdatedUtc
                        // still null) can never conflict, since nothing local existed to disagree with.
                        string localAt = finding.UpdatedUtc;
                        string externalAt = BcfMarkupReader.LastExternalChange(topic);
                        bool conflict = localAt != null && externalAt != null &&
                                        string.CompareOrdinal(localAt, externalAt) > 0;
                        if (!conflict || onConflict == "prefer_external")
                        {
                            CoordinationRules.AppendEvent(finding, "status",
                                "imported from BCF '" + Path.GetFileName(path) + "' (external topic " +
                                topic.Guid + "): " + finding.Status + " -> " + wantedStatus, nowUtc);
                            finding.Status = wantedStatus;
                            finding.UpdatedUtc = nowUtc;
                        }
                    }
                }
                if (string.IsNullOrWhiteSpace(finding.Assignee) && !string.IsNullOrWhiteSpace(topic.AssignedTo))
                {
                    finding.Assignee = topic.AssignedTo;
                    finding.UpdatedUtc = nowUtc;
                }
                foreach (BcfComment comment in topic.Comments)
                    if (!BcfMarkupReader.AlreadyRecorded(finding, comment))
                    {
                        CoordinationRules.AppendEvent(finding, "comment", BcfMarkupReader.ImportedCommentText(comment), nowUtc);
                        finding.UpdatedUtc = nowUtc;
                    }
            }

            CoordinationLedger.Save(ledgerPath, documentTitle ?? doc.Title, findings);

            // RE-READ. The ledger is a small file and the contract does not bend for
            // small files: a save that did not land must not be reported as one that did.
            string reloadedTitle;
            Dictionary<string, CoordinationFinding> reloaded =
                CoordinationLedger.Load(ledgerPath, out reloadedTitle);
            var notVerified = new JArray();
            foreach (Change change in changes)
            {
                CoordinationFinding after;
                if (!reloaded.TryGetValue(change.Finding.Id, out after))
                { notVerified.Add(change.Finding.Id); continue; }
                if (change.NewStatus != null && after.Status != change.NewStatus)
                    notVerified.Add(change.Finding.Id);
            }
            foreach (string findingId in extTopicByFindingId.Keys)
            {
                CoordinationFinding after;
                if (!reloaded.TryGetValue(findingId, out after) || after.ExternalSource != "bcf")
                    notVerified.Add(findingId);
            }
            if (notVerified.Count > 0)
                return CommandResult.Fail(
                    "The ledger was written and re-reading it does not show " + notVerified.Count +
                    " of the imported change(s): " + string.Join(", ", notVerified.Select(t => (string)t)) +
                    ". Success is not claimed; inspect " + ledgerPath + ".");

            summary["dry_run"] = false;
            summary["applied"] = changes.Count;
            summary["external_recorded"] = extDetected.Count;
            summary["verified_by_reread"] = true;
            return CommandResult.Ok(summary);
        }

        private sealed class Change
        {
            public CoordinationFinding Finding;
            public BcfTopic Topic;
            public string NewStatus;
            public readonly List<BcfComment> NewComments = new List<BcfComment>();
        }
    }
}
