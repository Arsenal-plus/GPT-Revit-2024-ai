// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_code_check operation=energy_readiness. Original Horizun code.
//
// READ-ONLY IN EFFECT. Revit's energy analytical model is built inside a transaction that
// is ALWAYS rolled back - the same build the gbXML export uses (EnergyModelBuild) - read
// while it exists, and discarded by the rollback, which also restores any energy model
// the document already had. Nothing is committed, so nothing needs re-reading afterwards.
//
// WHAT IT REPORTS
//   spaces          every room and MEP space: enclosed, placed but NOT ENCLOSED (Revit
//                   reports zero area for a not-enclosed and for a redundant one alike, so
//                   the rule says both), or not placed (counted). An enclosed one that the
//                   energy model did not turn into an analytical space is named too, matched
//                   by the analytical space's CADObjectUniqueId; when no analytical space
//                   resolves to an element, that match is reported unavailable - never as
//                   "every room is missing".
//   surfaces        analytical surfaces by type, and those WITHOUT A CONSTRUCTION. That
//                   needs EnergyAnalysisSurface.GetConstruction, which RevitAPI.xml gives
//                   "since 2024": in Revit 2023 it is reported NOT MEASURABLE by name, never
//                   as zero. SurfaceAir (virtual boundary) and Shade surfaces are not asked.
//                   Types are gbXML's (EnergyAnalysisSurface.Type, in every year; SurfaceType
//                   is obsolete in 2027).
//   window_to_wall  per orientation (Core/EnergyReadinessRules: sectors, azimuth from the
//                   outward normal, gross wall area) after TransformModel, which the API
//                   documents as applying the document's shared coordinates and TRUE NORTH.
//                   When it throws, the ratio is still reported relative to project north
//                   and the reply says so.
//
// Nothing here says compliant or passes: the counts are measurements the caller judges.
// NOT MEASURED YET (energy-readiness.probes.ps1): whether TransformModel rotates Normal
// in a project whose true north differs from project north.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CodeCheckCommand
    {
        private sealed class SpatialRow
        {
            public long Id;
            public string Category, Number, Name, Level, State;

            public JObject ToJson() =>
                new JObject { ["id"] = Id, ["category"] = Category, ["number"] = Number, ["name"] = Name, ["level"] = Level };
        }

        private CommandResult ExecuteEnergyReadiness(UIApplication app, JObject request)
        {
            Document doc = app.ActiveUIDocument.Document;
            CommandResult wrong = DocumentGate.ReadGuard(doc, request, Name);
            if (wrong != null) return wrong;
            foreach (string field in new[] { "requirement_set", "requirement_set_path", "include_passes", "travel", "confirmation_token" })
                if (request[field] != null)
                    return CommandResult.Fail("operation=energy_readiness does not take '" + field + "': it measures the energy model and writes nothing.");
            if (doc.IsFamilyDocument)
                return CommandResult.Fail("operation=energy_readiness needs a project: a family document has no rooms, spaces or energy model.");
            int max = Math.Max(1, Math.Min(5000, request.Value<int?>("max_findings") ?? 200));

            // Room and space states are read outside the transaction: nothing below changes them.
            var spatial = new List<SpatialRow>();
            foreach (var (bic, label) in new[] { (BuiltInCategory.OST_Rooms, "room"), (BuiltInCategory.OST_MEPSpaces, "space") })
                foreach (Element e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (!(e is SpatialElement s)) continue;
                    double area = 0;
                    try { area = s.Area; } catch { }
                    spatial.Add(new SpatialRow
                    {
                        Id = Rid.Value(s.Id), Category = label, Number = ReadText(() => s.Number), Name = ReadText(() => s.Name),
                        Level = ReadText(() => s.Level?.Name),
                        State = s.Location == null ? "unplaced" : area > 1e-9 ? "enclosed" : "not_enclosed"
                    });
                }
            List<SpatialRow> notEnclosed = spatial.Where(r => r.State == "not_enclosed").ToList();
            List<SpatialRow> enclosed = spatial.Where(r => r.State == "enclosed").ToList();
            var spaces = new JObject
            {
                ["rooms"] = spatial.Count(r => r.Category == "room"),
                ["spaces"] = spatial.Count(r => r.Category == "space"),
                ["enclosed"] = enclosed.Count,
                ["unplaced"] = spatial.Count(r => r.State == "unplaced"),
                ["not_enclosed_count"] = notEnclosed.Count,
                ["not_enclosed"] = new JArray(notEnclosed.Take(max).Select(r => r.ToJson())),
                ["not_enclosed_rule"] = "placed with zero area: Revit reports a not-enclosed and a redundant room or space alike"
            };
            var result = new JObject
            {
                ["document"] = doc.Title, ["operation"] = "energy_readiness",
                ["writes"] = "nothing: the energy model is built in a transaction that is always rolled back",
                ["spaces"] = spaces
            };
            if (enclosed.Count == 0)
            {
                result["energy_model"] = new JObject { ["built"] = false, ["why"] = "no spaces: no placed, enclosed room or space, so the energy model would be empty" };
                result["surfaces"] = new JObject { ["not_measured"] = "no energy model was built" };
                result["window_to_wall"] = new JObject { ["not_measured"] = "no energy model was built" };
                return CommandResult.Ok(result);
            }
            if (doc.IsReadOnly)
                return CommandResult.FailWithDetail("operation=energy_readiness builds Revit's energy model in a transaction that is rolled back, " +
                    "and this document is read-only, so none can start. Only the room/space states were read.", result);

            var energy = new JObject { ["built"] = false, ["build"] = EnergyModelBuild.Description };
            try { energy["project_angle_to_true_north_deg"] = Math.Round(doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Angle * 180.0 / Math.PI, 3); }
            catch { }
            var byType = new Dictionary<string, int>(StringComparer.Ordinal);
            var walls = new List<EnergyReadinessRules.WallSample>();
            var mapped = new HashSet<long>();
            int analyticalSpaces = 0, unresolvedSpaces = 0, surfaceCount = 0, unmeasuredWalls = 0;
#if !REVIT2023
            var noConstruction = new JArray();
            int noConstructionCount = 0, openingsNoConstruction = 0, constructionUnreadable = 0;
#endif
            string failure = null;
            using (var tx = new Transaction(doc, "Horizun: energy readiness (rolled back)"))
            {
                try
                {
                    EnergyAnalysisDetailModel model = null;
                    if (tx.Start() != TransactionStatus.Started) failure = "no transaction could start, so the energy model could not be built even temporarily";
                    else if ((model = EnergyModelBuild.CreateSpatial(doc)) == null) failure = "Revit returned no energy model";
                    else
                    {
                        energy["built"] = true;
                        energy["rolled_back"] = true;
                        energy["tier"] = ReadText(() => model.Tier.ToString());
                        energy["export_category"] = ReadText(() => model.ExportCategory.ToString());
                        try { model.TransformModel(); energy["azimuth_basis"] = "true_north: TransformModel applied the shared coordinates and true north"; }
                        catch (Exception ex) { energy["azimuth_basis"] = "project_north: TransformModel failed (" + ex.Message + ")"; }

                        foreach (EnergyAnalysisSpace es in model.GetAnalyticalSpaces())
                        {
                            analyticalSpaces++;
                            Element origin = ByUniqueId(doc, ReadText(() => es.CADObjectUniqueId));
                            if (origin is SpatialElement) mapped.Add(Rid.Value(origin.Id)); else unresolvedSpaces++;
                        }

                        foreach (EnergyAnalysisSurface s in model.GetAnalyticalSurfaces())
                        {
                            surfaceCount++;
                            gbXMLSurfaceType type = s.Type;
                            string typeName = type.ToString();
                            byType[typeName] = byType.TryGetValue(typeName, out int seen) ? seen + 1 : 1;
                            IList<EnergyAnalysisOpening> openings = s.GetAnalyticalOpenings() ?? new List<EnergyAnalysisOpening>();
#if !REVIT2023
                            if (type != gbXMLSurfaceType.SurfaceAir && type != gbXMLSurfaceType.Shade)
                            {
                                EnergyAnalysisConstruction construction = null;
                                bool readable = true;
                                try { construction = s.GetConstruction(); } catch { readable = false; }
                                if (!readable) constructionUnreadable++;
                                else if (construction == null)
                                {
                                    noConstructionCount++;
                                    if (noConstruction.Count < max) noConstruction.Add(SurfaceJson(doc, s, typeName));
                                }
                                foreach (EnergyAnalysisOpening o in openings)
                                {
                                    try { if (o.GetConstruction() == null) openingsNoConstruction++; }
                                    catch { constructionUnreadable++; }
                                }
                            }
#endif
                            if (type != gbXMLSurfaceType.ExteriorWall) continue;
                            double? azimuth = null;
                            try { XYZ n = s.Normal; azimuth = EnergyReadinessRules.Azimuth(n.X, n.Y); } catch { }
                            double area = LoopArea(() => s.GetPolyloops());
                            if (azimuth == null || area <= 0) { unmeasuredWalls++; continue; }
                            var sample = new EnergyReadinessRules.WallSample
                            {
                                Sector = EnergyReadinessRules.Sector(azimuth.Value), WallArea = area * EnergyReadinessRules.SquareFeetToSquareMetres
                            };
                            foreach (EnergyAnalysisOpening o in openings)
                            {
                                double oa = LoopArea(() => o.GetPolyloops());
                                if (oa <= 0) { try { oa = o.Width * o.Height; } catch { oa = 0; } }
                                oa *= EnergyReadinessRules.SquareFeetToSquareMetres;
                                if (o.OpeningType == EnergyAnalysisOpeningType.Window) { sample.WindowArea += oa; sample.Windows++; }
                                else if (o.OpeningType == EnergyAnalysisOpeningType.Door) { sample.DoorArea += oa; sample.Doors++; }
                            }
                            walls.Add(sample);
                        }
                    }
                }
                catch (Exception ex) { failure = "Revit could not build or read its energy model (" + ex.GetType().Name + ": " + ex.Message + ")"; }
                finally { if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); }
            }
            result["energy_model"] = energy;
            if (failure != null)
                return CommandResult.FailWithDetail(failure + ". Nothing was written; only the room/space states were read.", result);

            energy["analytical_spaces"] = analyticalSpaces;
            if (unresolvedSpaces > 0) energy["analytical_spaces_unresolved"] = unresolvedSpaces;
            int missingCount = 0;
            if (analyticalSpaces > 0 && mapped.Count == 0)
                spaces["not_in_energy_model"] = "unavailable: no analytical space resolves to a room or space by CADObjectUniqueId, so which are missing cannot be told";
            else
            {
                // The model is built from rooms OR spaces: an enclosed element of the other
                // category is not missing. With no analytical space at all, every enclosed one is.
                var used = new HashSet<string>(spatial.Where(r => mapped.Contains(r.Id)).Select(r => r.Category));
                var missing = new JArray();
                foreach (SpatialRow r in enclosed.Where(r => (used.Count == 0 || used.Contains(r.Category)) && !mapped.Contains(r.Id)))
                {
                    missingCount++;
                    if (missing.Count < max) missing.Add(r.ToJson());
                }
                spaces["not_in_energy_model_count"] = missingCount;
                spaces["not_in_energy_model"] = missing;
            }

            var surfaces = new JObject { ["analytical_surfaces"] = surfaceCount, ["by_type"] = JObject.FromObject(byType) };
            var findings = new JObject { ["not_enclosed"] = notEnclosed.Count, ["not_in_energy_model"] = missingCount };
#if REVIT2023
            surfaces["without_construction"] = "not measurable in Revit 2023: EnergyAnalysisSurface.GetConstruction exists from Revit 2024 (RevitAPI.xml 'since 2024')";
            findings["surfaces_without_construction"] = JValue.CreateNull();
#else
            surfaces["without_construction_count"] = noConstructionCount;
            surfaces["without_construction"] = noConstruction;
            surfaces["openings_without_construction"] = openingsNoConstruction;
            if (constructionUnreadable > 0) surfaces["construction_unreadable"] = constructionUnreadable;
            surfaces["construction_rule"] = "GetConstruction() returned null; SurfaceAir and Shade surfaces are not asked";
            findings["surfaces_without_construction"] = noConstructionCount;
#endif
            result["surfaces"] = surfaces;
            JObject wwr = EnergyReadinessRules.ByOrientation(walls);
            wwr["unmeasured_wall_surfaces"] = unmeasuredWalls;
            wwr["area_rule"] = "gross wall = the surface's polyloops (Polyloop.ComputeArea); an opening = its own polyloops, else width x height";
            result["window_to_wall"] = wwr;
            findings["unmeasured_wall_surfaces"] = unmeasuredWalls;
            result["findings"] = findings;
            return CommandResult.Ok(result);
        }

        private static JObject SurfaceJson(Document doc, EnergyAnalysisSurface s, string type)
        {
            Element origin = ByUniqueId(doc, ReadText(() => s.CADObjectUniqueId));
            return new JObject
            {
                ["surface"] = ReadText(() => s.SurfaceName) ?? ReadText(() => s.SurfaceId),
                ["type"] = type,
                ["element_id"] = origin == null ? JValue.CreateNull() : (JToken)Rid.Value(origin.Id),
                ["originating"] = ReadText(() => s.OriginatingElementDescription)
            };
        }

        /// <summary>Sum of a surface's or opening's polyloop areas in square feet; -1 when there is none to measure.</summary>
        private static double LoopArea(Func<IList<Polyloop>> loops)
        {
            try
            {
                IList<Polyloop> list = loops();
                if (list == null || list.Count == 0) return -1;
                double sum = 0;
                foreach (Polyloop p in list) sum += Math.Abs(p.ComputeArea());
                return sum;
            }
            catch { return -1; }
        }

        private static Element ByUniqueId(Document doc, string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId)) return null;
            try { return doc.GetElement(uniqueId); } catch { return null; }
        }

        private static string ReadText(Func<string> read)
        {
            try { return read(); } catch { return null; }
        }
    }
}
