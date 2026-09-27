// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Revit-free reader of ONE TIN surface from a LandXML file the caller exported
// (horizun_create_elements kind='toposolid', landxml_path). LandXML writes each
// surface point as "northing easting elevation" - north FIRST - in the unit the file
// declares, in the coordinates the exporting program worked in (for a civil model:
// shared/survey coordinates). This returns [east, north, elevation] in METRES and
// leaves the shared -> internal step to the caller, which holds the project position.
//
// Nothing is guessed. A file that declares no linear unit, a unit outside the table,
// several surfaces and none named, a grid (not TIN) surface, a point without an
// elevation, a face naming a point the surface does not define: each is refused by
// name. The faces decide WHICH points are used - a point that only an invisible face
// (i="1", the triangles an exporter hides outside a boundary) touches is dropped and
// counted - but not HOW they join: Toposolid.Create takes points, not triangles, and
// triangulates them itself. A DTD is refused: no entity expansion from a file on disk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace Horizun.Revit.Core
{
    public sealed class LandXmlTin
    {
        public string Surface;
        public string LinearUnit;
        /// <summary>[east, north, elevation] in metres, in file order: the points a visible face uses (every point when the surface has no faces).</summary>
        public List<double[]> PointsMetres = new List<double[]>();
        /// <summary>The file's own id of each entry of PointsMetres.</summary>
        public List<string> PointIds = new List<string>();
        public int PointsInFile, FacesVisible, FacesInvisible;
        public int PointsUnused => PointsInFile - PointsMetres.Count;
    }

    public static class LandXmlTinRules
    {
        /// <summary>Points one toposolid takes from a file. A guard on the work one Revit transaction does on the UI thread, not a measured Revit limit.</summary>
        public const int MaxFilePoints = 20000;
        /// <summary>The file is read into memory inside Revit's process; larger is refused.</summary>
        public const long MaxFileBytes = 64L * 1024 * 1024;

        private static readonly char[] Ws = { ' ', '\t', '\r', '\n' };
        private static readonly Dictionary<string, double> MetresPer = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["millimeter"] = 0.001, ["centimeter"] = 0.01, ["meter"] = 1.0, ["kilometer"] = 1000.0,
            ["foot"] = 0.3048, ["USSurveyFoot"] = 1200.0 / 3937.0, ["inch"] = 0.0254
        };

        private sealed class Surf
        {
            public string Name, SurfType, Problem;
            public readonly List<string> Ids = new List<string>();
            public readonly List<double[]> Nez = new List<double[]>();
            public readonly List<string[]> Visible = new List<string[]>();
            public int Invisible;
        }

        /// <summary>Null when one surface was read into <paramref name="tin"/>; otherwise why not.</summary>
        public static string Read(Stream xml, string surfaceName, out LandXmlTin tin)
        {
            tin = null;
            var surfaces = new List<Surf>();
            string linearUnit = null;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
            try
            {
                using (XmlReader r = XmlReader.Create(xml, settings))
                {
                    Surf cur = null;
                    bool root = false, inUnits = false, inPnts = false, inFaces = false;
                    while (!r.EOF)
                    {
                        if (r.NodeType == XmlNodeType.EndElement)
                        {
                            switch (r.LocalName)
                            {
                                case "Units": inUnits = false; break;
                                case "Pnts": inPnts = false; break;
                                case "Faces": inFaces = false; break;
                                case "Surface": cur = null; inPnts = inFaces = false; break;
                            }
                            r.Read(); continue;
                        }
                        if (r.NodeType != XmlNodeType.Element) { r.Read(); continue; }
                        string name = r.LocalName;
                        bool empty = r.IsEmptyElement;
                        if (!root)
                        {
                            root = true;
                            if (name != "LandXML") return "the file's root element is <" + name + ">, not <LandXML>.";
                        }
                        switch (name)
                        {
                            case "Units": inUnits = !empty; break;
                            case "Metric": case "Imperial": if (inUnits && linearUnit == null) linearUnit = r.GetAttribute("linearUnit") ?? ""; break;
                            case "Surface":
                                cur = new Surf { Name = r.GetAttribute("name") ?? "" };
                                surfaces.Add(cur);
                                if (empty) cur = null;
                                break;
                            case "Definition": if (cur != null) cur.SurfType = r.GetAttribute("surfType"); break;
                            case "Pnts": inPnts = cur != null && !empty; break;
                            case "Faces": inFaces = cur != null && !empty; break;
                            case "P":
                                if (inPnts) { string id = r.GetAttribute("id"); AddPoint(cur, id, r.ReadElementContentAsString()); continue; }
                                break;
                            case "F":
                                if (inFaces) { bool hidden = r.GetAttribute("i") == "1"; AddFace(cur, hidden, r.ReadElementContentAsString()); continue; }
                                break;
                        }
                        r.Read();
                    }
                }
            }
            catch (XmlException ex) { return "the file is not readable LandXML: " + ex.Message; }

            if (surfaces.Count == 0) return "the file holds no <Surface>.";
            Surf pick;
            if (string.IsNullOrEmpty(surfaceName))
            {
                if (surfaces.Count > 1)
                    return "the file holds " + surfaces.Count + " surfaces (" + Names(surfaces) + "): name one by ending landxml_path in #<surface name>.";
                pick = surfaces[0];
            }
            else
            {
                var hits = surfaces.Where(s => s.Name == surfaceName).ToList();
                if (hits.Count == 0) return "no surface named '" + surfaceName + "' in the file; it holds " + Names(surfaces) + ".";
                if (hits.Count > 1) return "the file holds " + hits.Count + " surfaces named '" + surfaceName + "': the name picks none of them.";
                pick = hits[0];
            }
            if (pick.Problem != null) return pick.Problem;
            if (pick.SurfType != null && !string.Equals(pick.SurfType, "TIN", StringComparison.OrdinalIgnoreCase))
                return "surface '" + pick.Name + "' is a " + pick.SurfType + " surface; only a TIN (points and triangles) is read.";
            if (linearUnit == null) return "the file declares no linear unit (<Units><Metric|Imperial linearUnit=...>): its numbers cannot be placed without one.";
            double metresPerUnit;
            if (!MetresPer.TryGetValue(linearUnit, out metresPerUnit))
                return "linearUnit '" + linearUnit + "' is not one this reader converts (" + string.Join(", ", MetresPer.Keys) + ").";
            if (pick.Ids.Count == 0) return "surface '" + pick.Name + "' holds no points.";

            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int k = 0; k < pick.Ids.Count; k++)
            {
                if (index.ContainsKey(pick.Ids[k])) return "point id '" + pick.Ids[k] + "' appears twice in surface '" + pick.Name + "'.";
                index[pick.Ids[k]] = k;
            }
            if (pick.Visible.Count == 0 && pick.Invisible > 0)
                return "every face of surface '" + pick.Name + "' is invisible (i=\"1\"): the file shows nothing of it to take.";
            bool[] used = new bool[pick.Ids.Count];
            if (pick.Visible.Count == 0) for (int k = 0; k < used.Length; k++) used[k] = true;
            foreach (string[] face in pick.Visible)
                foreach (string id in face)
                {
                    int at;
                    if (!index.TryGetValue(id, out at)) return "a face names point '" + id + "', which surface '" + pick.Name + "' does not define.";
                    used[at] = true;
                }
            int usedCount = used.Count(u => u);
            if (usedCount > MaxFilePoints)
                return "surface '" + pick.Name + "' uses " + usedCount + " points; one toposolid takes at most " + MaxFilePoints +
                       " from a file - thin the surface where it was made.";

            tin = new LandXmlTin
            {
                Surface = pick.Name, LinearUnit = linearUnit, PointsInFile = pick.Ids.Count,
                FacesVisible = pick.Visible.Count, FacesInvisible = pick.Invisible
            };
            for (int k = 0; k < used.Length; k++)
            {
                if (!used[k]) continue;
                double[] nez = pick.Nez[k];
                tin.PointsMetres.Add(new[] { nez[1] * metresPerUnit, nez[0] * metresPerUnit, nez[2] * metresPerUnit });
                tin.PointIds.Add(pick.Ids[k]);
            }
            return null;
        }

        /// <summary>
        /// Shared (east, north, elevation) to internal, every value in ONE unit. The project
        /// position read at the internal origin says shared = R(angle) * internal + (eastWest,
        /// northSouth) in plan and shared elevation = internal Z + elevation, so internal =
        /// R(-angle) * (shared - T). The same formula tabular_source applies to shared CSV rows.
        /// </summary>
        public static double[] SharedToInternal(double east, double north, double elevation,
                                                double angle, double eastWest, double northSouth, double positionElevation)
        {
            double cos = Math.Cos(-angle), sin = Math.Sin(-angle);
            double relativeEast = east - eastWest, relativeNorth = north - northSouth;
            return new[] { relativeEast * cos - relativeNorth * sin, relativeEast * sin + relativeNorth * cos, elevation - positionElevation };
        }

        private static void AddPoint(Surf s, string id, string text)
        {
            if (s.Problem != null) return;
            if (string.IsNullOrEmpty(id)) { s.Problem = "a <P> of surface '" + s.Name + "' has no id, so no face can name it."; return; }
            string[] parts = (text ?? "").Split(Ws, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                s.Problem = "point '" + id + "' of surface '" + s.Name + "' holds " + parts.Length + " numbers; a TIN point is 'northing easting elevation'.";
                return;
            }
            var v = new double[3];
            for (int k = 0; k < 3; k++)
                if (!double.TryParse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]) || double.IsNaN(v[k]) || double.IsInfinity(v[k]))
                {
                    s.Problem = "point '" + id + "' of surface '" + s.Name + "': '" + parts[k] + "' is not a number.";
                    return;
                }
            s.Ids.Add(id); s.Nez.Add(v);
        }

        private static void AddFace(Surf s, bool hidden, string text)
        {
            if (s.Problem != null) return;
            if (hidden) { s.Invisible++; return; }
            string[] ids = (text ?? "").Split(Ws, StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length < 3 || ids.Length > 4)
            {
                s.Problem = "a face of surface '" + s.Name + "' names " + ids.Length + " points; a TIN face names 3.";
                return;
            }
            s.Visible.Add(ids);
        }

        private static string Names(List<Surf> surfaces) => string.Join(", ", surfaces.Take(20).Select(s => "'" + s.Name + "'"));
    }
}
