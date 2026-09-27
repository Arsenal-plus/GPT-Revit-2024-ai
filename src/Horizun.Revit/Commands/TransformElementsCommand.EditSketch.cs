// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_transform_elements - edit_sketch: change the boundary of a Floor, a
// Ceiling or an Opening WITHOUT recreating it. Replacing one loop, or moving one
// vertex, inside the element's own SketchEditScope keeps its ElementId, its
// UniqueId and everything that refers to it - hosted instances, tags,
// schedules, parameters - where delete-and-recreate would orphan all of them.
//
// Same separate path as realign_wall_sketch, for the same reason: a
// SketchEditScope cannot nest inside the Transaction the other operations
// share, so edit_sketch is sent alone. Its dry_run is a REAL rehearsal of the
// curve edits inside the scope, which is then Cancelled. Revit checks the
// finished sketch as a whole only when the scope COMMITS, so the plan first
// holds the edited loop to what that check refuses (closed, not self-crossing,
// clear of the other loops): a rehearsal must not pass an edit the apply would
// refuse. Whatever Revit still refuses at the apply's commit rolls it all back.
//
// VERIFIED BY RE-READING, twice over: the committed sketch's loops must match
// the expected loops (Core/SketchEditRules.MatchLoops - cyclic, either
// direction, order-free), and the element's Area parameter must match the area
// the new sketch encloses - but only where it matched the old sketch before the
// edit. A sloped or shape-edited floor, or one a shaft cuts, reports an area the
// sketch alone does not predict; there the check is named not_applicable, never
// counted as a pass.
//
// FootPrintRoof is refused by name: the public API exposes no SketchId on it in
// any of 2023-2027 (RevitAPI.xml lists SketchId on Wall, Floor, Ceiling, Opening,
// and on Toposolid from 2024 and PropertyLine from 2027 - not on any roof).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class TransformElementsCommand
    {
        private const string EditSketchOp = "edit_sketch";
        private const double SqFtToM2 = 0.09290304;

        /// <summary>A profile read in its sketch plane: per loop, the curves in chain order and the tessellated outline.</summary>
        private sealed class SketchRead
        {
            public List<List<SketchPt>> Vertices = new List<List<SketchPt>>();
            public List<List<SketchPt>> Outlines = new List<List<SketchPt>>();
            public List<List<SketchSegment>> Segments = new List<List<SketchSegment>>();
            public List<List<bool>> IsLine = new List<List<bool>>();
            public double AreaM2 => SketchEditRules.NetArea(Outlines.Cast<IList<SketchPt>>().ToList()) / 1e6;
        }

        private static ElementId EditableSketchId(Element e, out string refusal)
        {
            refusal = null;
            if (e is Floor f) return f.SketchId;
            if (e is Ceiling c) return c.SketchId;
            if (e is Opening o) return o.SketchId;
            if (e is FootPrintRoof)
                refusal = "a FootPrintRoof exposes no SketchId in the Revit API (2023-2027), so its footprint cannot be edited through a SketchEditScope; edit it in Revit.";
            else if (e is Wall)
                refusal = "a wall's elevation profile is not covered by edit_sketch; realign_wall_sketch covers the stranded-profile case.";
            else
                refusal = "edit_sketch covers Floor, Ceiling and Opening; this element is " + (e == null ? "missing" : e.GetType().Name) + ".";
            return null;
        }

        private static SketchPt ToPlane(Plane plane, XYZ p)
        {
            XYZ d = p - plane.Origin;
            return new SketchPt(d.DotProduct(plane.XVec) * 304.8, d.DotProduct(plane.YVec) * 304.8);
        }

        private static XYZ FromPlane(Plane plane, SketchPt p) =>
            plane.Origin + plane.XVec * (p.X / 304.8) + plane.YVec * (p.Y / 304.8);

        private static SketchSegment SegmentOf(Plane plane, Curve c) =>
            new SketchSegment(ToPlane(plane, c.GetEndPoint(0)), ToPlane(plane, c.GetEndPoint(1)), ToPlane(plane, c.Evaluate(0.5, true)));

        private static SketchRead ReadSketch(Sketch sketch, Plane plane, out string problem)
        {
            problem = null;
            var read = new SketchRead();
            if (sketch?.Profile == null) { problem = "the sketch has no profile."; return null; }
            foreach (CurveArray array in sketch.Profile)
            {
                List<Curve> curves = array.Cast<Curve>().ToList();
                List<SketchSegment> segs = curves.Select(c => SegmentOf(plane, c)).ToList();
                List<List<ChainStep>> loops = SketchEditRules.Chain(segs, SketchEditRules.MatchToleranceMm, out problem);
                if (loops == null) return null;
                foreach (List<ChainStep> loop in loops)
                {
                    read.Vertices.Add(SketchEditRules.Vertices(segs, loop));
                    read.Segments.Add(loop.Select(s => segs[s.Segment]).ToList());
                    read.IsLine.Add(loop.Select(s => curves[s.Segment] is Line).ToList());
                    var outline = new List<SketchPt>();
                    foreach (ChainStep s in loop)
                    {
                        List<SketchPt> pts = curves[s.Segment].Tessellate().Select(p => ToPlane(plane, p)).ToList();
                        if (s.Reversed) pts.Reverse();
                        outline.AddRange(pts.Take(pts.Count - 1));   // the next step starts where this one ends
                    }
                    read.Outlines.Add(outline);
                }
            }
            return read;
        }

        private static double? AreaParamM2(Element e)
        {
            try
            {
                Parameter p = e.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                return p != null && p.HasValue ? (double?)(p.AsDouble() * SqFtToM2) : null;
            }
            catch { return null; }
        }

        private static JArray LoopsJson(IEnumerable<List<SketchPt>> loops) =>
            new JArray(loops.Select(l => new JArray(l.Select(p => new JArray(Math.Round(p.X, 1), Math.Round(p.Y, 1))))));

        /// <summary>What was asked, resolved against the current sketch.</summary>
        private sealed class SketchEditPlan
        {
            public long Id;
            public Element Element;
            public string UniqueId;
            public ElementId SketchId;
            public Plane Plane;
            public SketchRead Before;
            public int LoopIndex;
            public int VertexIndex = -1;          // move mode
            public SketchPt MoveTo;
            public List<SketchPt> NewLoop;        // replace mode
            public List<List<SketchPt>> ExpectedVertices;
            public double ExpectedAreaM2;
            public double? AreaBeforeM2;
            public bool AreaCheckApplies;
        }

        private CommandResult ExecuteEditSketch(UIApplication app, GateResult gate, JObject request, JArray input)
        {
            Document doc = gate.Document;
            if (input.Count != 1)
                return CommandResult.Fail(EditSketchOp + " edits ONE element per call; send one operation.");
            var o = input[0] as JObject;
            if (o == null) return CommandResult.Fail("operations[0] is not an object.");
            double scale;
            if (!Scale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out scale))
                return CommandResult.Fail("units must be mm, m or feet.");

            string error;
            SketchEditPlan plan = PlanEditSketch(doc, o, scale, out error);
            if (plan == null) return CommandResult.Fail(EditSketchOp + ": " + error + " Nothing was changed.");

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "units", "operations");
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name, DocumentKey = gate.Fingerprint, RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            resolvedPlan.Elements.Add(new PlannedElement
            {
                UniqueId = plan.UniqueId, ElementId = plan.Id, Category = plan.Element.Category?.Name,
                TypeName = SafePlanTypeName(doc, plan.Element), Action = PlannedAction.Modify,
                GeometryFingerprint = SafePlanGeometry(plan.Element),
                BeforeValues = new Dictionary<string, string>
                {
                    { "operation", EditSketchOp },
                    { "loops", LoopsJson(plan.Before.Vertices).ToString(Formatting.None) }
                },
                ProposedValues = new Dictionary<string, string>
                {
                    { "operation", EditSketchOp },
                    { "loops", LoopsJson(plan.ExpectedVertices).ToString(Formatting.None) }
                }
            });

            var planJson = new JObject
            {
                ["element_id"] = plan.Id,
                ["mode"] = plan.NewLoop != null ? "replace_loop" : "move_vertex",
                ["loop_index"] = plan.LoopIndex,
                ["vertex_index"] = plan.VertexIndex < 0 ? null : (JToken)plan.VertexIndex,
                ["loops_before_mm"] = LoopsJson(plan.Before.Vertices),
                ["loops_expected_mm"] = LoopsJson(plan.ExpectedVertices),
                ["coordinates"] = "sketch-plane (u, v) in mm from the plane origin, along its X and Y directions",
                ["sketch_area_before_m2"] = Math.Round(plan.Before.AreaM2, 4),
                ["expected_area_m2"] = Math.Round(plan.ExpectedAreaM2, 4),
                ["area_parameter_before_m2"] = plan.AreaBeforeM2.HasValue ? (JToken)Math.Round(plan.AreaBeforeM2.Value, 4) : null,
                ["area_check"] = plan.AreaCheckApplies ? "will_verify" : "not_applicable"
            };

            if (dryRun)
            {
                JObject detail; string err;
                bool ok = TryEditSketch(doc, plan, commit: false, out detail, out err);
                planJson["rehearsal_ok"] = ok;
                planJson["rehearsal_error"] = err;
                planJson["rehearsal_detail"] = detail;
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "rehearsed_and_cancelled",
                    ["targets"] = 1, ["plan"] = new JArray(planJson),
                    ["note"] = "The curve edits were made inside the element's SketchEditScope and the scope was Cancelled; nothing " +
                               "was committed. Revit checks the finished sketch only when the apply commits the scope (a refusal " +
                               "there rolls everything back), so the plan checked the loop first: closed, not self-crossing, clear " +
                               "of the other loops."
                };
                if (ok) DocumentGate.RecordResolvedPlan(resolvedPlan);
                ApplicationOutcome.StampRehearsal(result, 1, 0, ok ? 0 : 1, 0);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, ok,
                    ok ? "the token binds the element, the edit and the units" : "no usable confirmation is issued while the rehearsal fails");
                return CommandResult.Ok(result);
            }

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            using (var group = new TransactionGroup(doc, "Horizun: edit sketch"))
            {
                if (group.Start() != TransactionStatus.Started)
                    return CommandResult.Fail("the transaction group would not start. Nothing was changed.");
                try
                {
                    JObject detail; string err;
                    if (!TryEditSketch(doc, plan, commit: true, out detail, out err))
                    {
                        Guard.RollbackResult rb = Guard.RollBack(group);
                        return CommandResult.Fail("Editing the sketch of " + plan.Id + " failed: " + err + " " +
                            PlanFailure.SingleTransactionOutcome(true, rb.StatusName, "nothing was changed"));
                    }
                    Guard.Assimilate(group, EditSketchOp);
                }
                catch (SilentRollbackException)
                {
                    if (group.HasStarted()) Guard.RollBack(group);
                    throw;
                }
                catch (Exception ex)
                {
                    bool attempted = false; string rbStatus = PlanFailure.NotAttempted;
                    if (group.HasStarted()) { attempted = true; rbStatus = Guard.RollBack(group).StatusName; }
                    return CommandResult.Fail("Editing the sketch failed: " + ex.Message + ". " +
                        PlanFailure.SingleTransactionOutcome(attempted, rbStatus, "nothing was changed"));
                }
            }

            // ---- re-read from the model: the element, its sketch, its loops, its area. ----
            Element after = doc.GetElement(Rid.Make(plan.Id));
            bool idKept = after != null && string.Equals(after.UniqueId, plan.UniqueId, StringComparison.Ordinal);
            string readProblem = null, loopProblem;
            SketchRead now = null;
            if (idKept)
            {
                string ignored;
                ElementId sid = EditableSketchId(after, out ignored);
                var sketch = sid == null ? null : doc.GetElement(sid) as Sketch;
                now = sketch?.SketchPlane == null ? null : ReadSketch(sketch, sketch.SketchPlane.GetPlane(), out readProblem);
            }
            loopProblem = now == null ? (readProblem ?? "the committed sketch could not be read.")
                : SketchEditRules.MatchLoops(plan.ExpectedVertices.Cast<IList<SketchPt>>().ToList(),
                                             now.Vertices.Cast<IList<SketchPt>>().ToList(), SketchEditRules.MatchToleranceMm);
            double? areaAfter = after == null ? null : AreaParamM2(after);
            string areaCheck = !plan.AreaCheckApplies ? "not_applicable"
                : areaAfter.HasValue && SketchEditRules.AreaAgrees(areaAfter.Value, plan.ExpectedAreaM2) ? "verified" : "failed";
            bool verified = idKept && loopProblem == null && areaCheck != "failed";
            var row = new JObject
            {
                ["element_id"] = plan.Id, ["verified"] = verified,
                ["unique_id_kept"] = idKept,
                ["loops_verified"] = loopProblem == null, ["loops_problem"] = loopProblem,
                ["loops_after_mm"] = now == null ? null : LoopsJson(now.Vertices),
                ["sketch_area_after_m2"] = now == null ? null : (JToken)Math.Round(now.AreaM2, 4),
                ["expected_area_m2"] = Math.Round(plan.ExpectedAreaM2, 4),
                ["area_parameter_before_m2"] = plan.AreaBeforeM2.HasValue ? (JToken)Math.Round(plan.AreaBeforeM2.Value, 4) : null,
                ["area_parameter_after_m2"] = areaAfter.HasValue ? (JToken)Math.Round(areaAfter.Value, 4) : null,
                ["area_check"] = areaCheck,
                ["area_note"] = plan.AreaCheckApplies ? null :
                    "the element's Area parameter did not equal its sketch's area before the edit (slope, shape edit, a cut, or no Area parameter), so it cannot be held to the new sketch; the loops were verified instead."
            };
            if (!verified)
                return CommandResult.Fail("The sketch edit committed, but post-commit verification failed: " + row.ToString(Formatting.None));

            var applied = new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = "Committed",
                ["operations_verified"] = 1, ["targets"] = 1, ["rows"] = new JArray(row)
            };
            ApplicationOutcome.StampApplied(applied, ApplicationOutcome.Committed, 1, 1, 1, 0, 0, 0);
            applied["undo"] = new JObject
            {
                ["recorded"] = false,
                ["reason"] = "sketch geometry edits are not covered by horizun_undo; use Revit's own undo within this session."
            };
            return CommandResult.Ok(applied);
        }

        private static List<SketchPt> PointsOf(JToken token, double scale, Plane plane, string what, out string error)
        {
            error = null;
            var list = new List<SketchPt>();
            var arr = token as JArray;
            if (arr == null) { error = what + " must be an array of [x,y,z] points."; return null; }
            foreach (JToken t in arr)
            {
                var c = t as JArray;
                if (c == null || c.Count != 3) { error = what + ": every point must be [x,y,z]."; return null; }
                XYZ p;
                try { p = new XYZ(c[0].Value<double>() * scale, c[1].Value<double>() * scale, c[2].Value<double>() * scale); }
                catch { error = what + ": coordinates must be numbers."; return null; }
                double off = Math.Abs((p - plane.Origin).DotProduct(plane.Normal)) * 304.8;
                if (off > SketchEditRules.OffPlaneToleranceMm)
                {
                    // Invariant numbers: a caller re-sends on the plane this names (the live probe
                    // does), and a comma decimal would read as one more coordinate separator.
                    CultureInfo inv = CultureInfo.InvariantCulture;
                    error = what + ": a point lies " + Math.Round(off, 1).ToString(inv) + " mm off the sketch plane. Points are refused rather than " +
                            "projected, so a wrong elevation is never silently flattened; the plane passes through (" +
                            Math.Round(plane.Origin.X * 304.8, 1).ToString(inv) + ", " + Math.Round(plane.Origin.Y * 304.8, 1).ToString(inv) + ", " +
                            Math.Round(plane.Origin.Z * 304.8, 1).ToString(inv) + ") mm.";
                    return null;
                }
                list.Add(ToPlane(plane, p));
            }
            return list;
        }

        private static SketchEditPlan PlanEditSketch(Document doc, JObject o, double scale, out string error)
        {
            error = null;
            var allowed = new HashSet<string> { "operation", "element_ids", "loop", "loop_index", "start", "end" };
            foreach (JProperty field in o.Properties())
                if (!allowed.Contains(field.Name)) { error = field.Name + " is not applicable to " + EditSketchOp + "."; return null; }
            var ids = o["element_ids"] as JArray;
            if (ids == null || ids.Count != 1) { error = "element_ids must name exactly one element."; return null; }
            long id = ids[0].Value<long>();
            if (!Rid.CanRepresent(id)) { error = "ElementId " + id + " is outside the supported range."; return null; }
            bool replace = o["loop"] != null, move = o["start"] != null || o["end"] != null;
            if (replace == move)
            { error = "pass EITHER loop with loop_index (replace that loop) OR start and end (move the vertex at start to end)."; return null; }

            Element e = doc.GetElement(Rid.Make(id));
            if (e == null) { error = "element " + id + " does not exist."; return null; }
            string refusal;
            ElementId sketchId = EditableSketchId(e, out refusal);
            if (refusal != null) { error = refusal; return null; }
            if (sketchId == null || Rid.Value(sketchId) < 0) { error = "element " + id + " carries no sketch."; return null; }
            var sketch = doc.GetElement(sketchId) as Sketch;
            if (sketch?.SketchPlane == null) { error = "the element's sketch or its plane could not be read."; return null; }
            Plane plane = sketch.SketchPlane.GetPlane();
            string problem;
            SketchRead before = ReadSketch(sketch, plane, out problem);
            if (before == null) { error = problem; return null; }

            var plan = new SketchEditPlan
            {
                Id = id, Element = e, UniqueId = e.UniqueId, SketchId = sketchId, Plane = plane, Before = before,
                ExpectedVertices = before.Vertices.Select(l => l.ToList()).ToList()
            };
            var outlines = before.Outlines.Select(l => l.ToList()).ToList();

            if (replace)
            {
                if (o["loop_index"] == null) { error = "loop needs loop_index: which of the " + before.Vertices.Count + " loop(s) it replaces (the dry run lists them)."; return null; }
                int k = o.Value<int>("loop_index");
                if (k < 0 || k >= before.Vertices.Count) { error = "loop_index " + k + " is out of range; the sketch has " + before.Vertices.Count + " loop(s)."; return null; }
                List<SketchPt> pts = PointsOf(o["loop"], scale, plane, "loop", out error);
                if (pts == null) return null;
                pts = SketchEditRules.Normalise(pts);
                string invalid = SketchEditRules.ValidateLoop(pts);
                if (invalid != null) { error = "loop: " + invalid; return null; }
                plan.LoopIndex = k; plan.NewLoop = pts;
                plan.ExpectedVertices[k] = pts;
                outlines[k] = pts;
            }
            else
            {
                if (o["start"] == null || o["end"] == null) { error = "a vertex move needs both start (the vertex now) and end (where it goes)."; return null; }
                List<SketchPt> from = PointsOf(new JArray(o["start"]), scale, plane, "start", out error);
                if (from == null) return null;
                List<SketchPt> to = PointsOf(new JArray(o["end"]), scale, plane, "end", out error);
                if (to == null) return null;
                int l, v;
                if (!SketchEditRules.FindVertex(before.Vertices.Cast<IList<SketchPt>>().ToList(), from[0], SketchEditRules.MatchToleranceMm, out l, out v, out problem))
                { error = problem; return null; }
                int n = before.Vertices[l].Count;
                int prev = (v - 1 + n) % n;
                if (!before.IsLine[l][prev] || !before.IsLine[l][v])
                { error = "the vertex joins an arc or another non-line curve; moving it would redefine that curve, which edit_sketch does not guess. Replace the loop instead."; return null; }
                List<SketchPt> moved = SketchEditRules.MoveVertex(before.Vertices[l], v, to[0]);
                if (before.IsLine[l].All(x => x))
                {
                    string invalid = SketchEditRules.ValidateLoop(moved);
                    if (invalid != null) { error = "the moved loop: " + invalid; return null; }
                }
                plan.LoopIndex = l; plan.VertexIndex = v; plan.MoveTo = to[0];
                plan.ExpectedVertices[l] = moved;
                SketchPt old = before.Vertices[l][v];
                outlines[l] = outlines[l].Select(p => p.DistanceTo(old) < SketchEditRules.MatchToleranceMm ? to[0] : p).ToList();
            }

            // Loops that touch or cross are refused by Revit only when the sketch is finished -
            // after a dry run has Cancelled - so the plan refuses them first, by name.
            string crossing = SketchEditRules.CrossesOtherLoops(outlines.Cast<IList<SketchPt>>().ToList(), plan.LoopIndex);
            if (crossing != null) { error = (plan.NewLoop != null ? "loop: " : "the moved loop: ") + crossing; return null; }

            plan.ExpectedAreaM2 = SketchEditRules.NetArea(outlines.Cast<IList<SketchPt>>().ToList()) / 1e6;
            plan.AreaBeforeM2 = AreaParamM2(e);
            plan.AreaCheckApplies = plan.AreaBeforeM2.HasValue && SketchEditRules.AreaAgrees(plan.AreaBeforeM2.Value, before.AreaM2);
            return plan;
        }

        /// <summary>
        /// Applies the plan inside the element's SketchEditScope. commit=false lets Revit validate
        /// the edited sketch and then Cancels the whole scope; commit=true keeps it. Same
        /// Start/inner-transaction/Commit/finally-Cancel shape as TryRealign and Core/IfcProfileUpdate.cs.
        /// </summary>
        private static bool TryEditSketch(Document doc, SketchEditPlan plan, bool commit, out JObject detail, out string error)
        {
            detail = new JObject(); error = null;
            var scope = new SketchEditScope(doc, "Horizun: edit sketch");
            try
            {
                if (!scope.IsSketchEditingSupported(plan.SketchId))
                { error = "Revit reports that this sketch cannot be edited (a group, a part, or an element borrowed by somebody else answers this way)."; return false; }
                scope.Start(plan.SketchId);
                var sketch = doc.GetElement(plan.SketchId) as Sketch;
                if (sketch?.SketchPlane == null) { error = "the sketch could not be read after the edit scope opened."; return false; }
                Plane plane = plan.Plane;

                // The loop's OWN curve elements, by geometry: Sketch.Profile hands back curves, not
                // elements, and a sketch also holds slope arrows and span directions that are not
                // boundary at all and must be left alone.
                List<SketchSegment> targetSegs = plan.Before.Segments[plan.LoopIndex];
                var byStep = new ElementId[targetSegs.Count];
                foreach (ElementId eid in sketch.GetAllElements())
                {
                    var ce = doc.GetElement(eid) as CurveElement;
                    if (ce == null) continue;
                    SketchSegment s = SegmentOf(plane, ce.GeometryCurve);
                    for (int i = 0; i < targetSegs.Count; i++)
                        if (byStep[i] == null && SketchEditRules.SameSegment(s, targetSegs[i], SketchEditRules.MatchToleranceMm)) { byStep[i] = eid; break; }
                }
                if (byStep.Any(x => x == null))
                { error = "the loop's curves could not all be matched to sketch elements; nothing was edited."; return false; }

                using (var tx = new Transaction(doc, "Horizun: edit sketch curves"))
                {
                    RevitErrorRecorder recorder = RevitErrorRecorder.On(tx);
                    if (tx.Start() != TransactionStatus.Started) { error = "the sketch curve transaction would not start."; return false; }
                    if (plan.NewLoop != null)
                    {
                        doc.Delete(byStep.ToList());
                        List<SketchPt> pts = plan.NewLoop;
                        for (int i = 0; i < pts.Count; i++)
                            doc.Create.NewModelCurve(Line.CreateBound(FromPlane(plane, pts[i]), FromPlane(plane, pts[(i + 1) % pts.Count])), sketch.SketchPlane);
                        detail["curves_deleted"] = byStep.Length;
                        detail["curves_created"] = pts.Count;
                    }
                    else
                    {
                        List<SketchPt> verts = plan.Before.Vertices[plan.LoopIndex];
                        int n = verts.Count, v = plan.VertexIndex, prev = (v - 1 + n) % n;
                        // Both neighbours set explicitly with overrideJoins, so neither drags the
                        // other through its join: the result is exactly the two lines asked for.
                        ((CurveElement)doc.GetElement(byStep[prev])).SetGeometryCurve(
                            Line.CreateBound(FromPlane(plane, verts[prev]), FromPlane(plane, plan.MoveTo)), true);
                        ((CurveElement)doc.GetElement(byStep[v])).SetGeometryCurve(
                            Line.CreateBound(FromPlane(plane, plan.MoveTo), FromPlane(plane, verts[(v + 1) % n])), true);
                        detail["curves_reshaped"] = 2;
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                    { error = "the sketch curve transaction did not commit." + recorder.Said(); return false; }
                    detail["revit_said"] = recorder.Errors.Count == 0 ? null : string.Join("; ", recorder.Errors);
                }

                // COMMITTING THE SCOPE IS WHERE REVIT VALIDATES THE SKETCH AS A WHOLE.
                if (commit) scope.Commit(new RevitErrorRecorder());
                else scope.Cancel();
                return true;
            }
            catch (Exception ex)
            {
                error = "Revit refused the sketch edit: " + ex.Message + " A loop that is open, crosses itself or another loop, " +
                        "or would strand a hosted element is refused here, and the element keeps the boundary it had.";
                return false;
            }
            finally
            {
                try { if (scope.IsActive) scope.Cancel(); } catch { }
            }
        }
    }
}
