// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// THE ARITHMETIC OF THREE QUANTITY QUESTIONS, KEPT WHERE A DESK CAN PROVE IT.
//
// horizun_quantities mode='room_finishes' asks Revit for the faces of each room and
// who bounds them; mode='carbon' asks for volume and area per material; the room
// membership of query_model / takeoff group_by='room' asks which room holds each
// element. Revit answers the geometric half. What a reader then BILLS - how faces
// fold into rows, where an opening's deduction lands, what a factor multiplies,
// what is left out and why - is plain arithmetic, and it lives here, Revit-free,
// so every rule has a unit test instead of a hope.
//
// THE RULES THAT MATTER, and why:
//
//  * NEVER A SILENT NET. A room face is not cut by the doors in its wall: Revit's
//    room solid follows the wall face straight past the opening. So the face area
//    is GROSS, and the measured deduction of the openings travels in its OWN column,
//    with how each opening was sized (rough, nominal, bounding box). Subtracting it
//    here would publish a net nobody can take apart again, and finish contracts
//    disagree on whether small openings are deducted at all - that is the reader's
//    rule, not ours.
//
//  * A MISSING NUMBER IS NAMED, NEVER A ZERO. A material without a factor, a factor
//    per kg on a material with no density, a volume that could not be read: each is
//    counted by reason in its group and excluded from the sum, and the group says
//    it is incomplete. An environmental total that quietly omits the steel is worse
//    than no total.
//
//  * NO FACTOR IS COMPILED IN. Carbon factors are the caller's table (an EPD list,
//    an EC3 export). Negative factors are allowed on purpose: biogenic carbon in
//    timber is declared negative by real EPDs, and refusing it would force callers
//    to lie in the other direction.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One bounded (or unbounded) piece of a room face, already in m2.</summary>
    public sealed class FinishFaceFact
    {
        public string RoomKey;
        public string Surface;       // wall | floor | ceiling
        public string BoundingKey;   // null: no element bounds this face (the room's own limit)
        public string TypeName, Material, Code;
        public string MaterialSource;  // paint | face | paint_unreadable; null when no face was read
        public double GrossM2;
    }

    /// <summary>One opening hosted by a bounding wall and facing the room.</summary>
    public sealed class OpeningDeductionFact
    {
        public string RoomKey, BoundingKey, InsertId;
        public string InsertKind;    // door | window | wall_opening
        public string SizeBasis;     // rough | nominal | bounding_box | opening_rect
        public double? WidthM, HeightM;
    }

    public sealed class FinishGroup
    {
        public string RoomKey, Surface, TypeName, Material, MaterialSource, Code;
        public double GrossM2;
        public double OpeningDeductionM2;
        public int Openings, OpeningsUnsized;
        public readonly SortedSet<string> SizeBases = new SortedSet<string>(StringComparer.Ordinal);
        public readonly Dictionary<string, double> AreaByBoundingKey = new Dictionary<string, double>(StringComparer.Ordinal);
        public readonly List<string> OpeningIds = new List<string>();
        // Each opening as attributed, so a reader can apply the size threshold its contract names.
        public readonly List<OpeningDeductionFact> OpeningFacts = new List<OpeningDeductionFact>();
    }

    public static class RoomFinishRules
    {
        public const double SquareFeetToM2 = 0.09290304;
        public const string NoBoundingElement = "(no bounding element)";

        /// <summary>SubfaceType name to the finish it carries. Side bounds walls, Bottom the floor, Top the ceiling.</summary>
        public static string SurfaceOf(string subfaceType)
        {
            switch (subfaceType)
            {
                case "Side": return "wall";
                case "Bottom": return "floor";
                case "Top": return "ceiling";
                default: return null;
            }
        }

        /// <summary>A rectangle's area, or null when either side is missing, non-positive or not finite.</summary>
        public static double? RectangleM2(double? widthM, double? heightM)
        {
            if (!widthM.HasValue || !heightM.HasValue) return null;
            double w = widthM.Value, h = heightM.Value;
            if (double.IsNaN(w) || double.IsInfinity(w) || double.IsNaN(h) || double.IsInfinity(h)) return null;
            if (w <= 0 || h <= 0) return null;
            return w * h;
        }

        /// <summary>
        /// Whether an insert with this ElementOnPhaseStatus name is in the building of the measured
        /// phase. New and Existing are; None is an element that carries no phasing, so it is in every
        /// phase. Demolished, Past, Future and Temporary are not: that door is not in the wall then,
        /// and deducting it would take area off a face that is really there.
        /// </summary>
        public static bool ExistsInPhase(string phaseStatus)
            => phaseStatus == "New" || phaseStatus == "Existing" || phaseStatus == "None";

        /// <summary>
        /// Folds faces into rows keyed by room, surface, bounding type, material (and its source) and code,
        /// and hangs each opening's deduction on the row of ITS wall in ITS room. A wall
        /// whose faces in one room fall in two rows (two materials on one face) takes its
        /// deductions to the row where it has the most area - deterministic, and stated.
        /// An opening whose wall bounds no face of that room is returned in orphans, never
        /// dropped: it means the caller's opening list and the geometry disagree.
        /// </summary>
        public static List<FinishGroup> Group(IEnumerable<FinishFaceFact> faces,
                                              IEnumerable<OpeningDeductionFact> openings,
                                              out List<OpeningDeductionFact> orphans)
        {
            var groups = new Dictionary<string, FinishGroup>(StringComparer.Ordinal);
            var order = new List<FinishGroup>();
            foreach (var f in faces ?? Enumerable.Empty<FinishFaceFact>())
            {
                if (f == null) continue;
                string type = f.BoundingKey == null ? NoBoundingElement : (f.TypeName ?? "(unreadable type)");
                string key = string.Join("\u001f", f.RoomKey ?? "", f.Surface ?? "", type, f.Material ?? "", f.MaterialSource ?? "", f.Code ?? "");
                FinishGroup g;
                if (!groups.TryGetValue(key, out g))
                {
                    g = new FinishGroup { RoomKey = f.RoomKey, Surface = f.Surface, TypeName = type, Material = f.Material, MaterialSource = f.MaterialSource, Code = f.Code };
                    groups[key] = g;
                    order.Add(g);
                }
                g.GrossM2 += f.GrossM2;
                if (f.BoundingKey != null)
                {
                    double have;
                    g.AreaByBoundingKey.TryGetValue(f.BoundingKey, out have);
                    g.AreaByBoundingKey[f.BoundingKey] = have + f.GrossM2;
                }
            }

            orphans = new List<OpeningDeductionFact>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in openings ?? Enumerable.Empty<OpeningDeductionFact>())
            {
                if (o == null) continue;
                // One opening, one wall, one room: counted once however many subfaces the wall has there.
                if (!seen.Add((o.RoomKey ?? "") + "\u001f" + (o.BoundingKey ?? "") + "\u001f" + (o.InsertId ?? ""))) continue;
                FinishGroup best = null;
                double bestArea = double.NegativeInfinity;
                foreach (var g in order)
                {
                    if (g.Surface != "wall" || !string.Equals(g.RoomKey, o.RoomKey, StringComparison.Ordinal)) continue;
                    double a;
                    if (o.BoundingKey == null || !g.AreaByBoundingKey.TryGetValue(o.BoundingKey, out a)) continue;
                    if (a > bestArea) { best = g; bestArea = a; }
                }
                if (best == null) { orphans.Add(o); continue; }
                best.Openings++;
                best.OpeningIds.Add(o.InsertId);
                best.OpeningFacts.Add(o);
                double? area = RectangleM2(o.WidthM, o.HeightM);
                if (area.HasValue)
                {
                    best.OpeningDeductionM2 += area.Value;
                    if (!string.IsNullOrEmpty(o.SizeBasis)) best.SizeBases.Add(o.SizeBasis);
                }
                else best.OpeningsUnsized++;
            }
            return order;
        }
    }

    /// <summary>One caller factor: kgCO2e per m3 or per kg, keyed by material name OR material class.</summary>
    public sealed class CarbonFactor
    {
        public string Material, MaterialClass, Per;
        public double Factor;
    }

    /// <summary>One material of one element, as read. Nulls are unread, never zero.</summary>
    public sealed class CarbonReading
    {
        public string ElementId, Material, MaterialClass, Code, Level;
        public string PhaseCreated, PhaseDemolished;
        public double? VolumeM3, AreaM2, DensityKgM3;
    }

    public sealed class CarbonGroup
    {
        public string Material, MaterialClass, Code, Level, PhaseCreated, PhaseDemolished;
        public string FactorPer, MatchedBy;
        public double? Factor;
        public double VolumeM3, AreaM2, MassKg, KgCO2e;
        public int Readings, Counted, NoFactor, NoDensity, UnreadableVolume, MassReadings, UnreadableArea;
        public bool Complete => Counted == Readings;
        /// <summary>AreaM2 sums the readings that had an area; false when one of them had none readable.</summary>
        public bool AreaComplete => UnreadableArea == 0;
    }

    public static class CarbonRules
    {
        public const double CubicFeetToM3 = 0.028316846592;
        public const string Counted = "counted", NoFactor = "no_factor", NoDensity = "no_density",
                            UnreadableVolume = "unreadable_volume";

        /// <summary>Revit's internal density is kg per cubic foot.</summary>
        public static double KgPerCubicFootToKgPerM3(double kgPerFt3) => kgPerFt3 / CubicFeetToM3;

        /// <summary>Null when the table is usable; otherwise why not, naming the entry.</summary>
        public static string Validate(IList<CarbonFactor> factors)
        {
            if (factors == null || factors.Count == 0) return "carbon_factors is required and must not be empty: no factor is compiled in.";
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < factors.Count; i++)
            {
                var f = factors[i];
                if (f == null) return "carbon_factors[" + i + "] is not an object.";
                bool byName = !string.IsNullOrWhiteSpace(f.Material), byClass = !string.IsNullOrWhiteSpace(f.MaterialClass);
                if (byName == byClass)
                    return "carbon_factors[" + i + "] must name exactly one of material or material_class.";
                if (f.Per != "m3" && f.Per != "kg")
                    return "carbon_factors[" + i + "].per must be 'm3' or 'kg'.";
                if (double.IsNaN(f.Factor) || double.IsInfinity(f.Factor))
                    return "carbon_factors[" + i + "].factor must be a finite number.";
                string key = (byName ? "name:" + f.Material.Trim() : "class:" + f.MaterialClass.Trim());
                if (!keys.Add(key))
                    return "carbon_factors[" + i + "] repeats " + key + ": two factors for one material would make the total depend on order.";
            }
            return null;
        }

        /// <summary>Material name first, then material class; case-insensitive. Null when neither matches.</summary>
        public static CarbonFactor Resolve(IList<CarbonFactor> factors, string material, string materialClass, out string matchedBy)
        {
            matchedBy = null;
            if (factors == null) return null;
            if (!string.IsNullOrWhiteSpace(material))
                foreach (var f in factors)
                    if (!string.IsNullOrWhiteSpace(f.Material) &&
                        string.Equals(f.Material.Trim(), material.Trim(), StringComparison.OrdinalIgnoreCase))
                    { matchedBy = "material"; return f; }
            if (!string.IsNullOrWhiteSpace(materialClass))
                foreach (var f in factors)
                    if (!string.IsNullOrWhiteSpace(f.MaterialClass) &&
                        string.Equals(f.MaterialClass.Trim(), materialClass.Trim(), StringComparison.OrdinalIgnoreCase))
                    { matchedBy = "material_class"; return f; }
            return null;
        }

        /// <summary>The state of one reading against its factor, and its mass and kgCO2e when they exist.</summary>
        public static string Evaluate(CarbonReading r, CarbonFactor f, out double? massKg, out double? kgCO2e)
        {
            massKg = null; kgCO2e = null;
            if (r == null || !r.VolumeM3.HasValue || double.IsNaN(r.VolumeM3.Value)) return UnreadableVolume;
            if (r.DensityKgM3.HasValue && r.DensityKgM3.Value > 0) massKg = r.VolumeM3.Value * r.DensityKgM3.Value;
            if (f == null) return NoFactor;
            if (f.Per == "m3") { kgCO2e = f.Factor * r.VolumeM3.Value; return Counted; }
            if (!massKg.HasValue) return NoDensity;
            kgCO2e = f.Factor * massKg.Value;
            return Counted;
        }

        /// <summary>
        /// Rows by material x code x level x phase created x phase demolished (a sweep takes every
        /// phase, demolished elements too, so the phase is a column, never mixed in silently).
        /// Volume and area sum every reading that has one (UnreadableArea counts the rest);
        /// mass sums the readings with a density (MassReadings says how many); kgCO2e sums
        /// ONLY the counted ones, and every excluded reading is counted by reason.
        /// </summary>
        public static List<CarbonGroup> Group(IEnumerable<CarbonReading> readings, IList<CarbonFactor> factors)
        {
            var map = new Dictionary<string, CarbonGroup>(StringComparer.Ordinal);
            var order = new List<CarbonGroup>();
            foreach (var r in readings ?? Enumerable.Empty<CarbonReading>())
            {
                if (r == null) continue;
                string key = string.Join("\u001f", r.Material ?? "", r.Code ?? "", r.Level ?? "", r.PhaseCreated ?? "", r.PhaseDemolished ?? "");
                CarbonGroup g;
                if (!map.TryGetValue(key, out g))
                {
                    string matchedBy;
                    var f = Resolve(factors, r.Material, r.MaterialClass, out matchedBy);
                    g = new CarbonGroup
                    {
                        Material = r.Material, MaterialClass = r.MaterialClass, Code = r.Code, Level = r.Level,
                        PhaseCreated = r.PhaseCreated, PhaseDemolished = r.PhaseDemolished,
                        Factor = f?.Factor, FactorPer = f?.Per, MatchedBy = matchedBy
                    };
                    map[key] = g;
                    order.Add(g);
                }
                string ignored;
                var factor = Resolve(factors, r.Material, r.MaterialClass, out ignored);
                double? mass, kg;
                string state = Evaluate(r, factor, out mass, out kg);
                g.Readings++;
                if (r.VolumeM3.HasValue && !double.IsNaN(r.VolumeM3.Value)) g.VolumeM3 += r.VolumeM3.Value;
                if (r.AreaM2.HasValue && !double.IsNaN(r.AreaM2.Value)) g.AreaM2 += r.AreaM2.Value;
                else g.UnreadableArea++;
                if (mass.HasValue) { g.MassKg += mass.Value; g.MassReadings++; }
                switch (state)
                {
                    case Counted: g.Counted++; g.KgCO2e += kg.Value; break;
                    case NoFactor: g.NoFactor++; break;
                    case NoDensity: g.NoDensity++; break;
                    default: g.UnreadableVolume++; break;
                }
            }
            return order;
        }
    }

    /// <summary>How an element is placed in a room: which point is sampled, and what a miss is called.</summary>
    public static class RoomMembershipRules
    {
        public const string Unassigned = "(unassigned)";

        /// <summary>
        /// Walls and floors are sampled at a point of their solid (a wall's LocationCurve is its
        /// axis, which a room-bounding wall never lies inside); other elements at their location
        /// point, else their location curve's start, middle and end. Null when none of those exists.
        /// </summary>
        public static string SampleBasis(bool isWallOrFloor, bool hasSolid, bool hasPoint, bool hasCurve)
        {
            if (isWallOrFloor) return hasSolid ? "solid_interior" : null;
            if (hasPoint) return "location_point";
            if (hasCurve) return "curve_points";
            return null;
        }

        public const string MultipleRooms = "(multiple rooms)";
        public const string AssignedState = "assigned", SpansRoomsState = "spans_rooms", UnassignedState = "unassigned";

        public static string GroupKey(string roomKey) => string.IsNullOrWhiteSpace(roomKey) ? Unassigned : roomKey;

        /// <summary>
        /// The state of an element sampled at several points (a floor's top-face triangles, a
        /// curve's ends and middle) from the room key each sample fell in (null: in no room). A
        /// miss is ignored while another sample found a room - a slab's samples under its own
        /// walls are in no room. Two or more DISTINCT rooms is spans_rooms: billing the whole
        /// element to one of them would be a guess, so it gets its own key and the rooms are listed.
        /// </summary>
        public static string Classify(IEnumerable<string> sampleRoomKeys, out List<string> rooms)
        {
            rooms = new List<string>();
            foreach (var k in sampleRoomKeys ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(k) && !rooms.Contains(k)) rooms.Add(k);
            if (rooms.Count == 0) return UnassignedState;
            return rooms.Count == 1 ? AssignedState : SpansRoomsState;
        }

        /// <summary>The by_room key of a classified element: its room, (multiple rooms) or (unassigned).</summary>
        public static string KeyOf(string state, IList<string> rooms)
            => state == AssignedState && rooms != null && rooms.Count == 1 ? rooms[0]
             : state == SpansRoomsState ? MultipleRooms : Unassigned;
    }

    /// <summary>One quantity's tally in one rollup key (a classification code or a room).</summary>
    public class QuantityTally
    {
        public double Total;
        public int Measured, Absent, Empty, Unreadable, Invalid;
    }

    /// <summary>
    /// The per-quantity arithmetic of a takeoff rollup, shared by by_code and by_room so the two can never
    /// disagree: a measured reading adds its value, every other state is counted under its own name, and a
    /// key is complete for a quantity only when EVERY element in it was measured - an absent value is not a
    /// zero, and '(unassigned)', '(multiple rooms)' and '(unlocatable)' are keys like any other.
    /// </summary>
    public static class RollupRules
    {
        public const string UnreadableBucket = "unreadable";

        /// <summary>Counts one reading; returns the bucket it went to (a QuantityState name, or UnreadableBucket).</summary>
        public static string Add(QuantityTally t, string state, double? value)
        {
            if (state == QuantityState.Measured && value.HasValue && !double.IsNaN(value.Value))
            { t.Measured++; t.Total += value.Value; return QuantityState.Measured; }
            if (state == QuantityState.Absent) { t.Absent++; return QuantityState.Absent; }
            if (state == QuantityState.Empty) { t.Empty++; return QuantityState.Empty; }
            if (state == QuantityState.Invalid) { t.Invalid++; return QuantityState.Invalid; }
            t.Unreadable++;
            return UnreadableBucket;
        }

        public static bool Complete(QuantityTally t, int elements) => t != null && t.Measured == elements;
    }
}
