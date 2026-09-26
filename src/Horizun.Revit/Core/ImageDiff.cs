// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// horizun_verify_changes (operation=compare_to) - the pixel math behind
// before/after visual diffing, Revit-free so it is unit-testable on plain net8.
//
// Deliberately no `using System.Drawing` and no `using Autodesk.*` here: PNG
// decode/encode is the add-in's job (VerifyChangesSnapshot.cs, which is allowed
// System.Drawing because it never runs in this test project). This file only
// ever sees raw ARGB pixels as packed 32-bit ints (0xAARRGGBB, matching
// System.Drawing.Color.ToArgb() and Bitmap's Format32bppArgb once its BGRA
// bytes are read back as a little-endian int) plus width/height - arrays a
// unit test can build by hand.
//
// Pipeline: per-pixel threshold -> square dilation (a one-pixel building edge
// should not vanish because its own antialiasing sits just under the
// threshold on one side) -> 4-connected labelling into regions, each with a
// pixel bounding box and its own pixel count so a caller can drop specks.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    /// <summary>One connected group of changed pixels, in pixel (not model) coordinates.</summary>
    public sealed class ImageDiffRegion
    {
        public int MinX, MinY, MaxX, MaxY;
        public int PixelCount;
    }

    public sealed class ImageDiffResult
    {
        public int Width;
        public int Height;
        /// <summary>Pixels whose raw per-channel delta exceeded the threshold, BEFORE dilation.</summary>
        public int ChangedPixelCount;
        /// <summary>ChangedPixelCount / (Width*Height). 0 when the two images are identical.</summary>
        public double ChangedPixelRatio;
        /// <summary>The DILATED mask (row-major, length Width*Height) - what Overlay paints and what Regions are labelled from.</summary>
        public bool[] Mask;
        /// <summary>Connected components of Mask with at least MinRegionPixels pixels, largest first.</summary>
        public List<ImageDiffRegion> Regions = new List<ImageDiffRegion>();
    }

    public static class ImageDiff
    {
        /// <summary>0xFFDC1E1E - the same red horizun_verify_changes already paints error findings with.</summary>
        public const int DefaultOverlayColor = unchecked((int)0xFFDC1E1E);

        /// <summary>
        /// Compares two same-sized ARGB images. threshold is the maximum per-channel
        /// (R,G,B - alpha ignored) absolute difference that still counts as "unchanged";
        /// a real screen-space render of the same camera has some antialiasing jitter
        /// even with nothing moved, so 0 would flag noise as change. dilateRadius grows
        /// the raw mask by that many pixels (Chebyshev/square) before labelling, so a
        /// thin edge does not fragment into many tiny regions. minRegionPixels drops
        /// specks (isolated antialiasing) from Regions without changing ChangedPixelRatio,
        /// which is always measured on the RAW (undilated) mask.
        /// </summary>
        public static ImageDiffResult Compare(int[] before, int[] after, int width, int height,
            int threshold = 24, int dilateRadius = 2, int minRegionPixels = 8)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (after == null) throw new ArgumentNullException(nameof(after));
            if (width <= 0 || height <= 0) throw new ArgumentException("width and height must be positive.");
            long expected = (long)width * height;
            if (before.Length != expected) throw new ArgumentException("before.Length must equal width*height.");
            if (after.Length != expected) throw new ArgumentException("after.Length must equal width*height.");
            if (threshold < 0 || threshold > 255) throw new ArgumentException("threshold must be 0..255.");
            if (dilateRadius < 0 || dilateRadius > 32) throw new ArgumentException("dilateRadius must be 0..32.");
            if (minRegionPixels < 1) throw new ArgumentException("minRegionPixels must be >= 1.");

            int n = width * height;
            var raw = new bool[n];
            int rawCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (ChannelDelta(before[i], after[i]) > threshold) { raw[i] = true; rawCount++; }
            }

            bool[] dilated = dilateRadius == 0 ? raw : Dilate(raw, width, height, dilateRadius);
            List<ImageDiffRegion> regions = Label(dilated, width, height, minRegionPixels);

            return new ImageDiffResult
            {
                Width = width,
                Height = height,
                ChangedPixelCount = rawCount,
                ChangedPixelRatio = n == 0 ? 0.0 : (double)rawCount / n,
                Mask = dilated,
                Regions = regions
            };
        }

        /// <summary>Largest absolute per-channel (R,G,B) difference between two 0xAARRGGBB pixels. Alpha ignored.</summary>
        public static int ChannelDelta(int a, int b)
        {
            int aR = (a >> 16) & 0xFF, aG = (a >> 8) & 0xFF, aB = a & 0xFF;
            int bR = (b >> 16) & 0xFF, bG = (b >> 8) & 0xFF, bB = b & 0xFF;
            int dr = Math.Abs(aR - bR), dg = Math.Abs(aG - bG), db = Math.Abs(aB - bB);
            return Math.Max(dr, Math.Max(dg, db));
        }

        /// <summary>Square (Chebyshev) dilation: a pixel is set when any pixel within radius in the source is set.</summary>
        public static bool[] Dilate(bool[] mask, int width, int height, int radius)
        {
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            if (mask.Length != (long)width * height) throw new ArgumentException("mask.Length must equal width*height.");
            if (radius <= 0) return (bool[])mask.Clone();
            var result = new bool[mask.Length];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (result[y * width + x]) continue;
                    if (!AnySetNear(mask, width, height, x, y, radius)) continue;
                    result[y * width + x] = true;
                }
            }
            return result;
        }

        private static bool AnySetNear(bool[] mask, int width, int height, int cx, int cy, int radius)
        {
            int x0 = Math.Max(0, cx - radius), x1 = Math.Min(width - 1, cx + radius);
            int y0 = Math.Max(0, cy - radius), y1 = Math.Min(height - 1, cy + radius);
            for (int y = y0; y <= y1; y++)
            {
                int row = y * width;
                for (int x = x0; x <= x1; x++)
                    if (mask[row + x]) return true;
            }
            return false;
        }

        /// <summary>4-connected labelling into bounding boxes, iterative BFS (no recursion depth risk on a large mask).</summary>
        public static List<ImageDiffRegion> Label(bool[] mask, int width, int height, int minRegionPixels)
        {
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            if (mask.Length != (long)width * height) throw new ArgumentException("mask.Length must equal width*height.");
            var visited = new bool[mask.Length];
            var regions = new List<ImageDiffRegion>();
            var stack = new Stack<int>();
            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || visited[start]) continue;
                visited[start] = true;
                stack.Push(start);
                int minX = start % width, maxX = minX, minY = start / width, maxY = minY, count = 0;
                while (stack.Count > 0)
                {
                    int idx = stack.Pop();
                    int x = idx % width, y = idx / width;
                    count++;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    if (x > 0) TryPush(mask, visited, width, idx - 1, stack);
                    if (x < width - 1) TryPush(mask, visited, width, idx + 1, stack);
                    if (y > 0) TryPush(mask, visited, width, idx - width, stack);
                    if (y < height - 1) TryPush(mask, visited, width, idx + width, stack);
                }
                if (count >= minRegionPixels)
                    regions.Add(new ImageDiffRegion { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY, PixelCount = count });
            }
            regions.Sort((a, b) => b.PixelCount.CompareTo(a.PixelCount));
            return regions;
        }

        private static void TryPush(bool[] mask, bool[] visited, int width, int idx, Stack<int> stack)
        {
            if (!mask[idx] || visited[idx]) return;
            visited[idx] = true;
            stack.Push(idx);
        }

        /// <summary>Returns a COPY of after with every masked pixel replaced by color (fully opaque red by default).</summary>
        public static int[] Overlay(int[] after, bool[] mask, int width, int height, int color = DefaultOverlayColor)
        {
            if (after == null) throw new ArgumentNullException(nameof(after));
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            if (after.Length != (long)width * height || mask.Length != after.Length)
                throw new ArgumentException("after and mask must both be width*height.");
            var result = (int[])after.Clone();
            for (int i = 0; i < result.Length; i++)
                if (mask[i]) result[i] = color;
            return result;
        }
    }
}
