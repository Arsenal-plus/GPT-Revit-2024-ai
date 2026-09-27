// -----------------------------------------------------------------------------
// Horizun - original Horizun code.
//
// SHEET RENUMBERING, planned without a Revit (horizun_manage_views
// operation=renumber_sheets).
//
// Revit keeps sheet numbers unique and enforces it at ASSIGNMENT time: setting
// ViewSheet.SheetNumber to a number another sheet still holds throws, even when
// that other sheet is about to move away in the same transaction. So a map
// such as {A101 -> A102, A102 -> A101} cannot be written in any order as-is;
// the order and the temporary numbers are the whole problem, and they are
// arithmetic. This file decides them:
//
//   * every problem with the map is refused BEFORE anything is written, all
//     at once: an unknown old number, an old number named twice, two sheets
//     sent to the same number, and a new number held by a sheet the map does
//     NOT move (checked against every sheet, not only the mapped ones);
//   * steps are emitted in an order Revit accepts: a sheet moves straight to
//     its target as soon as the target is free; only a closed cycle parks ONE
//     of its sheets on a temporary number, so a swap costs one extra step;
//   * a temporary number never equals a number any sheet holds or any target.
//
// Sheet numbers compare case-insensitively here, the same way the create
// operations of horizun_manage_views check collisions (ManageViewsCommand
// RequireUnusedSheetNumber): refusing a case-only clash costs a retry, while
// accepting one Revit then rejects costs the whole batch.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class SheetRenumberStep
    {
        /// <summary>The ORIGINAL number of the sheet this step moves - its identity in the plan.</summary>
        public string Sheet { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        /// <summary>True when To is a parking number that a later step replaces.</summary>
        public bool Temporary { get; set; }
    }

    public sealed class SheetRenumberPlan
    {
        /// <summary>The steps in the order they must be written.</summary>
        public List<SheetRenumberStep> Steps { get; } = new List<SheetRenumberStep>();
        /// <summary>old -> new for every sheet that ends with a different number, in map order.</summary>
        public List<KeyValuePair<string, string>> Final { get; } = new List<KeyValuePair<string, string>>();
        /// <summary>Entries whose new number is exactly the old one: nothing to write.</summary>
        public List<string> Unchanged { get; } = new List<string>();
        public int TemporarySteps => Steps.Count(s => s.Temporary);
    }

    public static class SheetRenumberRules
    {
        public const int MaxEntries = 1000;
        public static readonly StringComparer Numbers = StringComparer.OrdinalIgnoreCase;

        /// <summary>
        /// Plans a renumbering. `map` is old -> new in the caller's order; `existing` is
        /// the number of EVERY sheet in the document (placeholders included - they hold
        /// numbers too); `reserved` are numbers other actions of the same batch are about
        /// to take. Throws ArgumentException naming every problem, before any write.
        /// </summary>
        public static SheetRenumberPlan Plan(IList<KeyValuePair<string, string>> map, IEnumerable<string> existing,
                                             IEnumerable<string> reserved = null)
        {
            if (map == null || map.Count == 0)
                throw new ArgumentException("renumber is required: an object of old sheet number -> new sheet number.");
            if (map.Count > MaxEntries)
                throw new ArgumentException("renumber takes at most " + MaxEntries + " sheets per action; split the map.");

            var held = new HashSet<string>((existing ?? Enumerable.Empty<string>()).Where(n => n != null), Numbers);
            var batch = new HashSet<string>((reserved ?? Enumerable.Empty<string>()).Where(n => n != null), Numbers);
            var problems = new List<string>();
            var olds = new HashSet<string>(Numbers);
            var news = new Dictionary<string, string>(Numbers);   // new -> old that claims it

            foreach (var kv in map)
            {
                string from = kv.Key, to = kv.Value;
                if (string.IsNullOrWhiteSpace(from)) { problems.Add("an old number is blank"); continue; }
                if (string.IsNullOrWhiteSpace(to)) { problems.Add("'" + from + "' -> a blank number: a sheet without a number cannot enter a register"); continue; }
                if (!olds.Add(from)) problems.Add("'" + from + "' is named twice as an old number");
                if (!held.Contains(from)) problems.Add("no sheet is numbered '" + from + "'");
                if (news.TryGetValue(to, out string other) && !Numbers.Equals(other, from))
                    problems.Add("'" + other + "' and '" + from + "' would both become '" + to + "'");
                else news[to] = from;
                if (batch.Contains(to)) problems.Add("'" + to + "' is taken by another action of this batch");
            }
            // A target held by a sheet the map leaves where it is. Moved sheets free their
            // numbers; everything else keeps them, so this is the collision against every
            // sheet in the document, not only the mapped ones.
            var moving = new HashSet<string>(map.Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value) &&
                                                             !string.Equals(kv.Key, kv.Value, StringComparison.Ordinal))
                                                .Select(kv => kv.Key), Numbers);
            foreach (var kv in map)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                if (Numbers.Equals(kv.Key, kv.Value)) continue;   // its own number (or a case-only change)
                if (held.Contains(kv.Value) && !moving.Contains(kv.Value))
                    problems.Add("'" + kv.Value + "' (target of '" + kv.Key + "') is held by a sheet the map does not move");
            }
            if (problems.Count > 0)
                throw new ArgumentException("renumber_sheets refused before anything was written: " +
                                            string.Join("; ", problems.Distinct()) + ".");

            var plan = new SheetRenumberPlan();
            var current = new Dictionary<string, string>(Numbers);      // sheet (original number) -> current number
            var occupant = new Dictionary<string, string>(Numbers);     // number -> sheet (original number)
            foreach (string n in held) { current[n] = n; occupant[n] = n; }
            var pending = new List<KeyValuePair<string, string>>();
            foreach (var kv in map)
            {
                if (string.Equals(kv.Key, kv.Value, StringComparison.Ordinal)) { plan.Unchanged.Add(kv.Key); continue; }
                pending.Add(kv);
                plan.Final.Add(kv);
            }
            var forbidden = new HashSet<string>(held, Numbers);
            forbidden.UnionWith(map.Select(kv => kv.Value));
            forbidden.UnionWith(batch);
            int serial = 0;

            while (pending.Count > 0)
            {
                bool progressed = false;
                for (int i = 0; i < pending.Count; i++)
                {
                    var p = pending[i];
                    if (occupant.TryGetValue(p.Value, out string holder) && !Numbers.Equals(holder, p.Key)) continue;
                    Move(plan, current, occupant, p.Key, p.Value, false);
                    pending.RemoveAt(i); i--;
                    progressed = true;
                }
                if (progressed) continue;
                // Every remaining sheet waits on another remaining sheet: a closed cycle.
                // Park the first one; its number frees the sheet that wanted it.
                string temp;
                do { temp = "HZTMP-" + (++serial).ToString(System.Globalization.CultureInfo.InvariantCulture); }
                while (forbidden.Contains(temp) || occupant.ContainsKey(temp));
                Move(plan, current, occupant, pending[0].Key, temp, true);
            }
            return plan;
        }

        private static void Move(SheetRenumberPlan plan, Dictionary<string, string> current, Dictionary<string, string> occupant,
                                 string sheet, string to, bool temporary)
        {
            string from = current[sheet];
            occupant.Remove(from);
            occupant[to] = sheet;
            current[sheet] = to;
            plan.Steps.Add(new SheetRenumberStep { Sheet = sheet, From = from, To = to, Temporary = temporary });
        }
    }
}
