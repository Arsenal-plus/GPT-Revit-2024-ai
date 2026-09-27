// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Revit-free decision of horizun_create_elements placement='all_enclosed' for ONE
// circuit of PlanTopology(level, phase) (CreateElementsEnclosed.cs): what the
// rehearsal lists for it, and whether a room/space row is planned there.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Revit.Core
{
    public static class EnclosedRules
    {
        public const string Create = "create";
        public const string SkippedMinArea = "skipped_min_area";

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
    }
}
