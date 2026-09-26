using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ImageDiffTests
    {
        private const int White = unchecked((int)0xFFFFFFFF);
        private const int Black = unchecked((int)0xFF000000);

        private static int[] Solid(int color, int width, int height)
        {
            var a = new int[width * height];
            for (int i = 0; i < a.Length; i++) a[i] = color;
            return a;
        }

        [Fact]
        public void Identical_images_have_zero_ratio_and_no_regions()
        {
            int[] before = Solid(White, 10, 10);
            int[] after = (int[])before.Clone();
            ImageDiffResult r = ImageDiff.Compare(before, after, 10, 10);
            Assert.Equal(0, r.ChangedPixelCount);
            Assert.Equal(0.0, r.ChangedPixelRatio);
            Assert.Empty(r.Regions);
        }

        [Fact]
        public void A_changed_block_is_measured_and_labelled()
        {
            int w = 20, h = 20;
            int[] before = Solid(White, w, h);
            int[] after = (int[])before.Clone();
            // paint a 4x4 black square at (2,2)..(5,5): 16 changed pixels out of 400.
            for (int y = 2; y <= 5; y++)
                for (int x = 2; x <= 5; x++)
                    after[y * w + x] = Black;

            ImageDiffResult r = ImageDiff.Compare(before, after, w, h, threshold: 24, dilateRadius: 0);
            Assert.Equal(16, r.ChangedPixelCount);
            Assert.Equal(16.0 / (w * h), r.ChangedPixelRatio);
            Assert.Single(r.Regions);
            ImageDiffRegion region = r.Regions[0];
            Assert.Equal(2, region.MinX);
            Assert.Equal(2, region.MinY);
            Assert.Equal(5, region.MaxX);
            Assert.Equal(5, region.MaxY);
            Assert.Equal(16, region.PixelCount);
        }

        [Fact]
        public void Ratio_is_measured_on_the_raw_mask_not_the_dilated_one()
        {
            int w = 20, h = 20;
            int[] before = Solid(White, w, h);
            int[] after = (int[])before.Clone();
            after[10 * w + 10] = Black; // exactly one changed pixel

            ImageDiffResult r = ImageDiff.Compare(before, after, w, h, threshold: 24, dilateRadius: 3);
            Assert.Equal(1, r.ChangedPixelCount);
            Assert.Equal(1.0 / (w * h), r.ChangedPixelRatio);
            // the DILATED mask must be wider than the single raw pixel.
            int maskCount = 0;
            foreach (bool b in r.Mask) if (b) maskCount++;
            Assert.True(maskCount > 1, "dilation should have grown the single-pixel mask");
        }

        [Fact]
        public void Below_threshold_delta_is_not_flagged()
        {
            int w = 5, h = 5;
            int baseColor = unchecked((int)0xFF808080);
            int[] before = Solid(baseColor, w, h);
            int nudged = unchecked((int)0xFF828280); // +2 on R and G channels
            int[] after = (int[])before.Clone();
            after[0] = nudged;
            ImageDiffResult r = ImageDiff.Compare(before, after, w, h, threshold: 24);
            Assert.Equal(0, r.ChangedPixelCount);
        }

        [Fact]
        public void Two_separate_blocks_yield_two_regions_without_dilation()
        {
            int w = 30, h = 10;
            int[] before = Solid(White, w, h);
            int[] after = (int[])before.Clone();
            after[5] = Black;      // block A near x=5
            after[25] = Black;     // block B near x=25, far enough apart
            ImageDiffResult r = ImageDiff.Compare(before, after, w, h, threshold: 24, dilateRadius: 0, minRegionPixels: 1);
            Assert.Equal(2, r.Regions.Count);
        }

        [Fact]
        public void MinRegionPixels_drops_specks_without_changing_the_ratio()
        {
            int w = 10, h = 10;
            int[] before = Solid(White, w, h);
            int[] after = (int[])before.Clone();
            after[0] = Black; // a single-pixel speck
            ImageDiffResult r = ImageDiff.Compare(before, after, w, h, threshold: 24, dilateRadius: 0, minRegionPixels: 4);
            Assert.Empty(r.Regions);
            Assert.Equal(1, r.ChangedPixelCount);
            Assert.Equal(1.0 / (w * h), r.ChangedPixelRatio);
        }

        [Fact]
        public void Overlay_paints_only_masked_pixels_and_leaves_the_rest_untouched()
        {
            int w = 4, h = 4;
            int[] after = Solid(White, w, h);
            var mask = new bool[w * h];
            mask[5] = true;
            int[] overlaid = ImageDiff.Overlay(after, mask, w, h);
            Assert.Equal(ImageDiff.DefaultOverlayColor, overlaid[5]);
            for (int i = 0; i < overlaid.Length; i++)
                if (i != 5) Assert.Equal(White, overlaid[i]);
            // the input array itself must be untouched (Overlay returns a copy).
            Assert.Equal(White, after[5]);
        }

        [Fact]
        public void ChannelDelta_ignores_alpha_and_uses_the_largest_channel_gap()
        {
            int a = unchecked((int)0x00102030); // alpha 0, R=0x10 G=0x20 B=0x30
            int b = unchecked((int)0xFF104030); // alpha FF (ignored), R=0x10 (same), G=0x40 (+0x20=32), B=0x30 (same)
            Assert.Equal(32, ImageDiff.ChannelDelta(a, b));
        }

        [Fact]
        public void Compare_rejects_mismatched_array_lengths()
        {
            int[] before = Solid(White, 4, 4);
            int[] after = Solid(White, 3, 3);
            Assert.Throws<ArgumentException>(() => ImageDiff.Compare(before, after, 4, 4));
        }

        [Fact]
        public void Dilate_zero_radius_is_a_pure_copy()
        {
            var mask = new bool[9];
            mask[4] = true;
            bool[] d = ImageDiff.Dilate(mask, 3, 3, 0);
            Assert.Equal(mask, d);
        }
    }
}
