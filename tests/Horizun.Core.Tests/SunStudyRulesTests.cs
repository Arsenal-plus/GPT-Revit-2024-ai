using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SunStudyRulesTests
    {
        [Theory]
        [InlineData("still", SunStudyKind.Still)]
        [InlineData("single_day", SunStudyKind.SingleDay)]
        [InlineData("Single-Day", SunStudyKind.SingleDay)]
        [InlineData("multi_day", SunStudyKind.MultiDay)]
        public void Kind_names_are_the_three_revit_studies(string text, SunStudyKind kind) =>
            Assert.Equal(kind, SunStudyRules.ParseKind(text));

        [Fact]
        public void Lighting_or_an_unknown_type_is_refused_by_name()
        {
            var ex = Assert.Throws<ArgumentException>(() => SunStudyRules.ParseKind("lighting"));
            Assert.Contains("still, single_day or multi_day", ex.Message);
        }

        [Fact]
        public void An_instant_with_an_offset_becomes_the_same_instant_in_utc()
        {
            DateTime utc = SunStudyRules.ParseInstant("2026-06-21T12:00:00-05:00", "start");
            Assert.Equal(DateTimeKind.Utc, utc.Kind);
            Assert.Equal(new DateTime(2026, 6, 21, 17, 0, 0, DateTimeKind.Utc), utc);
            Assert.Equal(new DateTime(2026, 6, 21, 9, 30, 0, DateTimeKind.Utc), SunStudyRules.ParseInstant("2026-06-21T09:30Z", "start"));
        }

        [Theory]
        [InlineData("2026-06-21T12:00:00")]
        [InlineData("2026-06-21")]
        [InlineData("21/06/2026 12:00 -05:00")]
        [InlineData("2026-06-21T12:00:00+0500")]
        public void An_instant_without_an_offset_is_refused_not_guessed(string text)
        {
            var ex = Assert.Throws<ArgumentException>(() => SunStudyRules.ParseInstant(text, "start"));
            Assert.Contains("offset", ex.Message);
        }

        [Fact]
        public void A_still_sun_is_one_instant_and_refuses_an_end()
        {
            SunStudyRequest r = SunStudyRules.Parse("still", "2026-06-21T12:00:00Z", null, null, null);
            Assert.Equal(SunStudyKind.Still, r.Kind);
            Assert.Null(r.EndUtc);
            Assert.False(r.MovesSite);
            var ex = Assert.Throws<ArgumentException>(() => SunStudyRules.Parse("still", "2026-06-21T12:00:00Z", "2026-06-21T13:00:00Z", null, null));
            Assert.Contains("ONE instant", ex.Message);
        }

        [Fact]
        public void A_study_needs_an_end_after_its_start()
        {
            Assert.Contains("needs sun.end", Assert.Throws<ArgumentException>(() =>
                SunStudyRules.Parse("single_day", "2026-06-21T06:00:00Z", null, null, null)).Message);
            Assert.Contains("after sun.start", Assert.Throws<ArgumentException>(() =>
                SunStudyRules.Parse("multi_day", "2026-06-21T06:00:00Z", "2026-06-21T06:00:00Z", null, null)).Message);
        }

        [Fact]
        public void A_single_day_study_longer_than_a_day_is_a_multi_day_one()
        {
            Assert.Contains("multi_day", Assert.Throws<ArgumentException>(() =>
                SunStudyRules.Parse("single_day", "2026-06-21T06:00:00Z", "2026-06-22T07:00:00Z", null, null)).Message);
            SunStudyRequest r = SunStudyRules.Parse("multi_day", "2026-06-21T06:00:00Z", "2026-12-21T18:00:00Z", null, null);
            Assert.Equal(new DateTime(2026, 12, 21, 18, 0, 0, DateTimeKind.Utc), r.EndUtc);
        }

        [Fact]
        public void Latitude_and_longitude_come_together_and_within_range()
        {
            Assert.Contains("go together", Assert.Throws<ArgumentException>(() =>
                SunStudyRules.Parse("still", "2026-06-21T12:00:00Z", null, 4.6, null)).Message);
            Assert.Contains("-90..90", Assert.Throws<ArgumentException>(() =>
                SunStudyRules.Parse("still", "2026-06-21T12:00:00Z", null, 91, 0)).Message);
            Assert.Contains("-180..180", Assert.Throws<ArgumentException>(() =>
                SunStudyRules.Parse("still", "2026-06-21T12:00:00Z", null, 0, 181)).Message);
            SunStudyRequest r = SunStudyRules.Parse("still", "2026-06-21T12:00:00Z", null, 4.6, -74.1);
            Assert.True(r.MovesSite);
            Assert.Equal(-74.1, r.LongitudeDegrees);
        }

        [Fact]
        public void The_reread_test_takes_a_minute_of_rounding_but_not_a_wrong_hour()
        {
            var wanted = new DateTime(2026, 6, 21, 17, 0, 0, DateTimeKind.Utc);
            Assert.True(SunStudyRules.SameInstant(wanted, wanted.AddSeconds(59)));
            Assert.False(SunStudyRules.SameInstant(wanted, wanted.AddHours(1)));
            // Revit documents its output as UTC: an Unspecified kind is not shifted by this machine's zone.
            Assert.True(SunStudyRules.SameInstant(wanted, DateTime.SpecifyKind(wanted, DateTimeKind.Unspecified)));
            Assert.Equal("2026-06-21T17:00:00Z", SunStudyRules.Iso(wanted));
        }

        [Fact]
        public void Angles_convert_and_compare_in_radians()
        {
            double rad = SunStudyRules.ToRadians(-74.1);
            Assert.Equal(-74.1, SunStudyRules.ToDegrees(rad), 12);
            Assert.True(SunStudyRules.SameAngle(rad, rad + 5e-8));
            Assert.False(SunStudyRules.SameAngle(rad, rad + 1e-5));
        }
    }
}
