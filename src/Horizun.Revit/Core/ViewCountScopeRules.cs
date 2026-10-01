// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// TWO TOOLS, TWO DIFFERENT QUESTIONS ABOUT VIEWS - SAID OUT LOUD.
//
// MEASURED (dry run 2026-09, Autodesk sample model): horizun_audit_model reported
// 28 views not on sheets and 34 views without a template; horizun_model_scan, on
// the same document a minute later, 36 and 42. Neither number was wrong. They
// count different things: audit_model leaves out schedules and legends (off-sheet
// check) and every view that cannot be printed (template check), and model_scan
// counts every view a person can open, schedules included. The difference - 8 -
// was the model's eight schedules, and the only way to find that out was to guess.
//
// Neither number is changed. Each finding now carries what it counts (`scope`),
// how its count splits by view type (`by_view_type`), what it left out and how
// many of those the other definition would have counted (`excluded_by_view_type`),
// and the arithmetic that takes one tool's number to the other's. The two numbers
// then reconcile by addition instead of by guessing.
//
// Revit-free: the commands tally the views they already walk; this shapes it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Per-view-type tally of one view check: what it counted and what it left out.</summary>
    public sealed class ViewCountTally
    {
        private readonly SortedDictionary<string, int> counted = new SortedDictionary<string, int>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> excluded = new SortedDictionary<string, int>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> onlyHere = new SortedDictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// A view this check reported (it is in the finding's count). `otherCountsIt`
        /// false: the other tool's definition would NOT list it (a sheet, a browser view),
        /// so it is part of the difference too.
        /// </summary>
        public void Counted(string viewType, bool otherCountsIt = true)
        {
            Bump(counted, viewType);
            if (!otherCountsIt) Bump(onlyHere, viewType);
        }

        /// <summary>
        /// A view this check deliberately left out, that the OTHER tool's definition would
        /// have reported (off-sheet / without template by its rules).
        /// </summary>
        public void Excluded(string viewType) => Bump(excluded, viewType);

        public int CountedTotal => counted.Values.Sum();
        public int ExcludedTotal => excluded.Values.Sum();
        public int OnlyHereTotal => onlyHere.Values.Sum();
        public IReadOnlyDictionary<string, int> OnlyHereByType => onlyHere;
        public IReadOnlyDictionary<string, int> CountedByType => counted;
        public IReadOnlyDictionary<string, int> ExcludedByType => excluded;

        private static void Bump(SortedDictionary<string, int> d, string key)
        {
            key = string.IsNullOrWhiteSpace(key) ? "(unknown)" : key;
            int had;
            d[key] = d.TryGetValue(key, out had) ? had + 1 : 1;
        }
    }

    public static class ViewCountScopeRules
    {
        public const string AuditOffSheetScope =
            "views on no sheet, EXCLUDING view templates, sheets, schedules and legends (schedules and legends " +
            "may legitimately live off-sheet). Placement is read from Viewports.";

        public const string AuditNoTemplateScope =
            "printable views that apply no view template, EXCLUDING view templates and every view that cannot " +
            "be printed (View.CanBePrinted false - schedules among them).";

        public const string ScanNotOnSheetScope =
            "every non-template view a person can open (plans, sections, 3D, drafting, legends AND schedules), " +
            "excluding sheets and browser/internal views, that ViewSheet.GetAllPlacedViews() does not place on a sheet.";

        public const string ScanNoTemplateScope =
            "every non-template view a person can open (plans, sections, 3D, drafting, legends AND schedules), " +
            "excluding sheets and browser/internal views, whose ViewTemplateId is unset.";

        /// <summary>
        /// The block added to a finding: its scope, the split by view type, what it left
        /// out, and the sentence that reconciles it with `otherTool`'s count.
        /// </summary>
        public static JObject Describe(string scope, ViewCountTally tally, string otherTool)
        {
            if (tally == null) throw new ArgumentNullException(nameof(tally));
            var o = new JObject
            {
                ["scope"] = scope,
                ["by_view_type"] = ToJson(tally.CountedByType)
            };
            if (otherTool == null) return o;
            o["excluded_by_view_type"] = ToJson(tally.ExcludedByType);
            o["counted_only_here_by_view_type"] = ToJson(tally.OnlyHereByType);
            o["reconcile"] = Reconcile(tally, otherTool);
            return o;
        }

        /// <summary>
        /// "28 counted here + 8 Schedule excluded = 36, the number horizun_model_scan
        /// reports". Stated as the expected arithmetic, not as a measurement of the
        /// other tool: the two definitions also differ in how they read placement.
        /// </summary>
        public static string Reconcile(ViewCountTally tally, string otherTool)
        {
            int there = tally.CountedTotal + tally.ExcludedTotal - tally.OnlyHereTotal;
            if (tally.ExcludedTotal == 0 && tally.OnlyHereTotal == 0)
                return "the two definitions select the same views here, so " + otherTool + " should also report " +
                       tally.CountedTotal + ".";
            string s = tally.CountedTotal + " counted here";
            if (tally.ExcludedTotal > 0)
                s += " + " + string.Join(" + ", tally.ExcludedByType.Select(kv => kv.Value + " " + kv.Key)) +
                     " left out here but counted there";
            if (tally.OnlyHereTotal > 0)
                s += " - " + string.Join(" - ", tally.OnlyHereByType.Select(kv => kv.Value + " " + kv.Key)) +
                     " counted here but not there";
            return s + " = " + there + ", the count " + otherTool + " should report under its definition.";
        }

        /// <summary>
        /// Whether horizun_model_scan's documentation lists include a view of this type
        /// (ModelScanCommand skips browser, internal, undefined and sheet views).
        /// </summary>
        public static bool ScanListsViewType(string viewType)
        {
            switch (viewType)
            {
                case "ProjectBrowser":
                case "SystemBrowser":
                case "Internal":
                case "Undefined":
                case "DrawingSheet":
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Whether horizun_audit_model's off-sheet check considers a view of this type at
        /// all (AuditModelCommand.ViewsOffSheets leaves out legends, schedules, sheets and
        /// internal views).
        /// </summary>
        public static bool AuditOffSheetConsidersViewType(string viewType)
        {
            switch (viewType)
            {
                case "Legend":
                case "Schedule":
                case "DrawingSheet":
                case "Internal":
                    return false;
                default:
                    return true;
            }
        }

        private static JObject ToJson(IReadOnlyDictionary<string, int> d)
        {
            var o = new JObject();
            foreach (KeyValuePair<string, int> kv in d) o[kv.Key] = kv.Value;
            return o;
        }
    }
}
