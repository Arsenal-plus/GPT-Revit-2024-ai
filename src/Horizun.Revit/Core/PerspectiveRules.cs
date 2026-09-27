// -----------------------------------------------------------------------------
// Horizun - original Horizun code.
//
// THE CAMERA of horizun_manage_views create_perspective, decided without a Revit:
// eye + target (+ up) become the eye/forward/up triple a ViewOrientation3D takes,
// and a fan of N azimuths from one eye becomes N such triples.
//
//   * NOTHING IS GUESSED. An eye on its target has no direction; looking straight
//     up or down has no "up" unless one is given. Both are refused, never nudged.
//   * Revit refuses an up direction that is not perpendicular to forward, so the
//     given up (world Z by default) is made perpendicular HERE (Gram-Schmidt) and
//     the triple actually sent is what gets reported and re-read.
//   * A fan keeps the pitch of eye->target and turns only the azimuth, in equal
//     steps of 360/N starting at the target's own azimuth.
//   * SameOrientation is the post-commit re-read test: the eye within a length
//     tolerance, each direction within an angle, in internal units (feet).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public sealed class CameraTriple
    {
        public double[] Eye { get; set; }
        public double[] Forward { get; set; }
        public double[] Up { get; set; }
        /// <summary>Azimuth of forward in degrees, counter-clockwise from +X in plan (0..360).</summary>
        public double AzimuthDegrees { get; set; }
        /// <summary>Elevation angle of forward above the horizontal, degrees.</summary>
        public double PitchDegrees { get; set; }
    }

    public static class PerspectiveRules
    {
        public const int MaxFan = 36;
        /// <summary>Eye and target closer than 1 mm (in feet) have no direction between them.</summary>
        public const double MinSightFeet = 1.0 / 304.8;
        private const double ParallelSine = 1e-6;

        public static CameraTriple FromEyeTarget(double[] eye, double[] target, double[] up = null)
        {
            Check(eye, "eye"); Check(target, "target");
            double[] f = Sub(target, eye);
            double len = Norm(f);
            if (len < MinSightFeet)
                throw new ArgumentException("eye and target coincide: a camera on its own target has no direction.");
            f = Scale(f, 1.0 / len);
            return Orient(eye, f, up);
        }

        /// <summary>N cameras from one eye, turned about world Z in steps of 360/N, keeping the pitch of eye->target.</summary>
        public static IList<CameraTriple> Fan(double[] eye, double[] target, int count, double[] up = null)
        {
            if (count < 1 || count > MaxFan)
                throw new ArgumentException("a fan takes 1.." + MaxFan + " azimuths (" + count + " was given).");
            CameraTriple first = FromEyeTarget(eye, target, up);
            double horizontal = Math.Sqrt(first.Forward[0] * first.Forward[0] + first.Forward[1] * first.Forward[1]);
            if (count > 1 && horizontal < ParallelSine)
                throw new ArgumentException("a fan turns the azimuth, and a camera looking straight up or down has none.");
            var cameras = new List<CameraTriple> { first };
            double az0 = Math.Atan2(first.Forward[1], first.Forward[0]);
            for (int k = 1; k < count; k++)
            {
                double az = az0 + 2 * Math.PI * k / count;
                var f = new[] { horizontal * Math.Cos(az), horizontal * Math.Sin(az), first.Forward[2] };
                cameras.Add(Orient(eye, f, up));
            }
            return cameras;
        }

        /// <summary>
        /// The name a camera of a request gets: the name itself for one camera; for a fan
        /// "<name> azNNN" in whole degrees - distinct, because a fan's steps are at least
        /// 360/MaxFan = 10 degrees apart - saying where each view looks. Null without a name.
        /// </summary>
        public static string ViewName(string name, CameraTriple camera, int count)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (count <= 1) return name;
            int degrees = (int)Math.Round(camera.AzimuthDegrees, MidpointRounding.AwayFromZero) % 360;
            return name + " az" + degrees.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>True when b is a re-read of a: eye within lengthTolerance (feet), directions within angleDegrees.</summary>
        public static bool SameOrientation(CameraTriple a, CameraTriple b, double lengthTolerance = 1e-6, double angleDegrees = 1e-4)
        {
            if (a == null || b == null) return false;
            if (Norm(Sub(a.Eye, b.Eye)) > lengthTolerance) return false;
            double cosTol = Math.Cos(angleDegrees * Math.PI / 180.0);
            return Dot(Unit(a.Forward), Unit(b.Forward)) >= cosTol && Dot(Unit(a.Up), Unit(b.Up)) >= cosTol;
        }

        private static CameraTriple Orient(double[] eye, double[] forward, double[] up)
        {
            bool given = up != null;
            double[] u = given ? up : new[] { 0.0, 0.0, 1.0 };
            if (given) Check(u, "up");
            double[] ortho = Sub(u, Scale(forward, Dot(u, forward)));
            double on = Norm(ortho);
            if (on < ParallelSine)
                throw new ArgumentException(given
                    ? "up is parallel to the line of sight: it cannot say which way is up."
                    : "the camera looks straight up or down: give an explicit up direction.");
            ortho = Scale(ortho, 1.0 / on);
            // Rounded BEFORE wrapping, so a full turn that lands a hair under 360 reads 0.
            double az = Math.Round(Math.Atan2(forward[1], forward[0]) * 180.0 / Math.PI, 9);
            if (az < 0) az += 360.0;
            if (az >= 360.0) az -= 360.0;
            double horizontal = Math.Sqrt(forward[0] * forward[0] + forward[1] * forward[1]);
            return new CameraTriple
            {
                Eye = (double[])eye.Clone(),
                Forward = forward,
                Up = ortho,
                AzimuthDegrees = az,
                PitchDegrees = Math.Round(Math.Atan2(forward[2], horizontal) * 180.0 / Math.PI, 9)
            };
        }

        private static void Check(double[] v, string name)
        {
            if (v == null || v.Length != 3) throw new ArgumentException(name + " must be [x, y, z].");
            foreach (double d in v)
                if (double.IsNaN(d) || double.IsInfinity(d)) throw new ArgumentException(name + " holds a non-finite number.");
        }

        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Norm(double[] a) => Math.Sqrt(Dot(a, a));
        private static double[] Unit(double[] a) { double n = Norm(a); return n > 0 ? Scale(a, 1.0 / n) : a; }
    }
}
