using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DataOnlyGeometryRulesTests
    {
        private static double[] Box(double x = 0, double y = 0, double z = 0) =>
            new[] { x, y, z, x + 1, y + 1, z + 1 };

        [Fact]
        public void A_known_element_that_moved_beyond_tolerance_is_always_checked()
        {
            var o = DataOnlyGeometryRules.Decide(new[]
            {
                new DataOnlyGeometryRules.Candidate { Id = 1, Physical = true, Previous = Box(0, 0, 0), Current = Box(5, 0, 0) }
            });
            Assert.Equal(new List<long> { 1 }, o.Subjects);
            Assert.Equal(1, o.Moved);
            Assert.Null(o.ScopeNote);
        }

        [Fact]
        public void A_known_element_that_did_not_move_is_not_checked()
        {
            var o = DataOnlyGeometryRules.Decide(new[]
            {
                new DataOnlyGeometryRules.Candidate { Id = 1, Physical = true, Previous = Box(0, 0, 0), Current = Box(0, 0, 0) }
            });
            Assert.Empty(o.Subjects);
            Assert.Equal(0, o.Moved);
        }

        [Fact]
        public void Jitter_under_the_tolerance_is_not_a_move()
        {
            Assert.False(DataOnlyGeometryRules.Moved(Box(0, 0, 0), Box(0.001, 0, 0)));
            Assert.True(DataOnlyGeometryRules.Moved(Box(0, 0, 0), Box(0.5, 0, 0)));
        }

        [Fact]
        public void A_non_physical_element_is_never_checked_even_if_it_lacks_a_previous_box()
        {
            var o = DataOnlyGeometryRules.Decide(new[]
            {
                new DataOnlyGeometryRules.Candidate { Id = 1, Physical = false, Previous = null, Current = null }
            });
            Assert.Empty(o.Subjects);
            Assert.Equal(1, o.NotPhysical);
            Assert.Null(o.ScopeNote);
        }

        [Fact]
        public void Unknown_elements_at_or_under_the_cap_are_all_checked_and_the_scope_says_so()
        {
            var candidates = new List<DataOnlyGeometryRules.Candidate>();
            for (long i = 0; i < DataOnlyGeometryRules.UnknownFallbackCap; i++)
                candidates.Add(new DataOnlyGeometryRules.Candidate { Id = i, Physical = true, Previous = null, Current = Box(i, 0, 0) });
            var o = DataOnlyGeometryRules.Decide(candidates);
            Assert.Equal(DataOnlyGeometryRules.UnknownFallbackCap, o.Subjects.Count);
            Assert.Equal(DataOnlyGeometryRules.UnknownFallbackCap, o.UnknownIncluded);
            Assert.Equal(0, o.UnknownSkipped);
            Assert.Contains(DataOnlyGeometryRules.UnknownFallbackCap.ToString(), o.ScopeNote);
        }

        [Fact]
        public void Unknown_elements_over_the_cap_are_skipped_whole_never_partially()
        {
            var candidates = new List<DataOnlyGeometryRules.Candidate>();
            int total = DataOnlyGeometryRules.UnknownFallbackCap + 1;
            for (long i = 0; i < total; i++)
                candidates.Add(new DataOnlyGeometryRules.Candidate { Id = i, Physical = true, Previous = null, Current = Box(i, 0, 0) });
            var o = DataOnlyGeometryRules.Decide(candidates);
            Assert.Empty(o.Subjects);
            Assert.Equal(total, o.UnknownSkipped);
            Assert.Equal(0, o.UnknownIncluded);
            Assert.Contains("skipped", o.ScopeNote);
        }

        [Fact]
        public void An_element_with_no_current_box_is_neither_moved_nor_unknown_fallback()
        {
            var o = DataOnlyGeometryRules.Decide(new[]
            {
                new DataOnlyGeometryRules.Candidate { Id = 1, Physical = true, Previous = Box(), Current = null }
            });
            Assert.Empty(o.Subjects);
            Assert.Equal(1, o.NoCurrentBox);
            Assert.Null(o.ScopeNote);
        }

        [Fact]
        public void A_mismatched_box_shape_is_never_reported_as_moved()
        {
            Assert.False(DataOnlyGeometryRules.Moved(new double[] { 1, 2, 3 }, Box()));
            Assert.False(DataOnlyGeometryRules.Moved(null, Box()));
        }
    }
}
