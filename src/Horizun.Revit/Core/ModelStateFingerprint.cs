using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace Horizun.Revit.Core
{
    internal static class ModelStateFingerprint
    {
        private static long revision;
        [ThreadStatic] private static Document rehearsing;
        internal static void Changed(Document doc = null)
        {
            if (doc != null && rehearsing != null && doc.Equals(rehearsing)) return;
            revision++;
        }
        internal sealed class RehearsalScope : IDisposable
        {
            private readonly Document previous;
            internal bool RollbackConfirmed;
            internal RehearsalScope(Document doc) { previous = rehearsing; rehearsing = doc; }
            public void Dispose() { rehearsing = previous; if (!RollbackConfirmed) revision++; }
        }
        // Conservative approval boundary for operations whose dependencies are selected
        // by Revit. Versions catch edits; UniqueIds catch replacement at equal counts.
        internal static string Read(Document doc)
        {
            // VersionGuid can remain unchanged between commits before a save.
            // The revision also covers transactions, undo/redo and reloads, excluding
            // our own rehearsals only when their rollback is confirmed.
            if (!QueryCacheLifecycle.Ready) throw new InvalidOperationException("Document change tracking is unavailable; a stable approval cannot be issued.");
            string state = revision + "\n" + string.Join("\n", new FilteredElementCollector(doc)
                .WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(false), new ElementIsElementTypeFilter(true)))
                .Select(e => e.UniqueId + ":" + e.VersionGuid).OrderBy(x => x, StringComparer.Ordinal));
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(state))).Replace("-", "");
        }
    }
}
