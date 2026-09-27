// -----------------------------------------------------------------------------
// Horizun - original Horizun code.
//
// horizun_manage_views operation=set_sun_study: a view's SunAndShadowSettings set
// to a still sun, a single-day or a multi-day study with the instants GIVEN, and
// optionally the site's latitude/longitude - ONE action of the batch's single
// transaction.
//
//   * The instants are read by JSON token (SunStudyRules.FromJson) and the request is
//     parsed verbatim (ParseVerbatim): an ISO string turned into a Date token and
//     printed back loses its offset, which refused every valid instant.
//   * The request is decided by the pure SunStudyRules: an instant without an
//     offset, an end on a still sun, a study without an end and a lone lat or lon
//     are refused by name in the rehearsal, never completed with a guess.
//   * Revit takes UTC (it refuses DateTimeKind.Unspecified), so each instant is
//     sent as the UTC it names; the view shows it in the site's own time zone.
//   * A study clears SunriseToSunset when it is set: with it on, Revit ignores the
//     start and end it was just given. The row says it did.
//   * lat/lon are NOT per view: SunAndShadowSettings.Latitude/Longitude are
//     read-only, and the only writable place is the SiteLocation of the project
//     location the settings use - every view's sun moves, and Revit re-derives the
//     place name, time zone and weather station from the coordinates. The
//     rehearsal names that scope before anything is written.
//   * The settings may be shared with other views (SharesSettings); the rehearsal
//     says so, because a change here is then a change there.
//   * The resolved plan binds the settings' current type, instants and the site
//     coordinates, so a change made between rehearsal and apply refuses the token.
//   * After the commit the view's settings are re-read: type, start and end within
//     SunStudyRules.InstantTolerance, sunrise-to-sunset OFF for a study, and the site's lat/lon in radians. The
//     settings' own Latitude/Longitude are reported raw and NOT judged: their unit
//     is not documented.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageViewsCommand
    {
        internal static bool IsSunStudyOperation(string op) => string.Equals(op, "set_sun_study", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The request with every string kept a string. JObject.Parse turns ISO-looking text
        /// into Date tokens that print back without their offset - a sun instant, or a view
        /// named like a timestamp, would not be what was sent. Trailing content still refuses.
        /// </summary>
        internal static JObject ParseVerbatim(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
            {
                JObject request = JObject.Load(reader);
                while (reader.Read()) { }
                return request;
            }
        }

        // By token, not by Value<string>: a Date token printed as text has lost its offset.
        private static SunStudyRequest ReadSunStudy(JObject a) => SunStudyRules.FromJson(a["sun"] as JObject);

        private static SunAndShadowType RevitSunType(SunStudyKind kind) =>
            kind == SunStudyKind.Still ? SunAndShadowType.StillImage :
            kind == SunStudyKind.SingleDay ? SunAndShadowType.OneDayStudy : SunAndShadowType.MultiDayStudy;

        /// <summary>The SiteLocation the settings' sun is computed for: the project location they name.</summary>
        private static SiteLocation SunSite(Document doc, SunAndShadowSettings s) =>
            (doc.GetElement(s.ProjectLocationId) as ProjectLocation)?.GetSiteLocation() ?? doc.SiteLocation;

        private static JObject SunState(Document doc, SunAndShadowSettings s)
        {
            var state = new JObject
            {
                ["settings_id"] = Rid.Value(s.Id),
                ["type"] = s.SunAndShadowType.ToString(),
                ["start_utc"] = SunStudyRules.Iso(s.StartDateAndTime),
                ["end_utc"] = SunStudyRules.Iso(s.EndDateAndTime),
                ["sunrise_to_sunset"] = s.SunriseToSunset,
                ["shares_settings"] = s.SharesSettings,
                ["settings_latitude_raw"] = s.Latitude,
                ["settings_longitude_raw"] = s.Longitude
            };
            try
            {
                SiteLocation site = SunSite(doc, s);
                state["site_latitude_degrees"] = SunStudyRules.ToDegrees(site.Latitude);
                state["site_longitude_degrees"] = SunStudyRules.ToDegrees(site.Longitude);
                state["site_time_zone_hours"] = site.TimeZone;
                state["site_place_name"] = site.PlaceName;
            }
            catch (Exception ex) { state["site_unreadable"] = ex.Message; }
            return state;
        }

        internal static void ValidateSunStudy(Document doc, JObject a, Dictionary<string, Type> known)
        {
            ReadSunStudy(a);
            Reference<View>(doc, a, "view_id", "view_key", known);
            // A view created earlier in the batch is checked when it exists (apply);
            // an existing one is checked now, so the rehearsal refuses a schedule or a sheet.
            if (string.IsNullOrWhiteSpace(a.Value<string>("view_key")) &&
                Need<View>(doc, a, "view_id").SunAndShadowSettings == null)
                throw new ArgumentException("view_id is a view that takes no sun settings (a schedule, a sheet, a legend...).");
        }

        /// <summary>The rehearsal: what the view's sun is now, what it will be, and how far the change reaches.</summary>
        internal static JObject SunStudyPreview(Document doc, JObject a)
        {
            SunStudyRequest r = ReadSunStudy(a);
            var preview = new JObject
            {
                ["requested"] = new JObject
                {
                    ["type"] = RevitSunType(r.Kind).ToString(),
                    ["start_utc"] = SunStudyRules.Iso(r.StartUtc),
                    ["end_utc"] = r.EndUtc.HasValue ? SunStudyRules.Iso(r.EndUtc.Value) : null,
                    ["site_latitude_degrees"] = r.LatitudeDegrees,
                    ["site_longitude_degrees"] = r.LongitudeDegrees
                },
                ["location_scope"] = r.MovesSite ? "project_site" : "unchanged"
            };
            if (r.MovesSite)
                preview["location_side_effects"] = new JArray("every view's sun moves", "Revit re-derives place name, time zone and weather station");
            long? id = a.Value<long?>("view_id");
            if (id.HasValue && Rid.CanRepresent(id.Value) &&
                doc.GetElement(Rid.Make(id.Value)) is View view && view.SunAndShadowSettings != null)
                preview["current"] = SunState(doc, view.SunAndShadowSettings);
            return preview;
        }

        /// <summary>Binds the view's current sun and the site into the resolved plan: a change since refuses as stale.</summary>
        internal static void BindSunStudy(Document doc, JObject a, IDictionary<string, string> before)
        {
            long? id = a.Value<long?>("view_id");
            if (!id.HasValue || !Rid.CanRepresent(id.Value) || !(doc.GetElement(Rid.Make(id.Value)) is View view) ||
                view.SunAndShadowSettings == null) return;
            JObject state = SunState(doc, view.SunAndShadowSettings);
            foreach (string field in new[] { "settings_id", "type", "start_utc", "end_utc", "sunrise_to_sunset",
                                             "site_latitude_degrees", "site_longitude_degrees" })
                before["sun." + field] = Convert.ToString(((JValue)state[field] ?? JValue.CreateNull()).Value, CultureInfo.InvariantCulture) ?? "";
        }

        internal static Element ApplySunStudy(Document doc, JObject a, Dictionary<string, ElementId> aliases)
        {
            SunStudyRequest r = ReadSunStudy(a);
            View view = Resolve<View>(doc, a, "view_id", "view_key", aliases);
            SunAndShadowSettings s = view.SunAndShadowSettings
                ?? throw new ArgumentException("view '" + view.Name + "' takes no sun settings.");
            JObject before = SunState(doc, s);
            s.SunAndShadowType = RevitSunType(r.Kind);
            bool cleared = false;
            if (r.Kind != SunStudyKind.Still && s.SunriseToSunset) { s.SunriseToSunset = false; cleared = true; }
            // End first when the new start lies after the current end, so the pair never
            // passes through start > end on the way to the values asked for.
            if (r.EndUtc.HasValue && r.StartUtc > SunStudyRules.AsUtc(s.EndDateAndTime))
            {
                s.EndDateAndTime = r.EndUtc.Value;
                s.StartDateAndTime = r.StartUtc;
            }
            else
            {
                s.StartDateAndTime = r.StartUtc;
                if (r.EndUtc.HasValue) s.EndDateAndTime = r.EndUtc.Value;
            }
            if (r.MovesSite)
            {
                SiteLocation site = SunSite(doc, s);
                site.Latitude = SunStudyRules.ToRadians(r.LatitudeDegrees.Value);
                site.Longitude = SunStudyRules.ToRadians(r.LongitudeDegrees.Value);
            }
            a["__sun"] = new JObject
            {
                ["view_id"] = Rid.Value(view.Id),
                ["before"] = before,
                ["sunrise_to_sunset_cleared"] = cleared,
                ["location_scope"] = r.MovesSite ? "project_site" : "unchanged"
            };
            return view;
        }

        /// <summary>
        /// The view's settings re-read after the commit: the type asked for, each instant
        /// within a minute of the one sent, and the site's coordinates when they were moved.
        /// </summary>
        internal static bool VerifySunStudy(Document doc, JObject a, Element e)
        {
            if (!(a["__sun"] is JObject detail)) return false;
            SunStudyRequest r = ReadSunStudy(a);
            SunAndShadowSettings s = (e as View)?.SunAndShadowSettings;
            if (s == null) { detail["reread"] = JValue.CreateNull(); detail["verified"] = false; return false; }
            JObject now = SunState(doc, s);
            bool typeOk = s.SunAndShadowType == RevitSunType(r.Kind);
            bool startOk = SunStudyRules.SameInstant(r.StartUtc, s.StartDateAndTime);
            bool endOk = !r.EndUtc.HasValue || SunStudyRules.SameInstant(r.EndUtc.Value, s.EndDateAndTime);
            bool siteOk = true;
            if (r.MovesSite)
            {
                try
                {
                    SiteLocation site = SunSite(doc, s);
                    siteOk = SunStudyRules.SameAngle(SunStudyRules.ToRadians(r.LatitudeDegrees.Value), site.Latitude) &&
                             SunStudyRules.SameAngle(SunStudyRules.ToRadians(r.LongitudeDegrees.Value), site.Longitude);
                }
                catch { siteOk = false; }
            }
            // A study left on sunrise-to-sunset ignores the start and end just written.
            bool sunriseOk = r.Kind == SunStudyKind.Still || !s.SunriseToSunset;
            detail["reread"] = now;
            detail["checks"] = new JObject { ["type"] = typeOk, ["start"] = startOk, ["end"] = endOk, ["site"] = siteOk,
                                             ["sunrise_to_sunset_off"] = sunriseOk };
            bool ok = typeOk && startOk && endOk && siteOk && sunriseOk;
            detail["verified"] = ok;
            return ok;
        }

        internal static JObject SunStudyDetail(JObject a) => a["__sun"] as JObject;
    }
}
