// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_views set_crop: WHEN A CROP MAY BE WRITTEN, AND WHEN IT IS DONE.
//
// Reported from the "Revit con agentes" dry run (2026-09-30): set_crop answered
// verified=true and the view on the sheet still looked uncropped. The check that
// passed read the CropBox back - and the CropBox is a number Revit stores whether
// or not it governs anything. Three facts decide whether a crop is REAL, and the
// verdict now needs every one it can read:
//
//   * the crop is ACTIVE - View.CropBoxActive AND the "Crop View" parameter
//     (VIEWER_CROP_REGION), which a view template can hold; the two are read
//     independently and must agree;
//   * the CropBox reads back as the requested rectangle;
//   * the crop SHAPE Revit draws (ViewCropRegionShapeManager.GetCropShape),
//     projected on the view plane, spans the requested rectangle. This is the
//     region on paper, not the stored box: a sketched crop keeps its own shape
//     while the box setter is ignored (RevitAPI: "Setting the crop box for a
//     view with a non-rectangular crop region will have no effect").
//
// And three situations are REFUSED BY NAME before a write, because in each the
// crop is owned by something else and the write would be overridden or ignored:
// a view template that controls the crop, a scope box that drives it, and a
// non-rectangular (sketched) crop region.
//
// What a viewport looks like on its sheet is a separate fact: annotation outside
// the crop (grids, levels, tags) widens the viewport when the annotation crop is
// off. That is reported as a named finding, never folded into the verdict - the
// crop can be exactly right while the sheet still needs the annotation crop.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Horizun.Revit.Core
{
    /// <summary>What was read about a view before its crop is written. Plain facts.</summary>
    public sealed class CropPreflightFacts
    {
        public long ViewId { get; set; }
        public string ViewName { get; set; }
        public bool IsTemplate { get; set; }
        /// <summary>Name of the assigned view template, null when none.</summary>
        public string TemplateName { get; set; }
        public long? TemplateId { get; set; }
        /// <summary>The template includes (controls) the crop parameter.</summary>
        public bool TemplateControlsCrop { get; set; }
        /// <summary>Name of the scope box driving the crop, null when none.</summary>
        public string ScopeBoxName { get; set; }
        public long? ScopeBoxId { get; set; }
        /// <summary>The crop region is a sketched (non-rectangular) shape; null when it could not be read.</summary>
        public bool? NonRectangularShape { get; set; }
    }

    /// <summary>What was read back after the crop was written. Null = could not be read.</summary>
    public sealed class CropReadback
    {
        public bool? CropBoxActive { get; set; }
        /// <summary>VIEWER_CROP_REGION as an integer (1 = crop on).</summary>
        public int? CropParameter { get; set; }
        public bool? BoxMatches { get; set; }
        public bool? ShapeMatches { get; set; }
    }

    public sealed class CropVerdict
    {
        public bool Verified { get; set; }
        /// <summary>Why it is not verified, by name; empty when verified.</summary>
        public List<string> Failures { get; } = new List<string>();
        /// <summary>Facts that could not be measured but did not decide the verdict.</summary>
        public List<string> Notes { get; } = new List<string>();
    }

    public static class ViewCropRules
    {
        public const string FindingViewportLargerThanCrop = "viewport_larger_than_crop";

        /// <summary>Null when the crop may be written; otherwise the refusal, naming what owns the crop.</summary>
        public static string Refusal(CropPreflightFacts f)
        {
            if (f == null) throw new ArgumentNullException(nameof(f));
            string view = "view " + f.ViewId + (string.IsNullOrEmpty(f.ViewName) ? "" : " '" + f.ViewName + "'");
            if (f.IsTemplate)
                return view + " is a view template; a template has no crop of its own to set.";
            if (f.TemplateControlsCrop)
                return view + "'s crop is controlled by its view template '" + f.TemplateName + "' (id " + f.TemplateId +
                       "): a crop written here would be overridden by the template. Either clear 'Crop View' from " +
                       "the template's controlled parameters (set_template_controls) or remove the template from the " +
                       "view (apply_template with template_view_id=-1), then set the crop.";
            if (f.ScopeBoxName != null || f.ScopeBoxId != null)
                return view + "'s crop is driven by scope box '" + f.ScopeBoxName + "' (id " + f.ScopeBoxId + "): Revit " +
                       "sizes the crop to the scope box, so a crop box written here does not govern the view. Move or " +
                       "resize the scope box, or clear it from the view, then set the crop.";
            if (f.NonRectangularShape == true)
                return view + " has a sketched (non-rectangular) crop region. The Revit API ignores a crop box written " +
                       "over one, so this would report work that did not happen. Use horizun_fix_planimetry set_crop " +
                       "with crop.loop to set a shape, or reset the crop to a rectangle in Revit first.";
            return null;
        }

        /// <summary>
        /// The post-write verdict. A box set on an inactive crop never verifies; nor does
        /// one whose drawn shape is elsewhere. An unreadable shape is a note, not a pass of
        /// its own: the verdict then rests on the active flag, the parameter and the box.
        /// </summary>
        public static CropVerdict Evaluate(CropReadback r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            var v = new CropVerdict();
            if (r.CropBoxActive == null) v.Failures.Add("crop_active_unreadable");
            else if (r.CropBoxActive == false) v.Failures.Add("crop_inactive");
            if (r.CropParameter == null) v.Notes.Add("crop_parameter_unreadable");
            else if (r.CropParameter.Value != 1) v.Failures.Add("crop_parameter_off");
            if (r.BoxMatches == null) v.Failures.Add("crop_box_unreadable");
            else if (r.BoxMatches == false) v.Failures.Add("crop_box_differs");
            if (r.ShapeMatches == null) v.Notes.Add("crop_shape_unreadable");
            else if (r.ShapeMatches == false) v.Failures.Add("crop_shape_differs");
            v.Verified = v.Failures.Count == 0;
            return v;
        }

        /// <summary>
        /// A rectangle read back (view-plane, feet) against the one requested, within a tolerance.
        /// </summary>
        public static bool RectangleMatches(double[] requested, double[] actual, double toleranceFeet)
        {
            if (requested == null || actual == null || requested.Length != 4 || actual.Length != 4) return false;
            for (int i = 0; i < 4; i++)
            {
                if (double.IsNaN(actual[i]) || double.IsInfinity(actual[i])) return false;
                if (Math.Abs(requested[i] - actual[i]) > toleranceFeet) return false;
            }
            return true;
        }

        /// <summary>
        /// The viewport on a sheet against the crop it shows, both in PAPER millimetres.
        /// Null when the viewport is no larger than the crop (within toleranceMm per side);
        /// otherwise the finding text. The viewport box grows past the crop when
        /// annotations outside it are drawn - the annotation crop is what bounds them.
        /// </summary>
        public static string ViewportFinding(double cropWidthMm, double cropHeightMm, double viewportWidthMm,
                                             double viewportHeightMm, bool? annotationCropActive, double toleranceMm)
        {
            bool wider = viewportWidthMm > cropWidthMm + 2 * toleranceMm;
            bool taller = viewportHeightMm > cropHeightMm + 2 * toleranceMm;
            if (!wider && !taller) return null;
            string sizes = string.Format(CultureInfo.InvariantCulture,
                "the viewport is {0:0.#} x {1:0.#} mm on the sheet while the crop at the view scale is {2:0.#} x {3:0.#} mm",
                viewportWidthMm, viewportHeightMm, cropWidthMm, cropHeightMm);
            return FindingViewportLargerThanCrop + ": " + sizes + ". " +
                   (annotationCropActive == true
                       ? "The annotation crop is on, so its offset (or annotation outside it) is what extends the viewport."
                       : "Annotation outside the crop (grids, levels, tags, section marks) still widens the viewport while " +
                         "the annotation crop is off: set_annotation_crop active=true bounds it.");
        }
    }
}
