// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_query_structure mode=analytical and mode=loads.
// Original Horizun code. Read-only: no transaction is opened.
//
// mode=analytical reads the ANALYTICAL model (Revit 2023+ AnalyticalMember and
// AnalyticalPanel) beside the physical one: which physical element each
// analytical element is associated with, member end releases, and member ends
// no other analytical element reaches within a tolerance. An end is CONNECTED
// when a member's REAL curve (a beam framing into mid-girder is connected), an
// analytical link, a panel edge or a panel SURFACE (a flat-slab column top)
// reaches it; SUPPORTED when only a boundary condition does (a column base is
// not a gap); otherwise UNCONNECTED - a near-miss or an intended free end such
// as a cantilever tip, which this read does not pretend to tell apart
// (Core/AnalysisReadRules.cs). Curved elements are measured to the real curve
// (Curve.Distance), not to Revit's display tessellation, whose chords sag
// millimetres. It also names the physical structural elements that have NO
// analytical counterpart, which is the gap an analysis export silently drops.
//
// 2023 vs 2024+: AnalyticalToPhysicalAssociationManager.GetAssociatedElementIds
// (plural, one-to-many) does not exist in the 2023 API - only the singular
// GetAssociatedElementId - so 2023 reads one associated id per element.
//
// mode=loads reads point, line and area loads with load case, nature, category
// and host, magnitudes converted through UnitUtils to kN-based units, in the
// frame the load is oriented to (vector_frame): only 'project' components are
// project coordinates. Nothing here judges a load; the numbers are what the
// model carries, and a field that would not read is named, not left null.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class QueryStructureCommand
    {
        private const int MaxListedUnassociated = 200;

        // ---------------------------------------------------------- analytical

        private CommandResult Analytical(Document doc, JObject request, List<long> ids, int offset, int maxRows)
        {
            double? asked = request.Value<double?>("tolerance_mm");
            if (asked.HasValue && !(asked.Value > 0))
                return CommandResult.Fail("tolerance_mm must be a positive number of millimetres.");
            double toleranceMm;
            string toleranceSource;
            if (asked.HasValue) { toleranceMm = asked.Value; toleranceSource = "caller"; }
            else
            {
                toleranceMm = doc.Application.VertexTolerance * FtToMm;
                toleranceSource = "revit_vertex_tolerance";
            }

            var reasons = new JArray();
            AnalyticalToPhysicalAssociationManager manager = null;
            try { manager = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc); }
            catch (Exception ex)
            {
                reasons.Add(StructuralCoverage.Reason("association",
                    "the association manager would not answer (" + ex.Message + "); every association is null."));
            }

            // Every analytical element in the model is a TARGET for the gap check,
            // whatever the page shows: an end is connected to things outside the page.
            List<Element> everyAnalytical = new FilteredElementCollector(doc).OfClass(typeof(AnalyticalMember))
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(AnalyticalPanel)))
                .OrderBy(e => Rid.Value(e.Id)).ToList();
            var idSet = new HashSet<long>(ids);
            List<Element> scope = ids.Count == 0 ? everyAnalytical
                : everyAnalytical.Where(e => idSet.Contains(Rid.Value(e.Id))).ToList();

            var polylines = new Dictionary<long, AnalysisReadRules.AnalyticalPolyline>();
            var surfaces = new List<AnalysisReadRules.AnalyticalSurface>();
            int panelsWithoutSurface = 0;
            var unreadableCurves = new List<long>();
            foreach (Element e in everyAnalytical)
            {
                AnalysisReadRules.AnalyticalPolyline line = Polyline(e);
                if (line != null) polylines[line.ElementId] = line;
                else unreadableCurves.Add(Rid.Value(e.Id));
                if (e is AnalyticalPanel panel)
                {
                    AnalysisReadRules.AnalyticalSurface surface = Surface(doc, panel);
                    if (surface != null) surfaces.Add(surface); else panelsWithoutSurface++;
                }
            }
            // Named for the whole call, not only on the page that shows the element: a curve that
            // would not read is neither checked (its ends) nor a target (ends framing into it), so the
            // count below is a floor wherever that element sits.
            if (unreadableCurves.Count > 0)
                reasons.Add(StructuralCoverage.Reason("node_gaps", unreadableCurves.Count + " analytical member " +
                    "curve(s) or panel contour(s) would not read (" + string.Join(", ", unreadableCurves.Take(20)) +
                    (unreadableCurves.Count > 20 ? ", ..." : "") + "): a member's ends were not checked, and an end " +
                    "framing into any of them may be counted as a gap."));
            if (panelsWithoutSurface > 0)
                reasons.Add(StructuralCoverage.Reason("panel_surfaces", panelsWithoutSurface + " panel(s) would not give a " +
                    "planar contour with readable openings: an end inside them was judged against their edges only."));
            var targets = polylines.Values.ToList();
            targets.AddRange(Links(doc, reasons));
            List<AnalysisReadRules.SupportTarget> supports = Supports(doc, reasons);

            var members = scope.OfType<AnalyticalMember>().Where(m => polylines.ContainsKey(Rid.Value(m.Id)))
                               .Select(m => polylines[Rid.Value(m.Id)]).ToList();
            var gapsById = new Dictionary<long, List<AnalysisReadRules.NodeGap>>();
            var supportedById = new Dictionary<long, List<AnalysisReadRules.NodeGap>>();
            bool gapsMeasured = AnalysisReadRules.PairChecks(members, targets, surfaces, supports) <= AnalysisReadRules.MaxPairChecks;
            int supportedEnds = 0;
            if (gapsMeasured)
            {
                AnalysisReadRules.EndClassification ends =
                    AnalysisReadRules.ClassifyEnds(members, targets, surfaces, supports, toleranceMm);
                Group(ends.Unconnected, gapsById);
                Group(ends.Supported, supportedById);
                supportedEnds = ends.Supported.Count;
            }
            else
            {
                reasons.Add(StructuralCoverage.Reason("node_gaps",
                    "ends x segments exceeds " + AnalysisReadRules.MaxPairChecks + " checks; narrow with element_ids. " +
                    "Node gaps were NOT measured, which is not the same as none."));
            }

            var rows = new JArray();
            foreach (Element e in scope.Skip(offset).Take(maxRows))
                rows.Add(AnalyticalRow(doc, e, manager, polylines, gapsMeasured ? gapsById : null,
                                       gapsMeasured ? supportedById : null, reasons));

            var physicalSeen = new HashSet<long>();
            JObject unassociated = PhysicalWithoutAnalytical(doc, manager, ids, reasons, physicalSeen);
            int gapEnds = gapsById.Values.Sum(l => l.Count);
            var extra = new JObject
            {
                ["tolerance_mm"] = Math.Round(toleranceMm, 4),
                ["tolerance_source"] = toleranceSource,
                ["node_gaps_measured"] = gapsMeasured,
                ["member_ends_beyond_tolerance"] = gapsMeasured ? (JToken)gapEnds : JValue.CreateNull(),
                ["member_ends_supported"] = gapsMeasured ? (JToken)supportedEnds : JValue.CreateNull(),
                ["supports_read"] = supports.Count,
                ["ends_mean"] = "member_ends_beyond_tolerance counts ends no member curve (real curve), analytical " +
                    "link, panel edge or panel surface reaches and no boundary condition holds: near-misses AND " +
                    "intended free ends such as cantilever tips - nearest_mm tells them apart. Supported ends " +
                    "are counted apart and are not gaps.",
                ["physical_without_analytical"] = unassociated,
                ["unmatched_ids"] = new JArray(ids.Where(id => !scope.Any(s => Rid.Value(s.Id) == id) &&
                    !physicalSeen.Contains(id)).Cast<object>().ToArray()),
                ["api_note"] =
#if REVIT2023
                    "Revit 2023: one associated physical id per analytical element (GetAssociatedElementId); " +
                    "the one-to-many GetAssociatedElementIds arrived in 2024."
#else
                    "associations read with GetAssociatedElementIds (one-to-many)."
#endif
            };
            return Ok("analytical", scope.Count, offset, rows, reasons, extra);
        }

        private static void Group(List<AnalysisReadRules.NodeGap> ends, Dictionary<long, List<AnalysisReadRules.NodeGap>> byId)
        {
            foreach (AnalysisReadRules.NodeGap g in ends)
            {
                if (!byId.TryGetValue(g.ElementId, out var list)) byId[g.ElementId] = list = new List<AnalysisReadRules.NodeGap>();
                list.Add(g);
            }
        }

        /// <summary>
        /// A member's curve or a panel's outer contour as a polyline in mm. A curved
        /// element also carries the distance to its REAL curves and the chord error
        /// of the display tessellation measured at each chord's midpoint, so an end
        /// near it is decided by Curve.Distance rather than by a chord.
        /// </summary>
        private static AnalysisReadRules.AnalyticalPolyline Polyline(Element e)
        {
            try
            {
                var curves = new List<Curve>();
                if (e is AnalyticalMember member)
                {
                    Curve c = member.GetCurve();
                    if (c == null) return null;
                    curves.Add(c);
                }
                else if (e is AnalyticalPanel panel)
                {
                    CurveLoop loop = panel.GetOuterContour();
                    if (loop == null) return null;
                    curves.AddRange(loop);
                }
                else return null;

                var line = new AnalysisReadRules.AnalyticalPolyline { ElementId = Rid.Value(e.Id) };
                double deviationMm = 0;
                bool curved = false;
                foreach (Curve c in curves)
                {
                    IList<XYZ> pts = c.Tessellate();
                    for (int i = line.Points.Count == 0 ? 0 : 1; i < pts.Count; i++) line.Points.Add(Mm(pts[i]));
                    if (c is Line) continue;
                    curved = true;
                    for (int i = 0; i + 1 < pts.Count; i++)
                        deviationMm = Math.Max(deviationMm, c.Distance((pts[i] + pts[i + 1]) * 0.5) * FtToMm);
                }
                if (curved)
                {
                    line.SlackMm = 2 * deviationMm + 1.0;
                    line.ExactMm = p =>
                    {
                        try
                        {
                            var q = new XYZ(p[0] / FtToMm, p[1] / FtToMm, p[2] / FtToMm);
                            return curves.Min(c => c.Distance(q)) * FtToMm;
                        }
                        catch { return null; }
                    };
                }
                return line.Points.Count >= 2 ? line : null;
            }
            catch { return null; }
        }

        /// <summary>A panel's outer contour less its openings; null when either would not read (edges still count).</summary>
        private static AnalysisReadRules.AnalyticalSurface Surface(Document doc, AnalyticalPanel panel)
        {
            try
            {
                var s = new AnalysisReadRules.AnalyticalSurface { ElementId = Rid.Value(panel.Id) };
                CurveLoop outer = panel.GetOuterContour();
                if (outer == null) return null;
                s.Outer = LoopPoints(outer);
                foreach (ElementId openingId in panel.GetAnalyticalOpeningsIds())
                {
                    // An opening that would not read is not guessed away: an end inside it would
                    // be called connected, so the whole surface is left to its edges.
                    if (!(doc.GetElement(openingId) is AnalyticalOpening opening)) return null;
                    CurveLoop hole = opening.GetOuterContour();
                    if (hole == null) return null;
                    s.Holes.Add(LoopPoints(hole));
                }
                return s.Outer.Count >= 3 ? s : null;
            }
            catch { return null; }
        }

        private static List<double[]> LoopPoints(CurveLoop loop)
        {
            var pts = new List<double[]>();
            foreach (Curve c in loop)
            {
                IList<XYZ> t = c.Tessellate();
                for (int i = pts.Count == 0 ? 0 : 1; i < t.Count; i++) pts.Add(Mm(t[i]));
            }
            if (pts.Count > 1 && AnalysisReadRules.PointSegment(pts[0], pts[pts.Count - 1], pts[pts.Count - 1]) < 1e-6)
                pts.RemoveAt(pts.Count - 1);
            return pts;
        }

        /// <summary>Analytical links (rigid links between hubs) as straight targets.</summary>
        private static List<AnalysisReadRules.AnalyticalPolyline> Links(Document doc, JArray reasons)
        {
            var list = new List<AnalysisReadRules.AnalyticalPolyline>();
            int unreadable = 0;
            foreach (AnalyticalLink link in new FilteredElementCollector(doc).OfClass(typeof(AnalyticalLink)).Cast<AnalyticalLink>())
            {
                try
                {
                    var l = new AnalysisReadRules.AnalyticalPolyline { ElementId = Rid.Value(link.Id) };
                    l.Points.Add(Mm(link.Start));
                    l.Points.Add(Mm(link.End));
                    list.Add(l);
                }
                catch { unreadable++; }
            }
            if (unreadable > 0)
                reasons.Add(StructuralCoverage.Reason("analytical_links", unreadable + " analytical link(s) would not " +
                    "give their end points; an end only they connect is counted unconnected."));
            return list;
        }

        /// <summary>Boundary conditions as point, line or area supports.</summary>
        private static List<AnalysisReadRules.SupportTarget> Supports(Document doc, JArray reasons)
        {
            var list = new List<AnalysisReadRules.SupportTarget>();
            int unreadable = 0;
            foreach (BoundaryConditions bc in new FilteredElementCollector(doc).OfClass(typeof(BoundaryConditions)).Cast<BoundaryConditions>())
            {
                try
                {
                    var s = new AnalysisReadRules.SupportTarget { ElementId = Rid.Value(bc.Id) };
                    BoundaryConditionsType kind = bc.GetBoundaryConditionsType();
                    if (kind == BoundaryConditionsType.Point) s.Points.Add(Mm(bc.Point));
                    else if (kind == BoundaryConditionsType.Line)
                        foreach (XYZ p in bc.GetCurve().Tessellate()) s.Points.Add(Mm(p));
                    else
                    {
                        IList<CurveLoop> loops = bc.GetLoops();
                        s.Surface = new AnalysisReadRules.AnalyticalSurface { ElementId = s.ElementId };
                        for (int i = 0; i < loops.Count; i++)
                        {
                            List<double[]> pts = LoopPoints(loops[i]);
                            if (i > 0) { s.Surface.Holes.Add(pts); continue; }
                            s.Surface.Outer = pts;
                            s.Points.AddRange(pts);
                            if (pts.Count > 0) s.Points.Add(pts[0]);
                        }
                    }
                    if (s.Points.Count == 0) { unreadable++; continue; }
                    list.Add(s);
                }
                catch { unreadable++; }
            }
            if (unreadable > 0)
                reasons.Add(StructuralCoverage.Reason("supports", unreadable + " boundary condition(s) would not give " +
                    "their geometry; an end only they hold is counted unconnected, not supported."));
            return list;
        }

        private static double[] Mm(XYZ p) => new[] { p.X * FtToMm, p.Y * FtToMm, p.Z * FtToMm };

        private static JArray MmArray(double[] p) =>
            new JArray(Math.Round(p[0], 1), Math.Round(p[1], 1), Math.Round(p[2], 1));

        private static JArray Ends(List<AnalysisReadRules.NodeGap> ends, string nearestKey) =>
            new JArray((ends ?? new List<AnalysisReadRules.NodeGap>()).Select(g => (object)new JObject
            {
                ["end"] = g.End == 0 ? "start" : "end",
                ["point_mm"] = MmArray(g.Point),
                ["nearest_mm"] = g.NearestMm.HasValue ? (JToken)g.NearestMm.Value : JValue.CreateNull(),
                [nearestKey] = g.NearestElementId.HasValue ? (JToken)g.NearestElementId.Value : JValue.CreateNull()
            }).ToArray());

        private static JObject AnalyticalRow(Document doc, Element e, AnalyticalToPhysicalAssociationManager manager,
            Dictionary<long, AnalysisReadRules.AnalyticalPolyline> polylines,
            Dictionary<long, List<AnalysisReadRules.NodeGap>> gaps,
            Dictionary<long, List<AnalysisReadRules.NodeGap>> supported, JArray reasons)
        {
            long id = Rid.Value(e.Id);
            var unread = new List<string>();
            var ae = e as AnalyticalElement;
            var row = new JObject
            {
                ["id"] = id,
                ["kind"] = e is AnalyticalMember ? "member" : "panel",
                ["name"] = Field(() => e.Name, unread, "name"),
                ["structural_role"] = Field(() => ae.StructuralRole.ToString(), unread, "structural_role"),
                ["analyze_as"] = Field(() => ae.AnalyzeAs.ToString(), unread, "analyze_as")
            };

            JToken associated = JValue.CreateNull();
            if (manager != null)
            {
                try
                {
#if REVIT2023
                    ElementId one = manager.GetAssociatedElementId(e.Id);
                    associated = (one == null || one == ElementId.InvalidElementId)
                        ? new JArray() : new JArray(Rid.Value(one));
#else
                    ISet<ElementId> set = manager.GetAssociatedElementIds(e.Id);
                    associated = new JArray((set ?? new HashSet<ElementId>()).Select(Rid.Value)
                        .OrderBy(v => v).Cast<object>().ToArray());
#endif
                }
                catch { unread.Add("association"); }
            }
            else unread.Add("association");
            row["associated_physical_ids"] = associated;
            row["association"] = associated is JArray a ? (a.Count > 0 ? "associated" : "none") : "unreadable";

            polylines.TryGetValue(id, out AnalysisReadRules.AnalyticalPolyline line);
            if (e is AnalyticalMember member)
            {
                var m = new JObject();
                if (line != null)
                {
                    m["start_mm"] = MmArray(line.Points[0]);
                    m["end_mm"] = MmArray(line.Points[line.Points.Count - 1]);
                }
                else unread.Add("curve");
                m["length_mm"] = Field(() => Math.Round(member.GetCurve().Length * FtToMm, 1), unread, "length");
                m["section_type_id"] = IdField(() => member.SectionTypeId, unread, "section_type_id");
                m["cross_section_rotation_deg"] = Field(() => Math.Round(member.CrossSectionRotation * 180 / Math.PI, 3),
                                                        unread, "cross_section_rotation");
                m["releases"] = Releases(member, unread);
                row["member"] = m;
                // Not measured - for the whole call, or for this member whose curve would not read - is
                // null and named, never an empty list that reads as "ends checked, no gap".
                if (gaps == null || line == null)
                {
                    row["node_gaps"] = null; row["supported_ends"] = null; unread.Add("node_gaps");
                }
                else
                {
                    gaps.TryGetValue(id, out List<AnalysisReadRules.NodeGap> mine);
                    row["node_gaps"] = Ends(mine, "nearest_element_id");
                    supported.TryGetValue(id, out List<AnalysisReadRules.NodeGap> held);
                    row["supported_ends"] = Ends(held, "boundary_condition_id");
                }
            }
            else if (e is AnalyticalPanel panel)
            {
                row["panel"] = new JObject
                {
                    ["thickness_mm"] = Field(() => Math.Round(panel.Thickness * FtToMm, 1), unread, "thickness"),
                    ["contour_points"] = line == null ? JValue.CreateNull() : (JToken)(line.Points.Count - 1),
                    ["opening_ids"] = IdsField(() => panel.GetAnalyticalOpeningsIds(), unread, "opening_ids")
                };
                if (line == null) unread.Add("contour");
            }

            row["unread"] = new JArray(unread.Cast<object>().ToArray());
            row["coverage"] = unread.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial;
            if (unread.Count > 0)
                reasons.Add(StructuralCoverage.Reason("analytical", "could not read " + string.Join(", ", unread) + ".", id));
            return row;
        }

        // RevitAPI.xml documents ReleaseConditions.Fx..Mz only as "the Fx of the release
        // type" - not whether true means released or fixed. Until a live run fixes the
        // polarity (a Pinned member read back), the flags are published raw, beside the
        // release type that gives them their meaning, and no polarity is claimed.
        // An end with no ReleaseConditions, or whose flags throw, is named ("releases.start"), so
        // a row never reads complete with the six flags silently missing.
        private static JObject Releases(AnalyticalMember member, List<string> unread)
        {
            IList<ReleaseConditions> conditions;
            try { conditions = member.GetReleaseConditions(); }
            catch { unread.Add("releases"); return null; }
            var o = new JObject();
            foreach (bool start in new[] { true, false })
            {
                string key = start ? "start" : "end";
                var end = new JObject
                {
                    ["type"] = Field(() => member.GetReleaseType(start).ToString(), unread, "releases." + key + ".type")
                };
                ReleaseConditions rc = null;
                try { rc = conditions?.FirstOrDefault(c => c != null && c.Start == start); } catch { }
                if (rc == null) unread.Add("releases." + key);
                else
                {
                    try
                    {
                        var fx = rc.Fx; var fy = rc.Fy; var fz = rc.Fz;
                        var mx = rc.Mx; var my = rc.My; var mz = rc.Mz;
                        end["fx"] = fx; end["fy"] = fy; end["fz"] = fz;
                        end["mx"] = mx; end["my"] = my; end["mz"] = mz;
                    }
                    catch { unread.Add("releases." + key); }
                }
                o[key] = end;
            }
            o["flags_polarity"] = "unmeasured: the API does not state whether true is released or fixed; " +
                                  "read the flags with the release type.";
            return o;
        }

        // `seen` collects the physical ids looked at, so an id the caller named that is
        // physical is not reported back as unmatched.
        private static JObject PhysicalWithoutAnalytical(Document doc, AnalyticalToPhysicalAssociationManager manager,
                                                         List<long> ids, JArray reasons, HashSet<long> seen)
        {
            var physical = new List<Element>();
            var excluded = new List<long>();
            var cats = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors
            };
            // Collect ignores categories when ids are named, so the category is checked here.
            var catIds = new HashSet<long>(cats.Select(c => Rid.Value(new ElementId(c))));
            long wallCat = Rid.Value(new ElementId(BuiltInCategory.OST_Walls));
            long floorCat = Rid.Value(new ElementId(BuiltInCategory.OST_Floors));
            foreach (Element e in Collect(doc, cats, ids))
            {
                if (e is AnalyticalElement || e is ElementType || e.Category == null) continue;
                long cat = Rid.Value(e.Category.Id);
                if (!catIds.Contains(cat)) continue;
                long eid = Rid.Value(e.Id);
                if (cat == wallCat || cat == floorCat)
                {
                    // The category decides before the class. In walls and floors only a Wall or a
                    // Floor carries the structural flag this check reads; an in-place or loadable
                    // wall/floor family is a FamilyInstance there, with no such flag, and accepted as
                    // one it would make every architectural in-place parapet "a gap the analysis export
                    // drops". It is excluded - named, the block partial - never counted.
                    if (e is Wall) { if (ParamNumberInt(e, BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT) != 1) continue; }
                    else if (e is Floor) { if (ParamNumberInt(e, BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL) != 1) continue; }
                    else { excluded.Add(eid); seen.Add(eid); continue; }
                }
                // Framing, columns and foundations: family instances, wall foundations and foundation
                // slabs (a Floor there is structural by its category; the floor flag may be unset on it).
                else if (!(e is FamilyInstance) && !(e is Floor) && !(e is WallFoundation))
                {
                    excluded.Add(eid); seen.Add(eid); continue;
                }
                physical.Add(e);
                seen.Add(eid);
            }
            if (excluded.Count > 0)
                reasons.Add(StructuralCoverage.Reason("physical_without_analytical",
                    excluded.Count + " element(s) in these categories are of a class this check does not read (an " +
                    "in-place or loadable wall/floor family carries no structural flag it reads) and were NOT " +
                    "checked: " + string.Join(", ", excluded.Take(20)) + (excluded.Count > 20 ? ", ..." : "") + "."));
            string scope = "structural framing, structural columns, structural foundations (family instances, wall " +
                           "foundations and foundation slabs), and Wall/Floor elements flagged structural";
            if (ids != null && ids.Count > 0)
            {
                scope = "only the element_ids named, where they are " + scope + " - not the model";
                // Named ids that are all analytical (the documented narrowing) checked nothing: a
                // count of 0 under complete would read as "the model has no gap".
                if (physical.Count == 0 && excluded.Count == 0)
                    return new JObject
                    {
                        ["checked"] = 0, ["count"] = null, ["ids"] = null,
                        ["excluded_other_classes"] = 0, ["scope"] = scope,
                        ["coverage"] = StructuralCoverage.NotApplicable,
                        ["means"] = "no element_ids entry is a physical element of these categories, so nothing was " +
                                    "checked; omit element_ids to check the model."
                    };
            }
            if (manager == null)
                return new JObject
                {
                    ["checked"] = physical.Count, ["count"] = null, ["ids"] = null,
                    ["excluded_other_classes"] = excluded.Count, ["scope"] = scope,
                    ["coverage"] = StructuralCoverage.Unreadable
                };
            var missing = new List<long>();
            int unreadable = 0;
            foreach (Element e in physical)
            {
                try { if (!manager.HasAssociation(e.Id)) missing.Add(Rid.Value(e.Id)); }
                catch { unreadable++; }
            }
            if (unreadable > 0)
                reasons.Add(StructuralCoverage.Reason("physical_without_analytical",
                    unreadable + " physical element(s) would not answer HasAssociation and are not counted either way."));
            return new JObject
            {
                ["checked"] = physical.Count,
                ["count"] = missing.Count,
                ["ids"] = new JArray(missing.Take(MaxListedUnassociated).Cast<object>().ToArray()),
                ["ids_truncated"] = missing.Count > MaxListedUnassociated,
                ["excluded_other_classes"] = excluded.Count,
                ["scope"] = scope,
                ["coverage"] = unreadable == 0 && excluded.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial
            };
        }

        private static int? ParamNumberInt(Element e, BuiltInParameter bip)
        {
            try
            {
                Parameter p = e.get_Parameter(bip);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Integer) return null;
                return p.AsInteger();
            }
            catch { return null; }
        }

        /// <summary>An id: null when none is set (InvalidElementId); null AND named when the read throws.</summary>
        private static JToken IdField(Func<ElementId> read, List<string> unread, string what)
        {
            try
            {
                ElementId id = read();
                return id == null || id == ElementId.InvalidElementId ? JValue.CreateNull() : (JToken)Rid.Value(id);
            }
            catch { unread.Add(what); return JValue.CreateNull(); }
        }

        /// <summary>A set of ids, sorted; null AND named when the read throws or answers null.</summary>
        private static JToken IdsField(Func<IEnumerable<ElementId>> read, List<string> unread, string what)
        {
            try
            {
                IEnumerable<ElementId> ids = read();
                if (ids == null) { unread.Add(what); return JValue.CreateNull(); }
                return new JArray(ids.Select(Rid.Value).OrderBy(v => v).Cast<object>().ToArray());
            }
            catch { unread.Add(what); return JValue.CreateNull(); }
        }

        // --------------------------------------------------------------- loads

        private CommandResult Loads(Document doc, List<long> ids, int offset, int maxRows)
        {
            List<Element> all = new FilteredElementCollector(doc).OfClass(typeof(PointLoad))
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(LineLoad)))
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(AreaLoad)))
                .Where(e => ids.Count == 0 || ids.Contains(Rid.Value(e.Id)))
                .OrderBy(e => Rid.Value(e.Id)).ToList();

            var reasons = new JArray();
            var rows = new JArray();
            foreach (Element e in all.Skip(offset).Take(maxRows))
                rows.Add(LoadRow(doc, (LoadBase)e, reasons));

            var byCase = new JObject();
            // Never keyed by an empty name: a load with no case is its own group (StructuralLoadRules).
            foreach (IGrouping<string, Element> g in all.GroupBy(e => StructuralLoadRules.CaseKey(Str(() => ((LoadBase)e).LoadCaseName), CaseAssigned((LoadBase)e)))
                                                        .OrderBy(g => g.Key, StringComparer.Ordinal))
                byCase[g.Key] = g.Count();
            var extra = new JObject
            {
                ["counts"] = new JObject
                {
                    ["point"] = all.Count(e => e is PointLoad),
                    ["line"] = all.Count(e => e is LineLoad),
                    ["area"] = all.Count(e => e is AreaLoad)
                },
                ["by_load_case"] = byCase,
                ["units"] = new JObject
                {
                    ["point_force"] = "kN", ["point_moment"] = "kN*m", ["line_force"] = "kN/m",
                    ["line_moment"] = "kN*m/m", ["area_force"] = "kN/m2", ["area"] = "m2", ["position"] = "mm",
                    ["vector_frame"] = "force and moment components are in each row's vector_frame (Revit's OrientTo): " +
                                       "project = project coordinates; work_plane and host_local are components in " +
                                       "that frame and do not add to project ones. Positions are project coordinates."
                },
                ["unmatched_ids"] = new JArray(ids.Where(id => !all.Any(e => Rid.Value(e.Id) == id)).Cast<object>().ToArray())
            };
            return Ok("loads", all.Count, offset, rows, reasons, extra);
        }

        private static JObject LoadRow(Document doc, LoadBase load, JArray reasons)
        {
            long id = Rid.Value(load.Id);
            var unread = new List<string>();

            JToken caseId = JValue.CreateNull();
            LoadCase caseElement = null;
            bool? assigned = CaseAssigned(load);
            if (assigned == null) unread.Add("load_case_id");
            else if (assigned == true)
            {
                try
                {
                    ElementId cid = load.LoadCaseId;
                    caseId = Rid.Value(cid);
                    caseElement = doc.GetElement(cid) as LoadCase;
                }
                catch { unread.Add("load_case_id"); }
            }
            JToken caseNumber = JValue.CreateNull();
            if (caseElement != null)
            {
                try { caseNumber = caseElement.Number; } catch { unread.Add("load_case_number"); }
            }
            string orient = null;
            try { orient = load.OrientTo.ToString(); } catch { unread.Add("orient_to"); }
            string frame = orient == "Project" ? "project" : orient == "WorkPlane" ? "work_plane"
                         : orient == "HostLocalCoordinateSystem" ? "host_local" : null;
            if (orient != null && frame == null) unread.Add("vector_frame");
            JToken host = JValue.CreateNull();
            try
            {
                ElementId h = load.HostElementId;
                if (h != null && h != ElementId.InvalidElementId) host = Rid.Value(h);
            }
            catch { unread.Add("host"); }

            var row = new JObject
            {
                ["id"] = id,
                ["kind"] = load is PointLoad ? "point" : load is LineLoad ? "line" : "area",
                ["load_case"] = new JObject
                {
                    ["id"] = caseId,
                    // A load with NO case has no name by design: null, and not a failure to read.
                    ["name"] = assigned == false ? JValue.CreateNull() : Field(() => load.LoadCaseName, unread, "load_case"),
                    ["number"] = caseNumber,
                    ["assigned"] = assigned.HasValue ? (JToken)assigned.Value : JValue.CreateNull()
                },
                ["nature"] = CaseField(() => load.LoadNatureName, assigned, unread, "nature"),
                ["category"] = CaseField(() => load.LoadCategoryName, assigned, unread, "category"),
                ["is_reaction"] = Field(() => load.IsReaction, unread, "is_reaction"),
                ["is_hosted"] = Field(() => load.IsHosted, unread, "is_hosted"),
                ["host_id"] = host,
                ["orient_to"] = orient,
                ["vector_frame"] = frame
            };
            JToken caseName = row["load_case"]["name"];
            if (StructuralLoadRules.CaseNameUnread(caseName.Type == JTokenType.String ? (string)caseName : null, assigned))
            {
                row["load_case"]["name"] = JValue.CreateNull();
                if (!unread.Contains("load_case")) unread.Add("load_case");
            }

            if (load is PointLoad pl)
            {
                row["point"] = new JObject
                {
                    ["position_mm"] = Vec(() => pl.Point, null, unread, "position"),
                    ["force_kn"] = Vec(() => pl.ForceVector, UnitTypeId.Kilonewtons, unread, "force"),
                    ["moment_kn_m"] = Vec(() => pl.MomentVector, UnitTypeId.KilonewtonMeters, unread, "moment")
                };
            }
            else if (load is LineLoad ll)
            {
                row["line"] = new JObject
                {
                    ["start_mm"] = Vec(() => ll.StartPoint, null, unread, "start"),
                    ["end_mm"] = Vec(() => ll.EndPoint, null, unread, "end"),
                    ["force1_kn_m"] = Vec(() => ll.ForceVector1, UnitTypeId.KilonewtonsPerMeter, unread, "force1"),
                    ["force2_kn_m"] = Vec(() => ll.ForceVector2, UnitTypeId.KilonewtonsPerMeter, unread, "force2"),
                    ["moment1_kn_m_per_m"] = Vec(() => ll.MomentVector1, UnitTypeId.KilonewtonMetersPerMeter, unread, "moment1"),
                    ["moment2_kn_m_per_m"] = Vec(() => ll.MomentVector2, UnitTypeId.KilonewtonMetersPerMeter, unread, "moment2"),
                    ["is_uniform"] = Field(() => ll.IsUniform, unread, "is_uniform"),
                    ["is_projected"] = Field(() => ll.IsProjected, unread, "is_projected")
                };
            }
            else if (load is AreaLoad al)
            {
                row["area"] = new JObject
                {
                    ["force1_kn_m2"] = Vec(() => al.ForceVector1, UnitTypeId.KilonewtonsPerSquareMeter, unread, "force1"),
                    ["force2_kn_m2"] = Vec(() => al.ForceVector2, UnitTypeId.KilonewtonsPerSquareMeter, unread, "force2"),
                    ["force3_kn_m2"] = Vec(() => al.ForceVector3, UnitTypeId.KilonewtonsPerSquareMeter, unread, "force3"),
                    ["area_m2"] = Field(() => Math.Round(UnitUtils.ConvertFromInternalUnits(al.Area, UnitTypeId.SquareMeters), 4),
                                        unread, "area"),
                    ["reference_points"] = Field(() => al.NumRefPoints, unread, "reference_points"),
                    ["is_projected"] = Field(() => al.IsProjected, unread, "is_projected")
                };
            }
            row["unread"] = new JArray(unread.Cast<object>().ToArray());
            row["coverage"] = unread.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial;
            if (unread.Count > 0)
                reasons.Add(StructuralCoverage.Reason("load", "could not read " + string.Join(", ", unread) + ".", id));
            return row;
        }

        /// <summary>
        /// A name that comes from the load's CASE (nature, category): null by design for a load
        /// with no case; for one with a case, a throw or an empty name is named in unread.
        /// </summary>
        private static JToken CaseField(Func<string> read, bool? assigned, List<string> unread, string what)
        {
            if (assigned == false) return JValue.CreateNull();
            string v;
            try { v = read(); } catch { unread.Add(what); return JValue.CreateNull(); }
            if (string.IsNullOrWhiteSpace(v)) { unread.Add(what); return JValue.CreateNull(); }
            return v;
        }

        /// <summary>Whether the load has a load case (a valid LoadCaseId); null when that could not be read.</summary>
        private static bool? CaseAssigned(LoadBase load)
        {
            try
            {
                ElementId cid = load.LoadCaseId;
                return cid != null && cid != ElementId.InvalidElementId;
            }
            catch { return null; }
        }

        /// <summary>A scalar field; a throw is NAMED in unread rather than published as a null that reads as "none".</summary>
        private static JToken Field<T>(Func<T> read, List<string> unread, string what)
        {
            try
            {
                T v = read();
                return v == null ? JValue.CreateNull() : JToken.FromObject(v);
            }
            catch { unread.Add(what); return JValue.CreateNull(); }
        }

        /// <summary>A vector in the given unit, or millimetres when unit is null; null (and named) when unreadable.</summary>
        private static JToken Vec(Func<XYZ> read, ForgeTypeId unit, List<string> unread, string what)
        {
            try
            {
                XYZ v = read();
                if (v == null) { unread.Add(what); return JValue.CreateNull(); }
                Func<double, double> f = unit == null
                    ? (Func<double, double>)(x => x * FtToMm)
                    : (x => UnitUtils.ConvertFromInternalUnits(x, unit));
                return new JArray(Math.Round(f(v.X), 4), Math.Round(f(v.Y), 4), Math.Round(f(v.Z), 4));
            }
            catch { unread.Add(what); return JValue.CreateNull(); }
        }
    }
}
