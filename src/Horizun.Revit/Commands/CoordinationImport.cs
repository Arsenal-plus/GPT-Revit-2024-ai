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
// THE HARD PART IS NOT THE ZIP. It is deciding what a returned topic MEANS about
// a finding this model measured, and the rule here is deliberately conservative:
//
//   A RETURNED TOPIC NEVER RESOLVES A FINDING. resolved_by_model is detection's
//   verdict and nothing else may assert it - an external tool saying "Closed"
//   means a person decided, not that the geometry moved. So a closed topic maps
//   to closed_by_decision, which is exactly what it is, and the comment that
//   accompanied it is preserved so the decision has its reason attached.
//
//   A TOPIC THIS LEDGER DOES NOT KNOW IS NOT INVENTED INTO IT. Someone else's
//   BCF, or a topic raised by hand in another tool, has no pair of elements in
//   this model. It is REPORTED as unmatched, with its title, rather than folded
//   in as a finding with no sides - which would put a row in the ledger that no
//   detection run could ever resolve or regress.
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
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CoordinationCommand
    {
        /// <summary>
        /// Fold a .bcfzip back into this document's ledger.
        ///
        /// Statuses and comments only. Nothing here can create a finding, and nothing
        /// here can resolve one.
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
            if (!TryReadTopics(path, out topics, out readError))
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

                string wantedStatus = MapStatus(topic.Status);
                var change = new Change { Finding = finding, Topic = topic };

                if (wantedStatus != null && wantedStatus != finding.Status)
                {
                    // BOTH SIDES MOVED? A coordinator's week-old "Closed" overwriting a
                    // re-detection that re-opened the issue yesterday leaves the ledger saying
                    // Closed about a clash that is still in the model. Two dates that were both
                    // already recorded and never compared.
                    string localAt = finding.UpdatedUtc;
                    string externalAt = LastExternalChange(topic);
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
                    if (!AlreadyRecorded(finding, comment))
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
                        ImportedCommentText(comment), nowUtc);
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

                string wantedStatus = MapStatus(topic.Status);
                if (wantedStatus != null && wantedStatus != finding.Status)
                {
                    string why;
                    if (CoordinationRules.CanTransition(finding.Status, wantedStatus, out why))
                    {
                        // Same conflict rule as a matched topic: a brand-new finding (UpdatedUtc
                        // still null) can never conflict, since nothing local existed to disagree with.
                        string localAt = finding.UpdatedUtc;
                        string externalAt = LastExternalChange(topic);
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
                    if (!AlreadyRecorded(finding, comment))
                    {
                        CoordinationRules.AppendEvent(finding, "comment", ImportedCommentText(comment), nowUtc);
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

        // =====================================================================
        // Reading the file
        // =====================================================================

        private static bool TryReadTopics(string path, out List<BcfTopic> topics, out string error)
        {
            topics = new List<BcfTopic>();
            error = null;
            try
            {
                using (FileStream stream = File.OpenRead(path))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (!entry.FullName.EndsWith("markup.bcf", StringComparison.OrdinalIgnoreCase)) continue;
                        var xml = new XmlDocument();
                        using (Stream entryStream = entry.Open()) xml.Load(entryStream);
                        BcfTopic topic = ReadTopic(xml, entry.FullName);
                        if (topic == null) continue;

                        // THE VIEWPOINT(S): a topic's Components live beside markup.bcf, never
                        // in it. A markup that names a .bcfv this zip does not hold, or one that
                        // is not valid XML, leaves this topic with zero components - which the
                        // resolution step below reports as not_traceable, never as a reason to
                        // fail the whole import (one broken viewpoint is not every topic's fault).
                        string folder = entry.FullName.Substring(0, entry.FullName.Length - "markup.bcf".Length);
                        foreach (string viewpointFile in DeclaredViewpointFiles(xml.DocumentElement))
                        {
                            ZipArchiveEntry vpEntry = zip.GetEntry(folder + viewpointFile);
                            if (vpEntry == null) continue;
                            try
                            {
                                var vpXml = new XmlDocument();
                                using (Stream vpStream = vpEntry.Open()) vpXml.Load(vpStream);
                                topic.Components.AddRange(ReadComponents(vpXml.DocumentElement));
                            }
                            catch (XmlException) { /* this topic's viewpoint is unreadable; it just resolves fewer components */ }
                        }
                        topics.Add(topic);
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                error = "'" + path + "' could not be opened as a zip: " + ex.Message +
                        ". A BCF is a zip; nothing was read.";
                return false;
            }
            catch (XmlException ex)
            {
                // A file with one broken topic is not a file with none: say which entry.
                error = "a markup.bcf entry inside '" + path + "' is not valid XML: " + ex.Message +
                        ". Nothing was imported - a partial import of somebody else's coordination file is " +
                        "worse than none, because nobody can tell which half arrived.";
                return false;
            }
            catch (Exception ex)
            {
                error = "'" + path + "' could not be read: " + ex.Message;
                return false;
            }
            return true;
        }

        private static BcfTopic ReadTopic(XmlDocument xml, string entryName)
        {
            XmlElement root = xml.DocumentElement;
            if (root == null || root.Name != "Markup") return null;
            XmlNode topicNode = root.SelectSingleNode("Topic");
            if (topicNode == null) return null;

            var topic = new BcfTopic
            {
                Entry = entryName,
                Guid = Attribute(topicNode, "Guid"),
                Status = Attribute(topicNode, "TopicStatus"),
                Title = Text(topicNode, "Title"),
                Priority = Text(topicNode, "Priority"),
                AssignedTo = Text(topicNode, "AssignedTo"),
                CreationDate = Text(topicNode, "CreationDate")
            };

            foreach (XmlNode commentNode in root.SelectNodes("Comment"))
                topic.Comments.Add(new BcfComment
                {
                    Guid = Attribute(commentNode, "Guid"),
                    Date = Text(commentNode, "Date"),
                    Author = Text(commentNode, "Author"),
                    Text = Text(commentNode, "Comment")
                });

            return string.IsNullOrWhiteSpace(topic.Guid) ? null : topic;
        }

        /// <summary>
        /// The viewpoint filename(s) a topic's markup.bcf declares - BOTH the BCF 2.1 shape
        /// (one or more sibling &lt;Viewpoints Guid="..."&gt;&lt;Viewpoint&gt;file&lt;/Viewpoint&gt;&lt;/Viewpoints&gt;
        /// elements) and the BCF 3.0 shape (one &lt;Viewpoints&gt; wrapping several
        /// &lt;ViewPoint Guid="..."&gt;&lt;Viewpoint&gt;file&lt;/Viewpoint&gt;&lt;/ViewPoint&gt; children). Both carry
        /// the file name in a child element literally named "Viewpoint"; only the nesting differs.
        /// </summary>
        private static List<string> DeclaredViewpointFiles(XmlElement markupRoot)
        {
            var files = new List<string>();
            if (markupRoot == null) return files;
            foreach (XmlNode viewpointsNode in markupRoot.SelectNodes("Viewpoints"))
            {
                foreach (XmlNode direct in viewpointsNode.SelectNodes("Viewpoint"))
                    if (!string.IsNullOrWhiteSpace(direct.InnerText)) files.Add(direct.InnerText.Trim());
                foreach (XmlNode nested in viewpointsNode.SelectNodes("ViewPoint"))
                {
                    XmlNode inner = nested.SelectSingleNode("Viewpoint");
                    if (inner != null && !string.IsNullOrWhiteSpace(inner.InnerText)) files.Add(inner.InnerText.Trim());
                }
            }
            return files;
        }

        /// <summary>
        /// Every Components/Selection/Component a viewpoint names, exactly as written: an
        /// IfcGuid attribute and/or an AuthoringToolId child. A component with neither is not
        /// collected - it names nothing this or any other tool could resolve.
        /// </summary>
        private static List<BcfExternalComponent> ReadComponents(XmlElement viewpointRoot)
        {
            var list = new List<BcfExternalComponent>();
            if (viewpointRoot == null) return list;
            foreach (XmlNode c in viewpointRoot.SelectNodes("Components/Selection/Component"))
            {
                string ifcGuid = Attribute(c, "IfcGuid");
                string authoringToolId = Text(c, "AuthoringToolId");
                if (string.IsNullOrWhiteSpace(ifcGuid) && string.IsNullOrWhiteSpace(authoringToolId)) continue;
                list.Add(new BcfExternalComponent
                {
                    IfcGuid = string.IsNullOrWhiteSpace(ifcGuid) ? null : ifcGuid.Trim(),
                    AuthoringToolId = string.IsNullOrWhiteSpace(authoringToolId) ? null : authoringToolId.Trim()
                });
            }
            return list;
        }

        private static string Attribute(XmlNode node, string name)
        {
            XmlAttribute attribute = node?.Attributes?[name];
            return attribute?.Value;
        }

        private static string Text(XmlNode node, string child)
        {
            XmlNode found = node?.SelectSingleNode(child);
            return found?.InnerText;
        }

        /// <summary>
        /// A BCF TopicStatus mapped to one of this ledger's statuses, or null when it
        /// says nothing this ledger can act on.
        ///
        /// "Closed" becomes closed_by_decision and never resolved_by_model. The two are
        /// different claims: one says a person decided, the other says the geometry
        /// changed and a complete detection run proved it.
        /// </summary>
        /// <summary>
        /// When this topic last changed OUTSIDE this ledger: its newest comment, or its
        /// creation date when it has none.
        ///
        /// ISO-8601 UTC strings compare correctly as ordinals, which is why they are written
        /// that way everywhere in this codebase. A topic with no date at all yields null, and
        /// a null cannot conflict: an unknown date is not evidence that something moved.
        /// </summary>
        private static string LastExternalChange(BcfTopic topic)
        {
            string newest = topic.CreationDate;
            foreach (BcfComment comment in topic.Comments ?? new List<BcfComment>())
            {
                if (string.IsNullOrWhiteSpace(comment.Date)) continue;
                if (newest == null || string.CompareOrdinal(comment.Date, newest) > 0) newest = comment.Date;
            }
            return string.IsNullOrWhiteSpace(newest) ? null : newest;
        }

        private static string MapStatus(string bcfStatus)
        {
            switch ((bcfStatus ?? "").Trim().ToLowerInvariant())
            {
                case "closed":
                case "resolved":
                    return CoordinationRules.StatusClosedByDecision;
                case "open":
                case "active":
                case "reopened":
                    return CoordinationRules.StatusOpen;
                default:
                    return null;
            }
        }

        private static string ImportedCommentText(BcfComment comment)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(comment.Author)) parts.Add(comment.Author);
            if (!string.IsNullOrWhiteSpace(comment.Date)) parts.Add(comment.Date);
            string who = parts.Count == 0 ? "" : " [" + string.Join(" · ", parts) + "]";
            return "imported from BCF" + who + ": " + (comment.Text ?? "");
        }

        /// <summary>
        /// Has this comment already been folded in? Compared on the text it would
        /// produce, so re-importing the same file - which coordinators do - adds nothing
        /// the second time.
        /// </summary>
        private static bool AlreadyRecorded(CoordinationFinding finding, BcfComment comment)
        {
            string wanted = ImportedCommentText(comment);
            foreach (CoordinationEvent entry in finding.History ?? new List<CoordinationEvent>())
                if (entry.Kind == "comment" && string.Equals(entry.Text, wanted, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private sealed class BcfTopic
        {
            public string Entry;
            public string Guid;
            public string Status;
            public string Title;
            public string Priority;
            public string AssignedTo;

            /// <summary>The topic's own CreationDate: the only external date a topic with no comments has.</summary>
            public string CreationDate;
            public readonly List<BcfComment> Comments = new List<BcfComment>();

            /// <summary>
            /// Every Component this topic's viewpoint(s) name - IfcGuid and/or AuthoringToolId,
            /// exactly what the file itself carries. Empty for a topic with no viewpoint, or
            /// one this reader could not parse; never invented.
            /// </summary>
            public readonly List<BcfExternalComponent> Components = new List<BcfExternalComponent>();
        }

        /// <summary>One Components/Selection/Component entry from ANY tool's viewpoint.bcfv.</summary>
        private sealed class BcfExternalComponent
        {
            public string IfcGuid;
            public string AuthoringToolId;
        }

        private sealed class BcfComment
        {
            public string Guid;
            public string Date;
            public string Author;
            public string Text;
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
