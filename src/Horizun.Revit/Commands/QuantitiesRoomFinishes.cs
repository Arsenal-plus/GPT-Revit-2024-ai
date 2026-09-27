// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// horizun_quantities mode='room_finishes' - the faces of each room, who bounds
// them, and the openings in those walls, measured and never netted.
//
// Revit's SpatialElementGeometryCalculator builds the room's solid at the FINISH
// face of its boundaries and tells, face by face, which element bounds each piece
// (GetBoundaryFaceInfo -> SpatialElementBoundarySubface). That is the gross wall,
// floor and ceiling area a finishes schedule starts from.
//
// The room solid is NOT cut by the doors and windows of its walls - it runs straight
// past them. So the face area here is gross, and the openings of each bounding wall
// that face this room are measured separately (rough size when the family publishes
// it, else nominal, else the bounding box - and each row says which) and reported in
// their own column. Deducting them is the reader's rule (many contracts do not deduct
// small openings); a net computed here could not be taken apart again.
//
// What is NOT counted is NAMED: unplaced rooms, unenclosed rooms, redundant rooms,
// rooms of another phase, geometry the calculator refused. None of them is a zero.
//
// LINKS. A room bounded by a linked wall gets that face's area (it is the room's own
// geometry) and the linked element's type, material and code read from the link
// document. The openings of a LINKED wall are not read: FamilyInstance.FromRoom/ToRoom
// in the link answer the link's rooms, not this document's, so a deduction from them
// would be attributed by a different model's room layout. They are named instead.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public partial class QuantitiesCommand
    {
        private const double FeetToM = 0.3048;

        /// <summary>The phase the caller named, exactly (case-insensitive). No default: rooms exist per phase.</summary>
        private static Phase FindPhase(Document doc, string name, out string problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                problem = "phase is required: rooms, spaces and the doors facing them are phase-dependent, and a hidden " +
                          "default (the last phase) would silently measure a different building. Name the phase.";
                return null;
            }
            var names = new List<string>();
            foreach (Phase p in doc.Phases)
            {
                names.Add(p.Name);
                if (string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) return p;
            }
            problem = "No phase is named '" + name + "'. Phases in this document: " + string.Join(", ", names) + ".";
            return null;
        }

        private static long? PhaseIdOf(SpatialElement se)
        {
            try
            {
                var p = se.get_Parameter(BuiltInParameter.ROOM_PHASE);
                if (p == null || !p.HasValue) return null;
                return Rid.Value(p.AsElementId());
            }
            catch { return null; }
        }

        private static bool SpatialContains(SpatialElement se, XYZ p)
        {
            try
            {
                var room = se as Autodesk.Revit.DB.Architecture.Room;
                if (room != null) return room.IsPointInRoom(p);
                var space = se as Autodesk.Revit.DB.Mechanical.Space;
                if (space != null) return space.IsPointInSpace(p);
            }
            catch { }
            return false;
        }

        private CommandResult ExecuteRoomFinishes(Document doc, JObject request, int top)
        {
            string problem;
            Phase phase = FindPhase(doc, request.Value<string>("phase"), out problem);
            if (phase == null) return CommandResult.Fail("mode 'room_finishes': " + problem + " Nothing was measured.");
            long phaseId = Rid.Value(phase.Id);
            string codeParameter = request.Value<string>("code_parameter");
            if (string.IsNullOrWhiteSpace(codeParameter)) codeParameter = null;
            string levelName = request.Value<string>("level");
            if (string.IsNullOrWhiteSpace(levelName)) levelName = null;

            // ---- The spatial elements in scope. ----
            var notMeasured = new JArray();
            var scope = new List<SpatialElement>();
            var idsToken = request["element_ids"] as JArray;
            bool explicitIds = idsToken != null && idsToken.Count > 0;
            int otherPhase = 0;
            var all = new List<SpatialElement>();
            foreach (var se in new FilteredElementCollector(doc).OfClass(typeof(SpatialElement)).Cast<SpatialElement>())
                if (se is Autodesk.Revit.DB.Architecture.Room || se is Autodesk.Revit.DB.Mechanical.Space) all.Add(se);

            if (explicitIds)
            {
                foreach (var tok in idsToken)
                {
                    long id;
                    if (tok.Type != JTokenType.Integer || !Rid.CanRepresentElementId(id = tok.Value<long>()))
                    { notMeasured.Add(new JObject { ["element_id"] = tok.ToString(), ["state"] = "invalid_id" }); continue; }
                    var e = doc.GetElement(Rid.ToElementId(id));
                    var se = e as SpatialElement;
                    if (se == null || !(se is Autodesk.Revit.DB.Architecture.Room || se is Autodesk.Revit.DB.Mechanical.Space))
                    { notMeasured.Add(new JObject { ["element_id"] = id, ["state"] = e == null ? "not_found" : "not_a_room_or_space" }); continue; }
                    scope.Add(se);
                }
            }
            else scope.AddRange(all);

            var calcOptions = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
            var calc = new SpatialElementGeometryCalculator(doc, calcOptions);

            var faces = new List<FinishFaceFact>();
            var openings = new List<OpeningDeductionFact>();
            var roomRows = new JArray();
            var linkedBounds = new JArray();
            var otherInserts = new JArray();
            var linkedSeen = new HashSet<string>(StringComparer.Ordinal);
            int measured = 0;

            foreach (var se in scope)
            {
                long sid = Rid.Value(se.Id);
                string number = Safe(() => se.Number), name = Safe(() => se.Name), lvl = Safe(() => se.Level?.Name);
                string kind = se is Autodesk.Revit.DB.Architecture.Room ? "room" : "space";
                if (levelName != null && !string.Equals(lvl, levelName, StringComparison.OrdinalIgnoreCase))
                {
                    if (explicitIds) notMeasured.Add(NotMeasured(sid, kind, number, name, lvl, "other_level", "Its level is '" + lvl + "', not '" + levelName + "'."));
                    continue;
                }
                long? pid = PhaseIdOf(se);
                if (pid != phaseId)
                {
                    if (explicitIds) notMeasured.Add(NotMeasured(sid, kind, number, name, lvl, "other_phase", "It belongs to another phase (id " + (pid?.ToString() ?? "unreadable") + ")."));
                    else otherPhase++;
                    continue;
                }
                var lp = se.Location as LocationPoint;
                if (lp == null)
                {
                    notMeasured.Add(NotMeasured(sid, kind, number, name, lvl, "unplaced", "It is not placed in the model, so it has no faces. Not a zero."));
                    continue;
                }
                double area = AreaOf(se);
                if (area <= 0)
                {
                    // Redundant = its point lies inside another placed, enclosed room of the same phase.
                    SpatialElement owner = all.FirstOrDefault(o => o.Id != se.Id && PhaseIdOf(o) == phaseId &&
                        o.GetType() == se.GetType() && AreaOf(o) > 0 && SpatialContains(o, lp.Point));
                    notMeasured.Add(owner != null
                        ? NotMeasured(sid, kind, number, name, lvl, "redundant", "It shares its enclosure with " + kind + " " + Rid.Value(owner.Id) + " (its point lies inside that one).")
                        : NotMeasured(sid, kind, number, name, lvl, "not_enclosed", "Its boundaries do not close, so Revit gives it no area. Not a zero."));
                    continue;
                }

                SpatialElementGeometryResults results;
                Solid solid;
                try { results = calc.CalculateSpatialElementGeometry(se); solid = results.GetGeometry(); }
                catch (Exception ex)
                {
                    notMeasured.Add(NotMeasured(sid, kind, number, name, lvl, "geometry_failed", "The geometry calculator refused it: " + ex.Message));
                    continue;
                }
                if (solid == null)
                {
                    notMeasured.Add(NotMeasured(sid, kind, number, name, lvl, "geometry_failed", "The geometry calculator returned no solid."));
                    continue;
                }

                string roomKey = sid.ToString();
                var hostWalls = new Dictionary<long, Wall>();
                double unbounded = 0;
                foreach (Face face in solid.Faces)
                {
                    double faceM2 = face.Area * RoomFinishRules.SquareFeetToM2;
                    IList<SpatialElementBoundarySubface> subs = null;
                    try { subs = results.GetBoundaryFaceInfo(face); } catch { }
                    double boundedM2 = 0;
                    if (subs != null)
                        foreach (var sub in subs)
                        {
                            string surface = RoomFinishRules.SurfaceOf(sub.SubfaceType.ToString());
                            double m2 = 0;
                            try { m2 = sub.GetSubface().Area * RoomFinishRules.SquareFeetToM2; } catch { m2 = 0; }
                            boundedM2 += m2;
                            faces.Add(BoundedFace(doc, sub, roomKey, surface ?? SurfaceOfNormal(face), m2, codeParameter,
                                                  hostWalls, linkedBounds, linkedSeen, sid));
                        }
                    double rest = faceM2 - boundedM2;
                    if (rest > 1e-6)
                    {
                        // The part of a room face nothing bounds (a room limit above an unbounded top, a gap):
                        // its own row, under a named type, so the totals still add up to the room.
                        unbounded += rest;
                        faces.Add(new FinishFaceFact { RoomKey = roomKey, Surface = SurfaceOfNormal(face), GrossM2 = rest });
                    }
                }

                foreach (var kv in hostWalls)
                    CollectOpenings(doc, se, phase, kv.Value, roomKey, openings, otherInserts);

                measured++;
                roomRows.Add(new JObject
                {
                    ["id"] = sid, ["kind"] = kind, ["number"] = number, ["name"] = name, ["level"] = lvl,
                    ["area_m2"] = Math.Round(area * RoomFinishRules.SquareFeetToM2, 4),
                    ["unbounded_face_m2"] = Math.Round(unbounded, 4)
                });
            }

            List<OpeningDeductionFact> orphans;
            var groups = RoomFinishRules.Group(faces, openings, out orphans);
            var rows = new JArray();
            foreach (var g in groups.Take(top))
                rows.Add(new JObject
                {
                    ["room_id"] = long.Parse(g.RoomKey), ["surface"] = g.Surface, ["bounding_type"] = g.TypeName,
                    ["material"] = g.Material, ["code"] = g.Code,
                    ["gross_m2"] = Math.Round(g.GrossM2, 4),
                    ["openings_deduction_m2"] = g.Surface == "wall" ? (JToken)Math.Round(g.OpeningDeductionM2, 4) : JValue.CreateNull(),
                    ["openings"] = g.Openings, ["openings_unsized"] = g.OpeningsUnsized,
                    ["opening_size_basis"] = new JArray(g.SizeBases),
                    ["opening_ids"] = new JArray(g.OpeningIds),
                    ["bounding_element_keys"] = new JArray(g.AreaByBoundingKey.Keys)
                });

            var bySurface = new JObject();
            foreach (var s in new[] { "wall", "floor", "ceiling" })
                bySurface[s] = Math.Round(groups.Where(g => g.Surface == s).Sum(g => g.GrossM2), 4);

            bool complete = notMeasured.Count == 0 && orphans.Count == 0 && groups.All(g => g.OpeningsUnsized == 0);
            return CommandResult.Ok(new JObject
            {
                ["mode"] = "room_finishes",
                ["phase"] = phase.Name,
                ["boundary_location"] = "Finish",
                ["code_parameter"] = codeParameter,
                ["rule"] = "gross_m2 is the room face, which Revit does NOT cut at openings. openings_deduction_m2 is the " +
                           "measured size of the doors/windows of each bounding wall that face this room, in its own column: " +
                           "no net is computed here - deduct by your contract's rule.",
                ["rooms"] = roomRows,
                ["rows"] = rows,
                ["rows_total"] = groups.Count,
                ["truncated"] = groups.Count > top,
                ["gross_m2_by_surface"] = bySurface,
                ["openings_deduction_m2_total"] = Math.Round(groups.Sum(g => g.OpeningDeductionM2), 4),
                ["not_measured"] = notMeasured,
                ["rooms_in_other_phases"] = otherPhase,
                ["openings_not_attributed"] = new JArray(orphans.Select(o => new JObject { ["room_id"] = o.RoomKey, ["wall"] = o.BoundingKey, ["insert_id"] = o.InsertId })),
                ["inserts_not_deducted"] = otherInserts,
                ["linked_bounding_elements"] = linkedBounds,
                ["links_rule"] = "A linked bounding element contributes its face area, and its type, material and code read " +
                                 "from the link document. Openings hosted in a LINKED wall are not read (their From/To room " +
                                 "answers the link's rooms), so a room bounded by linked walls carries no deduction for them.",
                ["coverage"] = new JObject
                {
                    ["rooms_measured"] = measured, ["rooms_not_measured"] = notMeasured.Count,
                    ["complete"] = complete
                }
            });
        }

        private static JObject NotMeasured(long id, string kind, string number, string name, string level, string state, string why)
            => new JObject { ["id"] = id, ["kind"] = kind, ["number"] = number, ["name"] = name, ["level"] = level, ["state"] = state, ["reason"] = why };

        private static string SurfaceOfNormal(Face face)
        {
            try
            {
                var pf = face as PlanarFace;
                if (pf != null)
                {
                    if (pf.FaceNormal.Z > 0.99) return "ceiling";
                    if (pf.FaceNormal.Z < -0.99) return "floor";
                }
            }
            catch { }
            return "wall";
        }

        private static FinishFaceFact BoundedFace(Document doc, SpatialElementBoundarySubface sub, string roomKey, string surface,
                                                  double m2, string codeParameter, Dictionary<long, Wall> hostWalls,
                                                  JArray linkedBounds, HashSet<string> linkedSeen, long roomId)
        {
            var fact = new FinishFaceFact { RoomKey = roomKey, Surface = surface, GrossM2 = m2 };
            LinkElementId lid = null;
            try { lid = sub.SpatialBoundaryElement; } catch { }
            if (lid == null) { fact.BoundingKey = "unreadable"; fact.TypeName = "(unreadable bounding element)"; return fact; }

            Document owner = doc;
            Element be;
            if (lid.LinkInstanceId != ElementId.InvalidElementId)
            {
                var link = doc.GetElement(lid.LinkInstanceId) as RevitLinkInstance;
                owner = link == null ? null : Safe(() => link.GetLinkDocument());
                be = owner == null ? null : owner.GetElement(lid.LinkedElementId);
                fact.BoundingKey = "link:" + Rid.Value(lid.LinkInstanceId) + "/" + Rid.Value(lid.LinkedElementId);
                if (linkedSeen.Add(roomKey + "|" + fact.BoundingKey))
                    linkedBounds.Add(new JObject
                    {
                        ["room_id"] = roomId, ["link_instance_id"] = Rid.Value(lid.LinkInstanceId),
                        ["linked_element_id"] = Rid.Value(lid.LinkedElementId),
                        ["read"] = be == null ? "area only: the link document is not loaded or the element is gone" : "area, type, material, code",
                        ["not_read"] = "hosted openings"
                    });
            }
            else
            {
                be = doc.GetElement(lid.HostElementId);
                fact.BoundingKey = "host:" + Rid.Value(lid.HostElementId);
                var wall = be as Wall;
                if (wall != null) hostWalls[Rid.Value(wall.Id)] = wall;
            }

            if (be == null) { fact.TypeName = "(unreadable bounding element)"; fact.Material = "(unreadable)"; return fact; }
            fact.TypeName = (SafeCategory(be) ?? "(no category)") + ": " + (SafeTypeName(owner, be) ?? SafeName(be) ?? "(no type)");
            try
            {
                Face bf = sub.GetBoundingElementFace();
                if (bf == null) fact.Material = "(face unreadable)";
                else
                {
                    var mid = bf.MaterialElementId;
                    var mat = mid == null || mid == ElementId.InvalidElementId ? null : owner.GetElement(mid) as Material;
                    fact.Material = mat?.Name ?? "(no material on face)";
                }
            }
            catch { fact.Material = "(unreadable)"; }
            if (codeParameter != null) fact.Code = ReadCode(owner, be, codeParameter);
            return fact;
        }

        /// <summary>Doors and windows of one bounding wall that face this room, each sized with its basis.</summary>
        private static void CollectOpenings(Document doc, SpatialElement se, Phase phase, Wall wall, string roomKey,
                                            List<OpeningDeductionFact> openings, JArray otherInserts)
        {
            IList<ElementId> inserts;
            try { inserts = wall.FindInserts(false, false, false, false); } catch { return; }
            XYZ along = null;
            try { along = ((wall.Location as LocationCurve)?.Curve as Line)?.Direction; } catch { }
            foreach (var iid in inserts)
            {
                var fi = doc.GetElement(iid) as FamilyInstance;
                long? cat = null;
                try { cat = fi?.Category == null ? (long?)null : Rid.Value(fi.Category.Id); } catch { }
                bool doorOrWindow = cat == (long)BuiltInCategory.OST_Doors || cat == (long)BuiltInCategory.OST_Windows;
                if (!doorOrWindow)
                {
                    otherInserts.Add(new JObject { ["room_id"] = long.Parse(roomKey), ["wall_id"] = Rid.Value(wall.Id), ["insert_id"] = Rid.Value(iid),
                                                   ["category"] = SafeCategory(doc.GetElement(iid)) });
                    continue;
                }
                if (!FacesRoom(fi, se, phase, wall)) continue;

                var fact = new OpeningDeductionFact { RoomKey = roomKey, BoundingKey = "host:" + Rid.Value(wall.Id), InsertId = Rid.Value(iid).ToString() };
                double? w = SizeParam(doc, fi, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM), h = SizeParam(doc, fi, BuiltInParameter.FAMILY_ROUGH_HEIGHT_PARAM);
                if (w.HasValue && h.HasValue) fact.SizeBasis = "rough";
                else
                {
                    w = SizeParam(doc, fi, BuiltInParameter.FAMILY_WIDTH_PARAM); h = SizeParam(doc, fi, BuiltInParameter.FAMILY_HEIGHT_PARAM);
                    if (w.HasValue && h.HasValue) fact.SizeBasis = "nominal";
                    else
                    {
                        // The box of the instance, measured along the wall: overstates a frame, never invents one.
                        w = null; h = null;
                        try
                        {
                            var bb = fi.get_BoundingBox(null);
                            if (bb != null && along != null)
                            {
                                var d = bb.Max - bb.Min;
                                w = (Math.Abs(d.X * along.X) + Math.Abs(d.Y * along.Y)) * FeetToM;
                                h = d.Z * FeetToM;
                                fact.SizeBasis = "bounding_box";
                            }
                        }
                        catch { }
                    }
                }
                fact.WidthM = w; fact.HeightM = h;
                openings.Add(fact);
            }
        }

        /// <summary>
        /// A room: Revit's own From/To room in that phase. A space has no From/To, so a point
        /// half a wall plus 150 mm to each side of the insert is probed.
        /// </summary>
        private static bool FacesRoom(FamilyInstance fi, SpatialElement se, Phase phase, Wall wall)
        {
            try
            {
                if (se is Autodesk.Revit.DB.Architecture.Room)
                {
                    var from = fi.get_FromRoom(phase); var to = fi.get_ToRoom(phase);
                    return (from != null && from.Id == se.Id) || (to != null && to.Id == se.Id);
                }
                var lp = fi.Location as LocationPoint;
                if (lp == null) return false;
                var bb = fi.get_BoundingBox(null);
                double z = bb != null ? (bb.Min.Z + bb.Max.Z) / 2 : lp.Point.Z + 1;
                var n = wall.Orientation;
                double off = wall.Width / 2 + 0.5;
                var c = new XYZ(lp.Point.X, lp.Point.Y, z);
                return SpatialContains(se, c + n * off) || SpatialContains(se, c - n * off);
            }
            catch { return false; }
        }

        /// <summary>A length parameter in metres, instance first then type; null when absent, empty or not positive.</summary>
        private static double? SizeParam(Document doc, FamilyInstance fi, BuiltInParameter bip)
        {
            foreach (Element e in new Element[] { fi, fi.Symbol })
            {
                try
                {
                    var p = e?.get_Parameter(bip);
                    if (p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > 0) return p.AsDouble() * FeetToM;
                }
                catch { }
            }
            return null;
        }

        private static double AreaOf(SpatialElement se) { try { return se.Area; } catch { return 0; } }

        private static T Safe<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }
    }
}
