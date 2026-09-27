// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Revit-free decisions of horizun_create_elements placement='all_enclosed'
// (CreateElementsEnclosed.cs): what the rehearsal lists for ONE region and whether a
// room/space row is planned there, the row it becomes, which batches may carry the
// placement, and what an expansion that leaves nothing to create answers.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class EnclosedRules
    {
        public const string Create = "create";
        public const string SkippedMinArea = "skipped_min_area";
        /// <summary>Revit could not give a valid interior point for the region (PlanCircuit.GetPointInside threw).</summary>
        public const string NoInteriorPoint = "no_interior_point";

        /// <summary>
        /// skipped_has_room / skipped_has_space when one already stands in the circuit - that
        /// wins over the area, because it is the reason nothing goes there whatever the size;
        /// skipped_min_area when the circuit is smaller than the caller's min_area_m2 (equal is
        /// kept); otherwise create.
        /// </summary>
        public static string CircuitAction(string kind, bool occupied, double areaM2, double minAreaM2)
        {
            if (kind != "room" && kind != "space") throw new ArgumentOutOfRangeException(nameof(kind), kind, "all_enclosed places a room or a space.");
            if (occupied) return "skipped_has_" + kind;
            return areaM2 < minAreaM2 ? SkippedMinArea : Create;
        }

        /// <summary>Null when the caller's min_area_m2 is usable; otherwise why not.</summary>
        public static string MinAreaProblem(double minAreaM2)
            => double.IsNaN(minAreaM2) || double.IsInfinity(minAreaM2) || minAreaM2 < 0 ? "min_area_m2 must be a finite number >= 0." : null;

        /// <summary>
        /// The room/space row one region becomes: the caller's level and phase, Revit's interior
        /// point in the caller's units - X and Y ONLY, because a room/space insertion applies no Z
        /// and the room planner refuses a third coordinate - and the expansion's bookkeeping
        /// (which entry it came from, the region's area).
        /// </summary>
        public static JObject GeneratedRow(string kind, long levelId, long phaseId, double x, double y, int entry, double areaM2)
            => new JObject
            {
                ["kind"] = kind, ["level_id"] = levelId, ["phase_id"] = phaseId,
                ["point"] = new JArray(x, y),
                ["enclosed_from"] = entry, ["circuit_area_m2"] = Math.Round(areaM2, 3)
            };

        /// <summary>
        /// Null when the batch may carry its all_enclosed entries; otherwise why not. The regions are
        /// read BEFORE anything in the batch is built, so walls or separators created beside them would
        /// never be seen; and each entry expands into rows that would renumber the caller's other entries.
        /// </summary>
        public static string MixProblem(int enclosedEntries, int otherEntries)
            => enclosedEntries > 0 && otherEntries > 0
                ? "placement='all_enclosed' entries go in a batch of their own: the regions are read before anything in the " +
                  "batch is built (walls or separators created beside them would not be seen), and the rows each entry expands " +
                  "into would renumber every other entry. Create the bounding elements first, then call again."
                : null;

        /// <summary>
        /// When the expansion leaves nothing to create: null = a truthful no-op rehearsal that lists
        /// what it saw; otherwise the refusal. An entry that saw NO region is refused by name instead of
        /// reading as "all filled". An apply is refused: a confirmation token is only issued for a plan
        /// that creates rows, so a token sent now describes a plan the model no longer matches.
        /// </summary>
        public static string NothingToFillProblem(bool dryRun, JArray block)
        {
            var none = (block ?? new JArray()).OfType<JObject>().Where(b => (b.Value<int?>("circuits_seen") ?? 0) == 0).ToList();
            if (none.Count > 0)
                return "no_enclosed_circuit: " + string.Join("; ", none.Select(b => "elements[" + b.Value<int?>("index") + "] sees no closed region on level '" +
                       b.Value<string>("level") + "' in phase '" + b.Value<string>("phase") + "'")) +
                       ". Nothing encloses a region Revit can see there - and walls of a LINKED model are not proven to count. Nothing was written.";
            if (!dryRun)
                return "stale_plan: nothing is left to fill now - every region listed holds a room/space, is under min_area_m2 or has no " +
                       "interior point. A confirmation token is only issued for a plan that creates rows, so any token sent was issued for " +
                       "a model that has changed since. Nothing was written; rehearse again.";
            return null;
        }
    }
}
