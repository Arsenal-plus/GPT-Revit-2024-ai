// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF the "hangers" operation of horizun_mep_routing: where
// along a run a support station goes, given the run's own length, the end
// clearance the caller asked for, the maximum spacing, and the interior
// fittings (elbows, tees) a support must also stay clear of.
//
// The rule, stated once so a unit test can hold it exactly: a station sits at
// end_offset from each of the run's own ends, then as many intermediate
// stations as needed so no gap exceeds spacing_mm - split EVENLY between the
// two end stations rather than packed from one side, so a run that is not an
// exact multiple of the spacing does not leave one short segment. A candidate
// station whose distance to an interior fitting is LESS than end_offset is
// dropped (the fitting's own clearance wins over an evenly spaced grid); the
// two end stations are exactly end_offset from a run's own ends and are never
// dropped by this rule, because that clearance is the one the caller asked for.
//
// A run shorter than 2 * end_offset has no station that can keep the clearance
// from BOTH ends at once, so it gets none - MepRoutingHangers.cs reports it
// rather than placing a support closer to an end than asked.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class HangerRules
    {
        private const double Tol = 1e-6;

        /// <summary>
        /// Stations along a run, in the same length unit as the inputs (feet, when
        /// called from the command). Returns distances measured from the run's
        /// start (0) to its end (runLength). Empty when the run cannot carry even
        /// its two end stations.
        /// </summary>
        public static List<double> ComputeStations(double runLength, double endOffset, double spacing,
            IReadOnlyList<double> interiorFittingPositions)
        {
            var result = new List<double>();
            if (!(runLength > 0) || !(spacing > 0) || endOffset < 0) return result;

            double start = endOffset, end = runLength - endOffset;
            if (end < start - Tol) return result; // run shorter than 2 * end_offset: no station keeps both clearances

            if (Math.Abs(end - start) <= Tol)
            {
                result.Add(start);
            }
            else
            {
                int intervals = (int)Math.Ceiling((end - start) / spacing - Tol);
                if (intervals < 1) intervals = 1;
                double step = (end - start) / intervals;
                for (int i = 0; i <= intervals; i++)
                    result.Add(start + i * step);
            }

            if (interiorFittingPositions != null && interiorFittingPositions.Count > 0)
            {
                // The two end stations are exactly `start` and `end`, i.e. exactly
                // end_offset from the run's own ends: never dropped here, because
                // dropping them would mean the run has NO station within its own
                // required clearance and that is the "too short" case above, not this one.
                result = result.Where(s =>
                        Math.Abs(s - start) <= Tol || Math.Abs(s - end) <= Tol ||
                        !interiorFittingPositions.Any(f => Math.Abs(s - f) < endOffset - Tol))
                    .ToList();
            }
            return result;
        }
    }
}
