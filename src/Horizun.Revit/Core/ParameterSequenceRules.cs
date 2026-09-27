// -----------------------------------------------------------------------------
// Horizun - original Horizun code.
//
// THE 'sequence' VALUE GENERATOR for horizun_write_params_verified, decided
// without a Revit: door/window/room marks numbered in a declared spatial order.
//
// The Revit half reads each target's level, location point and (for room order)
// the room it stands in at a named phase, and hands them here. This file only
// orders and formats, and every value it produces is shown in the rehearsal and
// bound by the token, so what is written is what was read and approved.
//
//   * NOTHING IS GUESSED. A target missing the datum its order needs (no level,
//     no location, not inside a room at the phase) refuses the whole generation
//     and is NAMED; it is never sorted to an arbitrary end.
//   * THE ORDER IS TOTAL. Keys apply left to right; coordinates are quantised to
//     a grid (1 mm by default, in the caller's internal units) so two doors a
//     rounding error apart do not swap between rehearsal and apply; the final
//     tie-break is the element id, so the same model always yields the same list.
//   * restart_per_level must make level the FIRST key: a counter that restarts
//     every time the level changes is only a per-level count if each level's
//     targets are contiguous. Refused otherwise rather than silently regrouped.
//   * Room numbers compare naturally ("2" before "10"), the order a person reads.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class SequenceTarget
    {
        public long Id { get; set; }
        public string Level { get; set; }
        public double? LevelElevation { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
        /// <summary>The number of the room the target stands in at the requested phase; null = none.</summary>
        public string Room { get; set; }
    }

    public sealed class SequenceOptions
    {
        public IList<string> OrderBy { get; set; } = new List<string>();
        public string Prefix { get; set; } = "";
        public long Start { get; set; } = 1;
        public long Step { get; set; } = 1;
        /// <summary>Zero-pad the number to this many digits; 0 = no padding.</summary>
        public int Pad { get; set; }
        public bool RestartPerLevel { get; set; }
        /// <summary>Coordinate quantum in the same units as X/Y (internal feet: 1 mm by default).</summary>
        public double Grid { get; set; } = 1.0 / 304.8;
    }

    public sealed class SequenceAssignment
    {
        public long Id { get; set; }
        public string Value { get; set; }
        /// <summary>0-based position in the global order.</summary>
        public int Position { get; set; }
        public string Level { get; set; }
    }

    public sealed class SequenceResult
    {
        public List<SequenceAssignment> Assignments { get; } = new List<SequenceAssignment>();
        /// <summary>True when restart_per_level produced the same value on two levels (Revit warns on duplicate marks).</summary>
        public bool RepeatsAcrossLevels { get; set; }
    }

    public static class ParameterSequenceRules
    {
        public const int MaxTargets = 5000;
        public static readonly string[] Keys = { "level", "x", "y", "room" };

        public static SequenceResult Generate(IList<SequenceTarget> targets, SequenceOptions o)
        {
            if (o == null) throw new ArgumentException("sequence options are required.");
            if (targets == null || targets.Count == 0) throw new ArgumentException("sequence has no targets to number.");
            if (targets.Count > MaxTargets) throw new ArgumentException("sequence numbers at most " + MaxTargets + " targets per call.");
            var order = (o.OrderBy ?? new List<string>()).Select(k => (k ?? "").Trim().ToLowerInvariant()).ToList();
            var problems = new List<string>();
            if (order.Count == 0) problems.Add("order_by is required: one or more of level, x, y, room");
            foreach (string k in order.Where(k => Array.IndexOf(Keys, k) < 0).Distinct())
                problems.Add("order_by '" + k + "' is not one of level, x, y, room");
            if (order.Distinct().Count() != order.Count) problems.Add("order_by names a key twice");
            if (o.RestartPerLevel && (order.Count == 0 || order[0] != "level"))
                problems.Add("restart_per_level needs level as the FIRST order_by key, so each level's targets are contiguous");
            if (o.Start < 0) problems.Add("start must be 0 or more");
            if (o.Step < 1) problems.Add("step must be 1 or more");
            if (o.Pad < 0 || o.Pad > 12) problems.Add("pad must be 0..12 digits");
            if ((o.Prefix ?? "").Length > 64) problems.Add("prefix is longer than 64 characters");
            if (!(o.Grid > 0)) problems.Add("the coordinate grid must be positive");
            var dupes = targets.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupes.Count > 0) problems.Add("targets named twice: " + Ids(dupes));

            bool byLevel = order.Contains("level") || o.RestartPerLevel;
            Missing(problems, targets, byLevel, t => t.Level == null || t.LevelElevation == null, "have no level");
            Missing(problems, targets, order.Contains("x") || order.Contains("y"), t => t.X == null || t.Y == null, "have no location point");
            Missing(problems, targets, order.Contains("room"), t => string.IsNullOrWhiteSpace(t.Room), "are not inside a room at the phase");
            if (problems.Count > 0)
                throw new ArgumentException("sequence refused before anything was generated: " + string.Join("; ", problems) + ".");

            IOrderedEnumerable<SequenceTarget> sorted = null;
            foreach (string k in order)
            {
                Func<SequenceTarget, IComparable> key;
                IComparer<IComparable> cmp = Comparer<IComparable>.Default;
                switch (k)
                {
                    case "level": key = t => new LevelKey(t.LevelElevation.Value, t.Level); break;
                    case "x": key = t => Quantise(t.X.Value, o.Grid); break;
                    case "y": key = t => Quantise(t.Y.Value, o.Grid); break;
                    default: key = t => new NaturalKey(t.Room); break;
                }
                sorted = sorted == null ? targets.OrderBy(key, cmp) : sorted.ThenBy(key, cmp);
            }
            List<SequenceTarget> list = sorted.ThenBy(t => t.Id).ToList();

            var result = new SequenceResult();
            long counter = o.Start;
            string lastLevel = null;
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);   // value -> level
            for (int i = 0; i < list.Count; i++)
            {
                SequenceTarget t = list[i];
                if (o.RestartPerLevel && i > 0 && !string.Equals(t.Level, lastLevel, StringComparison.Ordinal)) counter = o.Start;
                lastLevel = t.Level;
                string digits = counter.ToString(CultureInfo.InvariantCulture);
                if (o.Pad > 0) digits = digits.PadLeft(o.Pad, '0');
                string value = (o.Prefix ?? "") + digits;
                if (seen.TryGetValue(value, out string otherLevel) && !string.Equals(otherLevel, t.Level, StringComparison.Ordinal))
                    result.RepeatsAcrossLevels = true;
                seen[value] = t.Level;
                result.Assignments.Add(new SequenceAssignment { Id = t.Id, Value = value, Position = i, Level = t.Level });
                counter = checked(counter + o.Step);
            }
            return result;
        }

        private static void Missing(List<string> problems, IList<SequenceTarget> targets, bool needed,
                                    Func<SequenceTarget, bool> lacks, string what)
        {
            if (!needed) return;
            var ids = targets.Where(lacks).Select(t => t.Id).ToList();
            if (ids.Count > 0) problems.Add(ids.Count + " target(s) " + what + ": " + Ids(ids));
        }

        private static string Ids(List<long> ids) =>
            string.Join(", ", ids.Take(20).Select(i => i.ToString(CultureInfo.InvariantCulture))) + (ids.Count > 20 ? " and " + (ids.Count - 20) + " more" : "");

        private static IComparable Quantise(double v, double grid) => Math.Round(v / grid, MidpointRounding.AwayFromZero);

        private sealed class LevelKey : IComparable
        {
            private readonly double _elevation; private readonly string _name;
            public LevelKey(double elevation, string name) { _elevation = elevation; _name = name ?? ""; }
            public int CompareTo(object obj)
            {
                var other = (LevelKey)obj;
                int c = _elevation.CompareTo(other._elevation);
                return c != 0 ? c : string.CompareOrdinal(_name, other._name);
            }
        }

        /// <summary>"2" before "10", "A2" before "A10"; ties fall back to ordinal.</summary>
        internal sealed class NaturalKey : IComparable
        {
            private readonly string _s;
            public NaturalKey(string s) { _s = s ?? ""; }
            public int CompareTo(object obj)
            {
                string a = _s, b = ((NaturalKey)obj)._s;
                int i = 0, j = 0;
                while (i < a.Length && j < b.Length)
                {
                    if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                    {
                        int si = i, sj = j;
                        while (i < a.Length && char.IsDigit(a[i])) i++;
                        while (j < b.Length && char.IsDigit(b[j])) j++;
                        string da = a.Substring(si, i - si).TrimStart('0'), db = b.Substring(sj, j - sj).TrimStart('0');
                        if (da.Length != db.Length) return da.Length.CompareTo(db.Length);
                        int c = string.CompareOrdinal(da, db);
                        if (c != 0) return c;
                    }
                    else
                    {
                        int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                        if (c != 0) return c;
                        i++; j++;
                    }
                }
                int rest = (a.Length - i).CompareTo(b.Length - j);
                return rest != 0 ? rest : string.CompareOrdinal(a, b);
            }
        }
    }
}
