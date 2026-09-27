// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// USER-DEFINED PROPERTY SETS: the file that tells Revit's IFC exporter which
// sets to write, and the check that they are IN the file it wrote.
//
// ONE FILE, TWO USES. The mapping handed to horizun_deliver_ifc is the exporter's
// own "user defined property sets" text file - the format the open-source Revit
// IFC exporter reads when ExportUserDefinedPsets is on:
//
//   # comment
//   PropertySet:<TAB><Pset name><TAB>I|T<TAB><IfcClass>[,<IfcClass>...]
//   <TAB><Property name><TAB><Data type>[<TAB><Revit parameter name>]
//
// It is passed to the exporter unchanged, and then read here as the list of
// what the produced file must carry. There is no second, Horizun-shaped mapping
// that could drift from the one the exporter actually used.
//
// WHY PARSE STRICTLY. The exporter splits on TAB. A file written with spaces
// exports without the sets and without complaint, and the only symptom is an IFC
// that is missing them - so a line this reader cannot split the way the exporter
// does is refused BY LINE NUMBER before anything is exported.
//
// WHAT THE VERIFICATION CAN AND CANNOT SAY. The exporter writes a property only
// when the Revit parameter behind it has a value, and a set only when at least
// one of its properties was written. A property missing from an element is
// therefore either an empty parameter or a mapping the exporter did not apply;
// the file cannot tell those apart and the report does not pretend to. It
// reports coverage - n of m expected entities carry the property - and names
// the missing ones by GlobalId, which is what someone fixing it needs.
//
// Revit-free: text in, IFC entities in, JSON out.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class PsetMappingProperty
    {
        public string Name;
        public string DataType;
        public string RevitParameter;
        public int Line;
    }

    public sealed class PsetMappingSet
    {
        public string Name;
        /// <summary>'I' (instance) or 'T' (type), as the exporter reads the first letter.</summary>
        public char Level;
        public readonly List<string> Entities = new List<string>();
        public readonly List<PsetMappingProperty> Properties = new List<PsetMappingProperty>();
        public int Line;
    }

    public sealed class PsetMappingFile
    {
        public readonly List<PsetMappingSet> Sets = new List<PsetMappingSet>();

        /// <summary>Non-fatal observations: an unfamiliar data type, a set with no property.</summary>
        public readonly List<string> Warnings = new List<string>();

        public int PropertyCount => Sets.Sum(s => s.Properties.Count);

        public JObject SummaryJson() => new JObject
        {
            ["property_sets"] = Sets.Count,
            ["properties"] = PropertyCount,
            ["sets"] = new JArray(Sets.Select(s => new JObject
            {
                ["name"] = s.Name,
                ["level"] = s.Level == 'T' ? "type" : "instance",
                ["entities"] = new JArray(s.Entities),
                ["properties"] = new JArray(s.Properties.Select(p => p.Name))
            })),
            ["warnings"] = new JArray(Warnings)
        };
    }

    public static class PsetMapping
    {
        /// <summary>
        /// The data types the exporter's own template file lists. An unfamiliar one is a
        /// WARNING, not a refusal: this list is read from documentation, and refusing a
        /// type a newer exporter supports would block a valid file on this build's ignorance.
        /// </summary>
        public static readonly HashSet<string> KnownDataTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Area", "Boolean", "ClassificationReference", "ColorTemperature", "Count", "Currency",
            "ElectricalCurrent", "ElectricalEfficacy", "ElectricalVoltage", "Force", "Frequency", "Identifier",
            "Illuminance", "Integer", "Label", "Length", "Logical", "LuminousFlux", "LuminousIntensity",
            "NormalisedRatio", "PlaneAngle", "PositiveLength", "PositivePlaneAngle", "PositiveRatio", "Power",
            "Pressure", "Ratio", "Real", "Text", "ThermalTransmittance", "ThermodynamicTemperature", "Volume",
            "VolumetricFlowRate"
        };

        public const long MaxBytes = 4L * 1024 * 1024;

        public static PsetMappingFile Read(string path, out string error)
        {
            error = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { error = "no mapping file at '" + path + "'."; return null; }
                if (info.Length > MaxBytes) { error = "the mapping file is larger than 4 MB; that is not a property-set definition."; return null; }
                return Parse(File.ReadAllText(path), out error);
            }
            catch (Exception ex)
            {
                error = "the mapping file '" + path + "' could not be read: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Parse the exporter's format. Returns null with a reason naming the LINE on the
        /// first construction the exporter would not read the way it looks.
        /// </summary>
        public static PsetMappingFile Parse(string text, out string error)
        {
            error = null;
            var file = new PsetMappingFile();
            if (string.IsNullOrWhiteSpace(text)) { error = "the mapping file is empty."; return null; }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            PsetMappingSet current = null;
            var setNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < lines.Length; i++)
            {
                int lineNo = i + 1;
                string raw = lines[i].TrimEnd();
                if (i == 0) raw = raw.TrimStart('﻿');
                string trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;

                if (trimmed.StartsWith("PropertySet:", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = trimmed.Substring("PropertySet:".Length)
                        .Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
                    if (parts.Length < 3)
                    {
                        error = "line " + lineNo + ": a PropertySet line needs three TAB-separated fields - name, I or T, " +
                                "and the IFC classes - and this one has " + parts.Length + ". Revit's exporter splits on " +
                                "TAB; a line written with spaces exports no set at all, silently.";
                        return null;
                    }
                    char level = char.ToUpperInvariant(parts[1][0]);
                    if (level != 'I' && level != 'T')
                    {
                        error = "line " + lineNo + ": the second field must be I (instance) or T (type); it is '" + parts[1] + "'.";
                        return null;
                    }
                    if (!setNames.Add(parts[0]))
                    {
                        error = "line " + lineNo + ": property set '" + parts[0] + "' is defined twice.";
                        return null;
                    }
                    current = new PsetMappingSet { Name = parts[0], Level = level, Line = lineNo };
                    foreach (string entity in parts[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string e = entity.Trim();
                        if (e.Length == 0) continue;
                        if (!e.StartsWith("Ifc", StringComparison.OrdinalIgnoreCase))
                        {
                            error = "line " + lineNo + ": '" + e + "' is not an IFC class name (they start with Ifc).";
                            return null;
                        }
                        current.Entities.Add(e);
                    }
                    if (current.Entities.Count == 0)
                    {
                        error = "line " + lineNo + ": property set '" + current.Name + "' names no IFC class.";
                        return null;
                    }
                    file.Sets.Add(current);
                    continue;
                }

                // A property line belongs to the set above it, and is indented with a TAB.
                if (current == null)
                {
                    error = "line " + lineNo + ": a property appears before any PropertySet line.";
                    return null;
                }
                string[] fields = raw.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries)
                                     .Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
                if (fields.Length < 2)
                {
                    error = "line " + lineNo + ": a property line needs a name and a data type separated by TAB; " +
                            "found '" + trimmed + "'. Revit's exporter splits on TAB.";
                    return null;
                }
                if (current.Properties.Any(p => string.Equals(p.Name, fields[0], StringComparison.Ordinal)))
                {
                    error = "line " + lineNo + ": property '" + fields[0] + "' appears twice in set '" + current.Name + "'.";
                    return null;
                }
                if (!KnownDataTypes.Contains(fields[1]))
                    file.Warnings.Add("line " + lineNo + ": data type '" + fields[1] + "' is not in the exporter's documented " +
                                      "list; if the exporter does not know it either, the property will be missing from the file.");
                current.Properties.Add(new PsetMappingProperty
                {
                    Name = fields[0], DataType = fields[1], RevitParameter = fields.Length > 2 ? fields[2] : null, Line = lineNo
                });
            }

            if (file.Sets.Count == 0) { error = "the mapping file defines no PropertySet."; return null; }
            foreach (PsetMappingSet set in file.Sets.Where(s => s.Properties.Count == 0))
                file.Warnings.Add("property set '" + set.Name + "' (line " + set.Line + ") declares no property; the " +
                                  "exporter writes no empty set, so it can never be found.");
            return file;
        }

        // =====================================================================
        // Verification against the exported file
        // =====================================================================

        public sealed class Row
        {
            public string PropertySet, Property, Entities;
            public int Expected, Carrying;
            public double Coverage => Expected == 0 ? 0 : (double)Carrying / Expected;
            public readonly List<JObject> MissingExamples = new List<JObject>();
            public string Status;
        }

        /// <summary>
        /// What the MODEL says about one declared property, read BEFORE the export - the
        /// half the exported file alone cannot tell apart (see the header comment). Built
        /// Revit-side (a Revit category has to be resolved from the mapping's IFC classes,
        /// which this file does not touch) and combined with a Row here, purely.
        /// </summary>
        public sealed class ModelCensusRow
        {
            public string PropertySet, Property;
            /// <summary>True when none of the property set's IFC classes resolve to a known
            /// Revit category - the model was not read for this property at all.</summary>
            public bool CategoryUnmapped;
            public string UnmappedReason;
            /// <summary>Some, not all, of the set's IFC classes had no known category - Total
            /// undercounts the model's real population for this property.</summary>
            public List<string> PartiallyUnmappedClasses;
            public int Total, HasValue, Empty, ParameterMissing;
            public readonly List<JObject> EmptyExamples = new List<JObject>();
            public readonly List<JObject> ParameterMissingExamples = new List<JObject>();
        }

        /// <summary>
        /// Merges a file Row with what the model showed for the SAME property before the
        /// export, into the four-way classification the exporter's own behaviour cannot
        /// distinguish on its own: EXPORTED (the file carries it - necessarily from an
        /// element that had a value), EMPTY_IN_MODEL (the parameter had no value - not an
        /// exporter fault), NOT_APPLIED (a value existed in the model but fewer file
        /// entities carry the property than the model has values for - the exporter did not
        /// write it), PARAMETER_MISSING (the named parameter does not exist on the element
        /// at all). exported/not_applied are AGGREGATE counts, not element-matched: no IFC
        /// GlobalId correlates a model element to its file entity without recomputing the
        /// exporter's own GUID algorithm, which this bridge does not carry. The arithmetic
        /// holds because the exporter can only ever write a property from an element that
        /// had a value, so carrying &lt;= has_value for the SAME population; a
        /// population_mismatch_note says when the model census and the file's own count of
        /// candidate entities disagree, which is when that assumption is weakest.
        /// </summary>
        public static JArray CombineWithModel(IEnumerable<Row> fileRows, IEnumerable<ModelCensusRow> modelRows)
        {
            var byKey = new Dictionary<string, ModelCensusRow>(StringComparer.Ordinal);
            if (modelRows != null)
                foreach (ModelCensusRow m in modelRows) byKey[m.PropertySet + "\u001f" + m.Property] = m;

            var combined = new JArray();
            foreach (Row row in fileRows)
            {
                var o = new JObject
                {
                    ["property_set"] = row.PropertySet,
                    ["property"] = row.Property,
                    ["entities"] = row.Entities,
                    ["file"] = new JObject
                    {
                        ["expected"] = row.Expected, ["carrying"] = row.Carrying, ["status"] = row.Status
                    }
                };
                ModelCensusRow m;
                if (!byKey.TryGetValue(row.PropertySet + "\u001f" + row.Property, out m))
                {
                    o["model"] = JValue.CreateNull();
                    o["exported"] = JValue.CreateNull(); o["empty_in_model"] = JValue.CreateNull();
                    o["not_applied"] = JValue.CreateNull(); o["parameter_missing"] = JValue.CreateNull();
                    o["classification_reason"] = "no model census was taken for this property; only the exported " +
                                                 "file's coverage above is known.";
                }
                else if (m.CategoryUnmapped)
                {
                    o["model"] = new JObject { ["category_unmapped"] = true, ["reason"] = m.UnmappedReason };
                    o["exported"] = JValue.CreateNull(); o["empty_in_model"] = JValue.CreateNull();
                    o["not_applied"] = JValue.CreateNull(); o["parameter_missing"] = JValue.CreateNull();
                    o["classification_reason"] = "none of this property's IFC classes resolve to a known Revit " +
                                                 "category, so the model was not read for it; only the exported " +
                                                 "file's coverage above is known.";
                }
                else
                {
                    o["model"] = new JObject
                    {
                        ["total"] = m.Total, ["has_value"] = m.HasValue, ["empty"] = m.Empty,
                        ["parameter_missing"] = m.ParameterMissing,
                        ["empty_examples"] = new JArray(m.EmptyExamples),
                        ["parameter_missing_examples"] = new JArray(m.ParameterMissingExamples)
                    };
                    if (m.PartiallyUnmappedClasses != null && m.PartiallyUnmappedClasses.Count > 0)
                        o["model"]["partially_unmapped_classes"] = new JArray(m.PartiallyUnmappedClasses);
                    o["exported"] = row.Carrying;
                    o["empty_in_model"] = m.Empty;
                    o["not_applied"] = Math.Max(0, m.HasValue - row.Carrying);
                    o["parameter_missing"] = m.ParameterMissing;
                    if (m.Total != row.Expected)
                        o["population_mismatch_note"] = "the model census found " + m.Total + " element(s) by " +
                            "Revit category for this row; the exported file's own coverage check found " +
                            row.Expected + " candidate entities. The category match is a heuristic, so exported/" +
                            "not_applied above are aggregate, not element-matched, counts.";
                }
                combined.Add(o);
            }
            return combined;
        }

        /// <summary>
        /// For every declared property: how many of the entities the set is declared for
        /// carry it in the file. An occurrence is credited with its type's sets, exactly as
        /// the IDS evaluator reads them; a TYPE class named in the mapping (IfcWallType) is
        /// checked on its own HasPropertySets. The IFC2x3 "StandardCase" and "ElementedCase"
        /// subclasses count as the class they specialise - other subtypes are not expanded,
        /// and the reply says so.
        /// </summary>
        public static DeliveryGate Verify(PsetMappingFile mapping, IfcStepReader.Document ifc, double minCoverage,
                                          int maxExamples, out List<Row> rows)
        {
            rows = new List<Row>();
            var gate = new DeliveryGate { Name = "pset_mapping", Requested = true };
            if (mapping == null || ifc == null)
            {
                gate.Status = DeliveryGateStatus.NotDecidable;
                gate.Reason = "the mapping or the exported file could not be read.";
                return gate;
            }
            if (minCoverage < 0 || minCoverage > 1) minCoverage = 1;

            IdsIfcEvaluator.Index index = IdsIfcEvaluator.Build(ifc);
            var typeSets = new Dictionary<int, List<Tuple<string, string, string>>>();

            foreach (PsetMappingSet set in mapping.Sets)
            {
                var candidates = new List<IfcEntity>();
                foreach (string cls in set.Entities)
                    candidates.AddRange(EntitiesOf(ifc, cls));
                candidates = candidates.GroupBy(e => e.Id).Select(g => g.First()).OrderBy(e => e.Id).ToList();

                foreach (PsetMappingProperty property in set.Properties)
                {
                    var row = new Row
                    {
                        PropertySet = set.Name, Property = property.Name,
                        Entities = string.Join(",", set.Entities), Expected = candidates.Count
                    };
                    foreach (IfcEntity entity in candidates)
                    {
                        List<Tuple<string, string, string>> sets = SetsOf(ifc, index, entity, typeSets);
                        bool carries = sets.Any(t => string.Equals(t.Item1, set.Name, StringComparison.Ordinal) &&
                                                     string.Equals(t.Item2, property.Name, StringComparison.Ordinal));
                        if (carries) row.Carrying++;
                        else if (row.MissingExamples.Count < maxExamples)
                            row.MissingExamples.Add(new JObject
                            {
                                ["global_id"] = IfcStepReader.Text(entity.At(0)),
                                ["ifc_class"] = entity.Type,
                                ["name"] = IfcStepReader.Text(entity.At(2)),
                                ["entity"] = "#" + entity.Id
                            });
                    }
                    row.Status = row.Expected == 0 ? "no_entities"
                               : row.Coverage + 1e-12 >= minCoverage ? DeliveryGateStatus.Passed : DeliveryGateStatus.Failed;
                    rows.Add(row);
                }
            }

            var decided = rows.Where(r => r.Status != "no_entities").ToList();
            int expected = decided.Sum(r => r.Expected), carrying = decided.Sum(r => r.Carrying);
            int failing = decided.Count(r => r.Status == DeliveryGateStatus.Failed);
            gate.Evidence = new JObject
            {
                ["declared_properties"] = rows.Count,
                ["properties_checked"] = decided.Count,
                ["properties_without_entities"] = rows.Count - decided.Count,
                ["properties_failing"] = failing,
                ["min_coverage"] = minCoverage,
                ["coverage_total"] = new JObject { ["carrying"] = carrying, ["expected"] = expected },
                ["rows"] = new JArray(rows.Select(r => new JObject
                {
                    ["property_set"] = r.PropertySet, ["property"] = r.Property, ["entities"] = r.Entities,
                    ["status"] = r.Status, ["carrying"] = r.Carrying, ["expected"] = r.Expected,
                    ["coverage"] = r.Expected == 0 ? (JToken)JValue.CreateNull()
                                   : Math.Round(r.Coverage, 4).ToString("0.####", CultureInfo.InvariantCulture),
                    ["missing_examples"] = new JArray(r.MissingExamples)
                })),
                ["subtypes"] = "an IFC class counts its IFC2x3 StandardCase/ElementedCase subclass; other subtypes are " +
                               "not expanded - name them in the mapping if the exporter writes them.",
                ["what_missing_means"] = "the exporter writes a property only when its Revit parameter has a value. A " +
                               "missing property is an empty parameter OR a mapping the exporter did not apply; the file " +
                               "cannot tell those apart, and the GlobalIds above are where to look."
            };
            if (decided.Count == 0)
            {
                gate.Status = DeliveryGateStatus.NotDecidable;
                gate.Reason = "none of the IFC classes the mapping names occur in the exported file, so nothing could be " +
                              "checked. That is not a pass.";
            }
            else if (failing > 0)
            {
                gate.Status = DeliveryGateStatus.Failed;
                gate.Reason = failing + " of " + decided.Count + " declared propert(ies) are carried by fewer than " +
                              (minCoverage * 100).ToString("0.##", CultureInfo.InvariantCulture) + "% of the entities " +
                              "they are declared for (" + carrying + " of " + expected + " overall).";
            }
            else
            {
                gate.Status = DeliveryGateStatus.Passed;
                gate.Reason = "every declared property that has entities to land on is carried at or above the required " +
                              "coverage (" + carrying + " of " + expected + " overall)." +
                              (rows.Count > decided.Count ? " " + (rows.Count - decided.Count) + " propert(ies) name classes " +
                              "absent from the file and were not counted either way." : "");
            }
            return gate;
        }

        /// <summary>Entities of an IFC class, plus the two IFC2x3 subclasses Revit writes for it.</summary>
        public static List<IfcEntity> EntitiesOf(IfcStepReader.Document ifc, string ifcClass)
        {
            string upper = (ifcClass ?? "").Trim().ToUpperInvariant();
            var found = new List<IfcEntity>();
            found.AddRange(ifc.Of(upper));
            found.AddRange(ifc.Of(upper + "STANDARDCASE"));
            found.AddRange(ifc.Of(upper + "ELEMENTEDCASE"));
            return found;
        }

        private static bool IsTypeClass(string type)
        {
            string t = (type ?? "").ToUpperInvariant();
            return t.EndsWith("TYPE", StringComparison.Ordinal) || t.EndsWith("STYLE", StringComparison.Ordinal);
        }

        private static List<Tuple<string, string, string>> SetsOf(IfcStepReader.Document ifc, IdsIfcEvaluator.Index index,
            IfcEntity entity, Dictionary<int, List<Tuple<string, string, string>>> typeSets)
        {
            if (IsTypeClass(entity.Type))
            {
                List<Tuple<string, string, string>> own;
                if (!typeSets.TryGetValue(entity.Id, out own))
                {
                    own = new List<Tuple<string, string, string>>();
                    // IfcTypeObject.HasPropertySets is attribute 5 in IFC2x3 and IFC4.
                    foreach (string reference in IfcStepReader.List(entity.At(5)))
                        own.AddRange(IdsIfcEvaluator.ReadDefinition(ifc, ifc.Resolve(reference)));
                    typeSets[entity.Id] = own;
                }
                return own;
            }
            List<Tuple<string, string, string>> sets;
            return index.Properties.TryGetValue(entity.Id, out sets) ? sets : new List<Tuple<string, string, string>>();
        }
    }
}
