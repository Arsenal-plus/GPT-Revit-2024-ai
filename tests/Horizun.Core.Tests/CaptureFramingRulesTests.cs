// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_verify_changes picture. Reported 2026-09-30: 75 walls on a level at
// +30,000 mm, orientation=top, exported a blank 7.7 KB image with captured=true.
// Two halves: the camera must stand where every framed element is in front of
// it, and a blank image must be recognised as blank.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CaptureFramingRulesTests
    {
        private const double Mm = 1.0 / 304.8;

        // The dry run's walls: a ~66 x 40 m footprint, 3 m tall, on a level at +30 m,
        // padded the way VerifyChangesCommand.Frame pads (15 % of the largest side).
        private static readonly double[] Min = { -5000 * Mm, -4000 * Mm, 30000 * Mm - 10000 * Mm };
        private static readonly double[] Max = { 71000 * Mm, 44000 * Mm, 33000 * Mm + 10000 * Mm };

        [Theory]
        [InlineData("top")]
        [InlineData("front")]
        [InlineData("right")]
        [InlineData("isometric")]
        public void Every_corner_of_a_box_at_plus_30_m_is_in_front_of_the_eye(string orientation)
        {
            double[] forward, up;
            CaptureFramingRules.Directions(orientation, out forward, out up);
            double[] eye = CaptureFramingRules.Eye(Min, Max, forward);
            foreach (double[] corner in CaptureFramingRules.Corners(Min, Max))
                Assert.True(CaptureFramingRules.Depth(eye, forward, corner) > 0,
                    orientation + ": a corner is behind the eye");
        }

        [Fact]
        public void The_top_eye_stands_above_the_box_and_looks_down()
        {
            double[] forward, up;
            CaptureFramingRules.Directions("top", out forward, out up);
            Assert.Equal(new[] { 0.0, 0.0, -1.0 }, forward);
            double[] eye = CaptureFramingRules.Eye(Min, Max, forward);
            Assert.True(eye[2] > Max[2], "the eye must be above the highest element");
            Assert.Equal((Min[0] + Max[0]) / 2, eye[0], 9);
            Assert.Equal((Min[1] + Max[1]) / 2, eye[1], 9);
        }

        [Fact]
        public void The_old_eye_at_the_default_view_left_the_raised_walls_behind_it()
        {
            // What the defect looked like: an eye kept from a default view of a model
            // whose top is ~15 m, now looking straight down. The raised box is behind it.
            double[] forward = { 0, 0, -1 };
            double[] defaultEye = { 30000 * Mm, 20000 * Mm, 15000 * Mm };
            Assert.True(CaptureFramingRules.Corners(Min, Max).All(c => CaptureFramingRules.Depth(defaultEye, forward, c) < 0));
        }

        [Fact]
        public void The_crop_depth_covers_the_whole_box_in_the_crops_own_frame()
        {
            // A top view's crop frame: X right, Y up, Z toward the viewer, origin at the eye.
            double[] forward, up;
            CaptureFramingRules.Directions("top", out forward, out up);
            double[] eye = CaptureFramingRules.Eye(Min, Max, forward);
            var local = CaptureFramingRules.Corners(Min, Max)
                .Select(c => new[] { c[0] - eye[0], c[1] - eye[1], c[2] - eye[2] }).ToList();
            double[] cropMin, cropMax;
            CaptureFramingRules.CropAround(local, 0.5, out cropMin, out cropMax);
            foreach (double[] p in local)
                for (int i = 0; i < 3; i++)
                {
                    Assert.True(p[i] >= cropMin[i] && p[i] <= cropMax[i]);
                }
            Assert.True(cropMax[2] < 0, "the whole box is in front of the eye (negative Z toward the viewer)");
        }

        // ---- blank detector ----------------------------------------------------

        private const int White = unchecked((int)0xFFFFFFFF);
        private const int Blue = unchecked((int)0xFF286EDC);

        private static int[] Fill(int w, int h, int colour) => Enumerable.Repeat(colour, w * h).ToArray();

        [Fact]
        public void An_all_white_export_is_blank()
        {
            ImageContent c = ImageBlankness.Measure(Fill(1400, 1000, White), 1400, 1000);
            Assert.True(c.IsBlank);
            Assert.Equal(0, c.ContentPixels);
            Assert.Equal(White, c.BackgroundArgb);
        }

        [Fact]
        public void Antialiasing_noise_and_a_few_specks_are_still_blank()
        {
            int[] px = Fill(1400, 1000, White);
            for (int i = 0; i < px.Length; i += 97) px[i] = unchecked((int)0xFFF0F0F0); // within tolerance
            for (int i = 0; i < 300; i++) px[i * 4001] = Blue;                           // 300 specks < 700
            ImageContent c = ImageBlankness.Measure(px, 1400, 1000);
            Assert.True(c.IsBlank);
            Assert.Equal(300, c.ContentPixels);
        }

        [Fact]
        public void Thin_walls_seen_from_above_are_content()
        {
            // 75 walls x ~4 px thick x ~100 px long - the picture the defect should have produced.
            int w = 1400, h = 1000;
            int[] px = Fill(w, h, White);
            for (int wall = 0; wall < 75; wall++)
            {
                int y0 = 50 + (wall % 25) * 35, x0 = 100 + (wall / 25) * 400;
                for (int y = y0; y < y0 + 4; y++)
                    for (int x = x0; x < x0 + 100; x++) px[y * w + x] = Blue;
            }
            ImageContent c = ImageBlankness.Measure(px, w, h);
            Assert.False(c.IsBlank);
            Assert.Equal(75 * 4 * 100, c.ContentPixels);
        }

        [Fact]
        public void The_background_is_the_dominant_colour_not_assumed_white()
        {
            int[] px = Fill(200, 100, unchecked((int)0xFF202020));
            ImageContent blank = ImageBlankness.Measure(px, 200, 100);
            Assert.True(blank.IsBlank);
            for (int i = 0; i < 2000; i++) px[i] = White;
            Assert.False(ImageBlankness.Measure(px, 200, 100).IsBlank);
        }

        [Fact]
        public void A_fully_transparent_image_and_a_zero_area_image_are_blank()
        {
            Assert.True(ImageBlankness.Measure(Fill(100, 100, 0x00FFFFFF), 100, 100).IsBlank);
            Assert.True(ImageBlankness.Measure(new int[0], 0, 0).IsBlank);
        }
    }
}
