using System;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PerspectiveRulesTests
    {
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        [Fact]
        public void Forward_is_the_unit_sight_line_and_up_is_made_perpendicular()
        {
            CameraTriple c = PerspectiveRules.FromEyeTarget(new[] { 0.0, 0, 0 }, new[] { 10.0, 0, 5 });
            Assert.Equal(2 / Math.Sqrt(5), c.Forward[0], 12);
            Assert.Equal(1 / Math.Sqrt(5), c.Forward[2], 12);
            Assert.Equal(0, Dot(c.Forward, c.Up), 12);
            Assert.Equal(1, Math.Sqrt(Dot(c.Up, c.Up)), 12);
            Assert.True(c.Up[2] > 0);
            Assert.Equal(0, c.AzimuthDegrees, 9);
            Assert.Equal(Math.Atan2(1, 2) * 180 / Math.PI, c.PitchDegrees, 9);
        }

        [Fact]
        public void An_eye_on_its_target_is_refused()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                PerspectiveRules.FromEyeTarget(new[] { 1.0, 1, 1 }, new[] { 1.0, 1, 1 + 1e-4 }));
            Assert.Contains("coincide", ex.Message);
        }

        [Fact]
        public void Looking_straight_down_needs_an_explicit_up()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                PerspectiveRules.FromEyeTarget(new[] { 0.0, 0, 10 }, new[] { 0.0, 0, 0 }));
            Assert.Contains("explicit up", ex.Message);
            CameraTriple c = PerspectiveRules.FromEyeTarget(new[] { 0.0, 0, 10 }, new[] { 0.0, 0, 0 }, new[] { 0.0, 1, 0 });
            Assert.Equal(1, c.Up[1], 12);
            Assert.Equal(-90, c.PitchDegrees, 9);
        }

        [Fact]
        public void A_fan_turns_the_azimuth_in_equal_steps_and_keeps_the_pitch()
        {
            var fan = PerspectiveRules.Fan(new[] { 0.0, 0, 5 }, new[] { 0.0, 10, 5 }, 4);
            Assert.Equal(4, fan.Count);
            Assert.Equal(new[] { 90.0, 180, 270, 0 }, fan.Select(c => Math.Round(c.AzimuthDegrees, 6)).ToArray());
            Assert.All(fan, c => Assert.Equal(0, c.PitchDegrees, 9));
            Assert.All(fan, c => Assert.Equal(0, Dot(c.Forward, c.Up), 12));
            Assert.All(fan, c => Assert.Equal(new[] { 0.0, 0, 5 }, c.Eye));
        }

        [Fact]
        public void A_fan_outside_1_to_36_or_from_a_vertical_sight_is_refused()
        {
            Assert.Throws<ArgumentException>(() => PerspectiveRules.Fan(new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, 0));
            Assert.Throws<ArgumentException>(() => PerspectiveRules.Fan(new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, 37));
            Assert.Throws<ArgumentException>(() =>
                PerspectiveRules.Fan(new[] { 0.0, 0, 10 }, new[] { 0.0, 0, 0 }, 3, new[] { 0.0, 1, 0 }));
        }

        [Fact]
        public void The_reread_test_tolerates_noise_and_rejects_a_degree()
        {
            CameraTriple a = PerspectiveRules.FromEyeTarget(new[] { 3.0, 4, 5 }, new[] { 13.0, 4, 5 });
            var noisy = new CameraTriple
            {
                Eye = new[] { 3.0 + 1e-9, 4, 5 },
                Forward = new[] { 1.0, 1e-9, 0 },
                Up = new[] { 0.0, 0, 1 }
            };
            Assert.True(PerspectiveRules.SameOrientation(a, noisy));
            double r = Math.PI / 180;
            var turned = new CameraTriple { Eye = a.Eye, Forward = new[] { Math.Cos(r), Math.Sin(r), 0 }, Up = a.Up };
            Assert.False(PerspectiveRules.SameOrientation(a, turned));
            Assert.False(PerspectiveRules.SameOrientation(a, null));
        }

        [Fact]
        public void A_fan_names_every_view_by_a_distinct_whole_degree_azimuth()
        {
            var cams = PerspectiveRules.Fan(new[] { 0.0, 0, 0 }, new[] { 0.0, 10, 0 }, 4);
            Assert.Equal(new[] { "Cam az090", "Cam az180", "Cam az270", "Cam az000" },
                         cams.Select(c => PerspectiveRules.ViewName("Cam", c, cams.Count)).ToArray());
            // 36 azimuths 10 degrees apart from an awkward start still round to 36 names.
            var many = PerspectiveRules.Fan(new[] { 0.0, 0, 0 }, new[] { 10.0, 0.9, 0 }, PerspectiveRules.MaxFan);
            Assert.Equal(many.Count, many.Select(c => PerspectiveRules.ViewName("Cam", c, many.Count)).Distinct().Count());
            Assert.Equal("Cam", PerspectiveRules.ViewName("Cam", cams[0], 1));
            Assert.Null(PerspectiveRules.ViewName("  ", cams[0], 4));
        }
    }
}
