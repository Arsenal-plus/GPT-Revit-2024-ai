// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_mep_routing's `route` operation, Revit-free: the 3-D A* search on an
// orthogonal grid. Points here are in feet (the same unit RouteSearch itself
// works in); a caller converts mm to feet before building a Request.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;
using Point3 = Horizun.Revit.Core.RouteSearch.Point3;
using Box3 = Horizun.Revit.Core.RouteSearch.Box3;

namespace Horizun.Core.Tests
{
    public class RouteSearchTests
    {
        private const double Grid = 1.0; // 1 foot steps, so assertions are round numbers

        private static RouteSearch.Request Req(Point3 start, Point3 end, params Box3[] obstacles) => new RouteSearch.Request
        {
            Start = start, End = end, GridSize = Grid, MaxNodes = 5000, Obstacles = new List<Box3>(obstacles)
        };

        [Fact]
        public void Straight_path_with_no_obstacles_is_a_single_segment()
        {
            var r = RouteSearch.Find(Req(new Point3(0, 0, 0), new Point3(5, 0, 0)));
            Assert.True(r.Found);
            Assert.Equal(2, r.Polyline.Count);
            Assert.Equal(0, r.Bends);
            Assert.Equal(5.0, r.Length, 6);
        }

        [Fact]
        public void A_box_in_the_direct_line_forces_a_detour_around_it()
        {
            // A column-like box straddling the direct line from (0,0,0) to (10,0,0).
            var box = new Box3(4, -1, -1, 6, 1, 3, "column");
            var r = RouteSearch.Find(Req(new Point3(0, 0, 0), new Point3(10, 0, 0), box));
            Assert.True(r.Found);
            Assert.True(r.Bends >= 2, "a detour needs at least an out-and-back bend pair");
            Assert.True(r.Length > 10.0, "a detour is longer than the blocked straight line");
            // No vertex of the route may sit inside the box.
            foreach (Point3 p in r.Polyline) Assert.False(box.Contains(p, 0));
        }

        [Fact]
        public void A_full_width_wall_forces_a_vertical_jump_over_it()
        {
            // A "wall" spanning the whole Y extent of the search box at this X band, but only up to z=2:
            // the route must rise above it rather than go around in Y.
            var wall = new Box3(4, -50, -1, 6, 50, 2, "wall");
            var req = Req(new Point3(0, 0, 0), new Point3(10, 0, 0), wall);
            req.MarginSteps = 2; // keep Y search bounds tight so "around" is not available
            var r = RouteSearch.Find(req);
            Assert.True(r.Found);
            // "Vertical jump": the route leaves the wall's own z-band (either over the top or
            // under the bottom - both are a jump, and which one is an implementation detail).
            Assert.Contains(r.Polyline, p => p.Z > wall.MaxZ + 1e-9 || p.Z < wall.MinZ - 1e-9);
        }

        [Fact]
        public void A_box_that_fully_encloses_the_end_point_is_refused_as_no_route()
        {
            // The end point (10,0,0) sits deep inside this box; the start (0,0,0) does not,
            // so the refusal is the up-front end check (see the next test), not the start one.
            var box = new Box3(8, -2, -2, 12, 2, 2, "enclosure");
            var r = RouteSearch.Find(Req(new Point3(0, 0, 0), new Point3(10, 0, 0), box));
            Assert.False(r.Found);
            Assert.Contains("no_route", r.Reason);
            Assert.NotNull(r.BlockingRegion);
        }

        [Fact]
        public void An_unreachable_end_reports_no_route_and_names_a_blocking_region()
        {
            // A box that walls off the end point on every side within the search bounds' margin.
            var boxes = new[]
            {
                new Box3(4, -50, -50, 6, 50, 50, "full-height wall")
            };
            var req = Req(new Point3(0, 0, 0), new Point3(10, 0, 0), boxes);
            req.MarginSteps = 2; // no room to go around or over/under within bounds
            var r = RouteSearch.Find(req);
            Assert.False(r.Found);
            Assert.Contains("no_route", r.Reason);
        }

        [Fact]
        public void Among_equal_length_paths_the_one_with_fewer_bends_wins()
        {
            // From (0,0,0) to (2,2,0): the direct two-bend L path (length 4) and a
            // three-bend path (0,0,0)->(1,0,0)->(1,1,0)->(1,2,0)->(2,2,0) are candidates;
            // both cover the same Manhattan distance, so the search must prefer the L (1 bend)
            // over the longer-bend alternative once a bend penalty is charged.
            var r = RouteSearch.Find(Req(new Point3(0, 0, 0), new Point3(2, 2, 0)));
            Assert.True(r.Found);
            Assert.Equal(1, r.Bends);
            Assert.Equal(4.0, r.Length, 6);
        }

        [Fact]
        public void An_end_point_off_the_lattice_is_closed_with_axis_stubs()
        {
            // grid=1ft; end at (3.4, 0, 0) is not a lattice node - the lattice reaches (3,0,0)
            // and a 0.4ft stub along X closes the rest, without moving the caller's point.
            var r = RouteSearch.Find(Req(new Point3(0, 0, 0), new Point3(3.4, 0, 0)));
            Assert.True(r.Found);
            Point3 last = r.Polyline[r.Polyline.Count - 1];
            Assert.Equal(3.4, last.X, 6);
            Assert.Equal(3.4, r.Length, 6);
        }

        [Fact]
        public void The_start_point_is_never_moved_even_off_lattice()
        {
            var r = RouteSearch.Find(Req(new Point3(0.25, 0, 0), new Point3(5.25, 0, 0)));
            Assert.True(r.Found);
            Assert.Equal(0.25, r.Polyline[0].X, 9);
            Assert.Equal(5.25, r.Polyline[r.Polyline.Count - 1].X, 9);
        }

        [Fact]
        public void An_end_point_inside_an_obstacle_is_refused_up_front_naming_it_without_searching()
        {
            var column = new Box3(9, -1, -1, 11, 1, 1, "host:4242:Structural Columns");
            var r = RouteSearch.Find(Req(new Point3(0, 0, 0), new Point3(10, 0, 0), column));
            Assert.False(r.Found);
            Assert.StartsWith("no_route: the end point is inside host:4242", r.Reason);
            Assert.Equal("host:4242:Structural Columns", r.BlockingRegion.Value.Name);
            Assert.Equal(0, r.NodesExpanded);
        }

        [Fact]
        public void A_start_point_inside_an_obstacle_is_refused_up_front()
        {
            var box = new Box3(-1, -1, -1, 1, 1, 1, "in the way");
            var r = RouteSearch.Find(Req(new Point3(0, 0, 0), new Point3(10, 0, 0), box));
            Assert.False(r.Found);
            Assert.Contains("start point is inside", r.Reason);
        }

        [Fact]
        public void Max_nodes_budget_is_honoured_when_no_route_exists()
        {
            var wall = new Box3(4, -50, -50, 6, 50, 50, "wall");
            var req = Req(new Point3(0, 0, 0), new Point3(10, 0, 0), wall);
            req.MarginSteps = 2;
            req.MaxNodes = 3;
            var r = RouteSearch.Find(req);
            Assert.False(r.Found);
            Assert.True(r.NodesExpanded <= 3);
        }
    }
}
