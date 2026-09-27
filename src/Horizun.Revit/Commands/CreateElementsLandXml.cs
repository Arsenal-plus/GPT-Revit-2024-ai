// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_create_elements kind='toposolid' from landxml_path.
// Original Horizun code.
//
// The caller exports ONE TIN surface to LandXML and names the file; LandXmlTinRules
// reads it (N E Z in the file's own unit, only the points a visible face uses) and this
// places those points through the document's ACTIVE project position, because the file
// speaks shared coordinates. The conversion is the formula tabular_source applies to
// shared CSV rows (CreateElementsCommand.cs). Its sign on a ROTATED project position
// is not measured yet, and the post-commit re-read cannot catch it - it proves the
// solid stands at the CONVERTED points - so the rehearsal prints the position used.
//
// The plan binds the file's SHA-256, the surface and the position: a file edited, or a
// survey point moved, between rehearsal and apply is a different plan, refused as stale.
// -----------------------------------------------------------------------------
#if !REVIT2023
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        private const double FeetPerMetre = 1 / 0.3048;

        /// <summary>landxml_path: the used points of ONE TIN surface in internal feet, file order; <paramref name="ids"/> names each.</summary>
        private static List<double[]> ToposolidFromLandXml(Document doc, string spec, Plan p, out List<string> ids)
        {
            // "C:\...\site.xml#EG" picks surface EG of a file that holds several. A path that
            // exists as written is taken whole, so a '#' inside a folder name still works.
            string path = spec, surface = null;
            int hash = spec.LastIndexOf('#');
            if (!System.IO.File.Exists(spec) && hash > 0) { path = spec.Substring(0, hash); surface = spec.Substring(hash + 1); }
            if (!System.IO.Path.IsPathRooted(path)) throw new ArgumentException("landxml_path must be an absolute path.");
            if (!System.IO.File.Exists(path)) throw new ArgumentException("landxml_path: file '" + path + "' does not exist.");
            long size = new System.IO.FileInfo(path).Length;
            if (size > LandXmlTinRules.MaxFileBytes)
                throw new ArgumentException("landxml_path: the file holds " + size + " bytes and at most " + LandXmlTinRules.MaxFileBytes +
                                            " are read inside Revit - export the one surface on its own, thinned.");
            byte[] bytes = System.IO.File.ReadAllBytes(path);
            string sha;
            using (var hasher = System.Security.Cryptography.SHA256.Create())
                sha = BitConverter.ToString(hasher.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            LandXmlTin tin;
            string bad;
            using (var stream = new System.IO.MemoryStream(bytes, false)) bad = LandXmlTinRules.Read(stream, surface, out tin);
            if (bad != null) throw new ArgumentException("landxml_path: " + bad);

            ProjectPosition pos;
            try { pos = doc.ActiveProjectLocation?.GetProjectPosition(XYZ.Zero); }
            catch { pos = null; }
            if (pos == null)
                throw new ArgumentException("landxml_path: the active project position could not be read, and a LandXML surface is in shared coordinates.");
            var pts = new List<double[]>(tin.PointsMetres.Count);
            foreach (double[] m in tin.PointsMetres)
                pts.Add(LandXmlTinRules.SharedToInternal(m[0] * FeetPerMetre, m[1] * FeetPerMetre, m[2] * FeetPerMetre,
                                                         pos.Angle, pos.EastWest, pos.NorthSouth, pos.Elevation));
            ids = tin.PointIds;
            p.TopoSource = new JObject
            {
                ["path"] = path, ["surface"] = tin.Surface, ["sha256"] = sha, ["linear_unit"] = tin.LinearUnit,
                ["points_in_file"] = tin.PointsInFile, ["points_used"] = tin.PointsMetres.Count, ["points_unused"] = tin.PointsUnused,
                ["faces_visible"] = tin.FacesVisible, ["faces_invisible"] = tin.FacesInvisible,
                ["coordinates"] = "each P is 'northing easting elevation' in shared coordinates, placed through the active project position below",
                ["project_position"] = new JObject
                {
                    ["angle_degrees"] = Math.Round(pos.Angle * 180 / Math.PI, 6),
                    ["east_west_m"] = Math.Round(pos.EastWest * 0.3048, 4), ["north_south_m"] = Math.Round(pos.NorthSouth * 0.3048, 4),
                    ["elevation_m"] = Math.Round(pos.Elevation * 0.3048, 4)
                },
                ["triangulation"] = "Revit's own: Toposolid.Create takes points, not triangles. The file's visible faces decide which points are used, not how they join.",
                ["sample_index_means"] = "top_z_at_point_<k> counts the used points in file order; sampled_point_ids names them"
            };
            p.ExtraPlanFacts = p.ExtraPlanFacts ?? new Dictionary<string, string>();
            p.ExtraPlanFacts["toposolid.landxml_sha256"] = sha;
            p.ExtraPlanFacts["toposolid.landxml_surface"] = tin.Surface;
            p.ExtraPlanFacts["toposolid.project_position"] = string.Join("|",
                new[] { pos.Angle, pos.EastWest, pos.NorthSouth, pos.Elevation }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
            return pts;
        }
    }
}
#endif
