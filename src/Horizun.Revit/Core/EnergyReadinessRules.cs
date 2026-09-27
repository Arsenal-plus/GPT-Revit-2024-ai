// -----------------------------------------------------------------------------
// Horizun Revit MCP - the arithmetic of horizun_code_check operation=energy_readiness.
// Original Horizun code. Pure: no Revit type appears here, so every rule is unit-tested.
//
// ORIENTATION. Revit documents EnergyAnalysisSurface.Azimuth as "the angle of rotation
// between the outward normal of the surface and the Y-axis (clockwise is positive)",
// and only the 2026 RevitAPI.xml says the unit (radians); the 2023 text does not. So the
// azimuth is computed HERE from the surface's outward normal, with that same definition,
// and never read in a unit a year does not state. A (nearly) horizontal surface has no
// orientation and is counted as unmeasured, never binned.
//
// SECTORS. Four 90-degree sectors centred on N, E, S and W. A normal exactly on a
// boundary (45, 135, 225, 315) belongs to the sector clockwise of it, so every azimuth
// lands in exactly one sector. The rule is echoed in the reply, because a jurisdiction
// that bins differently must be able to see which binning produced the numbers.
//
// THE RATIO. Window area over GROSS exterior wall area per sector (the energy model's
// wall surface includes its openings, as in gbXML). A sector with no wall area has no
// ratio (null), never 0: zero windows on a wall is a measurement, no wall is not.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class EnergyReadinessRules
    {
        public const double SquareFeetToSquareMetres = 0.09290304;
        public const string SectorRule =
            "90-degree sectors centred on N/E/S/W, azimuth of the wall's outward normal clockwise from north; a boundary goes clockwise";
        public static readonly string[] Sectors = { "N", "E", "S", "W" };

        /// <summary>One exterior wall surface of the energy model, areas in square metres.</summary>
        public sealed class WallSample
        {
            public string Sector;
            public double WallArea, WindowArea, DoorArea;
            public int Windows, Doors;
        }

        /// <summary>
        /// Azimuth in degrees [0, 360) clockwise from +Y of a normal's horizontal part;
        /// null when the normal is (nearly) vertical and so has no orientation.
        /// </summary>
        public static double? Azimuth(double nx, double ny)
        {
            if (Math.Sqrt(nx * nx + ny * ny) < 1e-6) return null;
            double deg = Math.Atan2(nx, ny) * 180.0 / Math.PI;
            if (deg < 0) deg += 360.0;
            return deg >= 360.0 ? deg - 360.0 : deg;
        }

        /// <summary>The sector of an azimuth in degrees (any value; it is normalised first).</summary>
        public static string Sector(double azimuthDeg)
        {
            double a = ((azimuthDeg % 360.0) + 360.0) % 360.0;
            if (a >= 315.0 || a < 45.0) return "N";
            if (a < 135.0) return "E";
            return a < 225.0 ? "S" : "W";
        }

        /// <summary>Window-to-wall ratio, or null when there is no wall to divide by.</summary>
        public static double? Ratio(double windowArea, double wallArea) =>
            wallArea > 1e-9 ? Math.Round(windowArea / wallArea, 4) : (double?)null;

        /// <summary>One row per sector, always all four (a sector with no wall says so), then the total.</summary>
        public static JObject ByOrientation(IEnumerable<WallSample> walls)
        {
            List<WallSample> all = (walls ?? Enumerable.Empty<WallSample>()).ToList();
            var rows = new JArray();
            foreach (string s in Sectors)
                rows.Add(Row(s, all.Where(w => w.Sector == s).ToList()));
            return new JObject { ["sector_rule"] = SectorRule, ["by_orientation"] = rows, ["total"] = Row("all", all) };
        }

        private static JObject Row(string sector, List<WallSample> walls)
        {
            double wall = walls.Sum(w => w.WallArea), window = walls.Sum(w => w.WindowArea), door = walls.Sum(w => w.DoorArea);
            return new JObject
            {
                ["orientation"] = sector,
                ["wall_surfaces"] = walls.Count,
                ["wall_area_m2"] = Math.Round(wall, 3),
                ["windows"] = walls.Sum(w => w.Windows),
                ["window_area_m2"] = Math.Round(window, 3),
                ["doors"] = walls.Sum(w => w.Doors),
                ["door_area_m2"] = Math.Round(door, 3),
                ["wwr"] = Ratio(window, wall) is double r ? (JToken)r : JValue.CreateNull()
            };
        }
    }
}
