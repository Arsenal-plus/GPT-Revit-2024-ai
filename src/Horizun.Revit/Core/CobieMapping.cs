// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE COBIE MAPPING: the caller's data for horizun_export format=cobie, parsed and
// checked before a single element is read.
//
// Organisation-neutral by construction: who created the workbook, the facility's
// names and category, which categories are components, where the classification
// and the zones live - all of it arrives with the request. Nothing is taken from
// this machine (no user name, no account, no e-mail) and nothing is defaulted to a
// company's convention. A field the caller did not give stays empty in the
// workbook, and when COBie requires it that empty cell is a FINDING, never an
// invented "n/a".
//
// The one other source is the caller's own project context (the project-context.json
// horizun_project_context writes), read here without the server: a few of its
// fields fill what the arguments left out, an explicit argument always wins, and
// every default says where it came from (Provenance). project-context v1 carries
// no contact e-mail, so it cannot provide created_by.
//
// Revit-free: JSON in, a mapping out. Tested in Horizun.Core.Tests.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>The Facility row's caller-given values.</summary>
    public sealed class CobieFacilityArguments
    {
        public string Name, Category, ProjectName, SiteName, Phase, Description, ProjectDescription, SiteDescription, CurrencyUnit, AreaMeasurement;
    }

    public sealed class CobieMapping
    {
        public const string SpaceSourceRooms = "rooms", SpaceSourceSpaces = "spaces";
        public const int DefaultMaxFindings = 500, MaxMaxFindings = 5000;
        public const long MaxProjectContextBytes = 2L * 1024 * 1024;

        public static readonly string[] Fields =
        {
            "created_by", "created_on", "facility", "phase", "component_categories", "space_source", "category_parameter",
            "zone_parameter", "zone_category", "component_name_parameter", "type_fields", "component_fields",
            "project_context_path", "max_findings"
        };

        public static readonly string[] FacilityFields =
        {
            "name", "category", "project_name", "site_name", "phase", "description", "project_description", "site_description",
            "currency_unit", "area_measurement"
        };

        /// <summary>The fields a project context can fill, in reply order.</summary>
        public static readonly string[] DefaultableFields =
        {
            "created_by", "facility.project_name", "facility.site_name", "facility.project_description", "facility.phase", "category_parameter"
        };

        private static readonly Regex Email = new Regex(@"^[^@\s]+@[^@\s]+$", RegexOptions.CultureInvariant);
        private static readonly Regex IsoDate = new Regex(
            @"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})?)?$", RegexOptions.CultureInvariant);

        public string CreatedBy;
        /// <summary>Null: the workbook is stamped with the export time (UTC).</summary>
        public DateTime? CreatedOnUtc;
        public readonly CobieFacilityArguments Facility = new CobieFacilityArguments();
        /// <summary>The REVIT phase: which rooms/spaces exist, and where components are placed.</summary>
        public string Phase;
        public readonly List<string> ComponentCategories = new List<string>();
        public string SpaceSource = SpaceSourceRooms;
        public string CategoryParameter, ZoneParameter, ZoneCategory, ComponentNameParameter;
        public readonly List<KeyValuePair<string, string>> TypeFields = new List<KeyValuePair<string, string>>();
        public readonly List<KeyValuePair<string, string>> ComponentFields = new List<KeyValuePair<string, string>>();
        public string ProjectContextPath;
        public int MaxFindings = DefaultMaxFindings;

        /// <summary>Where each defaultable field came from: "argument", or "project_context /pointer".</summary>
        public readonly SortedDictionary<string, string> Provenance = new SortedDictionary<string, string>(StringComparer.Ordinal);

        public bool UsesSpaces => SpaceSource == SpaceSourceSpaces;

        public string TypeField(string column) => Lookup(TypeFields, column);
        public string ComponentField(string column) => Lookup(ComponentFields, column);

        private static string Lookup(List<KeyValuePair<string, string>> fields, string column)
        {
            foreach (KeyValuePair<string, string> kv in fields)
                if (kv.Key == column) return kv.Value;
            return null;
        }

        /// <summary>The CreatedOn text every row carries: ISO-8601 to the second, UTC.</summary>
        public static string CreatedOnText(DateTime utc) =>
            utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        // ---- parsing ------------------------------------------------------------------

        /// <summary>
        /// The mapping, with every problem named at once. An unknown field, a value of the
        /// wrong kind and a column that a type_fields/component_fields map cannot fill are
        /// problems: a mistyped key must not be silently dropped on its way to the workbook.
        /// Requirements that a project context could still satisfy are checked later, by
        /// <see cref="Missing"/>.
        /// </summary>
        public static CobieMapping Parse(JToken token, out List<string> problems)
        {
            problems = new List<string>();
            var m = new CobieMapping();
            var o = token as JObject;
            if (o == null)
            {
                problems.Add("cobie must be an object: the caller's mapping (created_by, facility, phase, component_categories, ...)");
                return m;
            }
            foreach (JProperty p in o.Properties())
                if (Array.IndexOf(Fields, p.Name) < 0)
                    problems.Add("cobie." + p.Name + " is not a cobie field; the fields are " + string.Join(", ", Fields));

            m.CreatedBy = Text(o, "created_by", "cobie.created_by", problems);
            m.CreatedOnUtc = CreatedOn(o["created_on"], problems);

            JToken facility = o["facility"];
            if (facility != null && facility.Type != JTokenType.Null)
            {
                var f = facility as JObject;
                if (f == null) problems.Add("cobie.facility must be an object");
                else
                {
                    foreach (JProperty p in f.Properties())
                        if (Array.IndexOf(FacilityFields, p.Name) < 0)
                            problems.Add("cobie.facility." + p.Name + " is not a facility field; the fields are " + string.Join(", ", FacilityFields));
                    m.Facility.Name = Text(f, "name", "cobie.facility.name", problems);
                    m.Facility.Category = Text(f, "category", "cobie.facility.category", problems);
                    m.Facility.ProjectName = Text(f, "project_name", "cobie.facility.project_name", problems);
                    m.Facility.SiteName = Text(f, "site_name", "cobie.facility.site_name", problems);
                    m.Facility.Phase = Text(f, "phase", "cobie.facility.phase", problems);
                    m.Facility.Description = Text(f, "description", "cobie.facility.description", problems);
                    m.Facility.ProjectDescription = Text(f, "project_description", "cobie.facility.project_description", problems);
                    m.Facility.SiteDescription = Text(f, "site_description", "cobie.facility.site_description", problems);
                    m.Facility.CurrencyUnit = Text(f, "currency_unit", "cobie.facility.currency_unit", problems);
                    m.Facility.AreaMeasurement = Text(f, "area_measurement", "cobie.facility.area_measurement", problems);
                }
            }

            m.Phase = Text(o, "phase", "cobie.phase", problems);

            JToken categories = o["component_categories"];
            if (categories != null && categories.Type != JTokenType.Null)
            {
                var array = categories as JArray;
                if (array == null) problems.Add("cobie.component_categories must be an array of OST_ tokens");
                else
                    foreach (JToken t in array)
                    {
                        string c = t.Type == JTokenType.String ? ((string)t).Trim() : null;
                        if (string.IsNullOrEmpty(c) || !c.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) || c.IndexOfAny(new[] { ',', ' ' }) >= 0)
                        {
                            problems.Add("cobie.component_categories holds " + t.ToString(Formatting.None) + ", which is not one BuiltInCategory token (OST_...)");
                            continue;
                        }
                        if (!m.ComponentCategories.Any(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase))) m.ComponentCategories.Add(c);
                    }
            }

            string source = Text(o, "space_source", "cobie.space_source", problems);
            if (source != null)
            {
                if (source == SpaceSourceRooms || source == SpaceSourceSpaces) m.SpaceSource = source;
                else problems.Add("cobie.space_source must be rooms or spaces, not '" + source + "'");
            }

            m.CategoryParameter = Text(o, "category_parameter", "cobie.category_parameter", problems);
            m.ZoneParameter = Text(o, "zone_parameter", "cobie.zone_parameter", problems);
            m.ZoneCategory = Text(o, "zone_category", "cobie.zone_category", problems);
            m.ComponentNameParameter = Text(o, "component_name_parameter", "cobie.component_name_parameter", problems);
            if (m.ZoneCategory != null && m.ZoneParameter == null)
                problems.Add("cobie.zone_category needs zone_parameter: without it no Zone row is written and the category would be silently ignored");
            ReadFieldMap(o["type_fields"], "cobie.type_fields", CobieRules.TypeFieldColumns, m.TypeFields, problems);
            ReadFieldMap(o["component_fields"], "cobie.component_fields", CobieRules.ComponentFieldColumns, m.ComponentFields, problems);

            m.ProjectContextPath = Text(o, "project_context_path", "cobie.project_context_path", problems);
            if (m.ProjectContextPath != null && !Path.IsPathRooted(m.ProjectContextPath))
                problems.Add("cobie.project_context_path must be an absolute path");

            JToken max = o["max_findings"];
            if (max != null && max.Type != JTokenType.Null)
            {
                if (max.Type != JTokenType.Integer) problems.Add("cobie.max_findings must be an integer");
                else
                {
                    long n = (long)max;
                    if (n < 1 || n > MaxMaxFindings) problems.Add("cobie.max_findings must be 1.." + MaxMaxFindings);
                    else m.MaxFindings = (int)n;
                }
            }

            if (m.CreatedBy != null) m.Provenance["created_by"] = "argument";
            if (m.Facility.ProjectName != null) m.Provenance["facility.project_name"] = "argument";
            if (m.Facility.SiteName != null) m.Provenance["facility.site_name"] = "argument";
            if (m.Facility.ProjectDescription != null) m.Provenance["facility.project_description"] = "argument";
            if (m.Facility.Phase != null) m.Provenance["facility.phase"] = "argument";
            if (m.CategoryParameter != null) m.Provenance["category_parameter"] = "argument";
            return m;
        }

        /// <summary>A trimmed, non-empty string; null when absent. A present value of another kind is a problem.</summary>
        private static string Text(JObject o, string key, string label, List<string> problems)
        {
            JToken t = o[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.String) { problems.Add(label + " must be a string"); return null; }
            string s = ((string)t).Trim();
            if (s.Length == 0) { problems.Add(label + " must not be empty (omit it instead)"); return null; }
            return s;
        }

        private static void ReadFieldMap(JToken token, string label, string[] allowed, List<KeyValuePair<string, string>> into, List<string> problems)
        {
            if (token == null || token.Type == JTokenType.Null) return;
            var map = token as JObject;
            if (map == null) { problems.Add(label + " must be an object {COBie column: Revit parameter name}"); return; }
            foreach (JProperty p in map.Properties())
            {
                if (Array.IndexOf(allowed, p.Name) < 0)
                {
                    problems.Add(label + "." + p.Name + " is not a column it can fill; it fills " + string.Join(", ", allowed));
                    continue;
                }
                string parameter = p.Value.Type == JTokenType.String ? ((string)p.Value).Trim() : null;
                if (string.IsNullOrEmpty(parameter)) { problems.Add(label + "." + p.Name + " must name a Revit parameter"); continue; }
                into.Add(new KeyValuePair<string, string>(p.Name, parameter));
            }
        }

        /// <summary>
        /// ISO-8601 date or date-time. A value without an offset is read as UTC. Newtonsoft may
        /// already have turned the string into a date while parsing the request; both arrive here.
        /// </summary>
        internal static DateTime? CreatedOn(JToken token, List<string> problems)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Date)
            {
                object value = ((JValue)token).Value;
                if (value is DateTimeOffset offset) return offset.UtcDateTime;
                var d = (DateTime)value;
                return d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime();
            }
            string raw = token.Type == JTokenType.String ? ((string)token).Trim() : null;
            DateTimeOffset parsed;
            if (raw == null || !IsoDate.IsMatch(raw) ||
                !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
            {
                problems.Add("cobie.created_on must be an ISO-8601 date or date-time (2026-09-27 or 2026-09-27T10:00:00Z), not " + token.ToString(Formatting.None));
                return null;
            }
            return parsed.UtcDateTime;
        }

        // ---- what must exist once the defaults are in ---------------------------------

        /// <summary>Every requirement still unmet, named. Empty: the mapping can build a workbook.</summary>
        public List<string> Missing()
        {
            var missing = new List<string>();
            if (CreatedBy == null)
                missing.Add("cobie.created_by is required: the COBie CreatedBy contact e-mail written on every row. It is never taken from " +
                            "this machine" + (ProjectContextPath != null ? ", and a project-context v1 file has no contact field to take it from" : ""));
            else if (!Email.IsMatch(CreatedBy))
                missing.Add("cobie.created_by '" + CreatedBy + "' is not an e-mail address: COBie CreatedBy names a contact by e-mail");
            if (Facility.Name == null) missing.Add("cobie.facility.name is required: the Facility row's Name");
            if (Phase == null)
                missing.Add("cobie.phase is required: rooms and spaces exist per phase and components are placed in that phase's rooms; " +
                            "a hidden default (the last phase) would describe a different building. Name the Revit phase");
            if (ComponentCategories.Count == 0)
                missing.Add("cobie.component_categories is required and must not be empty: which categories' instances are COBie " +
                            "Components is the caller's decision (e.g. OST_Doors, OST_MechanicalEquipment); there is no default list");
            return missing;
        }

        public JObject ProvenanceJson()
        {
            var o = new JObject();
            foreach (string field in DefaultableFields)
                o[field] = Provenance.TryGetValue(field, out string from) ? from : "absent";
            return o;
        }

        // ---- the project context ------------------------------------------------------

        /// <summary>
        /// The project-context.json horizun_project_context writes, read as JSON (dates stay
        /// text) and required to be schema_version 1. The file's SHA-256 comes back so the
        /// approval can be bound to its content. Null with the reason when it cannot be used.
        /// </summary>
        public static JObject ReadProjectContext(string path, out string sha256, out string error)
        {
            sha256 = null;
            error = null;
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) { error = "it must be an absolute path."; return null; }
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            { error = "it is not a valid path: " + ex.Message; return null; }
            if (!File.Exists(full)) { error = "there is no file at " + full + "."; return null; }
            byte[] bytes;
            try
            {
                long length = new FileInfo(full).Length;
                if (length > MaxProjectContextBytes)
                { error = full + " is " + length + " bytes; a project context is read up to " + MaxProjectContextBytes + " bytes."; return null; }
                bytes = File.ReadAllBytes(full);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { error = "it could not be read: " + ex.Message; return null; }
            using (var sha = SHA256.Create())
                sha256 = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            JObject context;
            try
            {
                int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                string text = new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip);
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                    context = JToken.ReadFrom(reader) as JObject;
            }
            catch (Exception ex) when (ex is JsonException || ex is DecoderFallbackException || ex is ArgumentException)
            { error = "it is not JSON: " + ex.Message; return null; }
            if (context == null) { error = "it is not a JSON object."; return null; }
            JToken version = context["schema_version"];
            if (version == null || version.Type != JTokenType.Integer || (long)version != 1)
            {
                error = "it is not a project-context v1 file: schema_version is " + (version == null ? "absent" : version.ToString(Formatting.None)) +
                        ", and horizun_project_context writes 1.";
                return null;
            }
            return context;
        }

        /// <summary>
        /// Fill what the arguments left out from a project-context v1 object. An explicit
        /// argument always wins. Returns "field &lt;- /pointer" for every default applied.
        /// </summary>
        public List<string> ApplyProjectContext(JObject context)
        {
            var applied = new List<string>();
            if (context == null) return applied;
            Facility.ProjectName = Default(Facility.ProjectName, "facility.project_name", context, "/project/name", applied);
            Facility.SiteName = Default(Facility.SiteName, "facility.site_name", context, "/project/location", applied);
            Facility.ProjectDescription = Default(Facility.ProjectDescription, "facility.project_description", context, "/project/description", applied);
            Facility.Phase = Default(Facility.Phase, "facility.phase", context, "/appointment/stage", applied);
            CategoryParameter = Default(CategoryParameter, "category_parameter", context, "/classification/type_parameter", applied);
            return applied;
        }

        private string Default(string current, string field, JObject context, string pointer, List<string> applied)
        {
            if (current != null) return current;
            JToken t = context;
            foreach (string part in pointer.Trim('/').Split('/'))
            {
                t = (t as JObject)?[part];
                if (t == null) return null;
            }
            string value = t.Type == JTokenType.String ? ((string)t).Trim() : null;
            if (string.IsNullOrEmpty(value)) return null;
            Provenance[field] = "project_context " + pointer;
            applied.Add(field + " <- " + pointer);
            return value;
        }
    }
}
