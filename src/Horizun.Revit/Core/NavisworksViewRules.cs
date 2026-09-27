// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// Which 3D view Navisworks reads out of a .rvt. MEASURED 2026-09-26 (Navisworks
// Manage 2026 opening a Revit 2026 file): with "{3D}" at Fine and a 3D view named
// "HZ Navisworks b3b1d996" at Medium, both pipes arrived as 1-primitive lines and
// a Hard clash test reported nothing; renaming only that view brought them back as
// 1587/1787-triangle solids. A view whose name CONTAINS "Navisworks" is preferred
// over "{3D}" - so any such view is a candidate, and a tool that creates views must
// never give one that name by accident.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Revit.Core
{
    public static class NavisworksViewRules
    {
        public static bool IsCandidateName(string viewName) =>
            !string.IsNullOrEmpty(viewName) &&
            viewName.IndexOf("navisworks", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>An exact "Navisworks" first, then the rest in name order.</summary>
        public static int Rank(string viewName) =>
            string.Equals(viewName, "Navisworks", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

        /// <summary>
        /// Null when a view created by this bridge may carry the name; otherwise the
        /// refusal. A coordination view named "... Navisworks ..." would silently become
        /// the view Navisworks exports from.
        /// </summary>
        public static string RefuseCreatedViewName(string viewName) =>
            IsCandidateName(viewName)
                ? "a view name containing 'Navisworks' is refused: Navisworks reads such a view out of the .rvt " +
                  "instead of {3D} (measured 2026-09-26 - a Medium-detail view named that way turned every pipe " +
                  "into a line and the Hard test found nothing). Choose a name without 'Navisworks'."
                : null;
    }
}
