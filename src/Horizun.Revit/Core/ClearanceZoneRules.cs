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
        /// clearance-rules.json file read for the automatic after-write check). Every
        /// malformed entry is reported by index and skipped - never guessed, never silently
        /// dropped without saying why, and never thrown: a value of the wrong JSON type
        /// ("900mm", null, an object) is an error for THAT index, like a missing one.
        /// <paramref name="canonicalCategory"/> (the Revit side passes one) maps a token to
        /// the exact BuiltInCategory name CategoryKey reports, or null when this Revit has
        /// no such category - so a typo such as OST_ElectricalEquipments is refused instead
        /// of matching nothing and reading as clean.
        /// </summary>
        public static List<Rule> Parse(JArray raw, List<string> errors, Func<string, string> canonicalCategory = null)
        {
            var rules = new List<Rule>();
            if (raw == null) return rules;
            errors = errors ?? new List<string>();
            for (int i = 0; i < raw.Count; i++)
            {
                string at = "clearance_rules[" + i + "]";
                if (!(raw[i] is JObject o)) { errors.Add(at + " must be an object."); continue; }
                if (!Text(o, "category", out string category) || string.IsNullOrWhiteSpace(category) || !category.StartsWith("OST_", StringComparison.Ordinal))
                { errors.Add(at + ".category must be a BuiltInCategory token such as OST_ElectricalEquipment."); continue; }
                if (canonicalCategory != null)
                {
                    string known = canonicalCategory(category);
                    if (known == null) { errors.Add(at + ".category '" + category + "' is not a BuiltInCategory this Revit knows (check spelling and case)."); continue; }
                    category = known;
                }
                if (!Text(o, "face", out string face)) { errors.Add(at + ".face must be the string 'front', 'all' or 'top'."); continue; }
                face = face ?? "front";
                if (!Faces.Contains(face)) { errors.Add(at + ".face must be 'front', 'all' or 'top'."); continue; }
                if (!Number(o, "depth_mm", out double? depth) || !depth.HasValue || !(depth.Value > 0))
                { errors.Add(at + ".depth_mm must be a positive number of millimetres."); continue; }
                if (!Number(o, "width_extra_mm", out double? widthExtra) || (widthExtra ?? 0) < 0)
                { errors.Add(at + ".width_extra_mm must be a non-negative number of millimetres."); continue; }
                if (!Number(o, "height_mm", out double? height) || (height.HasValue && !(height.Value > 0)))
                { errors.Add(at + ".height_mm must be a positive number of millimetres."); continue; }
                if (!Text(o, "family_contains", out string familyContains)) { errors.Add(at + ".family_contains must be a string."); continue; }
                if (!Text(o, "type_contains", out string typeContains)) { errors.Add(at + ".type_contains must be a string."); continue; }
                rules.Add(new Rule
                {
                    Category = category,
                    FamilyContains = familyContains,
                    TypeContains = typeContains,
                    Face = face,
                    DepthMm = depth.Value,
                    WidthExtraMm = widthExtra ?? 0,
                    HeightMm = height ?? DefaultHeightMm
                });
            }
            return rules;
        }

        /// <summary>Absent or JSON null: true, value null. A JSON string: true. Anything else: false.</summary>
        private static bool Text(JObject o, string key, out string value)
        {
            value = null;
            JToken t = o[key];
            if (t == null || t.Type == JTokenType.Null) return true;
            if (t.Type != JTokenType.String) return false;
            value = (string)t;
            return true;
        }

        /// <summary>Absent or JSON null: true, value null. A finite JSON number: true. Anything
        /// else - a string such as "900mm", an object, NaN - false, never an exception.</summary>
        private static bool Number(JObject o, string key, out double? value)
        {
            value = null;
            JToken t = o[key];
            if (t == null || t.Type == JTokenType.Null) return true;
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float) return false;
            double d;
            try { d = Convert.ToDouble(((JValue)t).Value, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return false; }
            if (double.IsNaN(d) || double.IsInfinity(d)) return false;
            value = d;
            return true;
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
        /// The horizontal frame a zone is built in: F (the front the instance looks out of)
        /// and H = F x up (Revit's own hand for an unrotated instance: F (0,1) gives H (1,0)).
        /// A WORK-PLANE/FACE-BASED instance looks out along its transform's Z - MEASURED
        /// (CadSplitDependents.cs, QueryModelCommand's facing note): its FacingOrientation
        /// lies IN the host face, so on a wall face it is vertical and useless as a front,
        /// and rotated in the face it runs ALONG the wall. So the candidates are tried in
        /// order - transform Z first for a work-plane-based instance, FacingOrientation
        /// first otherwise - and the first one with a real horizontal part wins (a face-
        /// based unit on a FLOOR face has a vertical Z but a horizontal facing: its own
        /// front). When neither is horizontal a front is undefined: false with the reason,
        /// never a guessed axis - except for a 'top' rule (needsFront false), whose box
        /// only needs SOME horizontal frame: the hand, else the world axes.
        /// </summary>
        public static bool FrontFrame(bool workPlaneBased, (double X, double Y, double Z) facing, (double X, double Y, double Z) transformZ,
            (double X, double Y, double Z) hand, bool needsFront, out double fx, out double fy, out double hx, out double hy, out string why)
        {
            fx = fy = hx = hy = 0; why = null;
            var order = workPlaneBased ? new[] { transformZ, facing } : new[] { facing, transformZ };
            foreach (var v in order)
            {
                if (!Horizontal(v, out double x, out double y)) continue;
                fx = x; fy = y; hx = fy; hy = -fx;
                return true;
            }
            if (!needsFront)
            {
                if (Horizontal(hand, out double x, out double y)) { hx = x; hy = y; fx = -hy; fy = hx; }
                else { fx = 0; fy = 1; hx = 1; hy = 0; }
                return true;
            }
            why = workPlaneBased
                ? "it is hosted on a horizontal face or work plane and its family front also points up or down: a front or side zone has no direction to go"
                : "its facing points up or down: a front or side zone has no direction to go";
            return false;
        }

        /// <summary>The unit horizontal part of v, when v is not mostly vertical (|z| under half its length).</summary>
        private static bool Horizontal((double X, double Y, double Z) v, out double x, out double y)
        {
            x = y = 0;
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            if (len < 1e-9 || Math.Abs(v.Z) / len >= 0.5) return false;
            double h = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            x = v.X / h; y = v.Y / h;
            return true;
        }

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

        /// <param name="floorZ">The elevation of the floor the equipment is served from (its
        /// level), when known. The working-space sides start THERE, not at the equipment's
        /// underside: a panel mounted 1.2 m up must still see a 900 mm cabinet standing in
        /// front of it. They reach at least the equipment's own top.</param>
        public static List<ZoneFootprint> Footprints(Rule rule, Extents ext, double? floorZ = null)
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
            double baseZ = floorZ.HasValue && floorZ.Value < ext.MinZ ? floorZ.Value : ext.MinZ, topZ = ext.MaxZ;
            double workTop = Math.Max(topZ, baseZ + height);
            var list = new List<ZoneFootprint>();
            switch (rule.Face)
            {
                case "front":
                    list.Add(new ZoneFootprint("front", ext.MaxF, ext.MaxF + depth, h0, h1, baseZ, workTop));
                    break;
                case "top":
                    list.Add(new ZoneFootprint("top", f0, f1, h0, h1, topZ, topZ + depth));
                    break;
                case "all":
                    list.Add(new ZoneFootprint("front", ext.MaxF, ext.MaxF + depth, h0, h1, baseZ, workTop));
                    list.Add(new ZoneFootprint("back", ext.MinF - depth, ext.MinF, h0, h1, baseZ, workTop));
                    list.Add(new ZoneFootprint("left", f0, f1, ext.MinH - depth, ext.MinH, baseZ, workTop));
                    list.Add(new ZoneFootprint("right", f0, f1, ext.MaxH, ext.MaxH + depth, baseZ, workTop));
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
        /// stands on or under, the structural frame overhead, railings, and doors/windows -
        /// an opening in the zone is the access route itself, the same exclusion the door
        /// clear zone makes (SpatialCoherenceRules.Clearance).</summary>
        private static readonly HashSet<string> NotAnObstacle = new HashSet<string>(StringComparer.Ordinal)
        {
            "OST_Floors", "OST_Ceilings", "OST_Roofs", "OST_StructuralFraming", "OST_StructuralFoundation",
            "OST_Railings", "OST_StairsRailing", "OST_Doors", "OST_Windows"
        };

        /// <summary>Blocks the zone outright: always an error, never a warning. The door
        /// clear zone's immovable set (walls, columns, stairs, curtain panels) plus other
        /// EQUIPMENT - a cabinet or unit standing in front of a panel is the field defect
        /// the rule exists to catch, not a piece of furniture someone can push aside.</summary>
        private static readonly HashSet<string> AlwaysBlocks = new HashSet<string>(StringComparer.Ordinal)
        {
            "OST_Walls", "OST_StructuralColumns", "OST_Columns", "OST_Stairs",
            "OST_CurtainWallPanels", "OST_CurtainWallMullions",
            "OST_ElectricalEquipment", "OST_MechanicalEquipment", "OST_SpecialityEquipment"
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
