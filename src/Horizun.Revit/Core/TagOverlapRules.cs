// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// Pure 2D overlap geometry for the tag/text-note overlap check in
// horizun_verify_changes. SpatialCoherence.cs measures shared SOLID volume in
// model space, which sees nothing here: a tag or a text note is annotation,
// drawn flat in a view, not a solid. Two tags stacked on top of each other in
// a plan are numbers nobody can read - a real defect a solid-intersection
// check cannot see. This file only compares axis-aligned rectangles; the
// Revit-side gathering (which elements, which view, which bounding box) lives
// in TagOverlapCheck.cs.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Revit.Core
{
    public static class TagOverlapRules
    {
        /// <summary>An axis-aligned rectangle in one view's own coordinates. Callers normalise
        /// Min/Max themselves; this type does not assume which corner is which.</summary>
        public struct Rect
        {
            public double MinX, MinY, MaxX, MaxY;

            public static Rect Normalized(double x0, double y0, double x1, double y1) =>
                new Rect { MinX = Math.Min(x0, x1), MinY = Math.Min(y0, y1), MaxX = Math.Max(x0, x1), MaxY = Math.Max(y0, y1) };
        }

        /// <summary>Shared area of two rectangles, 0 when they only touch or do not meet at all -
        /// touching edges are not an overlap finding.</summary>
        public static double OverlapArea(Rect a, Rect b)
        {
            double w = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
            double h = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            if (w <= 0 || h <= 0) return 0;
            return w * h;
        }

        public static bool Overlaps(Rect a, Rect b) => OverlapArea(a, b) > 0;
    }
}
