// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_verify_changes: WHERE THE CAMERA STANDS, AND WHETHER THE PICTURE SHOWS
// ANYTHING. Revit-free, so both are provable at a desk.
//
// Reported from the "Revit con agentes" dry run (2026-09-30): 75 walls on a new
// level at +30,000 mm, orientation=top, came back as a blank 7.7 KB PNG with
// captured=true. Two things were wrong, and either is enough for a blank image:
//
//   * THE EYE WAS NEVER MOVED. The temporary view was re-aimed with the eye of
//     the default isometric view (placed by Revit for the model as it was), and
//     the crop box kept that view's depth range. The crop box's Z IS the far/near
//     clip of a 3D view (RevitAPI, View.CropBox: setting it modifies "the crop
//     region and far clip plane"). Elements 30 m above the existing model sit
//     behind an eye that was never above them, or past a depth range built for a
//     smaller model. Now the eye stands OUTSIDE the elements' box on the viewer
//     side for every orientation, and the crop's depth covers the whole box.
//
//   * captured=true MEANT "A FILE EXISTS". A white rectangle is a file. The image
//     is now measured after export: when almost every pixel is the background
//     colour the reply says captured=false with a named blank_image finding.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public static class CaptureFramingRules
    {
        /// <summary>The orientations verify_changes offers, as (forward, up) unit vectors.</summary>
        public static void Directions(string orientation, out double[] forward, out double[] up)
        {
            switch (orientation)
            {
                case "top": forward = new[] { 0.0, 0.0, -1.0 }; up = new[] { 0.0, 1.0, 0.0 }; break;
                case "front": forward = new[] { 0.0, 1.0, 0.0 }; up = new[] { 0.0, 0.0, 1.0 }; break;
                case "right": forward = new[] { -1.0, 0.0, 0.0 }; up = new[] { 0.0, 0.0, 1.0 }; break;
                case "isometric": forward = Normalize(new[] { -1.0, 1.0, -1.0 }); up = Normalize(new[] { -1.0, 1.0, 2.0 }); break;
                default: throw new ArgumentException("orientation must be isometric, top, front or right.");
            }
        }

        /// <summary>
        /// An eye that sees the whole box: the box centre, backed off along -forward by the
        /// half-diagonal plus a margin, so every corner lies IN FRONT of the eye whatever
        /// the orientation and wherever the box is (a level at +30 m included).
        /// </summary>
        public static double[] Eye(double[] min, double[] max, double[] forward)
        {
            double[] c = { (min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2 };
            double dx = max[0] - min[0], dy = max[1] - min[1], dz = max[2] - min[2];
            double half = Math.Sqrt(dx * dx + dy * dy + dz * dz) / 2;
            double back = half + Math.Max(1.0, 0.1 * half);
            double[] f = Normalize(forward);
            return new[] { c[0] - f[0] * back, c[1] - f[1] * back, c[2] - f[2] * back };
        }

        /// <summary>Distance of a point in front of the eye along forward (negative = behind it).</summary>
        public static double Depth(double[] eye, double[] forward, double[] point)
        {
            double[] f = Normalize(forward);
            return (point[0] - eye[0]) * f[0] + (point[1] - eye[1]) * f[1] + (point[2] - eye[2]) * f[2];
        }

        /// <summary>The eight corners of an axis-aligned box.</summary>
        public static List<double[]> Corners(double[] min, double[] max)
        {
            var corners = new List<double[]>(8);
            for (int c = 0; c < 8; c++)
                corners.Add(new[] { (c & 1) == 0 ? min[0] : max[0], (c & 2) == 0 ? min[1] : max[1], (c & 4) == 0 ? min[2] : max[2] });
            return corners;
        }

        /// <summary>
        /// The crop box, in the crop's OWN frame, that holds every given point (already
        /// expressed in that frame) with pad on X and Y and Z. Z is the depth range - the
        /// far/near clip of a 3D view - and is set from the points, never kept from
        /// whatever view the crop came from.
        /// </summary>
        public static void CropAround(IEnumerable<double[]> localPoints, double pad, out double[] min, out double[] max)
        {
            min = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
            max = new[] { double.MinValue, double.MinValue, double.MinValue };
            int n = 0;
            foreach (double[] p in localPoints)
            {
                for (int i = 0; i < 3; i++) { min[i] = Math.Min(min[i], p[i]); max[i] = Math.Max(max[i], p[i]); }
                n++;
            }
            if (n == 0) throw new ArgumentException("no point to frame");
            for (int i = 0; i < 3; i++) { min[i] -= pad; max[i] += pad; }
        }

        private static double[] Normalize(double[] v)
        {
            double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (l <= 0) throw new ArgumentException("zero vector");
            return new[] { v[0] / l, v[1] / l, v[2] / l };
        }
    }

    /// <summary>How much of an exported image is something other than its background.</summary>
    public sealed class ImageContent
    {
        public int Width;
        public int Height;
        /// <summary>The most frequent colour, 0xAARRGGBB (fully transparent pixels count as one colour).</summary>
        public int BackgroundArgb;
        /// <summary>Pixels whose R, G or B differs from the background by more than the tolerance.</summary>
        public int ContentPixels;
        public double ContentRatio;
        public bool IsBlank;
    }

    public static class ImageBlankness
    {
        /// <summary>Per-channel difference that still counts as background (antialiasing, JPEG-free PNG noise).</summary>
        public const int DefaultTolerance = 24;
        /// <summary>Below this share of content pixels an image is blank (0.05 %: 700 px of 1400x1000).</summary>
        public const double DefaultMinContentRatio = 0.0005;
        /// <summary>And never blank above this many content pixels, however large the image.</summary>
        public const int DefaultMinContentPixels = 25;

        /// <summary>
        /// Measures packed 0xAARRGGBB pixels (row-major). The background is the most
        /// frequent colour - not assumed white, since a shaded or dark export has another.
        /// An image is blank when fewer than max(minContentPixels, minContentRatio x area)
        /// pixels differ from it. A zero-area image is blank.
        /// </summary>
        public static ImageContent Measure(int[] argb, int width, int height,
                                           int tolerance = DefaultTolerance,
                                           double minContentRatio = DefaultMinContentRatio,
                                           int minContentPixels = DefaultMinContentPixels)
        {
            if (argb == null) throw new ArgumentNullException(nameof(argb));
            long area = (long)width * height;
            if (width < 0 || height < 0 || argb.Length < area) throw new ArgumentException("pixel buffer does not match width x height");
            var result = new ImageContent { Width = width, Height = height };
            if (area == 0) { result.IsBlank = true; return result; }

            var counts = new Dictionary<int, int>();
            int best = 0, bestCount = -1;
            for (long i = 0; i < area; i++)
            {
                int key = Key(argb[i]);
                int n;
                counts.TryGetValue(key, out n);
                counts[key] = ++n;
                if (n > bestCount) { bestCount = n; best = key; }
            }
            result.BackgroundArgb = best;

            int content = 0;
            for (long i = 0; i < area; i++)
                if (Differs(Key(argb[i]), best, tolerance)) content++;
            result.ContentPixels = content;
            result.ContentRatio = (double)content / area;
            result.IsBlank = content < Math.Max(minContentPixels, minContentRatio * area);
            return result;
        }

        /// <summary>Opaque colours keep their RGB; any pixel under 16/255 alpha is "transparent".</summary>
        private static int Key(int argb) => ((argb >> 24) & 0xFF) < 16 ? 0 : (argb | unchecked((int)0xFF000000));

        private static bool Differs(int a, int b, int tolerance)
        {
            bool ta = a == 0, tb = b == 0;
            if (ta || tb) return ta != tb;
            return Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF)) > tolerance ||
                   Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF)) > tolerance ||
                   Math.Abs((a & 0xFF) - (b & 0xFF)) > tolerance;
        }
    }
}
