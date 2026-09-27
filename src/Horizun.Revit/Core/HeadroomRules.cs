// -----------------------------------------------------------------------------
// Horizun Revit MCP - the arithmetic of horizun_code_check operation=headroom.
// Original Horizun code. Revit-free, so it is unit-tested (tests/Horizun.Core.Tests).
//
// One vertical ray per sample point crosses the element itself and then, perhaps, the
// next surface. CodeCheckHeadroom.cs reduces what the ray returned to (proximity, kind,
// key) hits; everything decided about them is decided here:
//   * the element's far face is its LAST own hit along the ray;
//   * the clear height is the distance from there to the first TARGET hit at or beyond
//     it (a surface touching the element is a clear height of zero, not a skip);
//   * a sample whose ray never crosses the element is OFF it (a bounding-box grid over
//     an L-shaped floor), counted apart and never judged;
//   * a sample that crosses the element and finds no target beyond is NOT MEASURED -
//     never a pass - and so is one where the element lies INSIDE that target (the ray
//     met the target before the element's far face: the next hit is the target's own
//     far side, not a surface the element stands clear of).
// An element passes only when every sample on it was measured at or above the
// threshold; one measured sample below it fails the element whatever else was not
// measured; any other mix is not_decidable.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class HeadroomRules
    {
        public const double DefaultSpacingMm = 1000;
        public const double MinSpacingMm = 100;
        public const int MaxSamplesPerElement = 400;
        public const int MaxElements = 2000;
        /// <summary>Rays one call may cast, counted before the first is cast (each runs on Revit's UI thread).</summary>
        public const int MaxRays = 5000;

        public enum HitKind { Own, Target, Other }

        public struct Hit
        {
            public double Proximity;
            public HitKind Kind;
            /// <summary>Which element was hit (host or link element), so hits of one target can be grouped.</summary>
            public string Key;

            public Hit(double proximity, HitKind kind, string key) { Proximity = proximity; Kind = kind; Key = key; }
        }

        public enum SampleState { OffElement, Measured, NothingBeyond, InsideTarget }

        public sealed class Sample
        {
            public SampleState State;
            /// <summary>Distance along the ray from the origin to the element's far face.</summary>
            public double FarFace;
            /// <summary>Clear height (same unit as the proximities); set only when Measured.</summary>
            public double Clear;
            /// <summary>The target hit that bounds the clear height, or that the element lies inside.</summary>
            public string TargetKey;
        }

        /// <summary>Reads one ray. <paramref name="tolerance"/> is in the proximities' unit.</summary>
        public static Sample Read(IEnumerable<Hit> hits, double tolerance)
        {
            List<Hit> list = (hits ?? Enumerable.Empty<Hit>()).OrderBy(h => h.Proximity).ToList();
            int lastOwn = list.FindLastIndex(h => h.Kind == HitKind.Own);
            if (lastOwn < 0) return new Sample { State = SampleState.OffElement };
            double far = list[lastOwn].Proximity;
            foreach (Hit h in list)
            {
                if (h.Kind != HitKind.Target || h.Proximity < far - tolerance) continue;
                // The same target met before the far face: the element is inside it here.
                bool inside = list.Any(o => o.Kind == HitKind.Target && o.Key == h.Key && o.Proximity < far - tolerance);
                if (inside) return new Sample { State = SampleState.InsideTarget, FarFace = far, TargetKey = h.Key };
                return new Sample { State = SampleState.Measured, FarFace = far, Clear = Math.Max(0, h.Proximity - far), TargetKey = h.Key };
            }
            return new Sample { State = SampleState.NothingBeyond, FarFace = far };
        }

        /// <summary>
        /// passes | fails | not_decidable | not_measured. <paramref name="considered"/> counts the samples on the
        /// element plus those whose ray could not be cast (they might have been on it).
        /// </summary>
        public static string Outcome(int considered, int measured, double? minClear, double threshold)
        {
            if (measured <= 0 || minClear == null) return "not_measured";
            if (minClear.Value < threshold - 1e-9) return "fails";
            return measured < considered ? "not_decidable" : "passes";
        }

        /// <summary>Normalised curve parameters at the middle of n equal pieces, n = ceil(length / spacing), capped.</summary>
        public static IList<double> AlongCurve(double length, double spacing, int cap, out double spacingUsed)
        {
            int n = length > 0 && spacing > 0 ? (int)Math.Ceiling(length / spacing - 1e-9) : 1;
            n = Math.Max(1, Math.Min(Math.Max(1, cap), n));
            spacingUsed = length > 0 ? length / n : 0;
            var t = new List<double>(n);
            for (int i = 0; i < n; i++) t.Add((i + 0.5) / n);
            return t;
        }

        /// <summary>
        /// Cell centres of a grid over a rectangle. The spacing grows by 25 % steps until the count fits the cap,
        /// and the one used is returned so the reply can state it.
        /// </summary>
        public static IList<KeyValuePair<double, double>> Grid(double minX, double minY, double maxX, double maxY, double spacing, int cap, out double spacingUsed)
        {
            double dx = Math.Max(0, maxX - minX), dy = Math.Max(0, maxY - minY);
            double s = spacing > 0 ? spacing : Math.Max(Math.Max(dx, dy), 1e-9);
            cap = Math.Max(1, cap);
            int nx, ny;
            while (true)
            {
                nx = Math.Max(1, (int)Math.Ceiling(dx / s - 1e-9));
                ny = Math.Max(1, (int)Math.Ceiling(dy / s - 1e-9));
                if ((long)nx * ny <= cap) break;
                s *= 1.25;
            }
            spacingUsed = s;
            var points = new List<KeyValuePair<double, double>>(nx * ny);
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                    points.Add(new KeyValuePair<double, double>(minX + (i + 0.5) * dx / nx, minY + (j + 0.5) * dy / ny));
            return points;
        }

        /// <summary>Worst first: fails, not_measured, not_decidable, passes.</summary>
        public static int Rank(string outcome)
        {
            switch (outcome)
            {
                case "fails": return 0;
                case "not_measured": return 1;
                case "not_decidable": return 2;
                default: return 3;
            }
        }
    }
}
