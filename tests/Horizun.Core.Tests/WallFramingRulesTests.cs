// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_framing operation=wall, the Revit-free half: stud stations, end
// studs, openings (king/jack/header/sill/cripples), tracks and blocking in the
// wall's own frame. Millimetres throughout; 41 mm is a 1-5/8" stud flange.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WallFramingRulesTests
    {
        private const int Budget = 5000;
        private const double W = 41;

        private static WallFramingInput Wall(double length, double height = 2700, double spacing = 400, string rule = "wall_start")
            => new WallFramingInput
            {
                Length = length, Height = height, StudSpacing = spacing, StudWidth = W, StartRule = rule,
                StudTypeKey = "stud", BottomTrackTypeKey = "track", TopTrackTypeKey = "track",
            };

        private static int Count(WallFramingPlan plan, string role) => plan.CountByRole().TryGetValue(role, out int n) ? n : 0;

        private static List<double> Xs(WallFramingPlan plan, string role)
            => plan.Members.Where(m => m.Role == role).Select(m => m.X0).OrderBy(x => x).ToList();

        private static void AssertSound(WallFramingPlan plan, WallFramingInput input)
        {
            Assert.Null(plan.Refusal);
            var verticals = plan.Members.Where(m => FramingRoles.IsVertical(m.Role)).ToList();
            foreach (FramingMember v in verticals)
            {
                Assert.Equal(v.X0, v.X1, 9);
                Assert.True(v.X0 - W / 2 >= -1e-6 && v.X0 + W / 2 <= input.Length + 1e-6, v.Role + " outside the wall at " + v.X0);
                Assert.False(WallFramingRules.CrossesOpening(v.X0, Math.Min(v.Z0, v.Z1), Math.Max(v.Z0, v.Z1), W, input.Openings),
                    v.Role + " at " + v.X0 + " crosses an opening");
            }
            for (int i = 0; i < verticals.Count; i++)
                for (int j = i + 1; j < verticals.Count; j++)
                {
                    FramingMember a = verticals[i], b = verticals[j];
                    bool zOverlap = a.Z0 < b.Z1 - 1e-6 && b.Z0 < a.Z1 - 1e-6;
                    Assert.False(zOverlap && Math.Abs(a.X0 - b.X0) < W - 1e-6, a.Role + "@" + a.X0 + " overlaps " + b.Role + "@" + b.X0);
                }
        }

        [Fact]
        public void Plain_wall_gets_end_studs_layout_studs_and_two_tracks()
        {
            var input = Wall(3000);
            WallFramingPlan plan = WallFramingRules.Plan(input, Budget);
            AssertSound(plan, input);
            Assert.Equal(new[] { 20.5, 400, 800, 1200, 1600, 2000, 2400, 2800, 2979.5 }, Xs(plan, FramingRoles.Stud));
            Assert.Equal(2, Count(plan, FramingRoles.Track));
            FramingMember top = plan.Members.Last(m => m.Role == FramingRoles.Track);
            Assert.Equal(2700, top.Z0, 9);
            Assert.Equal(0, top.X0, 9);
            Assert.Equal(3000, top.X1, 9);
            FramingMember stud = plan.Members.First(m => m.Role == FramingRoles.Stud);
            Assert.Equal(0, stud.Z0, 9);
            Assert.Equal(2700, stud.Z1, 9);
        }

        [Fact]
        public void Track_thickness_shortens_the_studs_and_moves_the_track_axes()
        {
            var input = Wall(1200);
            input.BottomTrackThickness = 38;
            input.TopTrackThickness = 76;
            WallFramingPlan plan = WallFramingRules.Plan(input, Budget);
            FramingMember stud = plan.Members.First(m => m.Role == FramingRoles.Stud);
            Assert.Equal(38, stud.Z0, 9);
            Assert.Equal(2700 - 76, stud.Z1, 9);
            var tracks = plan.Members.Where(m => m.Role == FramingRoles.Track).Select(m => m.Z0).OrderBy(z => z).ToList();
            Assert.Equal(new[] { 19.0, 2700 - 38.0 }, tracks);
        }

        [Fact]
        public void Wall_shorter_than_one_bay_gets_only_its_two_end_studs()
        {
            var input = Wall(300);
            WallFramingPlan plan = WallFramingRules.Plan(input, Budget);
            AssertSound(plan, input);
            Assert.Equal(new[] { 20.5, 279.5 }, Xs(plan, FramingRoles.Stud));
            Assert.Equal(2, Count(plan, FramingRoles.Track));
        }

        [Fact]
        public void Wall_narrower_than_two_studs_gets_one_and_narrower_than_one_refuses()
        {
            WallFramingPlan one = WallFramingRules.Plan(Wall(60), Budget);
            Assert.Single(Xs(one, FramingRoles.Stud));
            Assert.Equal("wall_shorter_than_one_stud", WallFramingRules.Plan(Wall(30), Budget).Refusal);
        }

        [Fact]
        public void Door_gets_kings_jacks_header_cripples_and_a_cut_bottom_track()
        {
            var input = Wall(3000);
            input.Openings.Add(new WallOpeningSpan { Id = "door", Start = 1000, End = 1900, Sill = 0, Head = 2100 });
            WallFramingPlan plan = WallFramingRules.Plan(input, Budget);
            AssertSound(plan, input);

            Assert.Equal(new[] { 979.5, 1920.5 }, Xs(plan, FramingRoles.Jack));
            Assert.Equal(new[] { 938.5, 1961.5 }, Xs(plan, FramingRoles.King));
            // 1200 and 1600 fell in the door (they become cripples); 2000 collides with the right king.
            Assert.Equal(new[] { 20.5, 400, 800, 2400, 2800, 2979.5 }, Xs(plan, FramingRoles.Stud));
            Assert.Equal(new[] { 1200.0, 1600.0 }, Xs(plan, FramingRoles.Cripple));
            Assert.All(plan.Members.Where(m => m.Role == FramingRoles.Cripple), c => { Assert.Equal(2100, c.Z0, 9); Assert.Equal(2700, c.Z1, 9); });
            Assert.All(plan.Members.Where(m => m.Role == FramingRoles.Jack), j => Assert.Equal(2100, j.Z1, 9));

            FramingMember header = Assert.Single(plan.Members, m => m.Role == FramingRoles.Header);
            Assert.Equal(959, header.X0, 9);
            Assert.Equal(1941, header.X1, 9);
            Assert.Equal(2100, header.Z0, 9);
            Assert.Equal(0, Count(plan, FramingRoles.Sill));

            // Bottom track cut across the door, top track whole.
            var bottom = plan.Members.Where(m => m.Role == FramingRoles.Track && m.Z0 < 1).Select(m => new[] { m.X0, m.X1 }).ToList();
            Assert.Equal(2, bottom.Count);
            Assert.Equal(new[] { 0.0, 1000.0 }, bottom[0]);
            Assert.Equal(new[] { 1900.0, 3000.0 }, bottom[1]);
            Assert.Equal(3, Count(plan, FramingRoles.Track));
        }

        [Fact]
        public void Window_with_sill_gets_a_sill_and_cripples_above_and_below_at_the_cripple_spacing()
        {
            var input = Wall(3600);
            input.CrippleSpacing = 400;
            input.Openings.Add(new WallOpeningSpan { Id = "win", Start = 1000, End = 2200, Sill = 900, Head = 2100 });
            WallFramingPlan plan = WallFramingRules.Plan(input, Budget);
            AssertSound(plan, input);

            FramingMember sill = Assert.Single(plan.Members, m => m.Role == FramingRoles.Sill);
            Assert.Equal(1000, sill.X0, 9);
            Assert.Equal(2200, sill.X1, 9);
            Assert.Equal(900, sill.Z0, 9);
            Assert.Equal(1, Count(plan, FramingRoles.Header));
            var cripples = plan.Members.Where(m => m.Role == FramingRoles.Cripple).ToList();
            Assert.Equal(4, cripples.Count);
            Assert.Equal(new[] { 1400.0, 1800.0 }, cripples.Where(c => c.Z0 >= 2100).Select(c => c.X0).OrderBy(x => x));
            Assert.Equal(new[] { 1400.0, 1800.0 }, cripples.Where(c => c.Z1 <= 900).Select(c => c.X0).OrderBy(x => x));
            // A window does not cut the bottom track.
            Assert.Equal(2, Count(plan, FramingRoles.Track));
        }

        [Fact]
        public void Two_openings_close_together_share_the_pier_without_overlapping_members()
        {
            var input = Wall(3600);
            input.Openings.Add(new WallOpeningSpan { Id = "a", Start = 1000, End = 1800, Sill = 0, Head = 2100 });
            input.Openings.Add(new WallOpeningSpan { Id = "b", Start = 1900, End = 2500, Sill = 900, Head = 2100 });
            WallFramingPlan plan = WallFramingRules.Plan(input, Budget);
            AssertSound(plan, input);
            Assert.Equal(4, Count(plan, FramingRoles.Jack));
            // Both inner kings collide with the other opening's jack: dropped and reported.
            Assert.Equal(new[] { 938.5, 2561.5 }, Xs(plan, FramingRoles.King));
            Assert.Contains("king_merged:a", plan.Warnings);
            Assert.Contains("king_merged:b", plan.Warnings);
            Assert.Equal(2, Count(plan, FramingRoles.Header));
        }

        [Fact]
        public void Overlapping_openings_refuse()
        {
            var input = Wall(3600);
            input.Openings.Add(new WallOpeningSpan { Id = "a", Start = 1000, End = 1800, Sill = 0, Head = 2100 });
            input.Openings.Add(new WallOpeningSpan { Id = "b", Start = 1700, End = 2500, Sill = 900, Head = 2100 });
            Assert.StartsWith("openings_overlap", WallFramingRules.Plan(input, Budget).Refusal);
        }

        [Fact]
        public void Start_rules_measure_from_the_start_the_end_or_the_middle()
        {
            Assert.Equal(new[] { 20.5, 200, 600, 1000, 1400, 1800, 2200, 2600, 2979.5 }, Xs(WallFramingRules.Plan(Wall(3000, rule: "wall_end"), Budget), FramingRoles.Stud));
            List<double> centred = Xs(WallFramingRules.Plan(Wall(3000, rule: "centred"), Budget), FramingRoles.Stud);
            Assert.Equal(new[] { 20.5, 300, 700, 1100, 1500, 1900, 2300, 2700, 2979.5 }, centred);
            for (int i = 0; i < centred.Count; i++) Assert.Equal(3000, centred[i] + centred[centred.Count - 1 - i], 9);
            Assert.Equal("unknown_start_rule", WallFramingRules.Plan(Wall(3000, rule: "left"), Budget).Refusal);
        }

        [Fact]
        public void Max_first_bay_adds_a_stud_next_to_each_end_bay_that_is_too_wide()
        {
            var input = Wall(3000, spacing: 800, rule: "centred");
            Assert.Equal(new[] { 20.5, 700, 1500, 2300, 2979.5 }, Xs(WallFramingRules.Plan(input, Budget), FramingRoles.Stud));
            input.MaxFirstBay = 500;
            Assert.Equal(new[] { 20.5, 520.5, 700, 1500, 2300, 2479.5, 2979.5 }, Xs(WallFramingRules.Plan(input, Budget), FramingRoles.Stud));
        }

        [Fact]
        public void Doubled_ends_add_a_second_stud_inside_each_end()
        {
            var input = Wall(3000);
            input.DoubleAtEnds = true;
            WallFramingPlan plan = WallFramingRules.Plan(input, Budget);
            AssertSound(plan, input);
            Assert.Equal(new[] { 20.5, 61.5, 400, 800, 1200, 1600, 2000, 2400, 2800, 2938.5, 2979.5 }, Xs(plan, FramingRoles.Stud));
        }

        [Fact]
        public void Two_kings_per_side_stand_outside_the_first()
        {
            var input = Wall(3000);
            input.KingStuds = 2;
            input.Openings.Add(new WallOpeningSpan { Id = "door", Start = 1000, End = 1900, Sill = 0, Head = 2100 });
            WallFramingPlan plan = WallFramingRules.Plan(input, Budget);
            AssertSound(plan, input);
            Assert.Equal(new[] { 897.5, 938.5, 1961.5, 2002.5 }, Xs(plan, FramingRoles.King));
        }

        [Fact]
        public void Blocking_is_split_at_studs_and_interrupted_across_the_opening()
        {
            var input = Wall(3000);
            input.Openings.Add(new WallOpeningSpan { Id = "door", Start = 1000, End = 1900, Sill = 0, Head = 2100 });
            input.Blocking.Add(new BlockingRow { Height = 1200, TypeKey = "block" });
            input.Blocking.Add(new BlockingRow { Height = 2400, TypeKey = "block" });
            WallFramingPlan plan = WallFramingRules.Plan(input, Budget);
            AssertSound(plan, input);
            var low = plan.Members.Where(m => m.Role == FramingRoles.Blocking && m.Z0 == 1200).ToList();
            var high = plan.Members.Where(m => m.Role == FramingRoles.Blocking && m.Z0 == 2400).ToList();
            // Verticals through 1200: 20.5 400 800 938.5 979.5 | 1920.5 1961.5 2400 2800 2979.5 - the
            // king/jack pairs touch (no piece), the door interrupts the row.
            Assert.Equal(6, low.Count);
            Assert.DoesNotContain(low, b => b.X0 < 1900 && b.X1 > 1000);
            // Through 2400 the cripples above the header also split the row: no interruption.
            Assert.Equal(9, high.Count);
            Assert.All(plan.Members.Where(m => m.Role == FramingRoles.Blocking), b => Assert.Equal("block", b.TypeKey));
        }

        [Fact]
        public void Header_and_sill_depths_seat_the_header_on_the_head_and_the_sill_under_the_sill_line()
        {
            var input = Wall(3600);
            input.CrippleSpacing = 400;
            input.HeaderDepth = 150;
            input.SillDepth = 90;
            input.Openings.Add(new WallOpeningSpan { Id = "win", Start = 1000, End = 2200, Sill = 900, Head = 2100 });
            // A blocking row inside the header's depth: before the depth it ran king to king through the header.
            input.Blocking.Add(new BlockingRow { Height = 2200, TypeKey = "block" });
            WallFramingPlan plan = WallFramingRules.Plan(input, Budget);
            AssertSound(plan, input);

            FramingMember header = Assert.Single(plan.Members, m => m.Role == FramingRoles.Header);
            Assert.Equal(2175, header.Z0, 9);
            FramingMember sill = Assert.Single(plan.Members, m => m.Role == FramingRoles.Sill);
            Assert.Equal(855, sill.Z0, 9);
            var cripples = plan.Members.Where(m => m.Role == FramingRoles.Cripple).ToList();
            Assert.Equal(4, cripples.Count);
            Assert.All(cripples.Where(c => c.Z1 > 2000), c => { Assert.Equal(2250, c.Z0, 9); Assert.Equal(2700, c.Z1, 9); });
            Assert.All(cripples.Where(c => c.Z1 < 2000), c => { Assert.Equal(0, c.Z0, 9); Assert.Equal(810, c.Z1, 9); });
            Assert.DoesNotContain(plan.Members, m => m.Role == FramingRoles.Blocking && m.X0 < 1600 && m.X1 > 1600);
            Assert.DoesNotContain(plan.Warnings, x => x.StartsWith("no_header_depth") || x.StartsWith("no_sill_depth"));

            // Without depths the axes sit on the lines, and the plan says so.
            input.HeaderDepth = 0;
            input.SillDepth = 0;
            WallFramingPlan onLine = WallFramingRules.Plan(input, Budget);
            Assert.Equal(2100, Assert.Single(onLine.Members, m => m.Role == FramingRoles.Header).Z0, 9);
            Assert.Contains("no_header_depth:header_axis_on_head_line", onLine.Warnings);
            Assert.Contains("no_sill_depth:sill_axis_on_sill_line", onLine.Warnings);
        }

        [Fact]
        public void A_header_or_sill_that_does_not_fit_between_the_tracks_is_not_placed_and_is_named()
        {
            var input = Wall(3000);
            input.HeaderDepth = 150;
            input.SillDepth = 200;
            input.Openings.Add(new WallOpeningSpan { Id = "door", Start = 400, End = 1300, Sill = 0, Head = 2600 });
            input.Openings.Add(new WallOpeningSpan { Id = "win", Start = 1800, End = 2600, Sill = 150, Head = 2000 });
            WallFramingPlan plan = WallFramingRules.Plan(input, Budget);
            AssertSound(plan, input);

            Assert.Contains("header_does_not_fit_below_top_track:door", plan.Warnings);
            Assert.Contains("sill_does_not_fit_above_bottom_track:win", plan.Warnings);
            FramingMember header = Assert.Single(plan.Members, m => m.Role == FramingRoles.Header);
            Assert.Equal(1, header.Source);
            Assert.Equal(0, Count(plan, FramingRoles.Sill));
            // No cripples over the door (no header) and none under the window (no sill).
            Assert.DoesNotContain(plan.Members, m => m.Role == FramingRoles.Cripple && m.Source == 0);
            Assert.DoesNotContain(plan.Members, m => m.Role == FramingRoles.Cripple && m.Source == 1 && m.Z1 < 1000);
        }

        [Fact]
        public void A_spacing_in_the_wrong_unit_refuses_before_allocating()
        {
            var input = Wall(30000, spacing: 42);
            input.StudWidth = 41;
            Assert.Equal("over_budget", WallFramingRules.Plan(input, 100).Refusal);
            Assert.Equal("spacing_must_exceed_stud_width", WallFramingRules.Plan(Wall(3000, spacing: 0.4), Budget).Refusal);
        }

        [Fact]
        public void A_cripple_over_a_door_is_cut_around_a_vent_stacked_above_it()
        {
            var input = Wall(3000);
            input.Openings.Add(new WallOpeningSpan { Id = "door", Start = 1000, End = 1900, Sill = 0, Head = 2100 });
            input.Openings.Add(new WallOpeningSpan { Id = "vent", Start = 1200, End = 1700, Sill = 2300, Head = 2500 });
            WallFramingPlan plan = WallFramingRules.Plan(input, Budget);
            AssertSound(plan, input);

            Assert.Contains("cripple_cut_by_opening:door:vent", plan.Warnings);
            var cripples = plan.Members.Where(m => m.Role == FramingRoles.Cripple).ToList();
            // Door-to-vent and vent-to-top pieces at 1200 and 1600, each planned once.
            Assert.Equal(4, cripples.Count);
            Assert.Equal(2, cripples.Count(m => Math.Abs(m.Z0 - 2100) < 1e-6 && Math.Abs(m.Z1 - 2300) < 1e-6));
            Assert.Equal(2, cripples.Count(m => Math.Abs(m.Z0 - 2500) < 1e-6 && Math.Abs(m.Z1 - 2700) < 1e-6));
            Assert.DoesNotContain(plan.Warnings, w => w.StartsWith("cripple_overlaps_member_dropped", StringComparison.Ordinal));
        }

        [Fact]
        public void Cripples_at_a_spacing_barely_above_the_stud_width_never_overlap()
        {
            var input = Wall(3000);
            input.CrippleSpacing = 50;
            input.Openings.Add(new WallOpeningSpan { Id = "win", Start = 1000, End = 1101, Sill = 900, Head = 2000 });
            WallFramingPlan plan = WallFramingRules.Plan(input, Budget);
            AssertSound(plan, input);

            Assert.Contains("cripple_overlaps_member_dropped:win", plan.Warnings);
            Assert.Equal(2, Count(plan, FramingRoles.Cripple));
        }

        [Fact]
        public void The_re_read_crossing_test_forgives_round_off_but_not_a_member_in_the_void()
        {
            var door = new List<WallOpeningSpan> { new WallOpeningSpan { Id = "door", Start = 1000, End = 1900, Sill = 0, Head = 2100 } };
            double jack = 1000 - W / 2;
            Assert.True(WallFramingRules.CrossesOpening(jack + 0.0005, 0, 2100, W, door));
            Assert.False(WallFramingRules.CrossesOpening(jack + 0.0005, 0, 2100, W, door, 1.0));
            Assert.True(WallFramingRules.CrossesOpening(jack + 5, 0, 2100, W, door, 1.0));
        }

        [Fact]
        public void The_signature_changes_when_a_member_moves_and_not_on_sub_rounding_noise()
        {
            var plan = WallFramingRules.Plan(Wall(3000), Budget);
            string a = FramingPlanSignature.Of(plan.Members);
            plan.Members[1].X0 += 0.01;
            Assert.Equal(a, FramingPlanSignature.Of(plan.Members));
            plan.Members[1].X0 += 1;
            Assert.NotEqual(a, FramingPlanSignature.Of(plan.Members));
        }

        [Fact]
        public void Header_sill_and_blocking_types_left_out_are_named_in_the_warnings()
        {
            var input = Wall(3000);
            input.Openings.Add(new WallOpeningSpan { Id = "win", Start = 1000, End = 1800, Sill = 900, Head = 2100 });
            input.Blocking.Add(new BlockingRow { Height = 600 });
            WallFramingPlan plan = WallFramingRules.Plan(input, Budget);
            AssertSound(plan, input);
            Assert.Contains("header_type_defaulted:track", plan.Warnings);
            Assert.Contains("sill_type_defaulted:track", plan.Warnings);
            Assert.Contains("blocking_type_defaulted:stud", plan.Warnings);
        }

        [Fact]
        public void A_track_piece_shorter_than_revit_accepts_is_dropped_and_named()
        {
            var input = Wall(3000);
            input.Openings.Add(new WallOpeningSpan { Id = "door", Start = 1000, End = 2999.5, Sill = 0, Head = 2100 });
            WallFramingPlan plan = WallFramingRules.Plan(input, Budget);
            Assert.All(plan.Members.Where(m => !FramingRoles.IsVertical(m.Role)),
                m => Assert.True(Math.Abs(m.X1 - m.X0) >= WallFramingRules.MinPieceMm, m.Role + " of " + Math.Abs(m.X1 - m.X0) + " mm"));
            Assert.Contains(plan.Warnings, x => x.StartsWith("short_piece_dropped:track@", StringComparison.Ordinal));
        }
    }
}
