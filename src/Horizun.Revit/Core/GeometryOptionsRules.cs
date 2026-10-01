// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// ONE GEOMETRY READ, ONE SCOPE: A VIEW OR A DETAIL LEVEL.
//
// Autodesk.Revit.DB.Options carries both a View and a DetailLevel, and Revit
// lets a caller set only one of them: setting DetailLevel on Options that have
// a View - or a View on Options that have a DetailLevel - throws
// InvalidOperationException ("DetailLevel is already set"). A view-scoped read
// uses the view's own detail level. Measured in the 2026-09-30 dry run:
// horizun_cad_extract with view_id failed on every call, because the CAD harvest
// set Fine first and the view after.
//
// The rule is one line and lives here anyway, so every geometry read that can
// take a view asks the same question and a test pins the answer.
//
// Revit-free.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    public static class GeometryOptionsRules
    {
        /// <summary>
        /// Whether a geometry read may set Options.DetailLevel. Only when it is
        /// NOT scoped to a view: with a view, the view decides the detail level and
        /// Revit refuses a second answer.
        /// </summary>
        public static bool DetailLevelApplies(bool scopedToView) => !scopedToView;
    }
}
