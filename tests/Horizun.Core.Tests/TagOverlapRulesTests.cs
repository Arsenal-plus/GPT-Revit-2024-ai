using Horizun.Revit.Core;
using Xunit;
using Rect = Horizun.Revit.Core.TagOverlapRules.Rect;

namespace Horizun.Core.Tests
{
    public class TagOverlapRulesTests
    {
        [Fact]
        public void Two_rectangles_stacked_on_top_of_each_other_overlap()
        {
            var a = Rect.Normalized(0, 0, 2, 1);
            var b = Rect.Normalized(0.5, 0, 2.5, 1);
            Assert.True(TagOverlapRules.Overlaps(a, b));
            Assert.True(TagOverlapRules.OverlapArea(a, b) > 0);
        }

        [Fact]
        public void Touching_edges_are_not_an_overlap()
        {
            var a = Rect.Normalized(0, 0, 1, 1);
            var b = Rect.Normalized(1, 0, 2, 1);
            Assert.False(TagOverlapRules.Overlaps(a, b));
            Assert.Equal(0, TagOverlapRules.OverlapArea(a, b));
        }

        [Fact]
        public void Far_apart_rectangles_do_not_overlap()
        {
            var a = Rect.Normalized(0, 0, 1, 1);
            var b = Rect.Normalized(10, 10, 11, 11);
            Assert.False(TagOverlapRules.Overlaps(a, b));
        }

        [Fact]
        public void Normalized_does_not_care_which_corner_came_first()
        {
            var a = Rect.Normalized(2, 2, 0, 0);
            Assert.Equal(0, a.MinX);
            Assert.Equal(0, a.MinY);
            Assert.Equal(2, a.MaxX);
            Assert.Equal(2, a.MaxY);
        }

        [Fact]
        public void One_rectangle_fully_inside_another_overlaps_by_the_smaller_area()
        {
            var outer = Rect.Normalized(0, 0, 10, 10);
            var inner = Rect.Normalized(2, 2, 4, 4);
            Assert.Equal(4, TagOverlapRules.OverlapArea(outer, inner));
        }
    }
}
