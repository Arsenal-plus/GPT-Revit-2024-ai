// horizun_quantities, mode takeoff, group_by='room': the same caller-named quantities, rolled
// up per room of ONE phase beside the per-code rollup. Membership is RoomMembershipReader's
// (the rules are written there and in docs/TOOLS-EXTENDED.md). An element outside every room
// is '(unassigned)', one whose samples fall in several rooms '(multiple rooms)' and one with no
// sample point '(unlocatable)': all are keys of
// their own, so their sums are visible and never spread over the rooms or dropped.

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
        /// <summary>
        /// Reads group_by / phase for a takeoff. Null reader and null problem: no grouping asked.
        /// phase without group_by='room' is refused: it would be silently ignored.
        /// </summary>
        private static RoomMembershipReader ParseTakeoffRoomGrouping(Document doc, JObject request, out string problem)
        {
            problem = null;
            JToken groupBy = request["group_by"];
            if (groupBy == null)
            {
                if (request["phase"] != null)
                    problem = "mode 'takeoff' reads phase only with group_by='room'; without it the phase would be silently " +
                              "ignored. Pass group_by: 'room', or drop phase.";
                return null;
            }
            if (groupBy.Type != JTokenType.String || (string)groupBy != "room")
            {
                problem = "group_by must be 'room' (the only takeoff grouping besides the by_code rollup, which is always there).";
                return null;
            }
            return RoomMembershipReader.Create(doc, request.Value<string>("phase"), out problem);
        }

        /// <summary>The same Core rule as by_code (RollupRules.Add), applied to the room tally.</summary>
        private static void TallyRoomReading(TakeoffCodeTally tally, string quantityName, TakeoffReading r)
        {
            TakeoffQuantityTally qt;
            if (!tally.Quantities.TryGetValue(quantityName, out qt)) tally.Quantities[quantityName] = qt = new TakeoffQuantityTally();
            RollupRules.Add(qt, r.State, r.Value);
        }

        private static JObject RoomRollup(Dictionary<string, TakeoffCodeTally> byRoom, Dictionary<string, SpatialElement> rooms,
                                          List<TakeoffDefinition> defs)
        {
            var rollup = new JObject();
            foreach (var kv in byRoom.OrderByDescending(k => k.Value.Elements).ThenBy(k => k.Key, StringComparer.Ordinal))
            {
                var q = new JObject();
                foreach (TakeoffDefinition d in defs)
                {
                    TakeoffQuantityTally qt;
                    if (!kv.Value.Quantities.TryGetValue(d.Name, out qt)) qt = new TakeoffQuantityTally();
                    q[d.Name] = new JObject
                    {
                        ["unit"] = d.Unit,
                        ["known_total"] = Math.Round(qt.Total, 6),
                        ["measured"] = qt.Measured,
                        ["absent"] = qt.Absent,
                        ["empty"] = qt.Empty,
                        ["unreadable"] = qt.Unreadable,
                        ["invalid"] = qt.Invalid,
                        ["complete"] = RollupRules.Complete(qt, kv.Value.Elements)
                    };
                }
                SpatialElement room;
                rooms.TryGetValue(kv.Key, out room);
                rollup[kv.Key] = new JObject
                {
                    ["room"] = RoomMembershipReader.Describe(room),
                    ["elements"] = kv.Value.Elements,
                    ["quantities"] = q
                };
            }
            return rollup;
        }

        private static JObject RoomRowJson(RoomHit h) => new JObject
        {
            ["state"] = h.State,
            ["room_id"] = h.Room == null ? JValue.CreateNull() : new JValue(Rid.Value(h.Room.Id)),
            ["room_ids"] = h.Rooms.Count > 1 ? new JArray(h.Rooms.Select(r => (object)Rid.Value(r.Id)).ToArray()) : (JToken)JValue.CreateNull(),
            ["basis"] = h.Basis,
            ["reason"] = h.Reason
        };
    }
}
