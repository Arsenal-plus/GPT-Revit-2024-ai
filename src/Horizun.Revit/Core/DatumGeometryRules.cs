// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A GRID HAS A LINE AND A LEVEL HAS AN ELEVATION, EVEN WHEN THEY HAVE NO BOX.
//
// MEASURED (Comité de obra, 2026-10-01): horizun_query_model include_bounding_box
// answered bounding_box: null for every grid and level of three linked models.
// Revit gives datums no MODEL bounding box (get_BoundingBox(null) is null: their
// extents live per view), so the only typed way to compare the grids of two links
// returned nothing to compare.
//
// What a datum does have is what it IS: a grid's curve and a level's elevation, both
// read in host coordinates through the link transform. A row now carries them in a
// `datum` block, and a grid's bounding_box is the box of its curve (labelled as such).
// A level gets no invented box - a level is a plane, not an extent - only its
// elevation; a query box therefore still cannot be intersected with one, and says so.
//
// Revit-free: the Revit half reads the curve points and the elevation; this shapes them.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class DatumGeometryRules
    {
        public const string SourceElement = "element";
        public const string SourceGridCurve = "grid_curve";

        /// <summary>The axis-aligned box of a set of points (feet), or null for none.</summary>
        public static double[][] BoxOf(IEnumerable<double[]> points)
        {
            var list = points?.Where(p => p != null && p.Length == 3).ToList();
            if (list == null || list.Count == 0) return null;
            return new[]
            {
                new[] { list.Min(p => p[0]), list.Min(p => p[1]), list.Min(p => p[2]) },
                new[] { list.Max(p => p[0]), list.Max(p => p[1]), list.Max(p => p[2]) }
            };
        }

        private static JArray P(double[] p, double scale) =>
            new JArray(Math.Round(p[0] * scale, 3), Math.Round(p[1] * scale, 3), Math.Round(p[2] * scale, 3));

        /// <summary>A grid's line in host coordinates. `points` are the curve's ends (and, when curved, its tessellation).</summary>
        public static JObject Grid(double[] start, double[] end, bool curved, IList<double[]> points, double scale, string units)
        {
            var o = new JObject
            {
                ["kind"] = "grid",
                ["start"] = P(start, scale),
                ["end"] = P(end, scale),
                ["is_curved"] = curved,
                ["units"] = units
            };
            double dx = end[0] - start[0], dy = end[1] - start[1];
            if (!curved && (Math.Abs(dx) > 1e-9 || Math.Abs(dy) > 1e-9))
            {
                // Plan direction, 0..180: a grid drawn the other way is the same grid.
                double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                if (deg < 0) deg += 180.0;
                if (deg >= 180.0 - 1e-9) deg -= 180.0;
                o["plan_angle_degrees"] = Math.Round(deg, 4);
                o["length"] = Math.Round(Math.Sqrt(dx * dx + dy * dy) * scale, 3);
            }
            if (curved && points != null && points.Count > 2) o["points"] = new JArray(points.Select(p => (JToken)P(p, scale)));
            return o;
        }

        /// <summary>A level's elevation in host coordinates (the transform applied to its Z).</summary>
        public static JObject Level(double elevationHostFeet, double? elevationOwnFeet, double scale, string units)
        {
            var o = new JObject
            {
                ["kind"] = "level",
                ["elevation"] = Math.Round(elevationHostFeet * scale, 3),
                ["units"] = units,
                ["basis"] = "host coordinates: the level's own elevation through the link transform",
                ["bounding_box_note"] = "a level is a plane with no model extent; it has an elevation and no box"
            };
            if (elevationOwnFeet.HasValue) o["elevation_in_own_model"] = Math.Round(elevationOwnFeet.Value * scale, 3);
            return o;
        }
    }
}
