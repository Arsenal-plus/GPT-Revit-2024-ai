// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_coordination operation=navisworks_status (read-only): the STATUS
// SIDE of the naviscoord-mcp <-> Revit MCP handoff, the mirror of
// import_navisworks. That command brings issues IN (re-detected, folded into
// the ledger); this reports what THIS ledger now believes about every finding
// that came from Navisworks, in the shape a coordinator (or a future
// automation) feeds straight to naviscoord-mcp's navis_set_status.
//
// revit_status IS THIS LEDGER'S OWN STATE, never Navisworks' - resolved_by_model
// is set ONLY by a complete detection run over the finding's own scope
// (CoordinationRules.Merge), exactly as everywhere else in this file; this
// command asserts nothing new, it only reads what is already recorded and
// restates it as a suggestion. It writes NOTHING to Navisworks itself - the
// coordinator (or the tool orchestrating both MCPs) makes that call.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CoordinationCommand
    {
        private static CommandResult NavisworksStatus(Document doc, JObject request, string ledgerPath)
        {
            Dictionary<string, CoordinationFinding> findings = CoordinationLedger.Load(ledgerPath, out _);
            List<CoordinationFinding> navis = findings.Values
                .Where(f => f.ExternalSource == "navisworks" && !string.IsNullOrWhiteSpace(f.ExternalIssueId))
                .OrderBy(f => f.ExternalIssueId, StringComparer.Ordinal)
                .ToList();

            var rows = new JArray();
            var suggestion = new JArray();
            foreach (CoordinationFinding f in navis)
            {
                bool resolvedByModel = f.Status == CoordinationRules.StatusResolvedByModel;
                rows.Add(new JObject
                {
                    ["external_issue_id"] = f.ExternalIssueId,
                    ["finding_id"] = f.Id,
                    ["revit_status"] = f.Status,
                    ["resolved_by_model_at"] = resolvedByModel ? f.ResolvedUtc : null,
                    ["evidence"] = new JObject
                    {
                        ["category_a"] = f.CategoryA,
                        ["category_b"] = f.CategoryB,
                        ["point_mm"] = f.PointMm == null ? null : new JArray(f.PointMm),
                        ["times_seen"] = f.TimesSeen,
                        ["last_seen_utc"] = f.LastSeenUtc,
                        ["regression"] = f.Regression
                    }
                });
                suggestion.Add(new JObject
                {
                    ["issue_id"] = f.ExternalIssueId,
                    ["target_status"] = resolvedByModel ? "resolved" : "active"
                });
            }

            var summary = new JObject
            {
                ["document"] = doc.Title,
                ["ledger_path"] = ledgerPath,
                ["navisworks_findings"] = navis.Count,
                ["rows"] = rows,
                ["navis_set_status"] = suggestion,
                ["means"] =
                    "revit_status is THIS ledger's own state, never re-derived here; resolved_by_model_at is set " +
                    "only by a complete detection run over the finding's own scope. navis_set_status is a " +
                    "SUGGESTION - 'resolved' when this model measured the pair gone, 'active' otherwise (including " +
                    "a human closed_by_decision or accepted_risk, which is a decision about the finding, not a " +
                    "measurement that the clash is gone) - meant to be fed to naviscoord-mcp's navis_set_status. " +
                    "This command writes NOTHING to Navisworks itself."
            };

            string path = request.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) return CommandResult.Ok(summary);

            if (!Path.IsPathRooted(path))
                return CommandResult.Fail("path must be absolute. Nothing was written.");
            if (File.Exists(path) && request.Value<bool?>("overwrite") != true)
                return CommandResult.Fail("'" + path + "' already exists. Pass overwrite=true to replace it. Nothing was written.");

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string content = summary.ToString();
            File.WriteAllText(path, content, new UTF8Encoding(false));

            // RE-READ. A small JSON file is exactly the case the contract does not bend for.
            string rereadText;
            try { rereadText = File.ReadAllText(path); }
            catch (Exception ex) { return CommandResult.Fail("Wrote '" + path + "' but could not re-read it: " + ex.Message); }
            JObject rereadJson;
            try { rereadJson = JObject.Parse(rereadText); }
            catch (Exception ex) { return CommandResult.Fail("Wrote '" + path + "' but it does not re-parse as JSON: " + ex.Message + ". Success is not claimed."); }
            int rereadRows = (rereadJson["rows"] as JArray)?.Count ?? -1;
            if (rereadRows != rows.Count)
                return CommandResult.Fail("The file was written and re-reading it shows " + rereadRows + " row(s), not " +
                    rows.Count + ". Success is not claimed; inspect " + path + ".");

            summary["path"] = path;
            summary["written"] = true;
            summary["verified_by_reread"] = true;
            return CommandResult.Ok(summary);
        }
    }
}
