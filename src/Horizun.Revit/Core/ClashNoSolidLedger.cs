// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A PAIR THAT WAS NOT INTERSECTED HAS TO BE NAMEABLE.
//
// MEASURED (Comité de obra, 2026-10-01): horizun_clash EST vs MEP on linked copies
// answered result=partial with coverage.pairs.skipped_no_solids = 487 and
// coverage.unresolved_pairs = []. The headline said 487 pairs had no usable solid;
// the list a reader goes to for "which ones" was empty, because unresolved_pairs only
// ever held geometry FAILURES. 487 holes in a clash run, and no way to find one.
//
// This ledger keeps them: the distinct ELEMENTS that had no solid (the cause - a
// handful of families usually account for hundreds of pairs), how many pairs each
// one cost, what geometry it had instead (meshes, curves, nothing), counts by
// category, and a bounded sample of the pairs themselves. Bounded, because the
// number that matters is the count, and the count is never truncated.
//
// Revit-free: the Revit half describes the element and its geometry once; this
// accumulates and decides what the reply may say.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class ClashNoSolidLedger
    {
        public const int MaxElements = 50;
        public const int MaxPairExamples = 25;

        /// <summary>One element as the Revit half described it.</summary>
        public sealed class ElementFacts
        {
            /// <summary>PairLedger.ElementKey - source, link instance and element id.</summary>
            public string Key;
            public string ElementId, SourceModel, Category, Name;
            public bool HasSolid;
            /// <summary>What the element's geometry held instead of a solid ("2 mesh(es)", "no geometry").</summary>
            public string Geometry;
        }

        private sealed class Tally { public ElementFacts Facts; public int Pairs; }

        private readonly Dictionary<string, Tally> _elements = new Dictionary<string, Tally>(StringComparer.Ordinal);
        private readonly JArray _examples = new JArray();

        public int Pairs { get; private set; }

        /// <summary>Record one pair skipped because at least one side had no usable solid.</summary>
        public void Add(ElementFacts a, ElementFacts b)
        {
            Pairs++;
            string missing = !a.HasSolid && !b.HasSolid ? "both" : !a.HasSolid ? "a" : "b";
            if (!a.HasSolid) Count(a);
            if (!b.HasSolid) Count(b);
            if (_examples.Count < MaxPairExamples)
                _examples.Add(new JObject { ["a"] = Describe(a), ["b"] = Describe(b), ["without_solid"] = missing });
        }

        private void Count(ElementFacts e)
        {
            string key = e.Key ?? (e.SourceModel + "#" + e.ElementId);
            if (!_elements.TryGetValue(key, out Tally t)) _elements[key] = t = new Tally { Facts = e };
            t.Pairs++;
        }

        private static JObject Describe(ElementFacts e) => new JObject
        {
            ["element_id"] = e.ElementId, ["source_model"] = e.SourceModel,
            ["category"] = e.Category, ["name"] = e.Name
        };

        public JObject ToJson()
        {
            var ordered = _elements.Values.OrderByDescending(t => t.Pairs)
                .ThenBy(t => t.Facts.SourceModel, StringComparer.Ordinal)
                .ThenBy(t => t.Facts.ElementId, StringComparer.Ordinal).ToList();
            var elements = new JArray(ordered.Take(MaxElements).Select(t =>
            {
                JObject row = Describe(t.Facts);
                row["pairs"] = t.Pairs;
                row["geometry"] = t.Facts.Geometry;
                return (JToken)row;
            }));
            var byCategory = new JObject();
            foreach (var g in ordered.GroupBy(t => t.Facts.Category ?? "(no category)")
                                     .OrderByDescending(g => g.Sum(t => t.Pairs)).ThenBy(g => g.Key, StringComparer.Ordinal))
                byCategory[g.Key] = new JObject { ["elements"] = g.Count(), ["pairs"] = g.Sum(t => t.Pairs) };
            return new JObject
            {
                ["pairs"] = Pairs,
                ["elements_without_solid"] = _elements.Count,
                ["elements_shown"] = elements.Count,
                ["elements_truncated"] = _elements.Count > elements.Count,
                ["by_category"] = byCategory,
                ["elements"] = elements,
                ["pair_examples"] = _examples,
                ["pair_examples_truncated"] = Pairs > _examples.Count,
                ["means"] = Pairs == 0
                    ? "Every pair whose boxes overlapped had a solid on both sides."
                    : "These pairs' bounding boxes overlap, but at least one side had no usable solid, so nothing was " +
                      "intersected: they are NOT clean and NOT clashes. `elements` names the elements without a solid, " +
                      "most pairs first, with what their geometry held instead; fix or exclude those (or check them in " +
                      "Navisworks) and the pairs resolve. unresolved_pairs lists geometry FAILURES, a different hole."
            };
        }
    }
}
