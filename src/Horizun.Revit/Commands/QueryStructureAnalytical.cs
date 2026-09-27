// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_query_structure mode=analytical and mode=loads.
// Original Horizun code. Read-only: no transaction is opened.
//
// mode=analytical reads the ANALYTICAL model (Revit 2023+ AnalyticalMember and
// AnalyticalPanel) beside the physical one: which physical element each
// analytical element is associated with, member end releases, and member ends
// that no other analytical curve reaches within a tolerance - measured to the
// nearest point ON every other curve (Core/AnalysisReadRules.cs), because a
// beam framing into mid-girder is connected. It also names the physical
// structural elements that have NO analytical counterpart, which is the gap an
// analysis export silently drops.
//
// 2023 vs 2024+: AnalyticalToPhysicalAssociationManager.GetAssociatedElementIds
// (plural, one-to-many) does not exist in the 2023 API - only the singular
// GetAssociatedElementId - so 2023 reads one associated id per element.
//
// mode=loads reads point, line and area loads with load case, nature, category
// and host, magnitudes converted through UnitUtils to kN-based units. Nothing
// here judges a load; the numbers are what the model carries.
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
            foreach (Element e in everyAnalytical)
            {
                AnalysisReadRules.AnalyticalPolyline line = Polyline(e);
                if (line != null) polylines[line.ElementId] = line;
            }
            var members = scope.OfType<AnalyticalMember>().Where(m => polylines.ContainsKey(Rid.Value(m.Id)))
                               .Select(m => polylines[Rid.Value(m.Id)]).ToList();
            var targets = polylines.Values.ToList();
            var gapsById = new Dictionary<long, List<AnalysisReadRules.NodeGap>>();
            bool gapsMeasured = AnalysisReadRules.PairChecks(members, targets) <= AnalysisReadRules.MaxPairChecks;
            if (gapsMeasured)
            {
                foreach (AnalysisReadRules.NodeGap gap in AnalysisReadRules.NodeGaps(members, targets, toleranceMm))
                {
                    if (!gapsById.TryGetValue(gap.ElementId, out var list)) gapsById[gap.ElementId] = list = new List<AnalysisReadRules.NodeGap>();
                    list.Add(gap);
                }
            }
            else
            {
                reasons.Add(StructuralCoverage.Reason("node_gaps",
                    "ends x segments exceeds " + AnalysisReadRules.MaxPairChecks + " checks; narrow with element_ids. " +
                    "Node gaps were NOT measured, which is not the same as none."));
            }

            var rows = new JArray();
            foreach (Element e in scope.Skip(offset).Take(maxRows))
                rows.Add(AnalyticalRow(doc, e, manager, polylines, gapsMeasured ? gapsById : null, reasons));

            var physicalSeen = new HashSet<long>();
            JObject unassociated = PhysicalWithoutAnalytical(doc, manager, ids, reasons, physicalSeen);
            int gapEnds = gapsById.Values.Sum(l => l.Count);
            var extra = new JObject
            {
                ["tolerance_mm"] = Math.Round(toleranceMm, 4),
                ["tolerance_source"] = toleranceSource,
                ["node_gaps_measured"] = gapsMeasured,
                ["member_ends_beyond_tolerance"] = gapsMeasured ? (JToken)gapEnds : JValue.CreateNull(),
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

        private static AnalysisReadRules.AnalyticalPolyline Polyline(Element e)
        {
            try
            {
                var line = new AnalysisReadRules.AnalyticalPolyline { ElementId = Rid.Value(e.Id) };
                if (e is AnalyticalMember member)
                {
                    Curve c = member.GetCurve();
                    if (c == null) return null;
                    foreach (XYZ p in c.Tessellate()) line.Points.Add(Mm(p));
                }
                else if (e is AnalyticalPanel panel)
                {
                    CurveLoop loop = panel.GetOuterContour();
                    if (loop == null) return null;
                    foreach (Curve c in loop)
                    {
                        IList<XYZ> pts = c.Tessellate();
                        for (int i = line.Points.Count == 0 ? 0 : 1; i < pts.Count; i++) line.Points.Add(Mm(pts[i]));
                    }
                    if (line.Points.Count > 0) line.Points.Add(line.Points[0]);
                }
                return line.Points.Count >= 2 ? line : null;
            }
            catch { return null; }
        }

        private static double[] Mm(XYZ p) => new[] { p.X * FtToMm, p.Y * FtToMm, p.Z * FtToMm };

        private static JArray MmArray(double[] p) =>
            new JArray(Math.Round(p[0], 1), Math.Round(p[1], 1), Math.Round(p[2], 1));

        private static JObject AnalyticalRow(Document doc, Element e, AnalyticalToPhysicalAssociationManager manager,
            Dictionary<long, AnalysisReadRules.AnalyticalPolyline> polylines,
            Dictionary<long, List<AnalysisReadRules.NodeGap>> gaps, JArray reasons)
        {
            long id = Rid.Value(e.Id);
            var unread = new List<string>();
            var ae = e as AnalyticalElement;
            var row = new JObject
            {
                ["id"] = id,
                ["kind"] = e is AnalyticalMember ? "member" : "panel",
                ["name"] = Str(() => e.Name),
                ["structural_role"] = Str(() => ae.StructuralRole.ToString()),
                ["analyze_as"] = Str(() => ae.AnalyzeAs.ToString())
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
                m["length_mm"] = Round(Num(() => member.GetCurve().Length) * FtToMm, 1);
                m["section_type_id"] = SafeId(() => member.SectionTypeId);
                m["cross_section_rotation_deg"] = Round(Num(() => member.CrossSectionRotation) * 180 / Math.PI, 3);
                JObject releases = Releases(member);
                if (releases == null) unread.Add("releases");
                m["releases"] = releases;
                row["member"] = m;
                if (gaps == null) { row["node_gaps"] = null; unread.Add("node_gaps"); }
                else
                {
                    gaps.TryGetValue(id, out List<AnalysisReadRules.NodeGap> mine);
                    row["node_gaps"] = new JArray((mine ?? new List<AnalysisReadRules.NodeGap>()).Select(g => (object)new JObject
                    {
                        ["end"] = g.End == 0 ? "start" : "end",
                        ["point_mm"] = MmArray(g.Point),
                        ["nearest_mm"] = g.NearestMm.HasValue ? (JToken)g.NearestMm.Value : JValue.CreateNull(),
                        ["nearest_element_id"] = g.NearestElementId.HasValue ? (JToken)g.NearestElementId.Value : JValue.CreateNull()
                    }).ToArray());
                }
            }
            else if (e is AnalyticalPanel panel)
            {
                row["panel"] = new JObject
                {
                    ["thickness_mm"] = Round(Num(() => panel.Thickness) * FtToMm, 1),
                    ["contour_points"] = line == null ? JValue.CreateNull() : (JToken)(line.Points.Count - 1),
                    ["opening_ids"] = IdArray(() => panel.GetAnalyticalOpeningsIds().ToList())
                };
                if (line == null) unread.Add("contour");
            }

            row["unread"] = new JArray(unread.Cast<object>().ToArray());
            row["coverage"] = unread.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial;
            if (unread.Count > 0)
                reasons.Add(StructuralCoverage.Reason("analytical", "could not read " + string.Join(", ", unread) + ".", id));
            return row;
        }

        private static JObject Releases(AnalyticalMember member)
        {
            try
            {
                var o = new JObject();
                IList<ReleaseConditions> conditions = member.GetReleaseConditions();
                foreach (bool start in new[] { true, false })
                {
                    var end = new JObject { ["type"] = member.GetReleaseType(start).ToString() };
                    ReleaseConditions rc = conditions?.FirstOrDefault(c => c.Start == start);
                    if (rc != null)
                    {
                        end["fx"] = rc.Fx; end["fy"] = rc.Fy; end["fz"] = rc.Fz;
                        end["mx"] = rc.Mx; end["my"] = rc.My; end["mz"] = rc.Mz;
                    }
                    o[start ? "start" : "end"] = end;
                }
                o["true_means"] = "the degree of freedom is RELEASED at that end.";
                return o;
            }
            catch { return null; }
        }

        // `seen` collects the physical ids looked at, so an id the caller named that is
        // physical is not reported back as unmatched.
        private static JObject PhysicalWithoutAnalytical(Document doc, AnalyticalToPhysicalAssociationManager manager,
                                                         List<long> ids, JArray reasons, HashSet<long> seen)
        {
            var physical = new List<Element>();
            var cats = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors
            };
            // Collect ignores categories when ids are named, so the category is checked here.
            var catIds = new HashSet<long>(cats.Select(c => Rid.Value(new ElementId(c))));
            foreach (Element e in Collect(doc, cats, ids))
            {
                if (e is AnalyticalElement || e.Category == null || !catIds.Contains(Rid.Value(e.Category.Id))) continue;
                if (e is Wall && ParamNumberInt(e, BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT) != 1) continue;
                if (e is Floor && ParamNumberInt(e, BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL) != 1) continue;
                if (!(e is FamilyInstance) && !(e is Wall) && !(e is Floor)) continue;
                physical.Add(e);
                seen.Add(Rid.Value(e.Id));
            }
            if (manager == null)
                return new JObject
                {
                    ["checked"] = physical.Count, ["count"] = null, ["ids"] = null,
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
                ["scope"] = "structural framing, structural columns, structural foundations, and walls/floors " +
                            "flagged structural",
                ["coverage"] = unreadable == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial
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

        private static JToken SafeId(Func<ElementId> f)
        {
            ElementId id = Safe(f);
            return id == null || id == ElementId.InvalidElementId ? JValue.CreateNull() : (JToken)Rid.Value(id);
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
            foreach (IGrouping<string, Element> g in all.GroupBy(e => Str(() => ((LoadBase)e).LoadCaseName) ?? "(unreadable)")
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
                    ["line_moment"] = "kN*m/m", ["area_force"] = "kN/m2", ["area"] = "m2", ["position"] = "mm"
                },
                ["unmatched_ids"] = new JArray(ids.Where(id => !all.Any(e => Rid.Value(e.Id) == id)).Cast<object>().ToArray())
            };
            return Ok("loads", all.Count, offset, rows, reasons, extra);
        }

        private static JObject LoadRow(Document doc, LoadBase load, JArray reasons)
        {
            long id = Rid.Value(load.Id);
            var unread = new List<string>();
            var caseElement = Safe(() => load.LoadCaseId) is ElementId caseId ? doc.GetElement(caseId) as LoadCase : null;
            var row = new JObject
            {
                ["id"] = id,
                ["kind"] = load is PointLoad ? "point" : load is LineLoad ? "line" : "area",
                ["load_case"] = new JObject
                {
                    ["id"] = SafeId(() => load.LoadCaseId),
                    ["name"] = Str(() => load.LoadCaseName),
                    ["number"] = caseElement == null ? JValue.CreateNull() : (JToken)Num(() => caseElement.Number)
                },
                ["nature"] = Str(() => load.LoadNatureName),
                ["category"] = Str(() => load.LoadCategoryName),
                ["is_reaction"] = Bool(() => load.IsReaction),
                ["is_hosted"] = Bool(() => load.IsHosted),
                ["host_id"] = SafeId(() => load.HostElementId),
                ["orient_to"] = Str(() => load.OrientTo.ToString())
            };
            if ((string)row["load_case"]["name"] == null) unread.Add("load_case");

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
                    ["is_uniform"] = Bool(() => ll.IsUniform),
                    ["is_projected"] = Bool(() => ll.IsProjected)
                };
            }
            else if (load is AreaLoad al)
            {
                row["area"] = new JObject
                {
                    ["force1_kn_m2"] = Vec(() => al.ForceVector1, UnitTypeId.KilonewtonsPerSquareMeter, unread, "force1"),
                    ["force2_kn_m2"] = Vec(() => al.ForceVector2, UnitTypeId.KilonewtonsPerSquareMeter, unread, "force2"),
                    ["force3_kn_m2"] = Vec(() => al.ForceVector3, UnitTypeId.KilonewtonsPerSquareMeter, unread, "force3"),
                    ["area_m2"] = Round(Num(() => UnitUtils.ConvertFromInternalUnits(al.Area, UnitTypeId.SquareMeters)), 4),
                    ["reference_points"] = Num(() => al.NumRefPoints) is double n ? (JToken)(int)n : JValue.CreateNull(),
                    ["is_projected"] = Bool(() => al.IsProjected)
                };
            }
            row["unread"] = new JArray(unread.Cast<object>().ToArray());
            row["coverage"] = unread.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial;
            if (unread.Count > 0)
                reasons.Add(StructuralCoverage.Reason("load", "could not read " + string.Join(", ", unread) + ".", id));
            return row;
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
