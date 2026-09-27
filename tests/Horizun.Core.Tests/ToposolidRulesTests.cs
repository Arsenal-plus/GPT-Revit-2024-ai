using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ToposolidRulesTests
    {
        private static double[] P(double x, double y, double z) => new[] { x, y, z };
        private static string V(params double[][] p) => ToposolidRules.ValidatePoints(p.ToList(), 1e-3);

        [Fact] public void Too_few_points_are_refused() => Assert.Contains("at least 3", V(P(0, 0, 0), P(1, 0, 0)));
        [Fact] public void Points_on_one_plan_line_bound_no_area() => Assert.Contains("one line", V(P(0, 0, 0), P(1, 1, 5), P(2, 2, 1), P(-4, -4, 2)));
        [Fact] public void Two_heights_at_one_plan_point_are_refused() => Assert.Contains("share one X,Y", V(P(0, 0, 0), P(10, 0, 0), P(0, 10, 0), P(0, 0, 3)));
        [Fact] public void A_non_finite_coordinate_is_refused() => Assert.Contains("not finite", V(P(0, 0, 0), P(10, 0, double.NaN), P(0, 10, 0)));
        [Fact] public void A_surface_is_accepted() => Assert.Null(V(P(0, 0, 1), P(10, 0, 2), P(10, 10, 3), P(0, 10, 1.5), P(5, 5, 4)));

        [Fact]
        public void The_cap_is_the_callers_and_a_file_takes_more()
        {
            var grid = Enumerable.Range(0, 150).Select(i => P(i % 15, i / 15, i)).ToList();
            Assert.Contains("at most 100", ToposolidRules.ValidatePoints(grid, 1e-3));
            Assert.Null(ToposolidRules.ValidatePoints(grid, 1e-3, LandXmlTinRules.MaxFilePoints));
        }

        [Fact]
        public void A_repeated_plan_point_is_found_among_thousands()
        {
            var grid = Enumerable.Range(0, 5000).Select(i => P(i % 100, i / 100, 0)).ToList();
            grid.Add(P(37, 21, 9));
            Assert.Contains("points[2137] and points[5000]", ToposolidRules.ValidatePoints(grid, 1e-3, 10000));
        }

        [Fact]
        public void Few_points_are_all_sampled()
            => Assert.Equal(new[] { 0, 1, 2, 3 }, ToposolidRules.SampleIndices(new List<double[]> { P(0, 0, 0), P(1, 0, 0), P(0, 1, 0), P(1, 1, 1) }));

        [Fact]
        public void Many_points_sample_the_extremes_within_the_cap()
        {
            var pts = Enumerable.Range(0, 300).Select(i => P(i, i % 7, i == 137 ? 99 : i == 211 ? -5 : 1)).ToList();
            List<int> s = ToposolidRules.SampleIndices(pts, 50);
            Assert.True(s.Count <= 50 && s.Count >= 40, "count " + s.Count);
            Assert.Contains(137, s); Assert.Contains(211, s); Assert.Contains(0, s); Assert.Contains(299, s);
            Assert.Equal(s.OrderBy(x => x).ToList(), s);
            Assert.Equal(s.Count, s.Distinct().Count());
        }
    }
}
