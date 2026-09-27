// Room membership: which room (and which MEP space) of ONE phase an element is in.
// Shared by horizun_query_model include_room and horizun_quantities takeoff group_by='room'.
//
// THE TWO WRITTEN RULES (docs/TOOLS-EXTENDED.md, "Room membership"):
//  1. THE PHASE IS MANDATORY. Rooms and spaces exist per phase; a hidden default (the last
//     phase) would silently answer for a different building. It is the HOST's phase.
//  2. A LINKED element is placed in the HOST's rooms: its sample point goes through its
//     RevitLinkInstance.GetTotalTransform and the host answers GetRoomAtPoint(pt, phase).
//     Whether the link is room-bounding does not change that query - it only shapes the
//     host's rooms (a host room that needs the link's walls to close is not enclosed
//     without them, and holds nothing). Rooms INSIDE a linked model are not read.
//
// SAMPLING (Core/RoomQuantityRules.cs, RoomMembershipRules.SampleBasis):
//  - point-based elements at their LocationPoint, lifted 1 mm so an element standing on
//    its level is not lost on the room's bottom boundary;
//  - curve-based elements at their curve's midpoint, not lifted;
//  - walls at their largest solid's centroid, and only when a vertical line through it
//    proves the centroid lies INSIDE the wall (a curved wall's centroid can fall outside
//    it, into the very room it bounds). A room-bounding wall's centroid lies outside every
//    room computed at the wall finish, so it comes back UNASSIGNED - by design: a wall
//    that separates two rooms belongs to neither;
//  - floors at a point ON their top face (the centroid of one triangle of it, which lies
//    on the face even for an L-shaped floor), lifted 1 mm: a floor's body sits below the
//    room it carries, so its own solid is never inside that room.
// A miss is 'unassigned'. An element with no sample is 'unlocatable' - never folded into
// unassigned, never guessed.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal sealed class RoomHit
    {
        public string State;          // assigned | unassigned | unlocatable
        public string Basis;
        public XYZ Point;             // host coordinates, feet
        public SpatialElement Room;
        public SpatialElement Space;
        public string Reason;
        public string SpaceProblem;

        /// <summary>The by_room key: the room id, else '(unlocatable)' or '(unassigned)'.</summary>
        public string RoomKey => Room != null ? Rid.Value(Room.Id).ToString()
            : State == "unlocatable" ? RoomMembershipReader.UnlocatableKey : RoomMembershipRules.GroupKey(null);
    }

    internal sealed class RoomMembershipReader
    {
        public const string UnlocatableKey = "(unlocatable)";
        private const double LiftFeet = 1.0 / 304.8;   // 1 mm

        public const string Rules =
            "Points at their location point (+1 mm); curves at their midpoint; walls at their solid's centroid, proven " +
            "inside the wall; floors at a point on their top face (+1 mm). A room-bounding wall lies outside every room " +
            "computed at the wall finish, so it is unassigned by design. Linked elements: the point is moved by the link " +
            "instance's total transform and placed in the HOST's rooms of this phase, room-bounding link or not; rooms " +
            "inside a link are not read. No sample = unlocatable, never unassigned.";

        private readonly Document _host;
        private readonly Phase _phase;
        private readonly Options _options = new Options
        {
            ComputeReferences = false, IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Coarse
        };
        public int Assigned, Unassigned, Unlocatable, InSpace, SpaceUnreadable;

        private RoomMembershipReader(Document host, Phase phase) { _host = host; _phase = phase; }

        public string PhaseName => _phase.Name;

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
            XYZ local;
            string reason;
            try { local = Sample(e, out hit.Basis, out reason); }
            catch (Exception ex) { local = null; reason = "the sample point could not be read: " + ex.Message; }
            if (local == null)
            {
                hit.State = "unlocatable"; hit.Reason = reason; Unlocatable++;
                return hit;
            }
            hit.Point = toHost == null ? local : toHost.OfPoint(local);
            try { hit.Room = _host.GetRoomAtPoint(hit.Point, _phase); }
            catch (Exception ex)
            {
                hit.State = "unlocatable"; hit.Reason = "GetRoomAtPoint failed: " + ex.Message; Unlocatable++;
                return hit;
            }
            // The space is read beside the room, and a failed read is said, not nulled.
            try { hit.Space = _host.GetSpaceAtPoint(hit.Point, _phase); }
            catch (Exception ex) { hit.SpaceProblem = "GetSpaceAtPoint failed: " + ex.Message; SpaceUnreadable++; }
            if (hit.Space != null) InSpace++;
            if (hit.Room != null) { hit.State = "assigned"; Assigned++; }
            else { hit.State = "unassigned"; Unassigned++; }
            return hit;
        }

        public JObject ToJson(RoomHit h, double scale)
        {
            var j = new JObject
            {
                ["state"] = h.State,
                ["room"] = Describe(h.Room),
                ["space"] = Describe(h.Space),
                ["basis"] = h.Basis,
                ["sample_point"] = h.Point == null ? JValue.CreateNull()
                    : new JArray(Math.Round(h.Point.X * scale, 3), Math.Round(h.Point.Y * scale, 3), Math.Round(h.Point.Z * scale, 3))
            };
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
                                        "wall's centroid can sit ON a room's boundary, and which side Revit answers there is Revit's.";
            return j;
        }

        private XYZ Sample(Element e, out string basis, out string reason)
        {
            reason = null;
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
                    return lp.Point + new XYZ(0, 0, LiftFeet);
                case "curve_midpoint":
                    return curve.Evaluate(0.5, true);
                case "solid_interior":
                    if (isFloor)
                    {
                        basis = "floor_top_face";
                        XYZ top = TopFacePoint((Floor)e, out reason);
                        return top == null ? null : top + new XYZ(0, 0, LiftFeet);
                    }
                    basis = "wall_solid_centroid";
                    XYZ c = solid.ComputeCentroid();
                    if (InsideVertically(solid, c)) return c;
                    reason = "the wall's solid centroid lies outside the wall (a curved or non-convex wall); it is not guessed.";
                    return null;
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

        /// <summary>A point on a floor's top face: the centroid of one triangle of it.</summary>
        private static XYZ TopFacePoint(Floor floor, out string reason)
        {
            reason = null;
            IList<Reference> refs;
            try { refs = HostObjectUtils.GetTopFaces(floor); }
            catch (Exception ex) { reason = "the floor's top faces could not be read: " + ex.Message; return null; }
            foreach (Reference r in refs)
            {
                Face f = null;
                try { f = floor.GetGeometryObjectFromReference(r) as Face; } catch { }
                if (f == null) continue;
                Mesh m = null;
                try { m = f.Triangulate(); } catch { }
                if (m == null || m.NumTriangles == 0) continue;
                MeshTriangle t = m.get_Triangle(0);
                return (t.get_Vertex(0) + t.get_Vertex(1) + t.get_Vertex(2)) * (1.0 / 3.0);
            }
            reason = "no top face of the floor could be read and triangulated.";
            return null;
        }

        /// <summary>True when a vertical line through p crosses the solid in a segment that contains p.</summary>
        private static bool InsideVertically(Solid solid, XYZ p)
        {
            try
            {
                Line probe = Line.CreateBound(p - new XYZ(0, 0, 1000), p + new XYZ(0, 0, 1000));
                SolidCurveIntersection hits = solid.IntersectWithCurve(probe,
                    new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside });
                for (int i = 0; i < hits.SegmentCount; i++)
                {
                    Curve seg = hits.GetCurveSegment(i);
                    double z0 = seg.GetEndPoint(0).Z, z1 = seg.GetEndPoint(1).Z;
                    if (p.Z >= Math.Min(z0, z1) - 1e-6 && p.Z <= Math.Max(z0, z1) + 1e-6) return true;
                }
            }
            catch { }
            return false;
        }
    }
}
