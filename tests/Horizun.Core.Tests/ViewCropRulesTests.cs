// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_manage_views set_crop. Reported 2026-09-30: verified=true while the
// view on the sheet looked uncropped. The old check read the CropBox back and
// nothing else; these pin the rule that a box alone is never a cropped view.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ViewCropRulesTests
    {
        private static CropReadback Good() => new CropReadback
            { CropBoxActive = true, CropParameter = 1, BoxMatches = true, ShapeMatches = true };

        [Fact]
        public void A_crop_that_is_active_on_both_reads_with_box_and_shape_in_place_verifies()
        {
            CropVerdict v = ViewCropRules.Evaluate(Good());
            Assert.True(v.Verified);
            Assert.Empty(v.Failures);
        }

        [Fact]
        public void A_matching_box_on_an_inactive_crop_never_verifies()
        {
            CropReadback r = Good();
            r.CropBoxActive = false;
            CropVerdict v = ViewCropRules.Evaluate(r);
            Assert.False(v.Verified);
            Assert.Contains("crop_inactive", v.Failures);
        }

        [Fact]
        public void The_crop_parameter_is_read_independently_of_the_property()
        {
            CropReadback r = Good();
            r.CropParameter = 0;
            CropVerdict v = ViewCropRules.Evaluate(r);
            Assert.False(v.Verified);
            Assert.Contains("crop_parameter_off", v.Failures);
        }

        [Fact]
        public void An_unreadable_active_flag_is_not_agreement()
        {
            CropReadback r = Good();
            r.CropBoxActive = null;
            Assert.Contains("crop_active_unreadable", ViewCropRules.Evaluate(r).Failures);
        }

        [Fact]
        public void A_drawn_shape_elsewhere_fails_even_when_the_stored_box_matches()
        {
            CropReadback r = Good();
            r.ShapeMatches = false;
            CropVerdict v = ViewCropRules.Evaluate(r);
            Assert.False(v.Verified);
            Assert.Contains("crop_shape_differs", v.Failures);
        }

        [Fact]
        public void An_unreadable_shape_is_a_note_and_the_verdict_rests_on_the_other_reads()
        {
            CropReadback r = Good();
            r.ShapeMatches = null;
            CropVerdict v = ViewCropRules.Evaluate(r);
            Assert.True(v.Verified);
            Assert.Contains("crop_shape_unreadable", v.Notes);
            r.BoxMatches = null;
            Assert.Contains("crop_box_unreadable", ViewCropRules.Evaluate(r).Failures);
        }

        [Fact]
        public void A_view_template_that_controls_the_crop_is_refused_by_name()
        {
            string refusal = ViewCropRules.Refusal(new CropPreflightFacts
            {
                ViewId = 439877, ViewName = "MDP - Planta Nivel 02",
                TemplateControlsCrop = true, TemplateName = "Architectural Plan", TemplateId = 1234
            });
            Assert.NotNull(refusal);
            Assert.Contains("'Architectural Plan'", refusal);
            Assert.Contains("439877", refusal);
        }

        [Fact]
        public void A_template_that_does_not_control_the_crop_is_no_obstacle()
        {
            Assert.Null(ViewCropRules.Refusal(new CropPreflightFacts
                { ViewId = 1, TemplateName = "Architectural Plan", TemplateId = 2, TemplateControlsCrop = false, NonRectangularShape = false }));
        }

        [Fact]
        public void A_scope_box_a_sketched_crop_and_a_template_view_are_each_refused()
        {
            Assert.Contains("scope box 'SB-01'", ViewCropRules.Refusal(new CropPreflightFacts { ViewId = 1, ScopeBoxName = "SB-01", ScopeBoxId = 9 }));
            Assert.Contains("non-rectangular", ViewCropRules.Refusal(new CropPreflightFacts { ViewId = 1, NonRectangularShape = true }));
            Assert.Contains("view template", ViewCropRules.Refusal(new CropPreflightFacts { ViewId = 1, IsTemplate = true }));
        }

        [Fact]
        public void Rectangles_match_within_the_tolerance_only()
        {
            double mm = 1.0 / 304.8;
            var want = new[] { 0.0, 0.0, 100.0, 50.0 };
            Assert.True(ViewCropRules.RectangleMatches(want, new[] { 0.5 * mm, 0.0, 100.0, 50.0 - 0.5 * mm }, mm));
            Assert.False(ViewCropRules.RectangleMatches(want, new[] { 0.0, 0.0, 100.0 + 2 * mm, 50.0 }, mm));
            Assert.False(ViewCropRules.RectangleMatches(want, new[] { double.NaN, 0.0, 100.0, 50.0 }, mm));
            Assert.False(ViewCropRules.RectangleMatches(want, null, mm));
        }

        [Fact]
        public void A_viewport_wider_than_its_crop_is_a_named_finding_pointing_at_the_annotation_crop()
        {
            // 66 m x 40 m crop at 1:100 is 660 x 400 mm; grids outside push the viewport to 760 x 470.
            string finding = ViewCropRules.ViewportFinding(660, 400, 760, 470, annotationCropActive: false, toleranceMm: 2);
            Assert.StartsWith(ViewCropRules.FindingViewportLargerThanCrop, finding);
            Assert.Contains("set_annotation_crop", finding);
            Assert.Null(ViewCropRules.ViewportFinding(660, 400, 661, 401, false, 2));
        }
    }
}
