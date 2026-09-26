// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// Maintenance / access clearance zones for equipment: the same judgement the door
// clear zone (SpatialCoherence.cs / SpatialCoherenceRules.Clearance) makes for a
// door's swing, generalised to any category a caller declares - a panelboard's
// front working space, an AHU's service access, a valve's overhead clearance.
//
// Org-neutral by construction: no catalogue of real equipment or real clearance
// distances is compiled in here. A RULE is caller data - {category, an optional
// family/type name match, which face(s), how deep, how much wider than the
// equipment itself, how tall} - exactly like every other Horizun command that
// needs an organisation's standard takes it as an argument (see AGENTS.md).
//
// This file is the PURE half - no Revit API - so it is unit-tested directly:
// parsing and matching rules, projecting an instance's own bounding-box corners
// onto its FACING/HAND axes (so a rotated instance still gets a front/back/
// left/right in ITS OWN frame), building the zone footprint for each face
// choice, and classifying what invades it. SpatialCoherence.cs turns a footprint
// into an actual Solid and finds what intersects it in Revit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class ClearanceZoneRules
    {
        public const double MmPerFt = 304.8;
        public static double FeetFromMm(double mm) => mm / MmPerFt;

        /// <summary>Same order as the door clear zone's 2 m: a person's working height when a rule omits height_mm.</summary>
        public const double DefaultHeightMm = 2000.0;

        public static readonly HashSet<string> Faces = new HashSet<string>(StringComparer.Ordinal) { "front", "all", "top" };

        public sealed class Rule
        {
            public string Category;
            /// <summary>Case-insensitive substring match against the instance's Family Name. Null: any family.</summary>
            public string FamilyContains;
            /// <summary>Case-insensitive substring match against the instance's Type Name. Null: any type.</summary>
            public string TypeContains;
            public string Face = "front";
            public double DepthMm;
            public double WidthExtraMm;
            public double HeightMm;
        }

        /// <summary>
        /// Parses a clearance_rules JSON array (the horizun_verify_changes argument, or a
        /// project file/project-context.json block read for the automatic after-write
        /// check). Every malformed entry is reported by index and skipped - never guessed,
        /// never silently dropped without saying why.
        /// </summary>
        public static List<Rule> Parse(JArray raw, List<string> errors)
        {
            var rules = new List<Rule>();
            if (raw == null) return rules;
            errors = errors ?? new List<string>();
            for (int i = 0; i < raw.Count; i++)
            {
                if (!(raw[i] is JObject o)) { errors.Add("clearance_rules[" + i + "] must be an object."); continue; }
                string category = o.Value<string>("category");
                if (string.IsNullOrWhiteSpace(category) || !category.StartsWith("OST_", StringComparison.Ordinal))
                { errors.Add("clearance_rules[" + i + "].category must be a BuiltInCategory token such as OST_ElectricalEquipment."); continue; }
                string face = o.Value<string>("face") ?? "front";
                if (!Faces.Contains(face)) { errors.Add("clearance_rules[" + i + "].face must be 'front', 'all' or 'top'."); continue; }
                double? depth = o["depth_mm"]?.Value<double>();
                if (!depth.HasValue || !(depth.Value > 0))
                { errors.Add("clearance_rules[" + i + "].depth_mm must be a positive number of millimetres."); continue; }
                double widthExtra = o["width_extra_mm"]?.Value<double>() ?? 0;
                if (widthExtra < 0) { errors.Add("clearance_rules[" + i + "].width_extra_mm must not be negative."); continue; }
                double height = o["height_mm"]?.Value<double>() ?? DefaultHeightMm;
                if (!(height > 0)) { errors.Add("clearance_rules[" + i + "].height_mm must be a positive number of millimetres."); continue; }
                rules.Add(new Rule
                {
                    Category = category,
                    FamilyContains = o.Value<string>("family_contains"),
                    TypeContains = o.Value<string>("type_contains"),
                    Face = face,
                    DepthMm = depth.Value,
                    WidthExtraMm = widthExtra,
                    HeightMm = height
                });
            }
            return rules;
        }

        public static bool Matches(Rule rule, string category, string familyName, string typeName)
        {
            if (rule == null || category == null || !string.Equals(category, rule.Category, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(rule.FamilyContains) &&
                (familyName == null || familyName.IndexOf(rule.FamilyContains, StringComparison.OrdinalIgnoreCase) < 0)) return false;
            if (!string.IsNullOrEmpty(rule.TypeContains) &&
                (typeName == null || typeName.IndexOf(rule.TypeContains, StringComparison.OrdinalIgnoreCase) < 0)) return false;
            return true;
        }

        /// <summary>The first rule (in declaration order) an instance's category/family/type satisfies, or null.</summary>
        public static Rule FirstMatch(IList<Rule> rules, string category, string familyName, string typeName)
        {
            if (rules == null) return null;
            for (int i = 0; i < rules.Count; i++)
                if (Matches(rules[i], category, familyName, typeName)) return rules[i];
            return null;
        }

        // ---- pure geometry ------------------------------------------------------------

        /// <summary>
        /// An instance's own bounding-box corners projected onto its FACING/HAND axes
        /// (both horizontal, as Revit reports FamilyInstance.FacingOrientation/
        /// HandOrientation), relative to its origin. F grows along facing, H along hand -
        /// so MaxF is the front face, MinF the back, MinH/MaxH the left/right edges,
        /// whatever the instance's rotation in the model. Z is kept absolute (world
        /// elevation), never relative to origin, since a zone's height is measured from
        /// the model's own base, not from wherever the placement point happens to sit.
        /// </summary>
        public readonly struct Extents
        {
            public readonly double MinF, MaxF, MinH, MaxH, MinZ, MaxZ;
            public Extents(double minF, double maxF, double minH, double maxH, double minZ, double maxZ)
            { MinF = minF; MaxF = maxF; MinH = minH; MaxH = maxH; MinZ = minZ; MaxZ = maxZ; }
        }

        public static Extents Project(double originX, double originY,
            double facingX, double facingY, double handX, double handY,
            IEnumerable<(double X, double Y, double Z)> corners)
        {
            double fLen = Math.Sqrt(facingX * facingX + facingY * facingY);
            double hLen = Math.Sqrt(handX * handX + handY * handY);
            if (fLen < 1e-9 || hLen < 1e-9)
                throw new ArgumentException("facing and hand must be non-zero horizontal vectors.");
            double fx = facingX / fLen, fy = facingY / fLen;
            double hx = handX / hLen, hy = handY / hLen;
            double minF = double.MaxValue, maxF = double.MinValue;
            double minH = double.MaxValue, maxH = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;
            bool any = false;
            foreach (var c in corners)
            {
                any = true;
                double dx = c.X - originX, dy = c.Y - originY;
                double f = dx * fx + dy * fy, h = dx * hx + dy * hy;
                if (f < minF) minF = f; if (f > maxF) maxF = f;
                if (h < minH) minH = h; if (h > maxH) maxH = h;
                if (c.Z < minZ) minZ = c.Z; if (c.Z > maxZ) maxZ = c.Z;
            }
            if (!any) throw new ArgumentException("corners must not be empty.");
            return new Extents(minF, maxF, minH, maxH, minZ, maxZ);
        }

        /// <summary>A zone's footprint in the SAME facing/hand/Z coordinates as Extents - one
        /// per side the rule's face choice asks for. SpatialCoherence.cs turns this into a
        /// world-coordinate box by walking origin + facing*F + hand*H.</summary>
        public readonly struct ZoneFootprint
        {
            public readonly string Side;
            public readonly double MinF, MaxF, MinH, MaxH, MinZ, MaxZ;
            public ZoneFootprint(string side, double minF, double maxF, double minH, double maxH, double minZ, double maxZ)
            { Side = side; MinF = minF; MaxF = maxF; MinH = minH; MaxH = maxH; MinZ = minZ; MaxZ = maxZ; }
        }

        public static List<ZoneFootprint> Footprints(Rule rule, Extents ext)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            double depth = FeetFromMm(rule.DepthMm);
            double extra = FeetFromMm(rule.WidthExtraMm);
            double height = FeetFromMm(rule.HeightMm);
            // The zone is at least as wide as the equipment itself, plus width_extra_mm on
            // EACH side - not the equipment's width alone, so a narrow rule still clears a
            // person standing beside it.
            double h0 = ext.MinH - extra, h1 = ext.MaxH + extra;
            double f0 = ext.MinF - extra, f1 = ext.MaxF + extra;
            double baseZ = ext.MinZ, topZ = ext.MaxZ;
            var list = new List<ZoneFootprint>();
            switch (rule.Face)
            {
                case "front":
                    list.Add(new ZoneFootprint("front", ext.MaxF, ext.MaxF + depth, h0, h1, baseZ, baseZ + height));
                    break;
                case "top":
                    list.Add(new ZoneFootprint("top", f0, f1, h0, h1, topZ, topZ + depth));
                    break;
                case "all":
                    list.Add(new ZoneFootprint("front", ext.MaxF, ext.MaxF + depth, h0, h1, baseZ, baseZ + height));
                    list.Add(new ZoneFootprint("back", ext.MinF - depth, ext.MinF, h0, h1, baseZ, baseZ + height));
                    list.Add(new ZoneFootprint("left", f0, f1, ext.MinH - depth, ext.MinH, baseZ, baseZ + height));
                    list.Add(new ZoneFootprint("right", f0, f1, ext.MaxH, ext.MaxH + depth, baseZ, baseZ + height));
                    break;
                default:
                    throw new ArgumentException("rule.Face must be 'front', 'all' or 'top'.");
            }
            return list;
        }

        // ---- classification -------------------------------------------------------------
        // Reuses SpatialCoherenceRules' own Verdict/Kind/Label/Considered and its
        // ClearanceMinFt3 threshold - the SAME 10 L "a wall at the corner just grazes it"
        // calibration the door clear zone uses (SpatialCoherenceRules.cs), never a second
        // scale invented here.

        /// <summary>Never an obstacle to a clearance zone: floors/ceilings/roofs a person
        /// stands on or under, the structural frame overhead, railings.</summary>
        private static readonly HashSet<string> NotAnObstacle = new HashSet<string>(StringComparer.Ordinal)
        {
            "OST_Floors", "OST_Ceilings", "OST_Roofs", "OST_StructuralFraming", "OST_StructuralFoundation",
            "OST_Railings", "OST_StairsRailing"
        };

        /// <summary>Immovable by nature: always an error, never a warning.</summary>
        private static readonly HashSet<string> AlwaysBlocks = new HashSet<string>(StringComparer.Ordinal)
        {
            "OST_Walls", "OST_StructuralColumns", "OST_Columns", "OST_Stairs",
            "OST_CurtainWallPanels", "OST_CurtainWallMullions"
        };

        /// <summary>
        /// obstacleCategory invades equipmentCategory's clearance zone. isHost excuses the
        /// equipment's own host (a panelboard's wall). ruleCategories (every category a
        /// clearance_rules entry names) makes another piece of ruled equipment an error too,
        /// not just a warning - the same field defect a column made for a door. A shared
        /// volume Revit could not measure is kept as a finding, never promoted to clean,
        /// exactly like the door clear zone.
        /// </summary>
        public static SpatialCoherenceRules.Verdict Classify(string equipmentCategory, ISet<string> ruleCategories,
            string obstacleCategory, bool isHost, double? sharedVolumeFt3)
        {
            if (isHost || !SpatialCoherenceRules.Considered(obstacleCategory)) return None();
            if (NotAnObstacle.Contains(obstacleCategory)) return None();
            if (sharedVolumeFt3.HasValue && sharedVolumeFt3.Value < SpatialCoherenceRules.ClearanceMinFt3) return None();
            bool blocks = AlwaysBlocks.Contains(obstacleCategory) || (ruleCategories != null && ruleCategories.Contains(obstacleCategory));
            string eq = SpatialCoherenceRules.Label(equipmentCategory);
            string ob = SpatialCoherenceRules.Label(obstacleCategory);
            return new SpatialCoherenceRules.Verdict
            {
                Kind = SpatialCoherenceRules.Kind.Conflict,
                Severity = blocks ? "error" : "warning",
                Reason = "clearance zone of " + eq + " is invaded by " + ob,
                Suggestion = "keep the clearance zone of the " + eq + " free: move the " + ob + " or the equipment"
            };
        }

        private static SpatialCoherenceRules.Verdict None() => new SpatialCoherenceRules.Verdict { Kind = SpatialCoherenceRules.Kind.None };
    }
}
