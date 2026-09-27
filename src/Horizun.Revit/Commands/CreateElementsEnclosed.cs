// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_create_elements, placement='all_enclosed'. Original
// Horizun code.
//
// One room (or space) in every closed circuit of a level IN A PHASE that does not
// already hold one. The circuits come from Revit's own PlanTopology(level, phase);
// the entry is EXPANDED here, on every call, into one ordinary room/space row per
// circuit to fill - so the rehearsal lists every circuit it saw (area, Revit's
// interior point, whether a room/space already stands there, what will happen to
// it) and a wall moved between rehearsal and apply resolves a different plan, which
// the token refuses as stale.
//
// Why per circuit and not NewRooms2/NewSpaces2: those fill EVERY empty circuit in
// one call, so a caller's min_area (the shafts and chases nobody wants a room in)
// could only be honoured by creating and then deleting. Rooms are placed with
// NewRoom(Phase) + NewRoom(Room, PlanCircuit) - the circuit itself, no point
// guessing; spaces with NewSpace(Level, Phase, UV) at the circuit's own interior
// point (the API has no NewSpace(Space, PlanCircuit)).
//
// PlanCircuit exposes an area, a side count and ONE interior point - no centroid
// and no boundary. The rehearsal says point_inside, never centroid.
//
// NOT PROVEN: whether walls of a LINKED model marked Room Bounding close a
// PlanTopology circuit of the host. The reply says so; the live probe measures it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        private const double SquareFeetToM2 = 0.09290304;
        /// <summary>How close a re-read interior point must land to be the SAME circuit (feet, ~0.3 mm).</summary>
        private const double CircuitMatchFeet = 1e-3;

        /// <summary>
        /// Replaces every placement='all_enclosed' entry with one room/space row per
        /// circuit to fill. Null = fine (block is null when no entry asked for it).
        /// </summary>
        private static string ExpandEnclosed(Document doc, JObject request, ref JArray input, out JArray block)
        {
            block = null;
            for (int i = 0; i < input.Count; i++)
            {
                var r = input[i] as JObject;
                if (r == null) continue;
                // The expansion's own bookkeeping: a caller's row carrying it would be planned as a circuit it never was.
                if (r["enclosed_from"] != null || r["circuit_area_m2"] != null)
                    return "elements[" + i + "]: enclosed_from and circuit_area_m2 are written by placement='all_enclosed', not by a caller.";
                // A point room/space goes in with NewRoom(Level, UV)/NewSpace(Level, UV), in the phase Revit
                // gives it: a phase_id or min_area_m2 beside a point would be accepted and silently ignored.
                if (r["placement"] == null && (r["phase_id"] != null || r["min_area_m2"] != null))
                    return "elements[" + i + "]: phase_id and min_area_m2 go with placement='all_enclosed' only.";
            }
            if (!input.OfType<JObject>().Any(o => o["placement"] != null)) return null;
            double scale;
            if (!Scale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out scale)) return "units must be mm, m or feet.";
            var expanded = new JArray();
            block = new JArray();
            for (int i = 0; i < input.Count; i++)
            {
                var o = input[i] as JObject;
                string placement = o?.Value<string>("placement");
                if (placement == null) { expanded.Add(input[i]); continue; }
                string where = "elements[" + i + "]: ";
                if (placement != "all_enclosed") return where + "placement must be all_enclosed.";
                string kind = (o.Value<string>("kind") ?? "").ToLowerInvariant();
                if (kind != "room" && kind != "space") return where + "placement='all_enclosed' is for kind room or space only.";
                if (o["point"] != null) return where + "all_enclosed finds the points itself - give no point.";
                if (o["name"] != null || o["number"] != null)
                    return where + "a name or number would repeat on every room of the level; name them afterwards " +
                           "with horizun_write_params_verified.";
                Level level = Rid.CanRepresent(o.Value<long?>("level_id") ?? -1) ? doc.GetElement(Rid.Make(o.Value<long>("level_id"))) as Level : null;
                if (level == null) return where + "level_id must identify a Level.";
                Phase phase = Rid.CanRepresent(o.Value<long?>("phase_id") ?? -1) ? doc.GetElement(Rid.Make(o.Value<long>("phase_id"))) as Phase : null;
                if (phase == null)
                    return where + "phase_id is required and must identify a Phase: circuits and rooms exist per phase, " +
                           "and the phase is never guessed.";
                double minArea = o.Value<double?>("min_area_m2") ?? 0;
                string minBad = EnclosedRules.MinAreaProblem(minArea);
                if (minBad != null) return where + minBad;

                PlanTopology topology;
                try { topology = doc.get_PlanTopology(level, phase); }
                catch (Exception ex) { return where + "Revit could not give the plan topology of that level and phase: " + ex.Message; }
                var circuits = new JArray();
                int toCreate = 0;
                foreach (PlanCircuit c in topology.Circuits)
                {
                    UV pt = c.GetPointInside();
                    double areaM2 = c.Area * SquareFeetToM2;
                    bool located = kind == "room" ? c.IsRoomLocated : SpaceAt(doc, level, phase, pt) != null;
                    string action = EnclosedRules.CircuitAction(kind, located, areaM2, minArea);
                    circuits.Add(new JObject
                    {
                        ["point_inside"] = new JArray(Math.Round(pt.U / scale, 3), Math.Round(pt.V / scale, 3)),
                        ["area_m2"] = Math.Round(areaM2, 3), ["sides"] = c.SideNum,
                        ["is_room_located"] = c.IsRoomLocated, ["action"] = action
                    });
                    if (action != EnclosedRules.Create) continue;
                    toCreate++;
                    expanded.Add(new JObject
                    {
                        ["kind"] = kind, ["level_id"] = Rid.Value(level.Id), ["phase_id"] = Rid.Value(phase.Id),
                        ["point"] = new JArray(pt.U / scale, pt.V / scale, level.ProjectElevation / scale),
                        ["enclosed_from"] = i, ["circuit_area_m2"] = Math.Round(areaM2, 3)
                    });
                }
                block.Add(new JObject
                {
                    ["index"] = i, ["kind"] = kind, ["level"] = level.Name, ["level_id"] = Rid.Value(level.Id),
                    ["phase"] = phase.Name, ["phase_id"] = Rid.Value(phase.Id), ["min_area_m2"] = minArea,
                    ["circuits"] = circuits, ["circuits_seen"] = circuits.Count, ["to_create"] = toCreate,
                    ["point_inside_means"] = "PlanCircuit exposes an area, a side count and one interior point - no centroid; point_inside is Revit's own.",
                    ["link_bounding"] = "not_proven: whether Room Bounding walls of a LINKED model close a host circuit is not established by this build; measure it live."
                });
            }
            input = expanded;
            return null;
        }

        /// <summary>Every circuit already holds one, or is under min_area: nothing to write, and the listing says why.</summary>
        private static CommandResult NothingEnclosed(JObject request, JArray block)
        {
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            return CommandResult.Ok(new JObject
            {
                ["dry_run"] = dryRun, ["transaction_status"] = "not_started", ["requested"] = 0, ["created"] = 0,
                ["enclosed"] = block,
                ["note"] = "no circuit is left to fill: each one listed already holds a room/space or is under min_area_m2. Nothing was written."
            });
        }

        /// <summary>
        /// The row ValidateCreation judges. An expanded row carries the expansion's own bookkeeping
        /// (enclosed_from, circuit_area_m2), which is not a caller field; ExpandEnclosed refuses a
        /// caller's row that carries it, so only rows it wrote reach here with it.
        /// </summary>
        private static JObject EnclosedPublicView(JObject item)
        {
            if (item["enclosed_from"] == null) return item;
            var copy = (JObject)item.DeepClone();
            copy.Remove("enclosed_from"); copy.Remove("circuit_area_m2");
            return copy;
        }

        private static Space SpaceAt(Document doc, Level level, Phase phase, UV pt)
        {
            // Half a foot above the level: inside any space standing on it, whatever its height.
            try { return doc.GetSpaceAtPoint(new XYZ(pt.U, pt.V, level.ProjectElevation + 0.5), phase); }
            catch { return null; }
        }

        /// <summary>PlanItem half: the row came from ExpandEnclosed (it carries phase_id).</summary>
        private static void PlanEnclosed(Document doc, JObject item, Plan p)
        {
            p.Phase = doc.GetElement(Rid.Make(item.Value<long>("phase_id"))) as Phase
                      ?? throw new ArgumentException("phase_id must identify a Phase");
            p.Enclosed = true;
            p.ExtraPlanFacts = p.ExtraPlanFacts ?? new Dictionary<string, string>();
            p.ExtraPlanFacts["enclosed.phase_uid"] = SafePlanUid(p.Phase);
            p.ExtraPlanFacts["enclosed.point"] = Canon01(p.Start);
            p.ExtraPlanFacts["enclosed.area_m2"] = (item.Value<double?>("circuit_area_m2") ?? -1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Create half, inside the batch transaction.</summary>
        private static Element CreateEnclosed(Document doc, Plan p)
        {
            if (p.Kind == "space")
            {
                Space space = doc.Create.NewSpace(p.Level, p.Phase, new UV(p.Start.X, p.Start.Y));
                if (space == null) throw new InvalidOperationException("Revit placed no space in that circuit. Nothing was kept.");
                return space;
            }
            // The circuit is re-read NOW and matched by Revit's own interior point: a
            // PlanCircuit from the rehearsal belongs to a topology that no longer exists.
            PlanCircuit circuit = null;
            foreach (PlanCircuit c in doc.get_PlanTopology(p.Level, p.Phase).Circuits)
            {
                UV q = c.GetPointInside();
                if (Math.Abs(q.U - p.Start.X) <= CircuitMatchFeet && Math.Abs(q.V - p.Start.Y) <= CircuitMatchFeet) { circuit = c; break; }
            }
            if (circuit == null)
                throw new InvalidOperationException("enclosed_circuit_gone: no circuit of that level and phase still has its interior " +
                    "point where the plan found it - the bounding walls changed. Nothing was kept.");
            if (circuit.IsRoomLocated)
                throw new InvalidOperationException("a room already stands in that circuit. Nothing was kept.");
            Room room = doc.Create.NewRoom(p.Phase);
            doc.Create.NewRoom(room, circuit);
            return room;
        }

        /// <summary>Every boundary loop the element reports closes on itself (end of the last segment = start of the first).</summary>
        private static bool BoundaryClosed(Element e)
        {
            try
            {
                IList<IList<BoundarySegment>> loops = ((SpatialElement)e).GetBoundarySegments(new SpatialElementBoundaryOptions());
                if (loops == null || loops.Count == 0) return false;
                foreach (IList<BoundarySegment> loop in loops)
                {
                    if (loop == null || loop.Count == 0) return false;
                    XYZ first = loop[0].GetCurve().GetEndPoint(0), last = loop[loop.Count - 1].GetCurve().GetEndPoint(1);
                    if (first.DistanceTo(last) > 1e-3) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static long PhaseOf(Element e)
        {
            try { return Rid.Value(e.get_Parameter(BuiltInParameter.ROOM_PHASE_ID)?.AsElementId() ?? ElementId.InvalidElementId); }
            catch { return -1; }
        }
    }
}
