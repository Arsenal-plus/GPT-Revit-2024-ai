// Room membership: which room (and which MEP space) of ONE phase an element is in.
// Shared by horizun_query_model include_room and horizun_quantities takeoff group_by='room'.
//
// THE TWO WRITTEN RULES (docs/TOOLS-EXTENDED.md, "Room membership"):
//  1. THE PHASE IS MANDATORY. Rooms and spaces exist per phase; a hidden default (the last
//     phase) would silently answer for a different building. It is the HOST's phase.
//  2. A LINKED element is placed in the HOST's rooms: its sample points go through its
//     RevitLinkInstance.GetTotalTransform and the host answers GetRoomAtPoint(pt, phase).
//     Whether the link is room-bounding does not change that query - it only shapes the
//     host's rooms (a host room that needs the link's walls to close is not enclosed
//     without them, and holds nothing). Rooms INSIDE a linked model are not read.
//
// SAMPLING (Core/RoomQuantityRules.cs, RoomMembershipRules.SampleBasis and Classify):
//  - point-based elements at the family's own room calculation point when it has one
//    (what Revit's own Room/Space properties read); else at their LocationPoint lifted
//    1 mm, so an element standing on its level is not lost on the room's bottom boundary.
//    An instance hosted by a CEILING has its origin on the ceiling's underside - the top of
//    the room - where the lift lands inside the ceiling; when the lift finds no room it is
//    probed 1 mm BELOW (basis location_point_below). Only a ceiling-hosted instance goes
//    below: a floor-standing element outside every room would land in the room underneath;
//  - curve-based elements at their curve's start, middle and end, not lifted;
//  - floors at up to 32 points ON their top face (inside its triangles, so on the face even
//    for an L-shaped floor), lifted 1 mm: a floor's body sits below the room it carries;
//  - walls at an interior point of their largest solid: the centroid when a vertical line
//    through it proves it lies INSIDE the wall; else the middle of the longest piece of wall
//    that line crosses (a centred door or window leaves only the header and sill there);
//    else the same through the location curve's midpoint. A room-bounding wall's interior
//    lies outside every room computed at the wall finish, so it comes back UNASSIGNED - by
//    design: a wall that separates two rooms belongs to neither.
// Samples in two or more DIFFERENT rooms: spans_rooms, key '(multiple rooms)', the rooms
// listed - never billed whole to one of them. A miss beside a hit is ignored (a slab's
// samples under its own walls are in no room). No room at all is 'unassigned'. An element
// with no sample is 'unlocatable' - never folded into unassigned, never guessed.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal sealed class RoomHit
    {
        public string State;          // assigned | spans_rooms | unassigned | unlocatable
        public string Basis;
        public XYZ Point;             // host coordinates, feet: the first sample that found a room, else the first one
        public SpatialElement Room;   // only when exactly one room holds the samples
        public readonly List<SpatialElement> Rooms = new List<SpatialElement>();
        public SpatialElement Space;  // only when exactly one space holds the samples
        public readonly List<SpatialElement> Spaces = new List<SpatialElement>();
        public int Samples;
        public string Reason;
        public string SpaceProblem;

        /// <summary>The by_room key: the room id, '(multiple rooms)', '(unlocatable)' or '(unassigned)'.</summary>
        public string RoomKey => State == "unlocatable" ? RoomMembershipReader.UnlocatableKey
            : RoomMembershipRules.KeyOf(State, Rooms.Select(r => Rid.Value(r.Id).ToString()).ToList());
    }

    internal sealed class RoomMembershipReader
    {
        public const string UnlocatableKey = "(unlocatable)";
        private const double LiftFeet = 1.0 / 304.8;   // 1 mm
        private const int MaxFloorSamples = 32;

        public const string Rules =
            "Points at the family's room calculation point, else their location point +1 mm (-1 mm for a ceiling-hosted " +
            "instance the lift left in no room); curves at start, middle and end; floors at up to 32 points on their top " +
            "face (+1 mm); walls at an interior point of their solid. Samples in different rooms: spans_rooms under " +
            "'(multiple rooms)', never billed to one. A room-bounding wall lies outside every room computed at the wall " +
            "finish, so it is unassigned by design. Linked elements: the points are moved by the link instance's total " +
            "transform and placed in the HOST's rooms of this phase, room-bounding link or not; rooms inside a link are " +
            "not read. No sample = unlocatable, never unassigned.";

        private readonly Document _host;
        private readonly Phase _phase;
        private readonly Options _options = new Options
        {
            ComputeReferences = false, IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Coarse
        };
        public int Assigned, SpansRooms, Unassigned, Unlocatable, InSpace, SpaceUnreadable;

        private RoomMembershipReader(Document host, Phase phase) { _host = host; _phase = phase; }

        public string PhaseName => _phase.Name;

        /// <summary>The phase every query of this reader answers for (horizun_export cobie reads openings' To/From room in it).</summary>
        public Phase Phase => _phase;

        /// <summary>The phase the caller named, exactly (case-insensitive). No default.</summary>
        public static RoomMembershipReader Create(Document host, string phaseName, out string problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(phaseName))
            {
                problem = "phase is required: rooms and spaces exist per phase, and a hidden default (the last phase) " +
                          "would silently place elements in a different building. Name the phase.";
                return null;
            }
            var names = new List<string>();
            foreach (Phase p in host.Phases)
            {
                names.Add(p.Name);
                if (string.Equals(p.Name, phaseName.Trim(), StringComparison.OrdinalIgnoreCase))
                    return new RoomMembershipReader(host, p);
            }
            problem = "No phase is named '" + phaseName + "'. Phases in this document: " + string.Join(", ", names) + ".";
            return null;
        }

        /// <summary>Where an element of <paramref name="e"/>'s own document sits among the host's rooms.</summary>
        public RoomHit Locate(Element e, Transform toHost, string noTransform = null)
        {
            var hit = new RoomHit();
            if (noTransform != null)
            {
                hit.State = "unlocatable"; hit.Reason = noTransform; Unlocatable++;
                return hit;
            }
            List<XYZ> local;
            bool firstHitWins;
            string reason;
            try { local = Sample(e, out hit.Basis, out reason, out firstHitWins); }
            catch (Exception ex) { local = null; firstHitWins = false; reason = "the sample points could not be read: " + ex.Message; }
            if (local == null || local.Count == 0)
            {
                hit.State = "unlocatable"; hit.Reason = reason ?? "no sample point could be read."; Unlocatable++;
                return hit;
            }

            var roomKeys = new List<string>();
            int firstHit = -1;
            for (int i = 0; i < local.Count; i++)
            {
                XYZ pt = toHost == null ? local[i] : toHost.OfPoint(local[i]);
                if (hit.Point == null) hit.Point = pt;
                SpatialElement room;
                try { room = _host.GetRoomAtPoint(pt, _phase); }
                catch (Exception ex)
                {
                    hit.State = "unlocatable"; hit.Reason = "GetRoomAtPoint failed: " + ex.Message; Unlocatable++;
                    return hit;
                }
                hit.Samples++;
                roomKeys.Add(room == null ? null : Rid.Value(room.Id).ToString());
                AddDistinct(hit.Rooms, room);
                if (room != null && firstHit < 0) { firstHit = i; hit.Point = pt; }
                // The space is read at every sample the room is, and a failed read is said, not nulled.
                if (!firstHitWins) ReadSpace(hit, pt);
                if (firstHitWins && room != null) break;
            }
            // A point-based element has ONE position: its space is read where its room was found.
            if (firstHitWins) ReadSpace(hit, hit.Point);
            if (firstHitWins && firstHit == 1 && hit.Basis == "location_point") hit.Basis = "location_point_below";

            List<string> roomIds;
            hit.State = RoomMembershipRules.Classify(roomKeys, out roomIds);
            hit.Room = hit.Rooms.Count == 1 ? hit.Rooms[0] : null;
            hit.Space = hit.Spaces.Count == 1 ? hit.Spaces[0] : null;
            if (hit.SpaceProblem != null) SpaceUnreadable++;
            if (hit.Spaces.Count > 0) InSpace++;
            if (hit.State == RoomMembershipRules.AssignedState) Assigned++;
            else if (hit.State == RoomMembershipRules.SpansRoomsState) SpansRooms++;
            else Unassigned++;
            return hit;
        }

        private void ReadSpace(RoomHit hit, XYZ pt)
        {
            if (hit.SpaceProblem != null || pt == null) return;
            try { AddDistinct(hit.Spaces, _host.GetSpaceAtPoint(pt, _phase)); }
            catch (Exception ex) { hit.SpaceProblem = "GetSpaceAtPoint failed: " + ex.Message; }
        }

        private static void AddDistinct(List<SpatialElement> list, SpatialElement s)
        {
            if (s != null && !list.Any(x => x.Id == s.Id)) list.Add(s);
        }

        public JObject ToJson(RoomHit h, double scale)
        {
            var j = new JObject
            {
                ["state"] = h.State,
                ["room"] = Describe(h.Room),
                ["space"] = Describe(h.Space),
                ["basis"] = h.Basis,
                ["samples"] = h.Samples,
                ["sample_point"] = h.Point == null ? JValue.CreateNull()
                    : new JArray(Math.Round(h.Point.X * scale, 3), Math.Round(h.Point.Y * scale, 3), Math.Round(h.Point.Z * scale, 3))
            };
            if (h.Rooms.Count > 1) j["rooms"] = new JArray(h.Rooms.Select(r => Describe(r)).ToArray());
            if (h.Spaces.Count > 1) j["spaces"] = new JArray(h.Spaces.Select(s => Describe(s)).ToArray());
            if (h.Reason != null) j["reason"] = h.Reason;
            if (h.SpaceProblem != null) j["space_problem"] = h.SpaceProblem;
            return j;
        }

        public static JToken Describe(SpatialElement s)
        {
            if (s == null) return JValue.CreateNull();
            string number = null, name = null, level = null;
            try { number = s.Number; } catch { }
            try { name = s.Name; } catch { }
            try { level = s.Level?.Name; } catch { }
            return new JObject { ["id"] = Rid.Value(s.Id), ["number"] = number, ["name"] = name, ["level"] = level };
        }

        public JObject Summary()
        {
            string boundary = null;
            try
            {
                boundary = AreaVolumeSettings.GetAreaVolumeSettings(_host)
                    .GetSpatialElementBoundaryLocation(SpatialElementType.Room).ToString();
            }
            catch { }
            var j = new JObject
            {
                ["phase"] = _phase.Name,
                ["assigned"] = Assigned,
                ["spans_rooms"] = SpansRooms,
                ["unassigned"] = Unassigned,
                ["unlocatable"] = Unlocatable,
                ["in_space"] = InSpace,
                ["space_unreadable"] = SpaceUnreadable,
                ["complete"] = Unlocatable == 0 && SpaceUnreadable == 0,
                ["room_boundary_location"] = boundary,
                ["rules"] = Rules
            };
            if (boundary != null && boundary != "Finish")
                j["boundary_warning"] = "Rooms are computed at the wall " + boundary + ", not at its finish: a room-bounding " +
                                        "wall's interior point can sit ON a room's boundary, and which side Revit answers there is Revit's.";
            return j;
        }

        /// <summary>
        /// The points to place, in the element's own coordinates. firstHitWins: they are alternatives for ONE
        /// position (the first that finds a room is the answer); otherwise they are parts of the element and
        /// every one is placed.
        /// </summary>
        private List<XYZ> Sample(Element e, out string basis, out string reason, out bool firstHitWins)
        {
            reason = null;
            firstHitWins = false;
            bool isFloor = e is Floor;
            bool wallOrFloor = isFloor || e is Wall;
            Solid solid = wallOrFloor ? LargestSolid(e) : null;
            LocationPoint lp = e.Location as LocationPoint;
            Curve curve = null;
            if (lp == null) { try { curve = (e.Location as LocationCurve)?.Curve; } catch { } }
            basis = RoomMembershipRules.SampleBasis(wallOrFloor, solid != null, lp != null, curve != null);
            switch (basis)
            {
                case "location_point":
                {
                    firstHitWins = true;
                    var fi = e as FamilyInstance;
                    bool hasCalcPoint = false;
                    try { hasCalcPoint = fi != null && fi.HasSpatialElementCalculationPoint; } catch { }
                    if (hasCalcPoint)
                    {
                        basis = "calculation_point";
                        return new List<XYZ> { fi.GetSpatialElementCalculationPoint() };
                    }
                    var pts = new List<XYZ> { lp.Point + new XYZ(0, 0, LiftFeet) };
                    bool onCeiling = false;
                    try { onCeiling = fi != null && fi.Host is Ceiling; } catch { }
                    if (onCeiling) pts.Add(lp.Point - new XYZ(0, 0, LiftFeet));
                    return pts;
                }
                case "curve_points":
                    return new List<XYZ> { curve.Evaluate(0, true), curve.Evaluate(0.5, true), curve.Evaluate(1, true) };
                case "solid_interior":
                {
                    if (isFloor)
                    {
                        basis = "floor_top_face";
                        List<XYZ> top = TopFacePoints((Floor)e, out reason);
                        return top?.Select(t => t + new XYZ(0, 0, LiftFeet)).ToList();
                    }
                    basis = "wall_solid_centroid";
                    XYZ c = solid.ComputeCentroid();
                    XYZ inside = InteriorOnVertical(solid, c, true);
                    if (inside != null)
                    {
                        if (!inside.IsAlmostEqualTo(c)) basis = "wall_solid_interior";
                        return new List<XYZ> { inside };
                    }
                    Curve axis = null;
                    try { axis = (e.Location as LocationCurve)?.Curve; } catch { }
                    inside = axis == null ? null : InteriorOnVertical(solid, axis.Evaluate(0.5, true), false);
                    if (inside != null) { basis = "wall_axis_interior"; return new List<XYZ> { inside }; }
                    reason = "neither the vertical line through the wall's solid centroid nor the one through its location-curve " +
                             "midpoint crosses its solid; no interior point is guessed.";
                    return null;
                }
                default:
                    reason = wallOrFloor
                        ? "a wall or floor with no readable solid (a curtain wall carries its geometry in its panels)."
                        : "no location point, location curve or wall/floor solid to sample.";
                    return null;
            }
        }

        private Solid LargestSolid(Element e)
        {
            GeometryElement geo;
            try { geo = e.get_Geometry(_options); } catch { return null; }
            if (geo == null) return null;
            Solid best = null;
            foreach (GeometryObject go in geo)
            {
                if (go is GeometryInstance gi)
                {
                    foreach (GeometryObject inner in gi.GetInstanceGeometry())
                        if (inner is Solid si && si.Volume > 0 && (best == null || si.Volume > best.Volume)) best = si;
                }
                else if (go is Solid s && s.Volume > 0 && (best == null || s.Volume > best.Volume)) best = s;
            }
            return best;
        }

        /// <summary>
        /// Points on a floor's top faces: inside each triangle (its centroid and the midpoints from the centroid to
        /// its corners), at most MaxFloorSamples spread evenly over all of them.
        /// </summary>
        private static List<XYZ> TopFacePoints(Floor floor, out string reason)
        {
            reason = null;
            IList<Reference> refs;
            try { refs = HostObjectUtils.GetTopFaces(floor); }
            catch (Exception ex) { reason = "the floor's top faces could not be read: " + ex.Message; return null; }
            var all = new List<XYZ>();
            foreach (Reference r in refs)
            {
                Face f = null;
                try { f = floor.GetGeometryObjectFromReference(r) as Face; } catch { }
                if (f == null) continue;
                Mesh m = null;
                try { m = f.Triangulate(); } catch { }
                if (m == null) continue;
                for (int i = 0; i < m.NumTriangles; i++)
                {
                    MeshTriangle t = m.get_Triangle(i);
                    XYZ a = t.get_Vertex(0), b = t.get_Vertex(1), c = t.get_Vertex(2);
                    XYZ g = (a + b + c) * (1.0 / 3.0);
                    all.Add(g);
                    all.Add((g + a) * 0.5);
                    all.Add((g + b) * 0.5);
                    all.Add((g + c) * 0.5);
                }
            }
            if (all.Count == 0) { reason = "no top face of the floor could be read and triangulated."; return null; }
            if (all.Count <= MaxFloorSamples) return all;
            var picked = new List<XYZ>();
            double step = (double)all.Count / MaxFloorSamples;
            for (int i = 0; i < MaxFloorSamples; i++) picked.Add(all[(int)(i * step)]);
            return picked;
        }

        /// <summary>
        /// A point of the solid on the vertical line through p: p itself when acceptP and the line crosses the
        /// solid in a piece that contains p; else the middle of the longest piece it crosses; null when none.
        /// </summary>
        private static XYZ InteriorOnVertical(Solid solid, XYZ p, bool acceptP)
        {
            try
            {
                Line probe = Line.CreateBound(p - new XYZ(0, 0, 1000), p + new XYZ(0, 0, 1000));
                SolidCurveIntersection hits = solid.IntersectWithCurve(probe,
                    new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside });
                XYZ best = null;
                double bestLength = 0;
                for (int i = 0; i < hits.SegmentCount; i++)
                {
                    Curve seg = hits.GetCurveSegment(i);
                    XYZ a = seg.GetEndPoint(0), b = seg.GetEndPoint(1);
                    if (acceptP && p.Z >= Math.Min(a.Z, b.Z) - 1e-6 && p.Z <= Math.Max(a.Z, b.Z) + 1e-6) return p;
                    double length = Math.Abs(b.Z - a.Z);
                    if (length > bestLength) { bestLength = length; best = (a + b) * 0.5; }
                }
                return best;
            }
            catch { return null; }
        }
    }
}
