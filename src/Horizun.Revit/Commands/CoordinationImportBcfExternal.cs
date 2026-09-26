// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A BCF topic raised in NAVISWORKS, ACC, Solibri or BIMcollab names elements by
// their viewpoint Components (IfcGuid and/or AuthoringToolId) - never by the guid
// this ledger mints for its OWN exports (CoordinationRules.BcfTopicGuid), which is
// why CoordinationImport.cs's byGuid lookup reports every one of them as
// "unmatched" and stops. This file is what happens next, following the SAME rule
// import_navisworks already established beside it:
//
//   AN EXTERNAL TOOL NEVER CREATES A FINDING BY ASSERTION. A BCF topic says two
//   Components clash; this resolves each Component to an element in the ACTIVE
//   document or a loaded link and RE-DETECTS the pair with the identical solid-
//   intersection/bounding-box measurement CoordinationImportNavisworks.ReDetectPair
//   already uses. Only a REPRODUCED pair becomes a finding, with runComplete=false
//   always - a spot-check over named topics is not a detection run over a category
//   scope, and it must never resolve anything.
//
// RESOLVING A COMPONENT, two independent paths, tried in this order because the
// first is exact when it says anything at all:
//
//   AuthoringToolId - tried as a Revit Element Id (an integer, exactly what
//   NavisworksHandoff's targets carry), then as a Revit UniqueId
//   (Document.GetElement(string) - Revit's own reverse lookup, wrong for a
//   Element Id from a DIFFERENT document, which is exactly why it is tried on
//   every loaded link too, not only the host).
//
//   IfcGuid - the compressed IFC GlobalId a coordination tool actually writes.
//   Two ways an element carries one: the IFC_GUID parameter, set by an earlier
//   IFC export that stored it (a native ElementParameterFilter, so this never
//   walks the whole model to find it); or, when nothing was ever stored,
//   Revit's OWN COMPUTED default - ExportUtils.GetExportId(doc, id) - which is
//   what a fresh model's IFC export actually writes. There is no reverse API
///   for that, so the incoming compressed guid is DECODED back to the .NET
//   Guid ExportUtils deals in (IfcGuidCodec.Decode) and matched against an
//   index this file builds once per document per import (GuidScanCache),
//   capped defensively - a model too large to scan in one import call falls
//   back to the parameter path alone, and says so rather than hanging.
//
// A topic with fewer than two resolvable Components is reported not_traceable
// WITH THE REASON, never invented; one that resolves two but does not reproduce
// is reported, not silently dropped.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CoordinationCommand
    {
        private const int MaxComponentsPerBcfTopic = MaxTargetsPerSide; // 10 - same bound import_navisworks uses per side

        /// <summary>
        /// Per-import, per-document cache mapping a computed export Guid back to an
        /// ElementId - the fallback path for a Component whose IfcGuid was never stored
        /// as the IFC_GUID parameter. Built lazily and ONCE per document; a document too
        /// large to scan within MaxElementsPerDocument is remembered as such so a second
        /// Component in the same import does not re-attempt (and re-fail) the same scan.
        /// </summary>
        private sealed class GuidScanCache
        {
            private const int MaxElementsPerDocument = 60000;
            private readonly Dictionary<Document, Dictionary<Guid, ElementId>> _byDoc =
                new Dictionary<Document, Dictionary<Guid, ElementId>>();
            private readonly HashSet<Document> _tooLargeToScan = new HashSet<Document>();

            public bool SkippedAsTooLarge(Document doc) => _tooLargeToScan.Contains(doc);

            public Element FindByExportId(Document doc, Guid target)
            {
                Dictionary<Guid, ElementId> index;
                if (!_byDoc.TryGetValue(doc, out index))
                {
                    if (_tooLargeToScan.Contains(doc)) return null;
                    index = BuildIndex(doc);
                    if (index == null) { _tooLargeToScan.Add(doc); return null; }
                    _byDoc[doc] = index;
                }
                ElementId id;
                if (!index.TryGetValue(target, out id)) return null;
                try { return doc.GetElement(id); } catch { return null; }
            }

            private static Dictionary<Guid, ElementId> BuildIndex(Document doc)
            {
                var index = new Dictionary<Guid, ElementId>();
                int scanned = 0;
                foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    if (++scanned > MaxElementsPerDocument) return null;
                    Guid exportId;
                    try { exportId = ExportUtils.GetExportId(doc, e.Id); }
                    catch { continue; }
                    if (exportId != Guid.Empty && !index.ContainsKey(exportId)) index[exportId] = e.Id;
                }
                return index;
            }
        }

        /// <summary>One Component resolved by AuthoringToolId, then by IfcGuid; see file header.</summary>
        private static ResolvedTarget ResolveBcfComponent(Document hostDoc, List<LinkSrc> links,
                                                           BcfExternalComponent c, GuidScanCache cache)
        {
            if (!string.IsNullOrWhiteSpace(c.AuthoringToolId))
            {
                string authoringToolId = c.AuthoringToolId.Trim();
                long idVal;
                if (long.TryParse(authoringToolId, NumberStyles.Integer, CultureInfo.InvariantCulture, out idVal) &&
                    Rid.CanRepresent(idVal))
                {
                    Element e = TryGet(hostDoc, idVal);
                    if (e != null) return new ResolvedTarget { Element = e, Source = "host", Xf = Transform.Identity };
                    foreach (LinkSrc link in links)
                    {
                        Element le = TryGet(link.Doc, idVal);
                        if (le != null) return new ResolvedTarget { Element = le, Source = link.Label, InstanceId = link.InstanceId, Xf = link.Xf };
                    }
                }

                Element byUniqueId = TryGetByUniqueId(hostDoc, authoringToolId);
                if (byUniqueId != null) return new ResolvedTarget { Element = byUniqueId, Source = "host", Xf = Transform.Identity };
                foreach (LinkSrc link in links)
                {
                    Element lu = TryGetByUniqueId(link.Doc, authoringToolId);
                    if (lu != null) return new ResolvedTarget { Element = lu, Source = link.Label, InstanceId = link.InstanceId, Xf = link.Xf };
                }
            }

            if (!string.IsNullOrWhiteSpace(c.IfcGuid))
            {
                string ifcGuid = c.IfcGuid.Trim();
                Element byParameter = FindByIfcGuidParameter(hostDoc, ifcGuid);
                if (byParameter != null) return new ResolvedTarget { Element = byParameter, Source = "host", Xf = Transform.Identity };
                foreach (LinkSrc link in links)
                {
                    Element lp = FindByIfcGuidParameter(link.Doc, ifcGuid);
                    if (lp != null) return new ResolvedTarget { Element = lp, Source = link.Label, InstanceId = link.InstanceId, Xf = link.Xf };
                }

                Guid? decoded = IfcGuidCodec.Decode(ifcGuid);
                if (decoded.HasValue)
                {
                    Element computed = cache.FindByExportId(hostDoc, decoded.Value);
                    if (computed != null) return new ResolvedTarget { Element = computed, Source = "host", Xf = Transform.Identity };
                    foreach (LinkSrc link in links)
                    {
                        Element lc = cache.FindByExportId(link.Doc, decoded.Value);
                        if (lc != null) return new ResolvedTarget { Element = lc, Source = link.Label, InstanceId = link.InstanceId, Xf = link.Xf };
                    }
                }
            }

            string reason = string.IsNullOrWhiteSpace(c.IfcGuid) && string.IsNullOrWhiteSpace(c.AuthoringToolId)
                ? "component carries neither an IfcGuid nor an AuthoringToolId"
                : "no element in the active document or a loaded link matched" +
                  (string.IsNullOrWhiteSpace(c.AuthoringToolId) ? "" : " its AuthoringToolId (tried as an Element Id and a UniqueId)") +
                  (string.IsNullOrWhiteSpace(c.IfcGuid) ? "" : (string.IsNullOrWhiteSpace(c.AuthoringToolId) ? "" : " or") +
                    " its IfcGuid (tried the IFC_GUID parameter and the computed export id)");
            return new ResolvedTarget { Reason = reason };
        }

        private static Element TryGetByUniqueId(Document doc, string uniqueId)
        {
            try { return doc.GetElement(uniqueId); } catch { return null; }
        }

        private static Element FindByIfcGuidParameter(Document doc, string ifcGuid)
        {
            try
            {
                FilterRule rule = ParameterFilterRuleFactory.CreateEqualsRule(new ElementId(BuiltInParameter.IFC_GUID), ifcGuid);
                var filter = new ElementParameterFilter(rule);
                return new FilteredElementCollector(doc).WherePasses(filter).FirstElement();
            }
            catch { return null; }
        }

        /// <summary>
        /// Resolve and re-detect every topic outside this ledger's own guid index. Mutates
        /// nothing; the caller decides whether to Merge `detected` into the ledger.
        /// </summary>
        private static void ResolveExternalBcfTopics(Document doc, List<BcfTopic> topics,
            out JArray notTraceable, out JArray reproduced, out JArray notReproduced,
            out List<CoordinationDetected> detected, out Dictionary<string, BcfTopic> topicByFindingId,
            out List<string> linksUnloaded, out string matchRule)
        {
            notTraceable = new JArray();
            reproduced = new JArray();
            notReproduced = new JArray();
            detected = new List<CoordinationDetected>();
            topicByFindingId = new Dictionary<string, BcfTopic>(StringComparer.Ordinal);
            linksUnloaded = new List<string>();
            matchRule =
                "each viewpoint Component is resolved by AuthoringToolId (a Revit Element Id, then a Revit " +
                "UniqueId) and by IfcGuid (the IFC_GUID parameter, then ExportUtils.GetExportId decoded from " +
                "the same compressed guid) against the active document and every LOADED rvt link; only a topic " +
                "resolving two or more DISTINCT elements is re-detected.";

            List<LinkSrc> links = BuildLinkSources(doc, linksUnloaded);
            var cache = new GuidScanCache();
            var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            var geometryCache = new Dictionary<string, List<Solid>>();

            foreach (BcfTopic topic in topics.Take(MaxIssuesReported))
            {
                if (topic.Components.Count == 0)
                {
                    notTraceable.Add(NotTraceableRow(topic,
                        "the topic declares no viewpoint, or its viewpoint names no Components/Selection/Component"));
                    continue;
                }

                var resolved = new List<ResolvedTarget>();
                var reasons = new List<string>();
                foreach (BcfExternalComponent c in topic.Components.Take(MaxComponentsPerBcfTopic))
                {
                    ResolvedTarget r = ResolveBcfComponent(doc, links, c, cache);
                    if (r.Element != null) resolved.Add(r);
                    else if (r.Reason != null) reasons.Add(r.Reason);
                }

                // Two Components can legitimately name the same element twice (e.g. one by
                // IfcGuid, a duplicate by AuthoringToolId) - dedupe before counting sides.
                var distinct = new List<ResolvedTarget>();
                var seenKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (ResolvedTarget r in resolved)
                {
                    string uid = SafeUid(r.Element);
                    if (uid == null) continue;
                    string key = (r.Source ?? "") + "|" + (r.InstanceId ?? "") + "|" + uid;
                    if (seenKeys.Add(key)) distinct.Add(r);
                }

                if (distinct.Count < 2)
                {
                    string reason = distinct.Count == 0
                        ? topic.Components.Count + " component(s), 0 resolved" +
                          (reasons.Count == 0 ? "" : ": " + string.Join("; ", reasons.Distinct().Take(3)))
                        : "only 1 of " + topic.Components.Count + " component(s) resolved to an element; a clash needs two.";
                    notTraceable.Add(NotTraceableRow(topic, reason));
                    continue;
                }

                bool anyReproduced = false;
                var pairRows = new JArray();
                for (int i = 0; i < distinct.Count; i++)
                    for (int j = i + 1; j < distinct.Count; j++)
                    {
                        ResolvedTarget a = distinct[i], b = distinct[j];
                        if (ReferenceEquals(a.Element, b.Element) && a.InstanceId == b.InstanceId) continue;
                        JObject pair = ReDetectPair(doc, a, b, options, geometryCache);
                        pairRows.Add(pair);
                        if (!pair.Value<bool>("reproduced")) continue;

                        string uidA = SafeUid(a.Element), uidB = SafeUid(b.Element);
                        if (uidA == null || uidB == null) continue;
                        var hit = new CoordinationDetected
                        {
                            SideA = CoordinationRules.SideKey(a.Source, a.InstanceId, uidA),
                            SideB = CoordinationRules.SideKey(b.Source, b.InstanceId, uidB),
                            CategoryA = SafeCategory(a.Element), CategoryB = SafeCategory(b.Element),
                            PointMm = pair["point_mm"] is JArray pm ? new[] { (double)pm[0], (double)pm[1], (double)pm[2] } : null,
                            ExternalSource = "bcf", ExternalIssueId = topic.Guid, Priority = topic.Priority
                        };
                        detected.Add(hit);
                        anyReproduced = true;
                        string findingId = CoordinationRules.FindingId(hit.SideA, hit.SideB);
                        topicByFindingId[findingId] = topic;
                        reproduced.Add(new JObject
                        {
                            ["topic_guid"] = topic.Guid, ["title"] = topic.Title, ["priority"] = topic.Priority,
                            ["assigned_to"] = topic.AssignedTo, ["status"] = topic.Status,
                            ["finding_id"] = findingId,
                            ["category_a"] = hit.CategoryA, ["category_b"] = hit.CategoryB
                        });
                    }

                if (!anyReproduced)
                    notReproduced.Add(new JObject
                    {
                        ["topic_guid"] = topic.Guid, ["title"] = topic.Title,
                        ["resolved_components"] = distinct.Count, ["pairs_tested"] = pairRows.Count,
                        ["pairs"] = new JArray(pairRows.Take(5))
                    });
            }
        }

        private static JObject NotTraceableRow(BcfTopic topic, string reason) => new JObject
        {
            ["topic_guid"] = topic.Guid, ["title"] = topic.Title, ["reason"] = reason
        };
    }
}
