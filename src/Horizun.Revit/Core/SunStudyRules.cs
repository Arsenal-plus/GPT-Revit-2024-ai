// -----------------------------------------------------------------------------
// Horizun - original Horizun code.
//
// THE SUN STUDY of horizun_manage_views set_sun_study, decided without a Revit:
// the request's type, instants and optional site coordinates become one request
// the command writes into a view's SunAndShadowSettings, and the post-commit
// re-read test that says whether Revit kept it.
//
//   * NOTHING IS GUESSED. An instant without an offset ('2026-06-21T12:00') is
//     refused: Revit rejects DateTimeKind.Unspecified, and choosing a zone for the
//     caller would move the sun by hours without anyone noticing. 'Z' or +hh:mm.
//   * A still image is ONE instant: an 'end' on it would be written and then
//     ignored by Revit, so it is refused instead of silently dropped.
//   * A study needs start and end with end after start; a single-day study spans
//     at most 24 hours (longer is a multi-day study, and saying so beats Revit
//     quietly clamping it).
//   * lat/lon come together or not at all, in degrees within their ranges. They
//     move the PROJECT site (every view's sun), which the command discloses.
//   * SameInstant is the re-read test: Revit hands the instant back in UTC, and a
//     minute of tolerance absorbs its rounding without hiding a wrong hour.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Horizun.Revit.Core
{
    public enum SunStudyKind { Still, SingleDay, MultiDay }

    public sealed class SunStudyRequest
    {
        public SunStudyKind Kind { get; set; }
        /// <summary>The start (or, for a still image, the only) instant, DateTimeKind.Utc.</summary>
        public DateTime StartUtc { get; set; }
        /// <summary>The end instant for a study, DateTimeKind.Utc; null for a still image.</summary>
        public DateTime? EndUtc { get; set; }
        public double? LatitudeDegrees { get; set; }
        public double? LongitudeDegrees { get; set; }
        public bool MovesSite => LatitudeDegrees.HasValue;
    }

    public static class SunStudyRules
    {
        public static readonly TimeSpan InstantTolerance = TimeSpan.FromMinutes(1);
        /// <summary>1e-7 rad is about 0.6 m on the Earth's surface: far below any sun-angle effect.</summary>
        public const double AngleToleranceRadians = 1e-7;

        private static readonly Regex HasOffset = new Regex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$",
                                                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static SunStudyKind ParseKind(string type)
        {
            switch ((type ?? "").Trim().Replace('-', '_').ToLowerInvariant())
            {
                case "still": return SunStudyKind.Still;
                case "single_day": return SunStudyKind.SingleDay;
                case "multi_day": return SunStudyKind.MultiDay;
                default:
                    throw new ArgumentException("sun.type must be still, single_day or multi_day (got '" + type + "').");
            }
        }

        /// <summary>An ISO-8601 date-time WITH its offset, as a UTC DateTime; anything else refuses.</summary>
        public static DateTime ParseInstant(string text, string field)
        {
            string s = (text ?? "").Trim();
            if (!HasOffset.IsMatch(s))
                throw new ArgumentException("sun." + field + " must be an ISO-8601 date-time with its offset, e.g. 2026-06-21T12:00:00-05:00 " +
                                            "or ...Z (got '" + text + "'). An instant without one would be a guess about the time zone.");
            if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
                throw new ArgumentException("sun." + field + " is not a valid date-time: '" + text + "'.");
            return parsed.UtcDateTime;
        }

        public static SunStudyRequest Parse(string type, string start, string end, double? latitude, double? longitude)
        {
            SunStudyKind kind = ParseKind(type);
            if (string.IsNullOrWhiteSpace(start)) throw new ArgumentException("sun.start is required: the instant (still) or the start of the study.");
            var request = new SunStudyRequest { Kind = kind, StartUtc = ParseInstant(start, "start") };
            bool hasEnd = !string.IsNullOrWhiteSpace(end);
            if (kind == SunStudyKind.Still)
            {
                if (hasEnd) throw new ArgumentException("a still sun takes ONE instant (start); sun.end would be ignored by Revit - drop it or use a study type.");
            }
            else
            {
                if (!hasEnd) throw new ArgumentException("a " + Name(kind) + " study needs sun.end as well as sun.start.");
                request.EndUtc = ParseInstant(end, "end");
                if (request.EndUtc.Value <= request.StartUtc) throw new ArgumentException("sun.end must be after sun.start.");
                if (kind == SunStudyKind.SingleDay && request.EndUtc.Value - request.StartUtc > TimeSpan.FromHours(24))
                    throw new ArgumentException("a single_day study spans at most 24 hours; use multi_day for a longer one.");
            }
            if (latitude.HasValue != longitude.HasValue)
                throw new ArgumentException("sun.lat and sun.lon go together: give both (degrees) or neither.");
            if (latitude.HasValue)
            {
                double lat = latitude.Value, lon = longitude.Value;
                if (double.IsNaN(lat) || double.IsInfinity(lat) || lat < -90 || lat > 90) throw new ArgumentException("sun.lat must be degrees in -90..90.");
                if (double.IsNaN(lon) || double.IsInfinity(lon) || lon < -180 || lon > 180) throw new ArgumentException("sun.lon must be degrees in -180..180.");
                request.LatitudeDegrees = lat;
                request.LongitudeDegrees = lon;
            }
            return request;
        }

        public static string Name(SunStudyKind kind) => kind == SunStudyKind.Still ? "still" : kind == SunStudyKind.SingleDay ? "single_day" : "multi_day";

        /// <summary>
        /// Revit documents its output as UTC. A Local kind is converted; an Unspecified
        /// one is taken at its documented word (UTC) rather than shifted by this machine's zone.
        /// </summary>
        public static DateTime AsUtc(DateTime reread) =>
            reread.Kind == DateTimeKind.Local ? reread.ToUniversalTime() : DateTime.SpecifyKind(reread, DateTimeKind.Utc);

        public static bool SameInstant(DateTime wantedUtc, DateTime rereadUtc) =>
            (AsUtc(rereadUtc) - wantedUtc).Duration() <= InstantTolerance;

        public static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
        public static double ToDegrees(double radians) => radians * 180.0 / Math.PI;

        public static bool SameAngle(double wantedRadians, double rereadRadians) =>
            Math.Abs(wantedRadians - rereadRadians) <= AngleToleranceRadians;

        public static string Iso(DateTime utc) => AsUtc(utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}
