// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_plan_mep operation=system_analysis. READ-ONLY.
//
// Reads the critical path of duct and pipe systems AS REVIT COMPUTED IT:
// MEPSystem.GetCriticalPathSectionNumbers in flow order, then per MEPSection
// the flow, velocity, pressure loss and friction, converted through UnitUtils.
// This bridge computes no hydraulics of its own and carries no limits of its
// own: the caller passes them, and sections beyond them are listed.
//
// THE CALCULATION LEVEL IS READ FIRST. A system type set to None or Performance
// still publishes sections whose numbers read as zero; judged against a limit
// they would pass. Such systems are reported "not_calculated" and their numbers
// are not read at all (Core/AnalysisReadRules.cs decides what each level may
// claim). A calculated system with no critical path is named, never "ok".
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class PlanMepCommand
    {
        private const int MaxAnalysedSystems = 100;

        private static CommandResult SystemAnalysis(Document doc, JObject request)
        {
            string limitError = AnalysisReadRules.ParseLimits(request["limits"], out Dictionary<string, double> limits);
            if (limitError != null) return CommandResult.Fail(limitError);

            var systems = new List<MEPSystem>();
            if (request["element_ids"] is JArray idsToken && idsToken.Count > 0)
            {
                foreach (JToken token in idsToken)
                {
                    long id = token.Value<long?>() ?? -1;
                    Element element = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    if (!(element is MechanicalSystem) && !(element is PipingSystem))
                        return CommandResult.Fail("element_ids: " + id + (element == null ? " does not resolve" :
                            " is a " + element.GetType().Name + ", not a MechanicalSystem or PipingSystem") +
                            ". system_analysis reads systems; nothing was read.");
                    systems.Add((MEPSystem)element);
                }
            }
            else
            {
                string kind = (request.Value<string>("kind") ?? "").ToLowerInvariant();
                if (kind != "" && kind != "pipe" && kind != "duct")
                    return CommandResult.Fail("kind must be pipe or duct (or omitted for both).");
                string classificationName = request.Value<string>("classification");
                MEPSystemClassification? classification = null;
                if (!string.IsNullOrWhiteSpace(classificationName))
                {
                    if (!Enum.TryParse(classificationName.Trim(), true, out MEPSystemClassification parsed) ||
                        char.IsDigit(classificationName.Trim()[0]))
                        return CommandResult.Fail("classification '" + classificationName + "' is not a " +
                            "MEPSystemClassification name (e.g. SupplyAir, ReturnAir, DomesticColdWater).");
                    classification = parsed;
                }
                var collected = new List<MEPSystem>();
                if (kind != "pipe")
                    collected.AddRange(new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystem)).Cast<MEPSystem>());
                if (kind != "duct")
                    collected.AddRange(new FilteredElementCollector(doc).OfClass(typeof(PipingSystem)).Cast<MEPSystem>());
                foreach (MEPSystem system in collected)
                {
                    if (classification.HasValue)
                    {
                        MEPSystemClassification? actual = null;
                        try { actual = (doc.GetElement(system.GetTypeId()) as MEPSystemType)?.SystemClassification; }
                        catch { }
                        // An unreadable classification is not a match; it is not silently a non-match
                        // either - the system is kept so its row can say what could not be read.
                        if (actual.HasValue && actual.Value != classification.Value) continue;
                    }
                    systems.Add(system);
                }
            }
            systems = systems.GroupBy(s => Rid.Value(s.Id)).Select(g => g.First())
                             .OrderBy(s => Rid.Value(s.Id)).ToList();
            if (systems.Count > MaxAnalysedSystems)
                return CommandResult.Fail("system_analysis matched " + systems.Count + " systems; the bound is " +
                    MaxAnalysedSystems + " per call. Name them with element_ids or narrow with kind/classification. " +
                    "Nothing was read.");

            var rows = new JArray();
            var reasons = new JArray();
            int measured = 0, notMeasured = 0, beyond = 0;
            var words = new List<string>();
            foreach (MEPSystem system in systems)
            {
                JObject row = AnalyseSystem(doc, system, limits, out string coverage, out bool breached);
                rows.Add(row);
                words.Add(coverage);
                if (breached) beyond++;
                if (coverage == StructuralCoverage.Complete) measured++;
                else
                {
                    notMeasured++;
                    reasons.Add(StructuralCoverage.Reason("system " + Rid.Value(system.Id),
                        (string)row["verdict"] + ": " + (string)row["verdict_means"]));
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["operation"] = "system_analysis",
                ["systems"] = rows,
                ["system_count"] = rows.Count,
                ["systems_beyond_limits"] = beyond,
                ["limits"] = JObject.FromObject(limits),
                ["units"] = new JObject
                {
                    ["flow"] = "l/s", ["velocity"] = "m/s", ["pressure_loss"] = "Pa",
                    ["friction"] = "Pa/m", ["length"] = "mm"
                },
                ["coverage"] = StructuralCoverage.Declare(
                    words.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Weakest(words),
                    measured, notMeasured, reasons),
                ["note"] = "Numbers are Revit's own MEPSection results along GetCriticalPathSectionNumbers, in " +
                           "flow order. This bridge calculates nothing: a system whose type calculates None, " +
                           "Performance or Volume is not_calculated and was not judged, and a limit whose value " +
                           "the calculation level does not claim is listed as unmeasured, never as a pass."
            });
        }

        private static JObject AnalyseSystem(Document doc, MEPSystem system, IDictionary<string, double> limits,
                                             out string coverage, out bool breached)
        {
            breached = false;
            var type = doc.GetElement(system.GetTypeId()) as MEPSystemType;
            string level = null, classification = null;
            try { level = type?.CalculationLevel.ToString(); } catch { }
            try { classification = type?.SystemClassification.ToString(); } catch { }
            string status = AnalysisReadRules.CalculationStatus(level);
            bool? wellConnected = null;
            try
            {
                if (system is MechanicalSystem ms) wellConnected = ms.IsWellConnected;
                else if (system is PipingSystem ps) wellConnected = ps.IsWellConnected;
            }
            catch { }

            var row = new JObject
            {
                ["id"] = Rid.Value(system.Id),
                ["name"] = SafeString(() => system.Name),
                ["class"] = system.GetType().Name,
                ["classification"] = classification,
                ["system_type"] = type == null ? null : new JObject
                {
                    ["id"] = Rid.Value(type.Id), ["name"] = SafeString(() => type.Name)
                },
                ["calculation_level"] = level,
                ["calculation_status"] = status,
                ["is_well_connected"] = wellConnected
            };

            if (status == AnalysisReadRules.NotCalculated || status == AnalysisReadRules.Unreadable)
            {
                coverage = status == AnalysisReadRules.Unreadable ? StructuralCoverage.Unreadable
                                                                  : StructuralCoverage.NotApplicable;
                row["verdict"] = status;
                row["verdict_means"] = status == AnalysisReadRules.Unreadable
                    ? "the system type's calculation level could not be read, so no number was judged."
                    : "the system type calculates " + level + ": Revit computed no flow or pressure to read, " +
                      "so nothing was judged. This is not 'ok'.";
                return row;
            }

            IList<int> numbers;
            try { numbers = system.GetCriticalPathSectionNumbers() ?? new List<int>(); }
            catch (Exception ex)
            {
                coverage = StructuralCoverage.Unreadable;
                row["verdict"] = "critical_path_unreadable";
                row["verdict_means"] = "GetCriticalPathSectionNumbers threw: " + ex.Message;
                return row;
            }
            if (numbers.Count == 0)
            {
                coverage = StructuralCoverage.Unreadable;
                row["verdict"] = "no_critical_path";
                row["verdict_means"] = "Revit returned no critical path for a calculated system - usually no " +
                    "base equipment or a system that is not well connected. Nothing was judged.";
                return row;
            }

            var sections = new JArray();
            var unmeasured = new List<string>();
            double lossSum = 0;
            int lossCounted = 0, unreadableSections = 0;
            foreach (int number in numbers)
            {
                MEPSection section = null;
                try { section = system.GetSectionByNumber(number); } catch { }
                if (section == null)
                {
                    unreadableSections++;
                    sections.Add(new JObject { ["number"] = number, ["readable"] = false });
                    foreach (string key in limits.Keys) unmeasured.Add(number + "#" + key);
                    continue;
                }
                var reading = new AnalysisReadRules.SectionReading { Number = number };
                if (AnalysisReadRules.FlowClaimed(status))
                    reading.VelocityMs = InUnits(() => section.Velocity, UnitTypeId.MetersPerSecond);
                if (AnalysisReadRules.PressureClaimed(status))
                {
                    reading.PressureLossPa = InUnits(() => section.TotalPressureLoss, UnitTypeId.Pascals);
                    reading.FrictionPaPerM = InUnits(() => section.Friction, UnitTypeId.PascalsPerMeter);
                }
                double? flow = AnalysisReadRules.FlowClaimed(status)
                    ? InUnits(() => section.Flow, UnitTypeId.LitersPerSecond) : null;
                double? length = null;
                try { length = section.TotalCurveLength * 304.8; } catch { }
                if (reading.PressureLossPa.HasValue) { lossSum += reading.PressureLossPa.Value; lossCounted++; }

                JArray breaches = AnalysisReadRules.Breaches(reading, status, limits, unmeasured);
                var sectionRow = new JObject
                {
                    ["number"] = number,
                    ["flow_l_s"] = RoundOrNull(flow),
                    ["velocity_m_s"] = RoundOrNull(reading.VelocityMs),
                    ["pressure_loss_pa"] = RoundOrNull(reading.PressureLossPa),
                    ["friction_pa_per_m"] = RoundOrNull(reading.FrictionPaPerM),
                    ["curve_length_mm"] = RoundOrNull(length)
                };
                if (breaches.Count > 0)
                {
                    breached = true;
                    sectionRow["beyond_limits"] = breaches;
                    try
                    {
                        sectionRow["element_ids"] = new JArray(section.GetElementIds().Take(50)
                            .Select(Rid.Value).Cast<object>().ToArray());
                    }
                    catch { sectionRow["element_ids"] = null; }
                }
                sections.Add(sectionRow);
            }

            row["critical_path"] = sections;
            row["critical_path_sections"] = numbers.Count;
            row["critical_path_pressure_loss_pa"] = AnalysisReadRules.PressureClaimed(status) && lossCounted == numbers.Count
                ? (JToken)Math.Round(lossSum, 3) : JValue.CreateNull();
            row["critical_path_pressure_loss_counted"] = lossCounted;
            row["unmeasured_limits"] = new JArray(unmeasured.Cast<object>().ToArray());
            if (breached)
            {
                row["verdict"] = "beyond_limits";
                row["verdict_means"] = "at least one critical-path section exceeds a limit you gave.";
            }
            else if (limits.Count == 0)
            {
                row["verdict"] = "no_limits_given";
                row["verdict_means"] = "numbers read; nothing judged because no limits were given.";
            }
            else if (unmeasured.Count > 0)
            {
                row["verdict"] = "limits_partly_unmeasured";
                row["verdict_means"] = "no measured value exceeds a limit, but the listed limits were not " +
                    "measured, so this is not a pass.";
            }
            else
            {
                row["verdict"] = "within_limits";
                row["verdict_means"] = "every limit you gave was measured on every critical-path section.";
            }
            coverage = unreadableSections == 0 && unmeasured.Count == 0 && status == AnalysisReadRules.Calculated
                ? StructuralCoverage.Complete : StructuralCoverage.Partial;
            if (status == AnalysisReadRules.FlowOnly && limits.Count == 0 && unreadableSections == 0)
                coverage = StructuralCoverage.Partial; // pressure was never calculated for this system
            return row;
        }

        private static double? InUnits(Func<double> read, ForgeTypeId unit)
        {
            try { return UnitUtils.ConvertFromInternalUnits(read(), unit); }
            catch { return null; }
        }

        private static JToken RoundOrNull(double? v) =>
            v.HasValue ? (JToken)Math.Round(v.Value, 3) : JValue.CreateNull();

        private static string SafeString(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }
    }
}
