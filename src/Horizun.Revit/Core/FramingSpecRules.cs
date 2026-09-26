// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE SPEC HALF OF horizun_framing: the caller's typed FRAMING SPEC (spec.wall or
// spec.ceiling, usually filled by a vision-capable client from a detail image and
// confirmed by the person) read into the Revit-free plan inputs, or refused with
// every problem named by path.
//
// WHY STRICT. The spec is somebody's reading of a drawing. A typo'd key that
// silently fell back to a default would build framing nobody asked for and report
// it as verified, so:
//  * UNKNOWN KEYS ARE REFUSED (unknown_field), never ignored.
//  * TYPE IDS ARE INTEGERS > 0. A family NAME is not accepted here: the client
//    resolves names to ids with the query tools first, so the plan names exactly
//    one type per role.
//  * LENGTHS ARE MILLIMETRES, bounded. A spacing below 10 mm is refused as
//    below_minimum: 0.406 is 406 mm typed in metres, not a 0.4 mm stud bay.
//  * EVERY ERROR IS COLLECTED, not just the first, so one round-trip fixes them all.
//
// Defaults are geometric, never an organisation's rule: stud.start = wall_start,
// layer = core, king_studs = 1, jack_studs = true, the top track = the bottom
// track's type, hanger.max_length_mm = 3000, hanger.end_offset_mm = half the
// hanger spacing (the same centred-strip logic the grid uses), drop_mm = 0.
//
// The spec hash (canonical JSON, keys sorted) is what the marker on every member
// records, so read can say which spec built it and a second apply of the same
// spec is recognised as already done.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class FramingSpecError
    {
        public string Path { get; set; }
        /// <summary>missing | unknown_field | not_object | not_array | not_integer | not_number | not_boolean | bad_value | below_minimum | above_maximum | conflict.</summary>
        public string Code { get; set; }
        public string Detail { get; set; }
        public override string ToString() => Code + " at " + Path + (string.IsNullOrEmpty(Detail) ? "" : ": " + Detail);
    }

    public sealed class WallFramingSpec
    {
        /// <summary>True when the studs sit in the wall's core (structural) layer; false when LayerIndex names one.</summary>
        public bool CoreLayer { get; set; } = true;
        public int LayerIndex { get; set; } = -1;
        public long StudTypeId { get; set; }
        public double SpacingMm { get; set; }
        public string StartRule { get; set; } = "wall_start";
        public double MaxFirstBayMm { get; set; }
        public bool DoubleAtEnds { get; set; }
        /// <summary>Null: the command measures the stud type's width.</summary>
        public double? StudWidthMm { get; set; }
        public long BottomTrackTypeId { get; set; }
        public long TopTrackTypeId { get; set; }
        /// <summary>Null: the command measures the track type's thickness.</summary>
        public double? TrackThicknessMm { get; set; }
        public int KingStuds { get; set; } = 1;
        public bool JackStuds { get; set; } = true;
        public long? HeaderTypeId { get; set; }
        public long? SillTypeId { get; set; }
        /// <summary>0: cripples on the layout stations the opening removed.</summary>
        public double CrippleSpacingMm { get; set; }
        public List<BlockingRow> Blocking { get; set; } = new List<BlockingRow>();

        /// <summary>Every type the plan can name, so the command resolves and checks each once.</summary>
        public IEnumerable<long> TypeIds()
        {
            var ids = new List<long> { StudTypeId, BottomTrackTypeId, TopTrackTypeId };
            if (HeaderTypeId.HasValue) ids.Add(HeaderTypeId.Value);
            if (SillTypeId.HasValue) ids.Add(SillTypeId.Value);
            foreach (BlockingRow row in Blocking)
                if (long.TryParse(row.TypeKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)) ids.Add(id);
            return ids.Distinct();
        }

        /// <summary>The plan input for one wall, in millimetres, once the command has read the wall.</summary>
        public WallFramingInput ToInput(double lengthMm, double heightMm, double studWidthMm, double trackThicknessMm, IEnumerable<WallOpeningSpan> openingsMm)
            => new WallFramingInput
            {
                Length = lengthMm,
                Height = heightMm,
                StudSpacing = SpacingMm,
                StudWidth = studWidthMm,
                StartRule = StartRule,
                MaxFirstBay = MaxFirstBayMm,
                DoubleAtEnds = DoubleAtEnds,
                BottomTrackThickness = trackThicknessMm,
                TopTrackThickness = trackThicknessMm,
                KingStuds = KingStuds,
                JackStuds = JackStuds,
                CrippleSpacing = CrippleSpacingMm,
                StudTypeKey = Key(StudTypeId),
                BottomTrackTypeKey = Key(BottomTrackTypeId),
                TopTrackTypeKey = Key(TopTrackTypeId),
                HeaderTypeKey = HeaderTypeId.HasValue ? Key(HeaderTypeId.Value) : null,
                SillTypeKey = SillTypeId.HasValue ? Key(SillTypeId.Value) : null,
                Openings = (openingsMm ?? Enumerable.Empty<WallOpeningSpan>()).ToList(),
                Blocking = Blocking.Select(b => new BlockingRow { Height = b.Height, TypeKey = b.TypeKey }).ToList(),
            };

        internal static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class CeilingFramingSpec
    {
        public long MainTypeId { get; set; }
        public double MainSpacingMm { get; set; }
        /// <summary>short | long; ignored when DirectionDeg is set.</summary>
        public string Direction { get; set; } = "long";
        public double? DirectionDeg { get; set; }
        public long? CrossTypeId { get; set; }
        public double CrossSpacingMm { get; set; }
        public long? PerimeterTypeId { get; set; }
        public long HangerTypeId { get; set; }
        public double HangerSpacingMm { get; set; }
        public double HangerMaxLengthMm { get; set; } = 3000;
        public double HangerEndOffsetMm { get; set; }
        /// <summary>From the ceiling's TOP face up to the mains' underside.</summary>
        public double DropMm { get; set; }
        /// <summary>Section depths (optional): each member's axis sits half its depth above the face it bears on.</summary>
        public double? MainDepthMm { get; set; }
        public double? CrossDepthMm { get; set; }
        public double? PerimeterDepthMm { get; set; }

        public IEnumerable<long> TypeIds()
        {
            var ids = new List<long> { MainTypeId, HangerTypeId };
            if (CrossTypeId.HasValue) ids.Add(CrossTypeId.Value);
            if (PerimeterTypeId.HasValue) ids.Add(PerimeterTypeId.Value);
            return ids.Distinct();
        }

        /// <summary>The plan input for one ceiling, its boundary in millimetres (first loop outer, the rest holes).</summary>
        public CeilingFramingInput ToInput(List<List<double[]>> loopsMm)
            => new CeilingFramingInput
            {
                Loops = loopsMm ?? new List<List<double[]>>(),
                Direction = Direction,
                DirectionAngleRad = DirectionDeg.HasValue ? DirectionDeg.Value * Math.PI / 180.0 : (double?)null,
                MainSpacing = MainSpacingMm,
                MainTypeKey = WallFramingSpec.Key(MainTypeId),
                CrossSpacing = CrossTypeId.HasValue ? CrossSpacingMm : 0,
                CrossTypeKey = CrossTypeId.HasValue ? WallFramingSpec.Key(CrossTypeId.Value) : null,
                PerimeterTypeKey = PerimeterTypeId.HasValue ? WallFramingSpec.Key(PerimeterTypeId.Value) : null,
                HangerSpacing = HangerSpacingMm,
                HangerEndOffset = HangerEndOffsetMm,
                HangerTypeKey = WallFramingSpec.Key(HangerTypeId),
            };
    }

    public static class FramingSpecRules
    {
        public const double MinSpacingMm = 10, MaxSpacingMm = 20000, MaxLengthMm = 100000;
        public const int MaxBlockingRows = 20;

        public static WallFramingSpec ParseWall(JToken spec, out List<FramingSpecError> errors)
        {
            var r = new Reader();
            JObject w = r.Root(spec, "wall");
            errors = r.Errors;
            if (w == null) return null;
            r.Known(w, "spec.wall", "layer", "stud", "track", "openings", "blocking");
            var result = new WallFramingSpec();

            JToken layer = w["layer"];
            if (layer != null && layer.Type != JTokenType.Null)
            {
                if (layer.Type == JTokenType.String && string.Equals(((string)layer).Trim(), "core", StringComparison.OrdinalIgnoreCase)) { }
                else if (layer.Type == JTokenType.Integer && (long)layer >= 0 && (long)layer < 64) { result.CoreLayer = false; result.LayerIndex = (int)(long)layer; }
                else r.Fail("spec.wall.layer", "bad_value", "'core' or a compound layer index 0..63");
            }

            JObject stud = r.Obj(w, "stud", "spec.wall", true);
            if (stud != null)
            {
                r.Known(stud, "spec.wall.stud", "type_id", "spacing_mm", "start", "max_first_bay_mm", "double_at_ends", "width_mm");
                result.StudTypeId = r.Id(stud, "type_id", "spec.wall.stud", true) ?? 0;
                result.SpacingMm = r.Mm(stud, "spacing_mm", "spec.wall.stud", true, MinSpacingMm, MaxSpacingMm) ?? 0;
                result.StartRule = r.Choice(stud, "start", "spec.wall.stud", "wall_start", "wall_start", "wall_end", "centred");
                result.MaxFirstBayMm = r.Mm(stud, "max_first_bay_mm", "spec.wall.stud", false, MinSpacingMm, MaxSpacingMm) ?? 0;
                result.DoubleAtEnds = r.Bool(stud, "double_at_ends", "spec.wall.stud") ?? false;
                result.StudWidthMm = r.Mm(stud, "width_mm", "spec.wall.stud", false, 1, 1000);
            }

            JObject track = r.Obj(w, "track", "spec.wall", true);
            if (track != null)
            {
                r.Known(track, "spec.wall.track", "bottom_type_id", "top_type_id", "top_same_as_bottom", "thickness_mm");
                result.BottomTrackTypeId = r.Id(track, "bottom_type_id", "spec.wall.track", true) ?? 0;
                long? top = r.Id(track, "top_type_id", "spec.wall.track", false);
                bool same = r.Bool(track, "top_same_as_bottom", "spec.wall.track") ?? !top.HasValue;
                if (same && top.HasValue && top.Value != result.BottomTrackTypeId)
                    r.Fail("spec.wall.track.top_type_id", "conflict", "top_same_as_bottom is true but top_type_id names another type");
                else if (!same && !top.HasValue)
                    r.Fail("spec.wall.track.top_type_id", "missing", "top_same_as_bottom is false");
                result.TopTrackTypeId = same ? result.BottomTrackTypeId : top ?? 0;
                result.TrackThicknessMm = r.Mm(track, "thickness_mm", "spec.wall.track", false, 0.1, 500);
            }

            JObject openings = r.Obj(w, "openings", "spec.wall", false);
            if (openings != null)
            {
                r.Known(openings, "spec.wall.openings", "king_studs", "jack_studs", "header_type_id", "sill_type_id", "cripple_spacing_mm");
                JToken kings = openings["king_studs"];
                if (kings != null && kings.Type != JTokenType.Null)
                {
                    if (kings.Type == JTokenType.Integer && ((long)kings == 1 || (long)kings == 2)) result.KingStuds = (int)(long)kings;
                    else r.Fail("spec.wall.openings.king_studs", "bad_value", "1 or 2");
                }
                result.JackStuds = r.Bool(openings, "jack_studs", "spec.wall.openings") ?? true;
                result.HeaderTypeId = r.Id(openings, "header_type_id", "spec.wall.openings", false);
                result.SillTypeId = r.Id(openings, "sill_type_id", "spec.wall.openings", false);
                result.CrippleSpacingMm = r.Mm(openings, "cripple_spacing_mm", "spec.wall.openings", false, MinSpacingMm, MaxSpacingMm) ?? 0;
            }

            JToken blocking = w["blocking"];
            if (blocking != null && blocking.Type != JTokenType.Null)
            {
                if (!(blocking is JArray rows)) r.Fail("spec.wall.blocking", "not_array");
                else if (rows.Count > MaxBlockingRows) r.Fail("spec.wall.blocking", "above_maximum", MaxBlockingRows + " rows");
                else
                    for (int i = 0; i < rows.Count; i++)
                    {
                        string path = "spec.wall.blocking[" + i + "]";
                        if (!(rows[i] is JObject row)) { r.Fail(path, "not_object"); continue; }
                        r.Known(row, path, "height_mm", "type_id");
                        double? h = r.Mm(row, "height_mm", path, true, 1, MaxLengthMm);
                        long? t = r.Id(row, "type_id", path, false);
                        if (h.HasValue) result.Blocking.Add(new BlockingRow { Height = h.Value, TypeKey = t.HasValue ? WallFramingSpec.Key(t.Value) : null });
                    }
            }
            return r.Errors.Count == 0 ? result : null;
        }

        public static CeilingFramingSpec ParseCeiling(JToken spec, out List<FramingSpecError> errors)
        {
            var r = new Reader();
            JObject c = r.Root(spec, "ceiling");
            errors = r.Errors;
            if (c == null) return null;
            r.Known(c, "spec.ceiling", "main", "cross", "perimeter", "hanger", "drop_mm");
            var result = new CeilingFramingSpec();

            JObject main = r.Obj(c, "main", "spec.ceiling", true);
            if (main != null)
            {
                r.Known(main, "spec.ceiling.main", "type_id", "spacing_mm", "direction", "depth_mm");
                result.MainDepthMm = r.Mm(main, "depth_mm", "spec.ceiling.main", false, 0, 2000);
                result.MainTypeId = r.Id(main, "type_id", "spec.ceiling.main", true) ?? 0;
                result.MainSpacingMm = r.Mm(main, "spacing_mm", "spec.ceiling.main", true, MinSpacingMm, MaxSpacingMm) ?? 0;
                JToken dir = main["direction"];
                if (dir == null || dir.Type == JTokenType.Null) { }
                else if (dir.Type == JTokenType.Integer || dir.Type == JTokenType.Float)
                {
                    double deg = (double)dir;
                    if (double.IsNaN(deg) || double.IsInfinity(deg) || Math.Abs(deg) > 360) r.Fail("spec.ceiling.main.direction", "bad_value", "an angle in degrees within -360..360");
                    else result.DirectionDeg = deg;
                }
                else if (dir.Type == JTokenType.String && (((string)dir).Trim().ToLowerInvariant() is string d) && (d == "short" || d == "long"))
                    result.Direction = d;
                else r.Fail("spec.ceiling.main.direction", "bad_value", "'short', 'long' or an angle in degrees");
            }

            JObject cross = r.Obj(c, "cross", "spec.ceiling", false);
            if (cross != null)
            {
                r.Known(cross, "spec.ceiling.cross", "type_id", "spacing_mm", "depth_mm");
                result.CrossDepthMm = r.Mm(cross, "depth_mm", "spec.ceiling.cross", false, 0, 2000);
                result.CrossTypeId = r.Id(cross, "type_id", "spec.ceiling.cross", true);
                result.CrossSpacingMm = r.Mm(cross, "spacing_mm", "spec.ceiling.cross", true, MinSpacingMm, MaxSpacingMm) ?? 0;
            }

            JObject perimeter = r.Obj(c, "perimeter", "spec.ceiling", false);
            if (perimeter != null)
            {
                r.Known(perimeter, "spec.ceiling.perimeter", "type_id", "depth_mm");
                result.PerimeterDepthMm = r.Mm(perimeter, "depth_mm", "spec.ceiling.perimeter", false, 0, 2000);
                result.PerimeterTypeId = r.Id(perimeter, "type_id", "spec.ceiling.perimeter", true);
            }

            JObject hanger = r.Obj(c, "hanger", "spec.ceiling", true);
            if (hanger != null)
            {
                r.Known(hanger, "spec.ceiling.hanger", "type_id", "spacing_mm", "max_length_mm", "end_offset_mm", "attach");
                result.HangerTypeId = r.Id(hanger, "type_id", "spec.ceiling.hanger", true) ?? 0;
                result.HangerSpacingMm = r.Mm(hanger, "spacing_mm", "spec.ceiling.hanger", true, MinSpacingMm, MaxSpacingMm) ?? 0;
                result.HangerMaxLengthMm = r.Mm(hanger, "max_length_mm", "spec.ceiling.hanger", false, 1, MaxLengthMm) ?? 3000;
                double? end = r.Mm(hanger, "end_offset_mm", "spec.ceiling.hanger", false, 0, MaxSpacingMm);
                result.HangerEndOffsetMm = end ?? result.HangerSpacingMm / 2;
                r.Choice(hanger, "attach", "spec.ceiling.hanger", "structure_above", "structure_above");
            }

            result.DropMm = r.Mm(c, "drop_mm", "spec.ceiling", false, 0, MaxLengthMm) ?? 0;
            return r.Errors.Count == 0 ? result : null;
        }

        /// <summary>SHA-256 of the spec as canonical JSON (object keys sorted, ordinal): the marker records it.</summary>
        public static string Hash(JToken spec)
        {
            var sb = new StringBuilder();
            Canonical(spec, sb);
            using (SHA256 sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static void Canonical(JToken t, StringBuilder sb)
        {
            if (t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined) { sb.Append("null"); return; }
            if (t is JObject o)
            {
                sb.Append('{');
                bool first = true;
                foreach (JProperty p in o.Properties().OrderBy(q => q.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Newtonsoft.Json.JsonConvert.ToString(p.Name)).Append(':');
                    Canonical(p.Value, sb);
                }
                sb.Append('}');
                return;
            }
            if (t is JArray a)
            {
                sb.Append('[');
                for (int i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); Canonical(a[i], sb); }
                sb.Append(']');
                return;
            }
            // 406 and 406.0 are the same spacing: numbers hash by value, not by spelling.
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
            {
                sb.Append(((double)t).ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            sb.Append(t.ToString(Newtonsoft.Json.Formatting.None));
        }

        private sealed class Reader
        {
            public readonly List<FramingSpecError> Errors = new List<FramingSpecError>();

            public void Fail(string path, string code, string detail = null)
                => Errors.Add(new FramingSpecError { Path = path, Code = code, Detail = detail });

            public JObject Root(JToken spec, string key)
            {
                if (spec == null || spec.Type == JTokenType.Null) { Fail("spec", "missing", "spec." + key + " is required"); return null; }
                if (!(spec is JObject o)) { Fail("spec", "not_object"); return null; }
                foreach (JProperty p in o.Properties())
                    if (p.Name != key) Fail("spec." + p.Name, "unknown_field", "this operation reads only spec." + key);
                return Obj(o, key, "spec", true);
            }

            public void Known(JObject o, string path, params string[] keys)
            {
                foreach (JProperty p in o.Properties())
                    if (Array.IndexOf(keys, p.Name) < 0) Fail(path + "." + p.Name, "unknown_field", "known: " + string.Join(", ", keys));
            }

            public JObject Obj(JObject parent, string key, string path, bool required)
            {
                JToken t = parent[key];
                if (t == null || t.Type == JTokenType.Null) { if (required) Fail(path + "." + key, "missing"); return null; }
                if (t is JObject o) return o;
                Fail(path + "." + key, "not_object");
                return null;
            }

            public long? Id(JObject o, string key, string path, bool required)
            {
                JToken t = o[key];
                if (t == null || t.Type == JTokenType.Null) { if (required) Fail(path + "." + key, "missing", "an element id of a type"); return null; }
                long id;
                if (t.Type == JTokenType.Integer) id = (long)t;
                else if (t.Type == JTokenType.Float && Math.Abs((double)t - Math.Round((double)t)) < 1e-9 && Math.Abs((double)t) < 9e15) id = (long)Math.Round((double)t);
                else { Fail(path + "." + key, "not_integer", "an element id, not a name; resolve names with the query tools first"); return null; }
                if (id <= 0) { Fail(path + "." + key, "bad_value", "element ids are > 0"); return null; }
                return id;
            }

            public double? Mm(JObject o, string key, string path, bool required, double min, double max)
            {
                JToken t = o[key];
                if (t == null || t.Type == JTokenType.Null) { if (required) Fail(path + "." + key, "missing", "millimetres"); return null; }
                if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float) { Fail(path + "." + key, "not_number", "millimetres"); return null; }
                double v = (double)t;
                if (double.IsNaN(v) || double.IsInfinity(v)) { Fail(path + "." + key, "not_number"); return null; }
                if (v < min) { Fail(path + "." + key, "below_minimum", v.ToString(CultureInfo.InvariantCulture) + " < " + min.ToString(CultureInfo.InvariantCulture) + " mm (a value in metres?)"); return null; }
                if (v > max) { Fail(path + "." + key, "above_maximum", v.ToString(CultureInfo.InvariantCulture) + " > " + max.ToString(CultureInfo.InvariantCulture) + " mm"); return null; }
                return v;
            }

            public bool? Bool(JObject o, string key, string path)
            {
                JToken t = o[key];
                if (t == null || t.Type == JTokenType.Null) return null;
                if (t.Type == JTokenType.Boolean) return (bool)t;
                Fail(path + "." + key, "not_boolean");
                return null;
            }

            public string Choice(JObject o, string key, string path, string fallback, params string[] allowed)
            {
                JToken t = o[key];
                if (t == null || t.Type == JTokenType.Null) return fallback;
                string v = t.Type == JTokenType.String ? ((string)t).Trim().ToLowerInvariant() : null;
                if (v != null && Array.IndexOf(allowed, v) >= 0) return v;
                Fail(path + "." + key, "bad_value", "one of: " + string.Join(", ", allowed));
                return fallback;
            }
        }
    }
}
