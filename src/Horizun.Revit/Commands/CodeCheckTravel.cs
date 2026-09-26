// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_code_check, egress travel distance. Original Horizun code.
//
// THE GAP THIS CLOSES. A rule "max travel distance <= N m" could never pass or fail
// through the requirement-set grammar: Core/CodeCheckEvaluation.cs answered
// not_decidable for travel_distance_m, always. Two entry points now measure it with
// Revit's own Autodesk.Revit.DB.Analysis.PathOfTravel service (present with the same
// members in every year 2023-2027, read from each year's RevitAPI.xml):
//
//   operation=check        a rule whose measure is travel_distance_m and whose config
//                          names route_view_id(s) and exits gets a real routed number
//                          per room BEFORE the pure evaluator runs (AttachTravelDistance).
//   operation=travel_distance
//                          the measurement itself, per room, with its polyline, the exit
//                          reached and a verdict against travel.max_m; create_paths=true
//                          keeps one PathOfTravel element per room in the plan for review
//                          (dry_run -> confirmation_token -> apply, re-read after commit).
//
// MEASURING OPENS NO TRANSACTION AT ALL. FindShortestPaths, FindEndsOfShortestPaths
// and FindStartsOfLongestPathsFromRooms are computations: nothing is created, so
// there is nothing to roll back. Only create_paths writes.
//
// THE FARTHEST POINT, HONESTLY. FindStartsOfLongestPathsFromRooms has no room argument
// and returns the worst point(s) of the WHOLE plan. Per room, candidates are routed
// instead (Core/EgressTravelRules.cs says why corners): boundary corners pulled 300 mm
// inward, the room's point, and any whole-plan longest start inside the room; the
// longest routed candidate is the room's number, and the basis says so.
//
// ONE LEVEL, STATED. The service is two-dimensional and flattens every destination's
// Z onto the view's level. An exit on another level would be silently flattened into
// a plausible and wrong route, so exits are filtered to the plan's own level, and a
// room whose level has no plan or no declared exit is not_assessable: its egress runs
// through a stair this service cannot follow.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CodeCheckCommand
    {
        private const double TravelFeetToMm = AccessGeometry.MillimetresPerFoot;
        private const int MaxCornersPerRoom = 24;
        private const int MaxPolylinePoints = 200;

        /// <summary>One room's egress measurement. Distances in millimetres, points in Revit feet.</summary>
        private sealed class TravelRow
        {
            public long RoomId;
            public string Number, Name, Level;
            public long ViewId = -1;
            public double? DistanceMm;
            public XYZ Start, End;
            public long ExitId = -1;
            public IList<XYZ> Path;
            public int Candidates, Routed;
            public bool UsedLongestSearch;
            public string NotAssessable, Unavailable;
        }

        // =====================================================================
        // Measurement, shared by both entry points
        // =====================================================================

        private static List<TravelRow> MeasureTravel(Document doc, IList<ViewPlan> plans, List<FamilyInstance> exits,
                                                     HashSet<long> roomScope, JObject coverage)
        {
            var rows = new List<TravelRow>();
            var planByLevel = new Dictionary<long, ViewPlan>();
            foreach (ViewPlan p in plans)
            {
                Level lv = null;
                try { lv = p.GenLevel; } catch { }
                if (lv != null && !planByLevel.ContainsKey(Rid.Value(lv.Id))) planByLevel[Rid.Value(lv.Id)] = p;
            }

            IEnumerable<Room> rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType().OfType<Room>();
            if (roomScope != null) rooms = rooms.Where(r => roomScope.Contains(Rid.Value(r.Id)));

            var byPlan = new Dictionary<long, List<(TravelRow row, Room room)>>();
            foreach (Room room in rooms)
            {
                var row = new TravelRow
                {
                    RoomId = Rid.Value(room.Id), Number = Safe(() => room.Number), Name = Safe(() => room.Name),
                    Level = Safe(() => room.Level?.Name)
                };
                rows.Add(row);
                double area = 0;
                try { area = room.Area; } catch { }
                if (area <= 0 || room.Location == null) { row.Unavailable = "the room is not placed or not enclosed; it has no area to route from."; continue; }
                long levelId = Rid.Value(room.LevelId);
                if (!planByLevel.TryGetValue(levelId, out ViewPlan plan))
                {
                    row.NotAssessable = "no given plan view shows level '" + (row.Level ?? "?") + "'. Revit's path of travel " +
                                        "runs in ONE floor plan, per level; give a plan of this level to measure it.";
                    continue;
                }
                row.ViewId = Rid.Value(plan.Id);
                if (!byPlan.TryGetValue(row.ViewId, out var list)) byPlan[row.ViewId] = list = new List<(TravelRow, Room)>();
                list.Add((row, room));
            }

            var gaps = new List<string>();
            foreach (var kv in byPlan)
            {
                ViewPlan plan = (ViewPlan)doc.GetElement(Rid.Make(kv.Key));
                ElementId levelId = plan.GenLevel.Id;
                List<FamilyInstance> levelExits = exits.Where(d => d.LevelId != null && d.LevelId.Equals(levelId)).ToList();
                var destinations = levelExits.Select(d => (d.Location as LocationPoint)?.Point).ToList();
                var exitIds = new List<long>();
                var destPoints = new List<XYZ>();
                for (int i = 0; i < levelExits.Count; i++)
                    if (destinations[i] != null) { destPoints.Add(destinations[i]); exitIds.Add(Rid.Value(levelExits[i].Id)); }
                if (destPoints.Count == 0)
                {
                    foreach (var (row, _) in kv.Value)
                        row.NotAssessable = "no declared exit door is on level '" + (row.Level ?? "?") + "'. Egress from this level " +
                                            "runs through a stair or another level, which Revit's per-level path of travel cannot route.";
                    continue;
                }

                // The whole plan's worst start(s), attributed to rooms by Revit's own lookup.
                AccessGeometry.LongestPathResult longest = AccessGeometry.LongestPathStarts(plan, destPoints);
                var longestByRoom = new Dictionary<long, List<XYZ>>();
                foreach (XYZ pt in longest.Starts)
                {
                    Room r = AccessGeometry.RoomForPoint(plan, pt);
                    if (r == null) continue;
                    long id = Rid.Value(r.Id);
                    if (!longestByRoom.ContainsKey(id)) longestByRoom[id] = new List<XYZ>();
                    longestByRoom[id].Add(pt);
                }
                if (longest.Problem != null) gaps.Add("view " + kv.Key + ": " + longest.Problem);

                var starts = new List<XYZ>();
                var owner = new List<int>();
                var fromLongest = new List<bool>();
                for (int k = 0; k < kv.Value.Count; k++)
                {
                    var (row, room) = kv.Value[k];
                    foreach (XYZ c in Candidates(room, plan))
                    { starts.Add(c); owner.Add(k); fromLongest.Add(false); row.Candidates++; }
                    if (longestByRoom.TryGetValue(row.RoomId, out List<XYZ> extra))
                        foreach (XYZ c in extra) { starts.Add(c); owner.Add(k); fromLongest.Add(true); row.Candidates++; }
                    if (row.Candidates == 0) row.Unavailable = "the room reports no location point and no boundary.";
                }
                if (starts.Count == 0) continue;

                IList<IList<XYZ>> paths;
                try
                {
                    // Revit's signature is (view, DESTINATIONS, STARTS): backwards against every
                    // expectation, and a swap answers the wrong question plausibly.
                    paths = PathOfTravel.FindShortestPaths(plan, destPoints, starts);
                }
                catch (Exception ex)
                {
                    string why = "Revit's path-of-travel service did not run in view " + kv.Key + ": " + ex.Message +
                                 ". A gap in the MEASUREMENT, not a finding about the model.";
                    foreach (var (row, _) in kv.Value) if (row.Unavailable == null) row.Unavailable = why;
                    gaps.Add(why);
                    continue;
                }

                var chosen = new List<(TravelRow row, int start)>();
                for (int k = 0; k < kv.Value.Count; k++)
                {
                    TravelRow row = kv.Value[k].row;
                    var idx = new List<int>();
                    var dist = new List<double?>();
                    for (int s = 0; s < starts.Count; s++)
                    {
                        if (owner[s] != k) continue;
                        IList<XYZ> path = paths != null && s < paths.Count ? paths[s] : null;
                        idx.Add(s);
                        dist.Add(path != null && path.Count >= 2
                            ? EgressTravelRules.Length(path.Select(p => new PlanPoint(p.X, p.Y)).ToList()) * TravelFeetToMm
                            : (double?)null);
                    }
                    row.Routed = dist.Count(d => d.HasValue);
                    int best = EgressTravelRules.FarthestIndex(dist);
                    if (best < 0)
                    {
                        if (row.Unavailable == null && row.Candidates > 0)
                            row.Unavailable = "no route was found from any of " + row.Candidates + " points in this room to a declared " +
                                              "exit on its level. A finding, not a measurement failure: the plan's obstacles were all considered.";
                        continue;
                    }
                    int s0 = idx[best];
                    row.DistanceMm = dist[best];
                    row.Start = starts[s0];
                    row.Path = paths[s0];
                    row.UsedLongestSearch = fromLongest[s0];
                    chosen.Add((row, s0));
                }

                // Which exit each room's longest route ends at: Revit's own nearest-destination answer.
                if (chosen.Count == 0) continue;
                IList<XYZ> ends = null;
                try { ends = PathOfTravel.FindEndsOfShortestPaths(plan, destPoints, chosen.Select(c => starts[c.start]).ToList()); }
                catch (Exception ex) { gaps.Add("view " + kv.Key + ": FindEndsOfShortestPaths did not run: " + ex.Message); }
                var destPlan = destPoints.Select(p => new PlanPoint(p.X, p.Y)).ToList();
                for (int c = 0; c < chosen.Count; c++)
                {
                    TravelRow row = chosen[c].row;
                    XYZ end = ends != null && c < ends.Count ? ends[c] : row.Path[row.Path.Count - 1];
                    int exit = EgressTravelRules.NearestIndex(new PlanPoint(end.X, end.Y), destPlan, 1.0);
                    row.End = exit >= 0 ? destPoints[exit] : end;
                    row.ExitId = exit >= 0 ? exitIds[exit] : -1;
                }
            }

            int measured = rows.Count(r => r.DistanceMm.HasValue);
            JObject cov = AccessGeometry.Coverage("egress.travel_distance", rows.Count, measured, MeasurementBasis.TravelPath, gaps);
            cov["not_assessable"] = rows.Count(r => r.NotAssessable != null);
            cov["views"] = new JArray(plans.Select(p => Rid.Value(p.Id)));
            coverage["travel_distance"] = cov;
            return rows;
        }

        /// <summary>Boundary corners pulled inward toward the room's point, plus the point itself.</summary>
        private static List<XYZ> Candidates(Room room, ViewPlan plan)
        {
            XYZ centre = null;
            try { centre = (room.Location as LocationPoint)?.Point; } catch { }
            var corners = new List<PlanPoint>();
            try
            {
                IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                if (loops != null && loops.Count > 0)
                    foreach (BoundarySegment seg in loops[0])
                    {
                        XYZ p = seg.GetCurve()?.GetEndPoint(0);
                        if (p != null) corners.Add(new PlanPoint(p.X, p.Y));
                    }
            }
            catch { /* no boundary: the room point alone is still a candidate */ }
            double z = plan.GenLevel?.Elevation ?? 0;
            var result = new List<XYZ>();
            if (centre != null)
            {
                foreach (PlanPoint q in EgressTravelRules.InsetToward(corners, new PlanPoint(centre.X, centre.Y),
                                                                      EgressTravelRules.DefaultInsetMm / TravelFeetToMm).Take(MaxCornersPerRoom))
                    result.Add(new XYZ(q.X, q.Y, z));
                result.Add(new XYZ(centre.X, centre.Y, z));
            }
            return result;
        }

        private static string Safe(Func<string> read) { try { return read(); } catch { return null; } }

        private static JObject RowJson(TravelRow r, double? maxM)
        {
            double? m = r.DistanceMm.HasValue ? Math.Round(r.DistanceMm.Value / 1000.0, 3) : (double?)null;
            var o = new JObject
            {
                ["room_id"] = r.RoomId, ["number"] = r.Number, ["name"] = r.Name, ["level"] = r.Level,
                ["view_id"] = r.ViewId >= 0 ? (JToken)r.ViewId : JValue.CreateNull(),
                ["distance_m"] = m.HasValue ? (JToken)m.Value : JValue.CreateNull(),
                ["outcome"] = EgressTravelRules.Evaluate(m, maxM, r.NotAssessable),
                ["exit_door_id"] = r.ExitId >= 0 ? (JToken)r.ExitId : JValue.CreateNull()
            };
            if (r.NotAssessable != null) o["reason"] = r.NotAssessable;
            else if (r.Unavailable != null) o["reason"] = r.Unavailable;
            if (r.DistanceMm.HasValue)
            {
                o["basis"] = "measured_travel_path: longest of " + r.Routed + " routed points in the room (corners inset " +
                             EgressTravelRules.DefaultInsetMm + " mm, room point" + (r.UsedLongestSearch ? ", Revit's whole-plan longest start" : "") +
                             ") to its nearest declared exit";
                o["start_m"] = new JArray(Math.Round(r.Start.X * TravelFeetToMm / 1000, 3), Math.Round(r.Start.Y * TravelFeetToMm / 1000, 3));
                o["polyline_m"] = new JArray(r.Path.Take(MaxPolylinePoints).Select(p =>
                    new JArray(Math.Round(p.X * TravelFeetToMm / 1000, 3), Math.Round(p.Y * TravelFeetToMm / 1000, 3))));
            }
            return o;
        }

        // =====================================================================
        // operation=check: fill Measures["travel_distance_m"] before evaluation
        // =====================================================================

        private static void AttachTravelDistance(Document doc, List<CheckedElement> facts, RequirementSet set, JObject coverage)
        {
            List<CheckedElement> rooms = facts.Where(f => f.CategoryToken == "OST_Rooms").ToList();
            Requirement rule = set.Rules.FirstOrDefault(r => r.AssertionMeasure == "travel_distance_m" && r.Config != null);
            string refusal = null;
            List<ViewPlan> plans = rule == null ? null : Plans(doc, rule.Config, "route_view_id", "route_view_ids", out refusal);
            List<FamilyInstance> exits = null;
            if (rule == null) refusal = "no travel_distance_m rule carries a config: set config.route_view_id (a floor plan) and config.exits.";
            else if (refusal == null)
            {
                JObject exitsCfg = rule.Config["exits"] as JObject;
                if (exitsCfg == null) refusal = "config.exits must select the exit doors: {parameter, value?}, {mark_prefix} or {element_ids}.";
                else
                {
                    exits = AuditAccessCommand.ResolveExits(doc, exitsCfg, out refusal);
                    if (refusal == null && (exits == null || exits.Count == 0)) refusal = "config.exits matched no door in this document.";
                }
            }
            if (refusal != null)
            {
                foreach (CheckedElement r in rooms) r.Measures["travel_distance_m"] = MeasuredValue.None(refusal);
                coverage["travel_distance"] = new JObject { ["not_covered"] = true, ["reason"] = refusal };
                return;
            }

            var scope = new HashSet<long>(rooms.Select(r => r.Id));
            Dictionary<long, TravelRow> byId = MeasureTravel(doc, plans, exits, scope, coverage).ToDictionary(r => r.RoomId);
            foreach (CheckedElement fact in rooms)
            {
                if (!byId.TryGetValue(fact.Id, out TravelRow row)) { fact.Measures["travel_distance_m"] = MeasuredValue.None("the room could not be re-read."); continue; }
                if (row.NotAssessable != null) { fact.Measures["travel_distance_m"] = MeasuredValue.None("not_assessable: " + row.NotAssessable); continue; }
                if (!row.DistanceMm.HasValue) { fact.Measures["travel_distance_m"] = MeasuredValue.None(row.Unavailable ?? "no route was measured."); continue; }
                JObject j = RowJson(row, null);
                fact.Measures["travel_distance_m"] = new MeasuredValue
                {
                    Value = row.DistanceMm.Value / 1000.0,
                    Basis = (string)j["basis"],
                    Detail = new JObject { ["view_id"] = row.ViewId, ["exit_door_id"] = j["exit_door_id"], ["routed_points"] = row.Routed }
                };
            }
        }

        private static List<ViewPlan> Plans(Document doc, JObject cfg, string one, string many, out string refusal)
        {
            refusal = null;
            var ids = new List<long>();
            if (cfg[many] is JArray arr) ids.AddRange(arr.Select(t => t.Value<long?>() ?? -1));
            else if (cfg[one] != null) ids.Add(cfg.Value<long?>(one) ?? -1);
            if (ids.Count == 0) { refusal = one + " (or " + many + ") is missing: path of travel runs IN a floor plan view."; return null; }
            var plans = new List<ViewPlan>();
            foreach (long id in ids)
            {
                var p = id >= 0 && Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as ViewPlan : null;
                if (p == null || p.ViewType != ViewType.FloorPlan || p.IsTemplate || p.GenLevel == null)
                { refusal = "view " + id + " is not a floor plan view of a level in this document."; return null; }
                plans.Add(p);
            }
            return plans;
        }

        // =====================================================================
        // operation=travel_distance
        // =====================================================================

        private CommandResult ExecuteTravel(UIApplication app, JObject request)
        {
            JObject travel = request["travel"] as JObject;
            if (travel == null)
                return CommandResult.Fail("operation=travel_distance needs travel: {view_ids, exits, room_ids?, max_m?, create_paths?}.");
            bool createPaths = travel.Value<bool?>("create_paths") ?? false;

            GateResult gate = null;
            Document doc;
            if (createPaths)
            {
                gate = DocumentGate.ForMutation(app, request, Name);
                if (!gate.Ok) return gate.Refusal;
                doc = gate.Document;
            }
            else
            {
                doc = app?.ActiveUIDocument?.Document;
                if (doc == null) return CommandResult.Fail("No active Revit document.");
                CommandResult wrong = DocumentGate.ReadGuard(doc, request, Name);
                if (wrong != null) return wrong;
            }

            List<ViewPlan> plans = Plans(doc, travel, "view_id", "view_ids", out string refusal);
            if (refusal != null) return CommandResult.Fail(refusal + " Nothing was measured.");
            JObject exitsCfg = travel["exits"] as JObject;
            if (exitsCfg == null)
                return CommandResult.Fail("travel.exits must select the exit doors: {parameter, value?}, {mark_prefix} or {element_ids}. " +
                                          "Nothing in a Revit model reliably marks an exit, so it is never guessed.");
            List<FamilyInstance> exits = AuditAccessCommand.ResolveExits(doc, exitsCfg, out refusal);
            if (refusal != null) return CommandResult.Fail(refusal);
            if (exits == null || exits.Count == 0) return CommandResult.Fail("travel.exits matched no door in this document. Nothing was measured.");
            HashSet<long> scope = travel["room_ids"] is JArray ra ? new HashSet<long>(ra.Select(t => t.Value<long?>() ?? -1)) : null;
            double? maxM = travel.Value<double?>("max_m");

            var coverage = new JObject();
            List<TravelRow> rows = MeasureTravel(doc, plans, exits, scope, coverage);
            var outRows = new JArray(rows.Select(r => RowJson(r, maxM)));
            var head = new JObject
            {
                ["document"] = doc.Title,
                ["operation"] = "travel_distance",
                ["views"] = new JArray(plans.Select(p => Rid.Value(p.Id))),
                ["exit_door_ids"] = new JArray(exits.Select(d => Rid.Value(d.Id))),
                ["max_m"] = maxM.HasValue ? (JToken)maxM.Value : JValue.CreateNull(),
                ["summary"] = EgressTravelRules.Summary(outRows.Select(r => (string)r["outcome"])),
                ["rooms"] = outRows,
                ["coverage"] = coverage,
                ["limits"] = new JArray(
                    "one level per plan view: a stair or another level is not_assessable, never flattened into this number",
                    "obstacles are what the given plan view shows (hidden furniture is not an obstacle)",
                    "the farthest point is the longest of the routed sample points, not a proven maximum in non-convex rooms")
            };
            if (!createPaths) return CommandResult.Ok(head);
            return KeepPaths(app, gate, request, doc, rows, head);
        }

        /// <summary>create_paths=true: one PathOfTravel per measured room, dry_run -> token -> apply, re-read after commit.</summary>
        private CommandResult KeepPaths(UIApplication app, GateResult gate, JObject request, Document doc, List<TravelRow> rows, JObject head)
        {
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            List<TravelRow> ready = rows.Where(r => r.DistanceMm.HasValue && r.Start != null && r.End != null).ToList();
            int unresolved = rows.Count - ready.Count;

            var scope = new JObject
            {
                ["op"] = "travel_distance.create_paths", ["travel"] = request["travel"]?.DeepClone(),
                ["paths"] = new JArray(ready.Select(r => new JArray(r.RoomId, r.ViewId, Math.Round(r.Start.X, 4), Math.Round(r.Start.Y, 4),
                                                                     Math.Round(r.End.X, 4), Math.Round(r.End.Y, 4))))
            };
            string planHash;
            using (SHA256 sha = SHA256.Create())
                planHash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(scope.ToString(Formatting.None)))).Replace("-", "");
            var plan = new ResolvedPlan
            {
                Command = Name, DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber, DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (TravelRow r in ready)
                plan.Elements.Add(new PlannedElement
                {
                    Action = PlannedAction.Create, Category = "OST_PathOfTravelLines",
                    ProposedValues = new Dictionary<string, string> { ["room_id"] = r.RoomId.ToString(), ["view_id"] = r.ViewId.ToString() }
                });

            if (dryRun)
            {
                head["dry_run"] = true;
                head["would_create_paths"] = ready.Count;
                DocumentGate.RecordResolvedPlan(plan);
                DocumentGate.StampConfirmation(head, gate, Name, planHash, true,
                    "the token binds the views, the exits and each room's routed start and exit end.");
                ApplicationOutcome.StampRehearsal(head, rows.Count, unresolved, 0, 0);
                return CommandResult.Ok(head);
            }
            if (ready.Count == 0)
                return CommandResult.Fail("Nothing to keep: no room was measured, so no path of travel can be created. Nothing was changed.");
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, plan, null);
            if (refusal != null) return refusal;

            var created = new List<(TravelRow row, ElementId id, string status)>();
            TransactionStatus status = TransactionStatus.Uninitialized;
            using (var tx = new Transaction(doc, "Horizun: keep egress paths of travel"))
            {
                tx.Start();
                try
                {
                    foreach (TravelRow r in ready)
                    {
                        var view = (View)doc.GetElement(Rid.Make(r.ViewId));
                        PathOfTravel pot = PathOfTravel.Create(view, r.Start, r.End, out PathOfTravelCalculationStatus st);
                        created.Add((r, pot?.Id, st.ToString()));
                    }
                    try { status = Guard.Commit(tx, "egress paths of travel"); }
                    catch (SilentRollbackException ex) { status = ex.Status; }
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx);
                    throw;
                }
            }

            // Re-read from the committed model: the element exists, lives in the plan, and is the measured route.
            int verified = 0, unverified = 0;
            var verification = new JArray();
            foreach (var (row, id, st) in created)
            {
                var pot = id == null ? null : doc.GetElement(id) as PathOfTravel;
                double rereadMm = 0;
                try { if (pot != null) rereadMm = pot.GetCurves().Sum(c => c.Length) * TravelFeetToMm; } catch { }
                bool inView = pot != null && Rid.Value(pot.OwnerViewId) == row.ViewId;
                bool agrees = EgressTravelRules.LengthsAgree(row.DistanceMm.Value, rereadMm, out double delta);
                bool ok = inView && agrees;
                if (ok) verified++; else unverified++;
                verification.Add(new JObject
                {
                    ["room_id"] = row.RoomId, ["path_id"] = pot != null ? (JToken)Rid.Value(pot.Id) : JValue.CreateNull(),
                    ["creation_status"] = st, ["length_m"] = Math.Round(rereadMm / 1000, 3),
                    ["delta_mm"] = Math.Round(delta, 1), ["verified"] = ok
                });
            }
            head["dry_run"] = false;
            head["paths"] = verification;
            head["paths_verified"] = verified;
            DocumentGate.StampConfirmation(head, gate, Name, planHash, false);
            ApplicationOutcome.Stamp(head, WriteTally.PerTarget(status.ToString(), ready.Count, unresolved, verified, unverified));
            return status == TransactionStatus.Committed && unverified == 0
                ? CommandResult.Ok(head)
                : CommandResult.FailWithDetail("Paths of travel were not all verified after the commit (" + unverified +
                                               " unverified, commit " + status + "). See paths.", head);
        }
    }
}
