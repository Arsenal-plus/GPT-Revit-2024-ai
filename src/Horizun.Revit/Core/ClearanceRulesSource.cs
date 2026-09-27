// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// Where the AUTOMATIC after-write equipment clearance check (SpatialAfterWrite.cs)
// finds its clearance_rules, when the caller did not pass any to horizun_verify_changes
// directly (that explicit argument always wins - it is parsed and used as-is in
// VerifyChangesCommand.cs, never through this file).
//
// Precedence, most specific first:
//   1. A clearance-rules.json file of the PROJECT, found by walking up from the active
//      document's own folder - up to 4 levels, since a WIP/Shared/Published/Archived
//      layout can put the model a few folders below the project root. The walk stops at
//      the first folder that holds a project-context.json (the project root): a parent
//      folder's file belongs to another project. Its own file, not a block inside
//      project-context.json, because that file's schema (project-context.v1) is closed
//      (additionalProperties: false) and horizun_project_context validate would refuse it.
//   2. %USERPROFILE%\.horizun\clearance-rules.json - the machine-wide default when the
//      project has no file of its own.
//   3. Neither present: no automatic equipment clearance check (doors still run; see
//      SpatialCoherence.DoorClearance, which needs no caller-supplied rule).
//
// Both files are a plain JSON array of rules, or an object with a "clearance_rules" array.
// A LITERAL empty array is the explicit "no equipment clearance rules here" and wins over
// the machine-wide default. A file whose entries are malformed is NOT an opt-out: every
// error comes back (spatial_check.clearance_rules_errors) and the check reads partial -
// an automatic pass must never turn a caller's unrelated write into a hard failure, and
// must never turn a rule nobody could read into a clean answer either.
//
// Org-neutral: this file reads whatever rules are on disk, but ships none itself.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class ClearanceRulesSource
    {
        public const string FileName = "clearance-rules.json";

        /// <summary>The exact BuiltInCategory name (as SpatialCoherence.CategoryKey reports it)
        /// for a token, or null when this Revit has no such category. Case-sensitive on
        /// purpose: CategoryKey compares ordinally, so a wrongly cased token would never match.</summary>
        public static string CanonicalCategory(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            try
            {
                if (!Enum.TryParse(token, false, out BuiltInCategory bic) || !Enum.IsDefined(typeof(BuiltInCategory), bic)) return null;
                return bic.ToString();
            }
            catch { return null; }
        }

        /// <summary>Never throws: worst case is no automatic clearance check this call, and
        /// then <paramref name="errors"/> says why. <paramref name="origin"/> names the source
        /// without an absolute path - it is echoed into every write reply, and a path carries
        /// the Windows account name or a client's project folder.</summary>
        public static List<ClearanceZoneRules.Rule> Load(Document doc, out string origin, out List<string> errors)
        {
            origin = null;
            errors = new List<string>();
            try
            {
                List<ClearanceZoneRules.Rule> fromProject = FromProject(doc, errors, out string projectWhere);
                if (projectWhere != null) { origin = "project " + FileName + " (" + projectWhere + ")"; return fromProject; }
                string home = null;
                try { home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { }
                if (string.IsNullOrWhiteSpace(home)) return null;
                string path = Path.Combine(home, ".horizun", FileName);
                if (!File.Exists(path)) return null;
                origin = "global default (%USERPROFILE%\\.horizun\\" + FileName + ")";
                return ReadFile(path, origin, errors);
            }
            catch (Exception ex) { errors.Add("clearance rules could not be loaded: " + ex.Message); }
            return null;
        }

        /// <summary>The project's own rules file, or null. <paramref name="where"/> is set
        /// (relative to the model's folder) whenever a file was found, even an unreadable one:
        /// a project that has a file decides, the machine default does not step in.</summary>
        private static List<ClearanceZoneRules.Rule> FromProject(Document doc, List<string> errors, out string where)
        {
            where = null;
            string docPath = null;
            try { docPath = doc?.PathName; } catch { }
            if (string.IsNullOrWhiteSpace(docPath)) return null;
            DirectoryInfo dir = null;
            try { dir = new FileInfo(docPath).Directory; } catch { }
            for (int up = 0; dir != null && up <= 4; up++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, FileName);
                if (File.Exists(candidate))
                {
                    where = up == 0 ? "beside the model" : up + " folder(s) above the model";
                    return ReadFile(candidate, "project " + FileName, errors);
                }
                if (File.Exists(Path.Combine(dir.FullName, "project-context.json"))) return null;
            }
            return null;
        }

        /// <summary>Rules from one file; an unreadable file or a malformed entry is an error in
        /// <paramref name="errors"/>, never an exception. An empty list with no error is the
        /// file's literal empty array (the explicit opt-out).</summary>
        private static List<ClearanceZoneRules.Rule> ReadFile(string path, string label, List<string> errors)
        {
            JToken t;
            try { t = JToken.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { errors.Add(label + " could not be read: " + ex.Message); return new List<ClearanceZoneRules.Rule>(); }
            JArray arr = t as JArray ?? (t as JObject)?["clearance_rules"] as JArray;
            if (arr == null)
            {
                errors.Add(label + " must be a JSON array of rules, or an object with a clearance_rules array.");
                return new List<ClearanceZoneRules.Rule>();
            }
            var own = new List<string>();
            List<ClearanceZoneRules.Rule> rules = ClearanceZoneRules.Parse(arr, own, CanonicalCategory);
            foreach (string e in own) errors.Add(label + ": " + e);
            return rules;
        }
    }
}
