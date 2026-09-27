// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_framing's CURTAIN METHOD (spec.wall.method /
// spec.ceiling.method = 'curtain'; the default 'members' is the member method in
// WallFramingRules / CeilingFramingRules and is untouched by this file).
//
// WHY A SECOND METHOD. Some offices do not model light-gauge framing as one
// family instance per stud: they model the partition CORE as a Curtain Wall whose
// type does the layout - the vertical grid at the stud spacing, the vertical
// mullions are the studs, the border mullions are the tracks, the panel is the
// insulation - and a suspended ceiling as flat Sloped Glazing roofs (one per layer:
// mains, furring, perimeter angle as the border mullion) plus vertical curtain walls
// whose mullions are the hanger rods. Every one of those TYPES is the caller's: the
// spec carries type ids only, and no type name, grid spacing or profile is compiled.
//
// THE WALL, in the wall's own frame (x along the carrier from its start, z up from
// its base, millimetres):
//  * SEGMENTS of the curtain type cover [0, Length] minus every opening's span.
//  * A HEADER piece sits above every opening: x = the opening's span, z = [head, top].
//  * A SILL piece sits below every opening whose sill is above the wall base (a
//    window): x = the span, z = [base, sill].
//  * A piece shorter (or lower) than min_segment_mm is not built; it is NAMED in
//    Skipped, never dropped silently. A zero-length piece is simply not a piece.
//  * THE CARRIER keeps its identity and its inserts, because no public API
//    re-hosts a door in another wall: with ONE opening it is trimmed to that
//    opening's span and takes the placeholder type; with NONE it is deleted once
//    the curtain walls exist (replaced_by names them); with SEVERAL the default
//    (multi_opening = 'keep_carrier', the user's decision of 2026-09-26) keeps it
//    full length with the placeholder type, so every door keeps its id, tags and
//    data, and the plan and the result say plainly that the curtain walls overlap
//    it; multi_opening = 'refuse' refuses such a wall and names why.
//
// THE CEILING: one flat footprint roof per layer over the ceiling's boundary, its
// plane at the ceiling's top face + offset_mm, and optional hanger lines parallel
// to the first layer's grid direction at the hanger spacing, clipped to the
// boundary (CeilingFramingRules.GridSegments - the same centred strips the member
// method uses), each one a vertical curtain wall the command sizes by ray-casting
// to the structure above.
//
// VERIFYING A FIXED-DISTANCE GRID (CheckFixedSpacing) is justification-free on
// purpose: every interior spacing equals the type's distance within the
// tolerance, and neither edge bay is wider than one spacing (a wider edge bay
// means a grid line is missing). Other layouts are only counted and reported.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CurtainFramingRoles
    {
        public const string Segment = "curtain_segment", Header = "curtain_header", Sill = "curtain_sill",
            Layer = "curtain_layer", Hanger = "curtain_hanger";
        public static readonly string[] Wall = { Segment, Header, Sill };
        public static readonly string[] Ceiling = { Layer, Hanger };

        /// <summary>What happens to the wall the curtain walls replace.</summary>
        public const string CarrierTrim = "trim", CarrierDelete = "delete", CarrierKeep = "keep";
    }

    public sealed class CurtainWallFramingSpec
    {
        public long CurtainTypeId { get; set; }
        /// <summary>Null: the curtain type (named in the plan's warnings).</summary>
        public long? HeaderTypeId { get; set; }
        public long? SillTypeId { get; set; }
        public long PlaceholderTypeId { get; set; }
        /// <summary>keep_carrier (default) | refuse.</summary>
        public string MultiOpening { get; set; } = MultiOpeningDefault;
        public const string MultiOpeningDefault = "keep_carrier";
        public double MinSegmentMm { get; set; } = 50;

        public IEnumerable<long> TypeIds()
        {
            var ids = new List<long> { CurtainTypeId, PlaceholderTypeId };
            if (HeaderTypeId.HasValue) ids.Add(HeaderTypeId.Value);
            if (SillTypeId.HasValue) ids.Add(SillTypeId.Value);
            return ids.Distinct();
        }

        public CurtainWallInput ToInput(double lengthMm, double heightMm, IEnumerable<WallOpeningSpan> openingsMm)
            => new CurtainWallInput
            {
                Length = lengthMm,
                Height = heightMm,
                CurtainTypeKey = WallFramingSpec.Key(CurtainTypeId),
                HeaderTypeKey = WallFramingSpec.Key(HeaderTypeId ?? CurtainTypeId),
                SillTypeKey = WallFramingSpec.Key(SillTypeId ?? CurtainTypeId),
                PlaceholderTypeKey = WallFramingSpec.Key(PlaceholderTypeId),
                MultiOpening = MultiOpening,
                MinSegment = MinSegmentMm,
                Openings = (openingsMm ?? Enumerable.Empty<WallOpeningSpan>()).ToList(),
            };
    }

    public sealed class CurtainLayerSpec
    {
        public long TypeId { get; set; }
        /// <summary>Above the ceiling's TOP face, millimetres.</summary>
        public double OffsetMm { get; set; }
        /// <summary>Grid 1 direction in degrees; null leaves the type's own grid orientation.</summary>
        public double? AngleDeg { get; set; }
    }

    public sealed class CurtainCeilingFramingSpec
    {
        public List<CurtainLayerSpec> Layers { get; set; } = new List<CurtainLayerSpec>();
        public long? HangerTypeId { get; set; }
        public double HangerSpacingMm { get; set; }
        public double HangerMaxLengthMm { get; set; } = 3000;
        public double MinSegmentMm { get; set; } = 50;

        public IEnumerable<long> TypeIds()
        {
            var ids = Layers.Select(l => l.TypeId).ToList();
            if (HangerTypeId.HasValue) ids.Add(HangerTypeId.Value);
            return ids.Distinct();
        }

        /// <summary>The highest layer: the hangers start at its plane.</summary>
        public double TopOffsetMm => Layers.Count == 0 ? 0 : Layers.Max(l => l.OffsetMm);
    }

    public sealed class CurtainWallInput
    {
        public double Length { get; set; }
        public double Height { get; set; }
        public string CurtainTypeKey { get; set; }
        public string HeaderTypeKey { get; set; }
        public string SillTypeKey { get; set; }
        public string PlaceholderTypeKey { get; set; }
        public string MultiOpening { get; set; } = CurtainWallFramingSpec.MultiOpeningDefault;
        public double MinSegment { get; set; } = 50;
        public List<WallOpeningSpan> Openings { get; set; } = new List<WallOpeningSpan>();
    }

    public sealed class CurtainCarrierAction
    {
        /// <summary>trim | delete | keep.</summary>
        public string Action { get; set; }
        /// <summary>The span of the carrier that stays (trim: the opening's; keep: the whole wall; delete: none).</summary>
        public double X0 { get; set; }
        public double X1 { get; set; }
        /// <summary>The type the carrier takes (the placeholder); null when deleted.</summary>
        public string TypeKey { get; set; }
        public string OpeningId { get; set; }
    }

    public sealed class CurtainWallPlan
    {
        /// <summary>Each curtain wall: Role, TypeKey, x from X0 to X1 and z from Z0 to Z1 (Y = 0: on the carrier's line).</summary>
        public List<FramingMember> Pieces { get; } = new List<FramingMember>();
        public List<string> Skipped { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public CurtainCarrierAction Carrier { get; set; }
        public string Refusal { get; set; }

        /// <summary>Every piece plus the carrier action: what the confirmation token binds.</summary>
        public string Signature()
        {
            var all = new List<FramingMember>(Pieces);
            if (Carrier != null)
                all.Add(new FramingMember { Role = "carrier_" + Carrier.Action, TypeKey = Carrier.TypeKey, X0 = Carrier.X0, X1 = Carrier.X1 });
            return FramingPlanSignature.Of(all);
        }
    }

    public sealed class CurtainCeilingPlan
    {
        /// <summary>Plan-view hanger lines {x0,y0,x1,y1} in the ceiling's frame.</summary>
        public List<double[]> HangerLines { get; } = new List<double[]>();
        public double HangerAngleRad { get; set; }
        public List<string> Warnings { get; } = new List<string>();
        public string Refusal { get; set; }

        /// <summary>The layers (type, offset, angle) and every hanger line: what the token binds.</summary>
        public static string Signature(CurtainCeilingFramingSpec spec, IEnumerable<double[]> hangerLines)
        {
            var all = new List<FramingMember>();
            for (int i = 0; i < spec.Layers.Count; i++)
            {
                CurtainLayerSpec l = spec.Layers[i];
                all.Add(new FramingMember
                {
                    Role = CurtainFramingRoles.Layer, TypeKey = WallFramingSpec.Key(l.TypeId), Source = i,
                    X0 = l.AngleDeg ?? 0, Y0 = l.AngleDeg.HasValue ? 1 : 0, Z0 = l.OffsetMm, Z1 = l.OffsetMm
                });
            }
            string hangerKey = spec.HangerTypeId.HasValue ? WallFramingSpec.Key(spec.HangerTypeId.Value) : "";
            foreach (double[] s in hangerLines ?? Enumerable.Empty<double[]>())
                all.Add(new FramingMember { Role = CurtainFramingRoles.Hanger, TypeKey = hangerKey, X0 = s[0], Y0 = s[1], X1 = s[2], Y1 = s[3] });
            return FramingPlanSignature.Of(all);
        }
    }

    public static class CurtainFramingRules
    {
        private const double Tol = 1e-6;
        /// <summary>How far (mm) an opening may reach past a wall end or top and still be read as flush with it.</summary>
        public const double EdgeTolerance = 1.0;

        public static CurtainWallPlan PlanWall(CurtainWallInput input)
        {
            var plan = new CurtainWallPlan();
            double length = input.Length, height = input.Height, min = input.MinSegment;
            if (!(length > Tol) || !(height > Tol)) { plan.Refusal = "the wall has no length or no height"; return plan; }

            var openings = new List<WallOpeningSpan>();
            foreach (WallOpeningSpan o in (input.Openings ?? new List<WallOpeningSpan>()).OrderBy(o => o.Start))
            {
                string name = "opening " + (o.Id ?? "?");
                if (!(o.End - o.Start > Tol)) { plan.Refusal = name + " has no width along the wall"; return plan; }
                if (o.Start < -EdgeTolerance || o.End > length + EdgeTolerance)
                { plan.Refusal = name + " reaches past the wall's ends (" + Mm(o.Start) + " to " + Mm(o.End) + " on a " + Mm(length) + " mm wall)"; return plan; }
                if (!(o.Head - o.Sill > Tol) || o.Sill < -EdgeTolerance || o.Head > height + EdgeTolerance)
                { plan.Refusal = name + " is not inside the wall's height (sill " + Mm(o.Sill) + ", head " + Mm(o.Head) + ", wall " + Mm(height) + " mm)"; return plan; }
                openings.Add(new WallOpeningSpan
                {
                    Id = o.Id, Start = Math.Max(0, o.Start), End = Math.Min(length, o.End),
                    Sill = Math.Max(0, o.Sill), Head = Math.Min(height, o.Head)
                });
            }
            for (int i = 1; i < openings.Count; i++)
                if (openings[i].Start < openings[i - 1].End - Tol)
                { plan.Refusal = "openings " + openings[i - 1].Id + " and " + openings[i].Id + " overlap along the wall; the curtain method needs disjoint spans"; return plan; }

            // Anything but keep_carrier refuses: the parser admits only the two values, and a
            // caller-built input with an unknown one must not silently keep a carrier.
            string multi = input.MultiOpening ?? CurtainWallFramingSpec.MultiOpeningDefault;
            if (openings.Count > 1 && multi != "keep_carrier")
            {
                plan.Refusal = openings.Count + " openings in one wall and multi_opening='" + multi + "': the carrier can keep only one insert as a trimmed placeholder. "
                               + "Split the wall at the openings first, or leave multi_opening at its default 'keep_carrier' to keep it full length under the curtain walls";
                return plan;
            }

            double cursor = 0;
            foreach (WallOpeningSpan o in openings)
            {
                AddPiece(plan, CurtainFramingRoles.Segment, input.CurtainTypeKey, cursor, o.Start, 0, height, -1, min);
                cursor = o.End;
            }
            AddPiece(plan, CurtainFramingRoles.Segment, input.CurtainTypeKey, cursor, length, 0, height, -1, min);
            for (int i = 0; i < openings.Count; i++)
            {
                WallOpeningSpan o = openings[i];
                if (height - o.Head > Tol) AddPiece(plan, CurtainFramingRoles.Header, input.HeaderTypeKey, o.Start, o.End, o.Head, height, i, min);
                else plan.Skipped.Add("opening " + o.Id + " reaches the wall top: no header");
                if (o.Sill > EdgeTolerance) AddPiece(plan, CurtainFramingRoles.Sill, input.SillTypeKey, o.Start, o.End, 0, o.Sill, i, min);
            }

            // Whatever the carrier action, a plan with no piece is refused: nothing would replace the
            // carrier, and no piece would carry the record operation=remove restores it from.
            if (plan.Pieces.Count == 0)
            {
                plan.Refusal = openings.Count == 0
                    ? "the wall is shorter or lower than min_segment_mm: deleting it would leave nothing in its place"
                    : "every curtain piece of this wall is empty or below min_segment_mm: nothing would replace the carrier, and no piece would carry the record operation=remove restores it from";
                return plan;
            }
            if (openings.Count == 0)
                plan.Carrier = new CurtainCarrierAction { Action = CurtainFramingRoles.CarrierDelete };
            else if (openings.Count == 1)
                plan.Carrier = new CurtainCarrierAction
                {
                    Action = CurtainFramingRoles.CarrierTrim, X0 = openings[0].Start, X1 = openings[0].End,
                    TypeKey = input.PlaceholderTypeKey, OpeningId = openings[0].Id
                };
            else
            {
                plan.Carrier = new CurtainCarrierAction { Action = CurtainFramingRoles.CarrierKeep, X0 = 0, X1 = length, TypeKey = input.PlaceholderTypeKey };
                plan.Warnings.Add(openings.Count + " openings: " + OverlapNote());
            }
            return plan;
        }

        /// <summary>What a kept carrier means, said the same way in the plan's warnings and in the carrier row of the plan and the result.</summary>
        // No count in it: a re-verification from the record does not carry the openings.
        public static string OverlapNote()
            => "the curtain walls OVERLAP the kept carrier: a wall with several openings (multi_opening=keep_carrier) stays full length "
               + "with the placeholder type so its inserts keep their ids, tags and data";

        private static void AddPiece(CurtainWallPlan plan, string role, string typeKey, double x0, double x1, double z0, double z1, int source, double min)
        {
            double len = x1 - x0, h = z1 - z0;
            if (len <= Tol || h <= Tol) return;
            if (len < min - Tol || h < min - Tol)
            {
                plan.Skipped.Add(role + " from x=" + Mm(x0) + " to " + Mm(x1) + ", z=" + Mm(z0) + " to " + Mm(z1)
                                 + " is below min_segment_mm (" + Mm(min) + "): not built");
                return;
            }
            plan.Pieces.Add(new FramingMember { Role = role, TypeKey = typeKey, X0 = x0, X1 = x1, Z0 = z0, Z1 = z1, Source = source });
        }

        /// <summary>
        /// Hanger lines for the ceiling: parallel to the first layer's grid (angleRad), spacing
        /// apart, centred and clipped to the boundary loops (first outer, the rest holes).
        /// </summary>
        public static CurtainCeilingPlan PlanCeiling(IReadOnlyList<List<double[]>> loops, CurtainCeilingFramingSpec spec, double angleRad, int maxLines)
        {
            var plan = new CurtainCeilingPlan { HangerAngleRad = angleRad };
            if (loops == null || loops.Count == 0 || loops[0].Count < 3) { plan.Refusal = "the ceiling has no boundary loop"; return plan; }
            if (spec.Layers.Count == 0) { plan.Refusal = "no layer"; return plan; }
            if (!spec.HangerTypeId.HasValue) return plan;
            List<double[]> lines = CeilingFramingRules.GridSegments(loops, angleRad, spec.HangerSpacingMm, maxLines);
            if (lines.Count > maxLines) { plan.Refusal = "more than " + maxLines + " hanger lines: over_budget"; return plan; }
            foreach (double[] s in lines)
            {
                double len = Math.Sqrt((s[2] - s[0]) * (s[2] - s[0]) + (s[3] - s[1]) * (s[3] - s[1]));
                if (len < spec.MinSegmentMm - Tol) plan.Warnings.Add("hanger line of " + Mm(len) + " mm is below min_segment_mm: not built");
                else plan.HangerLines.Add(s);
            }
            return plan;
        }

        /// <summary>
        /// A fixed-distance grid read back from the model: positions (mm along the host from its
        /// start, any order) against the type's spacing. Every interior spacing must be within tol,
        /// and neither edge bay may be wider than one spacing (a missing line). Empty: it holds.
        /// </summary>
        public static List<string> CheckFixedSpacing(IEnumerable<double> positions, double hostLength, double spacing, double tol)
        {
            var problems = new List<string>();
            List<double> p = (positions ?? Enumerable.Empty<double>()).OrderBy(v => v).ToList();
            if (!(spacing > Tol)) { problems.Add("the type's spacing is not positive"); return problems; }
            if (p.Count == 0)
            {
                if (hostLength > spacing + tol) problems.Add("no grid line on a host " + Mm(hostLength) + " mm long with a " + Mm(spacing) + " mm spacing");
                return problems;
            }
            if (p[0] > spacing + tol) problems.Add("first bay " + Mm(p[0]) + " mm is wider than the spacing: a grid line is missing");
            if (hostLength - p[p.Count - 1] > spacing + tol) problems.Add("last bay " + Mm(hostLength - p[p.Count - 1]) + " mm is wider than the spacing: a grid line is missing");
            for (int i = 1; i < p.Count; i++)
            {
                double d = p[i] - p[i - 1];
                if (Math.Abs(d - spacing) > tol) problems.Add("grid lines " + (i - 1) + " and " + i + " are " + Mm(d) + " mm apart, not " + Mm(spacing));
            }
            return problems;
        }

        internal static string Mm(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
    }

    public static partial class FramingSpecRules
    {
        public const int MaxCurtainLayers = 6;

        /// <summary>spec.wall.method / spec.ceiling.method: 'members' (default) or 'curtain'; anything else is refused by the parser.</summary>
        private static bool IsCurtain(JObject o, Reader r, string path)
            => r.Choice(o, "method", path, "members", "members", "curtain") == "curtain";

        private static WallFramingSpec ParseWallCurtain(JObject w, Reader r)
        {
            r.Known(w, "spec.wall", "method", "curtain_type_id", "header_type_id", "sill_type_id", "placeholder_type_id", "multi_opening", "min_segment_mm");
            var c = new CurtainWallFramingSpec
            {
                CurtainTypeId = r.Id(w, "curtain_type_id", "spec.wall", true) ?? 0,
                HeaderTypeId = r.Id(w, "header_type_id", "spec.wall", false),
                SillTypeId = r.Id(w, "sill_type_id", "spec.wall", false),
                PlaceholderTypeId = r.Id(w, "placeholder_type_id", "spec.wall", true) ?? 0,
                MultiOpening = r.Choice(w, "multi_opening", "spec.wall", CurtainWallFramingSpec.MultiOpeningDefault, "keep_carrier", "refuse"),
                MinSegmentMm = r.Mm(w, "min_segment_mm", "spec.wall", false, 1, 10000) ?? 50,
            };
            if (c.PlaceholderTypeId != 0 && c.PlaceholderTypeId == c.CurtainTypeId)
                r.Fail("spec.wall.placeholder_type_id", "conflict", "the placeholder is a thin Basic wall type, not the curtain type");
            if (c.PlaceholderTypeId != 0 && c.HeaderTypeId == c.PlaceholderTypeId)
                r.Fail("spec.wall.header_type_id", "conflict", "the header is a Curtain Wall type, not the placeholder");
            if (c.PlaceholderTypeId != 0 && c.SillTypeId == c.PlaceholderTypeId)
                r.Fail("spec.wall.sill_type_id", "conflict", "the sill is a Curtain Wall type, not the placeholder");
            return r.Errors.Count == 0 ? new WallFramingSpec { Curtain = c } : null;
        }

        private static CeilingFramingSpec ParseCeilingCurtain(JObject c, Reader r)
        {
            r.Known(c, "spec.ceiling", "method", "layers", "hanger", "min_segment_mm");
            var result = new CurtainCeilingFramingSpec { MinSegmentMm = r.Mm(c, "min_segment_mm", "spec.ceiling", false, 1, 10000) ?? 50 };
            JToken layers = c["layers"];
            if (layers == null || layers.Type == JTokenType.Null) r.Fail("spec.ceiling.layers", "missing", "1.." + MaxCurtainLayers + " sloped-glazing layers");
            else if (!(layers is JArray rows)) r.Fail("spec.ceiling.layers", "not_array");
            else if (rows.Count == 0) r.Fail("spec.ceiling.layers", "missing", "at least one layer");
            else if (rows.Count > MaxCurtainLayers) r.Fail("spec.ceiling.layers", "above_maximum", MaxCurtainLayers + " layers");
            else
                for (int i = 0; i < rows.Count; i++)
                {
                    string path = "spec.ceiling.layers[" + i + "]";
                    if (!(rows[i] is JObject row)) { r.Fail(path, "not_object"); continue; }
                    r.Known(row, path, "type_id", "offset_mm", "angle_deg");
                    long? id = r.Id(row, "type_id", path, true);
                    double? offset = r.Mm(row, "offset_mm", path, true, 0, MaxLengthMm);
                    double? angle = null;
                    JToken a = row["angle_deg"];
                    if (a != null && a.Type != JTokenType.Null)
                    {
                        if ((a.Type == JTokenType.Integer || a.Type == JTokenType.Float) && Math.Abs((double)a) <= 360) angle = (double)a;
                        else r.Fail(path + ".angle_deg", "bad_value", "an angle in degrees within -360..360");
                    }
                    if (id.HasValue && offset.HasValue) result.Layers.Add(new CurtainLayerSpec { TypeId = id.Value, OffsetMm = offset.Value, AngleDeg = angle });
                }

            JObject hanger = r.Obj(c, "hanger", "spec.ceiling", false);
            if (hanger != null)
            {
                r.Known(hanger, "spec.ceiling.hanger", "type_id", "spacing_mm", "max_length_mm", "attach");
                result.HangerTypeId = r.Id(hanger, "type_id", "spec.ceiling.hanger", true);
                result.HangerSpacingMm = r.Mm(hanger, "spacing_mm", "spec.ceiling.hanger", true, MinSpacingMm, MaxSpacingMm) ?? 0;
                result.HangerMaxLengthMm = r.Mm(hanger, "max_length_mm", "spec.ceiling.hanger", false, 1, MaxLengthMm) ?? 3000;
                r.Choice(hanger, "attach", "spec.ceiling.hanger", "structure_above", "structure_above");
                // The hanger lines follow the first layer's grid: without its angle the plan cannot say where they go.
                if (result.Layers.Count > 0 && !result.Layers[0].AngleDeg.HasValue)
                    r.Fail("spec.ceiling.layers[0].angle_deg", "missing", "the hanger lines run parallel to the first layer's grid; give its angle");
            }
            return r.Errors.Count == 0 ? new CeilingFramingSpec { Curtain = result } : null;
        }
    }
}
