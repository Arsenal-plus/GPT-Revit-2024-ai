// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_code_check operation=energy_readiness. Original Horizun code.
//
// READ-ONLY IN EFFECT. Revit's energy analytical model is built inside a transaction that
// is ALWAYS rolled back - the same build the gbXML export uses (EnergyModelBuild) - read
// while it exists, and discarded by the rollback, which also restores any energy model
// the document already had. Nothing is committed, so nothing needs re-reading afterwards.
//
// WHAT IT REPORTS
//   spaces          every room and MEP space of the energy settings' ProjectPhase (others
//                   are counted as out_of_energy_phase, never judged: the model is built for
//                   that phase only): enclosed, placed but NOT ENCLOSED (Revit
//                   reports zero area for a not-enclosed and for a redundant one alike, so
//                   the rule says both), or not placed (counted). An enclosed one that the
//                   energy model did not turn into an analytical space is named too, matched
//                   by the analytical space's CADObjectUniqueId; when no analytical space
//                   resolves to an element, that match is reported unavailable - never as
//                   "every room is missing".
//   surfaces        analytical surfaces by type, and those WITHOUT A CONSTRUCTION. That
//                   needs EnergyAnalysisSurface.GetConstruction, which RevitAPI.xml gives
//                   "since 2024", on a model of tier Final (the only tier that computes
//                   constructions): in Revit 2023, or when model.Tier reads back below Final,
//                   it is reported NOT MEASURABLE by name, never as zero. SurfaceAir (virtual
//                   boundary) and Shade surfaces are not asked.
//                   Types are gbXML's (EnergyAnalysisSurface.Type, in every year; SurfaceType
//                   is obsolete in 2027).
//   window_to_wall  per orientation (Core/EnergyReadinessRules: sectors, azimuth from the
//                   outward normal, gross wall area) after TransformModel, which the API
//                   documents as applying the document's shared coordinates and TRUE NORTH.
//                   That is READ, not assumed: one exterior wall's normal is taken before and
//                   after it, and azimuth_basis says true_north only when it turned by the
//                   project angle (or the angle is 0), project_north when it did not turn or
//                   TransformModel threw, and unverified otherwise.
//
// Nothing here says compliant or passes: the counts are measurements the caller judges.
// NOT MEASURED YET (energy-readiness.probes.ps1): a live project whose true north differs
// from project north, where azimuth_basis would show whether TransformModel turns Normal.
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
            // Only the energy settings' phase is judged - the model is built for that phase alone -
            // and from their export category only (rooms or spaces).
            EnergyModelBuild.Scope(doc, out BuiltInCategory? exportCategory, out ElementId energyPhase);
            string exportLabel = exportCategory == BuiltInCategory.OST_Rooms ? "room" : exportCategory == BuiltInCategory.OST_MEPSpaces ? "space" : null;
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
                        State = s.Location == null ? "unplaced" : !EnergyModelBuild.InPhase(s, energyPhase) ? "out_of_energy_phase"
                              : area > 1e-9 ? "enclosed" : "not_enclosed"
                    });
                }
            List<SpatialRow> notEnclosed = spatial.Where(r => r.State == "not_enclosed").ToList();
            List<SpatialRow> enclosed = spatial.Where(r => r.State == "enclosed").ToList();
            int otherPhase = spatial.Count(r => r.State == "out_of_energy_phase");
            // What the model can hold: the export category's enclosed rows (both when it is unreadable).
            int modelable = enclosed.Count(r => exportLabel == null || r.Category == exportLabel);
            string scope = EnergyModelBuild.ScopeText(doc, exportCategory, energyPhase);
            var spaces = new JObject
            {
                ["rooms"] = spatial.Count(r => r.Category == "room"),
                ["spaces"] = spatial.Count(r => r.Category == "space"),
                ["enclosed"] = enclosed.Count,
                ["unplaced"] = spatial.Count(r => r.State == "unplaced"),
                ["not_enclosed_count"] = notEnclosed.Count,
                ["not_enclosed"] = new JArray(notEnclosed.Take(max).Select(r => r.ToJson())),
                ["not_enclosed_rule"] = "placed with zero area: Revit reports a not-enclosed and a redundant room or space alike",
                ["energy_scope"] = scope,
                ["out_of_energy_phase_count"] = otherPhase,
                ["out_of_energy_phase_rule"] = "placed in another phase than the energy settings' ProjectPhase: the model is built for that phase only, so these are not judged"
            };
            var result = new JObject
            {
                ["document"] = doc.Title, ["operation"] = "energy_readiness",
                ["writes"] = "nothing: the energy model is built in a transaction that is always rolled back",
                ["spaces"] = spaces
            };
            if (modelable == 0)
            {
                result["energy_model"] = new JObject { ["built"] = false, ["why"] = "no spaces: no placed, enclosed " + scope + ", so the energy model would be empty" };
                result["surfaces"] = new JObject { ["not_measured"] = "no energy model was built" };
                result["window_to_wall"] = new JObject { ["not_measured"] = "no energy model was built" };
                return CommandResult.Ok(result);
            }
            if (doc.IsReadOnly)
                return CommandResult.FailWithDetail("operation=energy_readiness builds Revit's energy model in a transaction that is rolled back, " +
                    "and this document is read-only, so none can start. Only the room/space states were read.", result);

            var energy = new JObject { ["built"] = false, ["build"] = EnergyModelBuild.Description };
            double? angleDeg = null;
            try
            {
                angleDeg = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Angle * 180.0 / Math.PI;
                energy["project_angle_to_true_north_deg"] = Math.Round(angleDeg.Value, 3);
            }
            catch { }
            bool finalTier = false, rolledBack = false;
            string refSurface = null, transformError = null;
            double refBefore = double.NaN;
            double? refAfter = null;
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
                        energy["tier"] = ReadText(() => model.Tier.ToString());
                        finalTier = EnergyModelBuild.IsFinal(model);
                        energy["tier_final"] = finalTier;
                        energy["export_category"] = ReadText(() => model.ExportCategory.ToString());
                        // One exterior wall's normal before and after TransformModel: whether it
                        // really turned the model to true north is read, not assumed.
                        try
                        {
                            foreach (EnergyAnalysisSurface s0 in model.GetAnalyticalSurfaces())
                            {
                                if (s0.Type != gbXMLSurfaceType.ExteriorWall) continue;
                                XYZ n0 = s0.Normal;
                                double? a0 = EnergyReadinessRules.Azimuth(n0.X, n0.Y);
                                if (a0 == null) continue;
                                refBefore = a0.Value;
                                refSurface = s0.SurfaceId;
                                break;
                            }
                        }
                        catch { refSurface = null; }
                        try { model.TransformModel(); }
                        catch (Exception ex) { transformError = ex.Message; }

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
                            if (refSurface != null && refAfter == null && azimuth != null && ReadText(() => s.SurfaceId) == refSurface) refAfter = azimuth;
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
                finally { if (tx.HasStarted() && !tx.HasEnded()) rolledBack = tx.RollBack() == TransactionStatus.RolledBack; }
            }
            // Measured, not stamped: the rollback is what restores the settings and any main model.
            bool built = energy.Value<bool>("built");
            energy["rolled_back"] = rolledBack;
            if (built) energy["azimuth_basis"] = AzimuthBasis(transformError, angleDeg, refSurface, refBefore, refAfter);
            if (built && !rolledBack)
                failure = (failure == null ? "" : failure + "; ") + "the rollback did not report RolledBack, so the energy model and energy settings may have changed";
            result["energy_model"] = energy;
            if (failure != null)
                return CommandResult.FailWithDetail(failure + (built && !rolledBack ? "." : ". Nothing was written; only the room/space states were read."), result);

            energy["analytical_spaces"] = analyticalSpaces;
            if (unresolvedSpaces > 0) energy["analytical_spaces_unresolved"] = unresolvedSpaces;
            int missingCount = 0, possiblyMissing = 0;
            if (analyticalSpaces > 0 && mapped.Count == 0)
                spaces["not_in_energy_model"] = "unavailable: no analytical space resolves to a room or space by CADObjectUniqueId, so which are missing cannot be told";
            else
            {
                // The model is built from rooms OR spaces: an enclosed element of the other
                // category is not missing. With no analytical space at all, every enclosed one is.
                var used = new HashSet<string>(spatial.Where(r => mapped.Contains(r.Id)).Select(r => r.Category));
                if (exportLabel != null) { used.Clear(); used.Add(exportLabel); }
                var missing = new JArray();
                foreach (SpatialRow r in enclosed.Where(r => (used.Count == 0 || used.Contains(r.Category)) && !mapped.Contains(r.Id)))
                {
                    missingCount++;
                    if (missing.Count < max) missing.Add(r.ToJson());
                }
                if (unresolvedSpaces > 0 && missingCount > 0)
                {
                    // Some analytical spaces resolve to no element and these rows may be among
                    // them: possible, never definite, findings.
                    spaces["possibly_not_in_energy_model_count"] = missingCount;
                    spaces["possibly_not_in_energy_model"] = missing;
                    spaces["may_be_among_unresolved"] = unresolvedSpaces;
                    possiblyMissing = missingCount;
                    missingCount = 0;
                }
                else
                {
                    spaces["not_in_energy_model_count"] = missingCount;
                    spaces["not_in_energy_model"] = missing;
                }
            }

            var surfaces = new JObject { ["analytical_surfaces"] = surfaceCount, ["by_type"] = JObject.FromObject(byType) };
            var findings = new JObject { ["not_enclosed"] = notEnclosed.Count, ["not_in_energy_model"] = missingCount };
            if (possiblyMissing > 0) findings["possibly_not_in_energy_model"] = possiblyMissing;
#if REVIT2023
            surfaces["without_construction"] = "not measurable in Revit 2023: EnergyAnalysisSurface.GetConstruction exists from Revit 2024 (RevitAPI.xml 'since 2024')";
            findings["surfaces_without_construction"] = JValue.CreateNull();
#else
            if (!finalTier)
            {
                // Only tier Final computes constructions (RevitAPI.xml EnergyAnalysisDetailModelTier):
                // below it every surface would read "without construction", which is not a finding.
                surfaces["without_construction"] = "not measurable: the model read back tier " + ((string)energy["tier"] ?? "unreadable") +
                    ", and only tier Final computes constructions";
                findings["surfaces_without_construction"] = JValue.CreateNull();
            }
            else
            {
                surfaces["without_construction_count"] = noConstructionCount;
                surfaces["without_construction"] = noConstruction;
                surfaces["openings_without_construction"] = openingsNoConstruction;
                if (constructionUnreadable > 0) surfaces["construction_unreadable"] = constructionUnreadable;
                surfaces["construction_rule"] = "GetConstruction() returned null on a tier-Final model; SurfaceAir and Shade surfaces are not asked";
                findings["surfaces_without_construction"] = noConstructionCount;
                if (constructionUnreadable > 0) findings["construction_unreadable"] = constructionUnreadable;
            }
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

        /// <summary>What the WWR azimuths are relative to, from one exterior wall's normal read before and after TransformModel.</summary>
        private static string AzimuthBasis(string transformError, double? projectAngleDeg, string surfaceId, double before, double? after)
        {
            if (transformError != null) return "project_north: TransformModel failed (" + transformError + ")";
            double angle = projectAngleDeg.HasValue ? WrapDegrees(projectAngleDeg.Value) : double.NaN;
            if (!double.IsNaN(angle) && Math.Abs(angle) < 0.01) return "true_north: project north is true north here (project angle 0)";
            if (surfaceId == null || after == null || double.IsNaN(before))
                return "unverified: TransformModel ran, but no exterior wall normal could be compared before and after it";
            double turned = WrapDegrees(after.Value - before);
            string seen = "exterior wall " + surfaceId + " turned " + turned.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
                " deg; project angle " + (double.IsNaN(angle) ? "unreadable" : angle.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " deg");
            if (!double.IsNaN(angle) && Math.Abs(Math.Abs(turned) - Math.Abs(angle)) < 0.5) return "true_north: " + seen;
            if (Math.Abs(turned) < 0.01) return "project_north: TransformModel left the normals unturned; " + seen;
            return "unverified: " + seen;
        }

        private static double WrapDegrees(double d)
        {
            d %= 360.0;
            if (d > 180) d -= 360; else if (d <= -180) d += 360;
            return d;
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
