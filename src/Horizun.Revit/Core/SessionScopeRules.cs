// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// horizun_verify_changes scope=session: several writes in a row, checked
// together, instead of only the last one. ChangeLedger (Core/ChangeWatch.cs)
// now keeps a bounded HISTORY of writes per document, not just the last; this
// file is the pure half that turns that history into the id set to check -
// Commands/VerifyChangesCommand.cs does the Revit-side resolving.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class SessionScopeRules
    {
        /// <summary>Above this many distinct ids, the union is capped rather than checked whole -
        /// the same "checked means all of it" honesty as DataOnlyGeometryRules, reported, never silent.</summary>
        public const int MaxIds = 2000;

        public sealed class WriteEntry
        {
            public DateTime AtUtc;
            public string Tool;
            public long[] Added;
            public long[] Modified;
        }

        public sealed class Outcome
        {
            /// <summary>Distinct ids to check, oldest write first, capped at MaxIds.</summary>
            public readonly List<long> Ids = new List<long>();
            public readonly List<string> Tools = new List<string>();
            public int WritesConsidered;
            public int TotalIdsFound;
            public bool Truncated;
        }

        /// <summary>
        /// Union of added+modified ids across every entry at or after <paramref name="sinceUtc"/>
        /// (the whole history when null), in the order first seen, capped at MaxIds distinct ids.
        /// Truncation is reported on the outcome, never silently dropped.
        /// </summary>
        public static Outcome Union(IEnumerable<WriteEntry> history, DateTime? sinceUtc)
        {
            var o = new Outcome();
            var seen = new HashSet<long>();
            var tools = new HashSet<string>(StringComparer.Ordinal);
            foreach (WriteEntry e in history ?? Enumerable.Empty<WriteEntry>())
            {
                if (e == null) continue;
                if (sinceUtc.HasValue && e.AtUtc < sinceUtc.Value) continue;
                o.WritesConsidered++;
                if (!string.IsNullOrEmpty(e.Tool)) tools.Add(e.Tool);
                foreach (long id in (e.Added ?? Array.Empty<long>()).Concat(e.Modified ?? Array.Empty<long>()))
                {
                    if (!seen.Add(id)) continue;
                    o.TotalIdsFound++;
                    if (o.Ids.Count < MaxIds) o.Ids.Add(id);
                }
            }
            o.Truncated = o.TotalIdsFound > o.Ids.Count;
            o.Tools.AddRange(tools);
            return o;
        }
    }
}
