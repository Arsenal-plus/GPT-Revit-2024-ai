// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// Where the AUTOMATIC after-write equipment clearance check (SpatialAfterWrite.cs)
// finds its clearance_rules, when the caller did not pass any to horizun_verify_changes
// directly (that explicit argument always wins - it is parsed and used as-is in
// VerifyChangesCommand.cs, never through this file).
//
// Precedence, most specific first:
//   1. project-context.json's "clearance_rules" array (an empty array is the project's
//      explicit opt-out and also wins), found by walking up from the
//      active document's own folder (a project's own CDE structure) - up to 4 levels,
//      since a WIP/Shared/Published/Archived layout can put the model a few folders
//      below the project root.
//   2. %USERPROFILE%\.horizun\clearance-rules.json - a plain JSON array, the same shape
//      as clearance_rules - a machine-wide default when no project file declares one.
//   3. Neither present: no automatic equipment clearance check (doors still run; see
//      SpatialCoherence.DoorClearance, which needs no caller-supplied rule).
//
// Org-neutral: this file reads whatever rules are on disk, but ships none itself.
// Malformed entries are skipped (ClearanceZoneRules.Parse) rather than thrown - an
// automatic pass must never turn a caller's unrelated write into a hard failure.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class ClearanceRulesSource
    {
        public const string GlobalFileName = "clearance-rules.json";

        /// <summary>Never throws: worst case is no automatic clearance check this call.</summary>
        public static List<ClearanceZoneRules.Rule> Load(Document doc, out string origin)
        {
            origin = null;
            try
            {
                // A project that DECLARES the block decides, even with an empty array: that is
                // the project's explicit "no equipment clearance rules here", never a reason
                // to fall through to the machine-wide default.
                List<ClearanceZoneRules.Rule> fromProject = FromProjectContext(doc, out string projectPath);
                if (fromProject != null) { origin = "project-context.json (" + projectPath + ")"; return fromProject; }
                List<ClearanceZoneRules.Rule> fromGlobal = FromGlobalFile(out string globalPath);
                if (fromGlobal != null && fromGlobal.Count > 0) { origin = "global default (" + globalPath + ")"; return fromGlobal; }
            }
            catch { }
            return null;
        }

        private static List<ClearanceZoneRules.Rule> FromProjectContext(Document doc, out string foundPath)
        {
            foundPath = null;
            string docPath = null;
            try { docPath = doc?.PathName; } catch { }
            if (string.IsNullOrWhiteSpace(docPath)) return null;
            DirectoryInfo dir = null;
            try { dir = new FileInfo(docPath).Directory; } catch { }
            for (int up = 0; dir != null && up <= 4; up++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "project-context.json");
                if (!File.Exists(candidate)) continue;
                try
                {
                    JObject j = JObject.Parse(File.ReadAllText(candidate));
                    if (j["clearance_rules"] is JArray arr)
                    {
                        var ignored = new List<string>();
                        List<ClearanceZoneRules.Rule> rules = ClearanceZoneRules.Parse(arr, ignored);
                        foundPath = candidate;
                        return rules;
                    }
                    // The nearest project-context.json is THIS model's project: stop walking up
                    // (a parent folder's file belongs to another project) and let the caller
                    // fall back to the machine-wide default, precedence step 2.
                    return null;
                }
                catch (JsonException) { return null; }
            }
            return null;
        }

        private static List<ClearanceZoneRules.Rule> FromGlobalFile(out string foundPath)
        {
            foundPath = null;
            string home = null;
            try { home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { }
            if (string.IsNullOrWhiteSpace(home)) return null;
            string path = Path.Combine(home, ".horizun", GlobalFileName);
            if (!File.Exists(path)) return null;
            try
            {
                JToken t = JToken.Parse(File.ReadAllText(path));
                JArray arr = t as JArray ?? (t as JObject)?["clearance_rules"] as JArray;
                if (arr == null) return null;
                var ignored = new List<string>();
                foundPath = path;
                return ClearanceZoneRules.Parse(arr, ignored);
            }
            catch (JsonException) { return null; }
        }
    }
}
