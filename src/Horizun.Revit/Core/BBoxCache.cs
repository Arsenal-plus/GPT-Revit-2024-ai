// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// The last bounding box SpatialAfterWrite saw for an element, kept across calls,
// so a data-only write (DataOnlyGeometryRules.cs) can tell "this element moved"
// from "this element was only renamed" without the dispatcher having to guess
// which ids a future call will touch. Every call that leaves the model changed
// refreshes the entries it looked at, whether or not it is a data-only tool -
// so the cache is warm for whichever call needs it next.
//
// Keyed by document + element id (ChangeWatch.Key), because two documents can
// share numerically identical ElementIds. Bounded (Cap) with FIFO eviction: a
// long Revit session that has touched tens of thousands of elements must not
// grow this dictionary without limit, and a stale evicted entry only means the
// NEXT data-only write on that element falls back to the "unknown" tier - never
// a wrong answer, just a less precise one.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace Horizun.Revit.Core
{
    internal static class BBoxCache
    {
        private const int Cap = 20000;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, double[]> Store = new Dictionary<string, double[]>(StringComparer.Ordinal);
        private static readonly Queue<string> Order = new Queue<string>();

        private static string Key(Document doc, long id) => ChangeWatch.Key(doc) + "#" + id;

        /// <summary>The bounding box recorded for this id the last time any call saw it, or
        /// null when this id has never been recorded (or was evicted).</summary>
        public static double[] Get(Document doc, long id)
        {
            lock (Gate) return Store.TryGetValue(Key(doc, id), out double[] v) ? v : null;
        }

        /// <summary>Read an element's current bounding box as a flat array, or null when it has
        /// none (view-specific, no geometry, or the read itself failed).</summary>
        public static double[] Snapshot(Element e)
        {
            try
            {
                BoundingBoxXYZ b = e?.get_BoundingBox(null);
                if (b == null) return null;
                return new[] { b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z };
            }
            catch { return null; }
        }

        /// <summary>Remember this box for next time. A null box is a no-op, never an eviction:
        /// a call that could not read the box this time leaves the previous entry intact.</summary>
        public static void Put(Document doc, long id, double[] bbox)
        {
            if (bbox == null || bbox.Length != 6) return;
            string key = Key(doc, id);
            lock (Gate)
            {
                if (!Store.ContainsKey(key))
                {
                    Order.Enqueue(key);
                    while (Order.Count > Cap && Order.Count > 0)
                    {
                        string old = Order.Dequeue();
                        Store.Remove(old);
                    }
                }
                Store[key] = bbox;
            }
        }
    }
}
