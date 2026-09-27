// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_framing operation=read (read-only).
// Original Horizun code.
//
// What framing a previous apply produced, found ONLY by the marker on each member
// (FramingMarker), never by geometry: per source element, the members by role,
// the spec hash(es) and plan signature(s) they were built from, and each member's
// id, type and axis. element_ids narrows to those sources; without it every marked
// member of the document is listed. A marker whose source no longer exists is
// reported as orphaned, not hidden - remove can still take it. A curtain-method
// source adds its carrier's record (action, original type and line): what remove restores.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class FramingCommand
    {
        private const int ReadMemberCap = 2000;

        private CommandResult ReadFraming(UIApplication app, JObject request)
        {
            Document doc = null;
            try { doc = app?.ActiveUIDocument?.Document; } catch { doc = null; }
            if (doc == null) return CommandResult.Fail("No document is active in Revit; there is nothing to read.");
            CommandResult refused = DocumentGate.ReadGuard(doc, request, Name);
            if (refused != null) return refused;

            HashSet<long> sources = null;
            if (request["element_ids"] is JArray ids && ids.Count > 0)
            {
                sources = new HashSet<long>();
                foreach (JToken t in ids)
                {
                    if (t.Type != JTokenType.Integer) return CommandResult.Fail("element_ids must be integers. Nothing was read.");
                    sources.Add((long)t);
                }
            }

            List<KeyValuePair<Element, FramingMark>> found = FramingMarker.Find(doc, sources);
            var bySource = new JArray();
            int listed = 0;
            foreach (IGrouping<long, KeyValuePair<Element, FramingMark>> g in found.GroupBy(p => p.Value.SourceId))
            {
                Element source = Rid.CanRepresent(g.Key) ? doc.GetElement(Rid.Make(g.Key)) : null;
                var members = new JArray();
                foreach (KeyValuePair<Element, FramingMark> p in g)
                {
                    if (listed >= ReadMemberCap) break;
                    listed++;
                    var row = new JObject
                    {
                        ["id"] = Rid.Value(p.Key.Id), ["role"] = p.Value.Role, ["index"] = p.Value.Index,
                        ["type_id"] = Rid.Value(p.Key.GetTypeId())
                    };
                    if (p.Key.Location is LocationCurve lc && lc.Curve != null)
                        row["axis_mm"] = new JArray(ModelEditRunner.Arr(lc.Curve.GetEndPoint(0), 1 / 304.8), ModelEditRunner.Arr(lc.Curve.GetEndPoint(1), 1 / 304.8));
                    members.Add(row);
                }
                var counts = new JObject();
                foreach (IGrouping<string, KeyValuePair<Element, FramingMark>> r in g.Where(p => p.Value.Role != FramingMarker.WorkPlaneRole)
                                                                                     .GroupBy(p => p.Value.Role).OrderBy(r => r.Key, StringComparer.Ordinal))
                    counts[r.Key] = r.Count();
                var sourceRow = new JObject
                {
                    ["source_id"] = g.Key,
                    ["source_exists"] = source != null,
                    ["source_matches_uniqueid"] = source != null && string.Equals(source.UniqueId, g.First().Value.SourceUniqueId, StringComparison.Ordinal),
                    ["operation"] = g.First().Value.Operation,
                    ["spec_hashes"] = new JArray(g.Select(p => p.Value.SpecHash).Distinct().ToArray()),
                    ["plan_signatures"] = new JArray(g.Select(p => p.Value.PlanSignature).Distinct().ToArray()),
                    ["count_by_role"] = counts,
                    ["work_plane_count"] = g.Count(p => p.Value.Role == FramingMarker.WorkPlaneRole),
                    ["members"] = members
                };
                // A curtain-method source also carries its carrier's record: what remove will restore.
                JObject curtain = CurtainReadRow(doc, g.Select(p => p.Key));
                if (curtain != null) sourceRow["curtain"] = curtain;
                bySource.Add(sourceRow);
            }
            // A work plane the tool created for a line-based member carries the marker too, but it
            // is not a member: counted apart so member_count compares with the apply's plan.
            int planes = found.Count(p => p.Value.Role == FramingMarker.WorkPlaneRole);
            var result = new JObject
            {
                ["operation"] = "read",
                ["sources"] = bySource,
                ["member_count"] = found.Count - planes,
                ["work_plane_count"] = planes,
                ["members_listed"] = listed,
                ["truncated"] = listed < found.Count,
                ["note"] = "Members are found by the horizun_framing marker only; hand-modelled framing is never listed."
            };
            // Copies of members (a framed wall copied with its framing): their marker names a source
            // but they are not what the tool made for it, so they are listed apart and never counted.
            List<KeyValuePair<Element, FramingMark>> foreign = FramingMarker.FindForeign(doc, sources);
            result["foreign_copy_count"] = foreign.Count;
            result["foreign_copies"] = new JArray(foreign.Take(ReadMemberCap).Select(p => new JObject
            {
                ["id"] = Rid.Value(p.Key.Id), ["names_source_id"] = p.Value.SourceId, ["role"] = p.Value.Role, ["index"] = p.Value.Index
            }));
            if (sources != null)
                result["sources_without_framing"] = new JArray(sources.Where(s => found.All(p => p.Value.SourceId != s)).OrderBy(s => s).ToArray());
            return CommandResult.Ok(result);
        }
    }
}
