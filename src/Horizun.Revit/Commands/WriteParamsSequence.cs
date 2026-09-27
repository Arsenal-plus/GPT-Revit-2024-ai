// -----------------------------------------------------------------------------
// Horizun - original Horizun code.
//
// horizun_write_params_verified 'sequence': the writes are GENERATED from the
// targets' spatial order instead of being listed - door, window and room marks
// numbered by level, x, y or the room each target stands in at a named phase.
//
// This file only READS the model (level, location point, room) and hands those
// datums to ParameterSequenceRules, which orders and formats without a Revit.
// The generated writes then ride the command's ordinary path unchanged: resolved,
// rehearsed, bound by the token (the request hash binds the options and the
// resolved plan binds every generated value, so a door moved between rehearsal
// and apply changes the plan and is refused as stale), written in one transaction
// and re-read row by row after the commit.
//
//   * A target missing a datum its order needs is NAMED and refuses the whole
//     generation (ParameterSequenceRules); nothing is sorted to an arbitrary end.
//   * Room order needs a phase: the same point can stand in one room in one phase
//     and in another (or none) in the next. Refused without phase_id.
//   * Every target's datums (level, x/y in internal mm, room and WHERE the room
//     came from) travel in the reply, so the order can be checked, not trusted.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;

using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public partial class WriteParamsCommand
    {
        private static readonly string[] SequenceFields =
            { "parameter", "order_by", "element_ids", "category", "prefix", "start", "step", "pad", "restart_per_level", "phase_id" };

        private const int SequenceOrderShown = 500;
        private const double FeetToMm = 304.8;

        private static CommandResult ExpandSequence(Document doc, JObject source, out JArray writes, out JObject report)
        {
            writes = new JArray(); report = null;
            var unknown = source.Properties().Select(p => p.Name).Where(n => Array.IndexOf(SequenceFields, n) < 0).ToList();
            if (unknown.Count > 0)
                return CommandResult.Fail("sequence does not take " + string.Join(", ", unknown) + "; its fields are " +
                    string.Join(", ", SequenceFields) + ". Nothing was written.");
            string parameter = source.Value<string>("parameter");
            if (string.IsNullOrWhiteSpace(parameter))
                return CommandResult.Fail("sequence.parameter is required: the parameter every generated value is " +
                    "written to (Mark, ROOM_NUMBER, a shared parameter...). Nothing was written.");

            var options = new SequenceOptions { Prefix = source.Value<string>("prefix") ?? "" };
            JToken orderToken = source["order_by"];
            if (orderToken is JArray orderArray)
                options.OrderBy = orderArray.Select(t => t.Type == JTokenType.String ? (string)t : t.ToString()).ToList();
            else if (orderToken != null && orderToken.Type == JTokenType.String)
                options.OrderBy = new List<string> { (string)orderToken };
            string numberError = ReadInteger(source, "start", v => options.Start = v)
                              ?? ReadInteger(source, "step", v => options.Step = v)
                              ?? ReadInteger(source, "pad", v => options.Pad = (int)Math.Max(-1, Math.Min(99, v)));
            if (numberError != null) return CommandResult.Fail(numberError + " Nothing was written.");
            JToken restart = source["restart_per_level"];
            if (restart != null && restart.Type != JTokenType.Boolean && restart.Type != JTokenType.Null)
                return CommandResult.Fail("sequence.restart_per_level must be true or false. Nothing was written.");
            options.RestartPerLevel = restart != null && restart.Type == JTokenType.Boolean && (bool)restart;

            // ---- The phase: required for room order, and a real phase of THIS document.
            Phase phase = null;
            JToken phaseToken = source["phase_id"];
            if (phaseToken != null && phaseToken.Type != JTokenType.Null)
            {
                long phaseId;
                if (phaseToken.Type != JTokenType.Integer || !Rid.CanRepresentElementId(phaseId = phaseToken.Value<long>()) ||
                    (phase = doc.GetElement(Rid.Make(phaseId)) as Phase) == null)
                    return CommandResult.Fail("sequence.phase_id " + phaseToken + " is not a phase of this document. " +
                        "Nothing was written.");
            }
            bool byRoom = options.OrderBy.Any(k => string.Equals((k ?? "").Trim(), "room", StringComparison.OrdinalIgnoreCase));
            if (byRoom && phase == null)
                return CommandResult.Fail("sequence.order_by room needs phase_id: a room exists in a phase, and the same " +
                    "point can stand in one room in one phase and in another (or none) in the next. Nothing was written.");

            // ---- The targets: named ids, or one category swept whole. Exactly one.
            var idsToken = source["element_ids"] as JArray;
            string categoryName = source.Value<string>("category");
            bool haveIds = idsToken != null && idsToken.Count > 0;
            bool haveCategory = !string.IsNullOrWhiteSpace(categoryName);
            if (haveIds == haveCategory)
                return CommandResult.Fail("sequence needs element_ids OR category (an OST_ BuiltInCategory name), exactly " +
                    "one: the set being numbered must be stated, and two statements of it cannot be reconciled. " +
                    "Nothing was written.");
            var elements = new List<Element>();
            var excluded = new JArray();
            if (haveIds)
            {
                var bad = new List<string>();
                foreach (JToken t in idsToken)
                {
                    long id;
                    Element e = t.Type == JTokenType.Integer && Rid.CanRepresentElementId(id = t.Value<long>())
                        ? doc.GetElement(Rid.Make(id)) : null;
                    if (e == null || e is ElementType) bad.Add(t.ToString());
                    else elements.Add(e);
                }
                if (bad.Count > 0)
                    return CommandResult.Fail("sequence.element_ids holds " + bad.Count + " id(s) that are not instance " +
                        "elements of this document: " + string.Join(", ", bad.Take(20)) + (bad.Count > 20 ? " ..." : "") +
                        ". Nothing was written.");
            }
            else
            {
                BuiltInCategory category;
                if (!Enum.TryParse(categoryName, false, out category))
                    return CommandResult.Fail("sequence.category '" + categoryName + "' is not a BuiltInCategory name. " +
                        "Nothing was written.");
                foreach (Element e in new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType())
                {
                    // An UNPLACED room/area/space has no position in the model: numbering it by position
                    // would be a guess. It is excluded BY ID, never silently dropped or sorted to an end.
                    if (e is SpatialElement && e.Location == null) { excluded.Add(Rid.Value(e.Id)); continue; }
                    elements.Add(e);
                }
                if (elements.Count == 0)
                    return CommandResult.Fail("sequence.category " + categoryName + " holds no placed instance element " +
                        "in this document (" + excluded.Count + " unplaced). Nothing was written.");
            }

            // ---- The datums, read once per target. Nothing here writes.
            var targets = new List<SequenceTarget>(elements.Count);
            var datums = new Dictionary<long, JObject>();
            foreach (Element e in elements)
            {
                var t = new SequenceTarget { Id = Rid.Value(e.Id) };
                Level level = TargetLevel(doc, e);
                if (level != null) { t.Level = level.Name; t.LevelElevation = level.Elevation; }
                XYZ point = TargetPoint(e);
                if (point != null) { t.X = point.X; t.Y = point.Y; }
                string roomFrom = null;
                if (byRoom)
                {
                    Room room = TargetRoom(doc, e, point, phase, out roomFrom);
                    if (room != null) t.Room = room.Number;
                }
                targets.Add(t);
                datums[t.Id] = new JObject
                {
                    ["level"] = t.Level,
                    ["x_mm"] = point == null ? null : (JToken)Math.Round(point.X * FeetToMm, 1),
                    ["y_mm"] = point == null ? null : (JToken)Math.Round(point.Y * FeetToMm, 1),
                    ["room"] = byRoom ? (JToken)t.Room : null,
                    ["room_from"] = roomFrom
                };
            }

            SequenceResult result;
            try { result = ParameterSequenceRules.Generate(targets, options); }
            catch (ArgumentException ex) { return CommandResult.Fail(ex.Message + " Nothing was written."); }
            catch (OverflowException) { return CommandResult.Fail("sequence: start + step overflows a 64-bit counter. Nothing was written."); }

            var order = new JArray();
            foreach (SequenceAssignment a in result.Assignments)
            {
                writes.Add(new JObject { ["target_id"] = a.Id, ["parameter"] = parameter, ["value"] = a.Value });
                if (order.Count >= SequenceOrderShown) continue;
                var shown = new JObject { ["position"] = a.Position, ["target_id"] = a.Id, ["value"] = a.Value };
                foreach (var d in datums[a.Id].Properties()) shown[d.Name] = d.Value;
                order.Add(shown);
            }
            report = new JObject
            {
                ["parameter"] = parameter,
                ["targets_from"] = haveIds ? "element_ids" : categoryName,
                ["order_by"] = new JArray(options.OrderBy.Select(k => (k ?? "").Trim().ToLowerInvariant())),
                ["prefix"] = options.Prefix,
                ["start"] = options.Start,
                ["step"] = options.Step,
                ["pad"] = options.Pad,
                ["restart_per_level"] = options.RestartPerLevel,
                ["phase"] = phase == null ? null : new JObject { ["id"] = Rid.Value(phase.Id), ["name"] = phase.Name },
                ["targets"] = result.Assignments.Count,
                ["excluded_unplaced"] = excluded,
                ["repeats_across_levels"] = result.RepeatsAcrossLevels,
                ["order"] = order,
                ["order_truncated"] = result.Assignments.Count > order.Count,
                ["note"] = "Values generated from the model NOW, in the order shown (keys left to right, x/y quantised " +
                           "to 1 mm in internal coordinates, element id as the last tie-break). They ride the normal " +
                           "rehearsal: the token binds every generated value, so a target moved, added or re-roomed " +
                           "before apply refuses as a stale plan. Each written value is re-read after the commit." +
                           (result.RepeatsAcrossLevels
                               ? " restart_per_level repeats values across levels; Revit may warn about duplicate marks."
                               : "")
            };
            return null;
        }

        private static string ReadInteger(JObject source, string field, Action<long> set)
        {
            JToken t = source[field];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.Integer) return "sequence." + field + " must be a whole number ('" + t + "' was given).";
            set(t.Value<long>());
            return null;
        }

        // The storey a target stands on: its own level, else its host's (a hosted door or
        // window often reads its level through the wall that carries it).
        private static Level TargetLevel(Document doc, Element e)
        {
            Level level = e.LevelId != null && e.LevelId != ElementId.InvalidElementId ? doc.GetElement(e.LevelId) as Level : null;
            if (level == null && e is FamilyInstance fi && fi.Host != null && fi.Host.LevelId != ElementId.InvalidElementId)
                level = doc.GetElement(fi.Host.LevelId) as Level;
            return level;
        }

        // A point-based element's location point, or the midpoint of a curve-based one.
        // An element without a readable location has no position: the rules name it.
        private static XYZ TargetPoint(Element e)
        {
            try
            {
                if (e.Location is LocationPoint lp) return lp.Point;
                if (e.Location is LocationCurve lc && lc.Curve != null) return lc.Curve.Evaluate(0.5, true);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            return null;
        }

        // The room a target stands in AT THE PHASE. A door takes the room it opens INTO,
        // then the one it opens from (the usual door-numbering rule); any other instance
        // its own Room. Otherwise the room at its point - retried 1 ft up, because an
        // insertion point lying exactly on the floor plane is on the room's lower boundary.
        // WHERE the room came from is reported per target.
        private static Room TargetRoom(Document doc, Element e, XYZ point, Phase phase, out string from)
        {
            from = null;
            if (e is FamilyInstance fi)
            {
                Room r = fi.get_ToRoom(phase);
                if (r != null) { from = "to_room"; return r; }
                r = fi.get_FromRoom(phase);
                if (r != null) { from = "from_room"; return r; }
                r = fi.get_Room(phase);
                if (r != null) { from = "room"; return r; }
            }
            if (point == null) return null;
            Room at = doc.GetRoomAtPoint(point, phase);
            if (at != null) { from = "point"; return at; }
            at = doc.GetRoomAtPoint(point + new XYZ(0, 0, 1.0), phase);
            if (at != null) { from = "point_raised_1ft"; return at; }
            return null;
        }
    }
}
