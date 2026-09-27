// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_framing's CURTAIN method, Revit-free: the spec (method='curtain', type
// ids only, strict), the wall plan (segments around the openings, a header above
// each, a sill below each window, short pieces named, the carrier trimmed /
// deleted / kept / refused), the ceiling's hanger lines, and the fixed-distance
// grid check the verification uses. Type ids and sizes are neutral examples.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CurtainFramingRulesTests
    {
        private const string CurtainWall = @"{ ""wall"": { ""method"": ""curtain"", ""curtain_type_id"": 3001, ""placeholder_type_id"": 3002 } }";

        private const string CurtainCeiling = @"{ ""ceiling"": { ""method"": ""curtain"",
            ""layers"": [ { ""type_id"": 4001, ""offset_mm"": 40, ""angle_deg"": 0 }, { ""type_id"": 4002, ""offset_mm"": 20, ""angle_deg"": 90 } ],
            ""hanger"": { ""type_id"": 4003, ""spacing_mm"": 1200, ""max_length_mm"": 2500 } } }";

        private static CurtainWallInput Wall(double length, double height, params WallOpeningSpan[] openings)
            => new CurtainWallInput
            {
                Length = length, Height = height, CurtainTypeKey = "3001", HeaderTypeKey = "3003", SillTypeKey = "3004",
                PlaceholderTypeKey = "3002", Openings = openings.ToList()
            };

        private static WallOpeningSpan Door(string id, double start, double end, double head) => new WallOpeningSpan { Id = id, Start = start, End = end, Sill = 0, Head = head };
        private static WallOpeningSpan Window(string id, double start, double end, double sill, double head) => new WallOpeningSpan { Id = id, Start = start, End = end, Sill = sill, Head = head };

        [Fact]
        public void The_curtain_method_is_read_into_its_own_spec_and_defaults_are_geometric()
        {
            WallFramingSpec spec = FramingSpecRules.ParseWall(JObject.Parse(CurtainWall), out var errors);
            Assert.Empty(errors);
            Assert.NotNull(spec.Curtain);
            Assert.Equal(3001, spec.Curtain.CurtainTypeId);
            Assert.Null(spec.Curtain.HeaderTypeId);
            Assert.Equal("keep_carrier", spec.Curtain.MultiOpening);   // the user's decision of 2026-09-26
            Assert.Equal(50, spec.Curtain.MinSegmentMm);
            CurtainWallInput input = spec.Curtain.ToInput(3000, 2700, null);
            Assert.Equal("3001", input.HeaderTypeKey);   // header / sill default to the curtain type
            Assert.Equal("3001", input.SillTypeKey);
        }

        [Fact]
        public void The_member_method_still_reads_and_accepts_an_explicit_members_method()
        {
            JObject spec = JObject.Parse(@"{ ""wall"": { ""method"": ""members"",
                ""stud"": { ""type_id"": 1001, ""spacing_mm"": 406 }, ""track"": { ""bottom_type_id"": 1002, ""thickness_mm"": 1.2 } } }");
            WallFramingSpec wall = FramingSpecRules.ParseWall(spec, out var errors);
            Assert.Empty(errors);
            Assert.Null(wall.Curtain);
            Assert.Equal(1001, wall.StudTypeId);
        }

        [Fact]
        public void Curtain_spec_errors_are_named_by_path_and_member_keys_are_unknown_there()
        {
            JObject spec = JObject.Parse(@"{ ""wall"": { ""method"": ""curtain"", ""curtain_type_id"": ""Core studs"", ""stud"": {},
                ""placeholder_type_id"": 3002, ""multi_opening"": ""split"", ""min_segment_mm"": 0.05 } }");
            Assert.Null(FramingSpecRules.ParseWall(spec, out var errors));
            List<string> codes = errors.Select(e => e.Code + "@" + e.Path).ToList();
            Assert.Contains("not_integer@spec.wall.curtain_type_id", codes);
            Assert.Contains("unknown_field@spec.wall.stud", codes);
            Assert.Contains("bad_value@spec.wall.multi_opening", codes);
            Assert.Contains("below_minimum@spec.wall.min_segment_mm", codes);

            Assert.Null(FramingSpecRules.ParseWall(JObject.Parse(@"{ ""wall"": { ""method"": ""panels"" } }"), out errors));
            Assert.Contains(errors, e => e.Code == "bad_value" && e.Path == "spec.wall.method");

            Assert.Null(FramingSpecRules.ParseWall(JObject.Parse(@"{ ""wall"": { ""method"": ""curtain"", ""curtain_type_id"": 5, ""placeholder_type_id"": 5 } }"), out errors));
            Assert.Contains(errors, e => e.Code == "conflict" && e.Path == "spec.wall.placeholder_type_id");
        }

        [Fact]
        public void A_wall_without_openings_is_segments_only_and_the_carrier_is_replaced()
        {
            CurtainWallPlan plan = CurtainFramingRules.PlanWall(Wall(3000, 2700));
            Assert.Null(plan.Refusal);
            FramingMember seg = Assert.Single(plan.Pieces);
            Assert.Equal(CurtainFramingRoles.Segment, seg.Role);
            Assert.Equal(0, seg.X0); Assert.Equal(3000, seg.X1); Assert.Equal(0, seg.Z0); Assert.Equal(2700, seg.Z1);
            Assert.Equal(CurtainFramingRoles.CarrierDelete, plan.Carrier.Action);
        }

        [Fact]
        public void One_door_gives_two_segments_a_header_and_a_trimmed_placeholder()
        {
            CurtainWallPlan plan = CurtainFramingRules.PlanWall(Wall(3000, 2700, Door("D1", 1000, 1900, 2100)));
            Assert.Null(plan.Refusal);
            Assert.Equal(new[] { "curtain_segment", "curtain_segment", "curtain_header" }, plan.Pieces.Select(p => p.Role).ToArray());
            Assert.Equal(1000, plan.Pieces[0].X1);
            Assert.Equal(1900, plan.Pieces[1].X0);
            FramingMember header = plan.Pieces[2];
            Assert.Equal("3003", header.TypeKey);
            Assert.Equal(2100, header.Z0); Assert.Equal(2700, header.Z1);
            Assert.Equal(1000, header.X0); Assert.Equal(1900, header.X1);
            Assert.Equal(CurtainFramingRoles.CarrierTrim, plan.Carrier.Action);
            Assert.Equal(1000, plan.Carrier.X0); Assert.Equal(1900, plan.Carrier.X1);
            Assert.Equal("3002", plan.Carrier.TypeKey);
            Assert.Equal("D1", plan.Carrier.OpeningId);
        }

        [Fact]
        public void A_window_also_gets_a_sill_piece_from_the_base_to_its_sill()
        {
            CurtainWallPlan plan = CurtainFramingRules.PlanWall(Wall(3000, 2700, Window("W1", 800, 2000, 900, 2100)));
            FramingMember sill = Assert.Single(plan.Pieces, p => p.Role == CurtainFramingRoles.Sill);
            Assert.Equal("3004", sill.TypeKey);
            Assert.Equal(0, sill.Z0); Assert.Equal(900, sill.Z1);
        }

        [Fact]
        public void Short_pieces_are_named_not_built_a_flush_opening_leaves_no_zero_piece_and_a_wall_left_with_none_is_refused()
        {
            // Door flush with the start (no left piece at all), 30 mm of wall after it (below 50 mm),
            // head 20 mm under the top (a 20 mm header, below 50 mm). Nothing is left to build, so the
            // carrier must not be trimmed: no piece would carry the record remove restores it from.
            CurtainWallPlan plan = CurtainFramingRules.PlanWall(Wall(1000, 2700, Door("D1", 0, 970, 2680)));
            Assert.Contains("nothing would replace the carrier", plan.Refusal);
            Assert.Empty(plan.Pieces);
            Assert.Equal(2, plan.Skipped.Count);
            Assert.Contains(plan.Skipped, s => s.StartsWith("curtain_segment from x=970"));
            Assert.Contains(plan.Skipped, s => s.StartsWith("curtain_header"));
            Assert.Null(plan.Carrier);
        }

        [Fact]
        public void Several_openings_keep_the_carrier_by_default_saying_the_pieces_overlap_it_and_refuse_when_asked()
        {
            // The default is the input's own (keep_carrier): the doors keep their ids, tags and data.
            CurtainWallInput input = Wall(5000, 2700, Door("D1", 500, 1400, 2100), Window("W1", 2500, 3700, 900, 2100));
            CurtainWallPlan kept = CurtainFramingRules.PlanWall(input);
            Assert.Null(kept.Refusal);
            Assert.Equal(3, kept.Pieces.Count(p => p.Role == CurtainFramingRoles.Segment));
            Assert.Equal(2, kept.Pieces.Count(p => p.Role == CurtainFramingRoles.Header));
            Assert.Equal(1, kept.Pieces.Count(p => p.Role == CurtainFramingRoles.Sill));
            Assert.Equal(CurtainFramingRoles.CarrierKeep, kept.Carrier.Action);
            Assert.Equal(0, kept.Carrier.X0); Assert.Equal(5000, kept.Carrier.X1);
            Assert.Equal("3002", kept.Carrier.TypeKey);
            // Never silent: the plan says the pieces overlap the kept carrier.
            Assert.Contains(kept.Warnings, w => w.StartsWith("2 openings: ") && w.Contains("OVERLAP the kept carrier"));

            // The same wall through the parser's default, and the explicit refusal naming why.
            WallFramingSpec parsed = FramingSpecRules.ParseWall(JObject.Parse(CurtainWall), out _);
            Assert.Equal(CurtainFramingRoles.CarrierKeep,
                CurtainFramingRules.PlanWall(parsed.Curtain.ToInput(5000, 2700, input.Openings)).Carrier.Action);
            input.MultiOpening = "refuse";
            CurtainWallPlan refused = CurtainFramingRules.PlanWall(input);
            Assert.Contains("2 openings in one wall and multi_opening='refuse'", refused.Refusal);
            Assert.Contains("Split the wall at the openings", refused.Refusal);
            Assert.Empty(refused.Pieces);

            // A value the parser would refuse never silently keeps a carrier.
            input.MultiOpening = "split";
            Assert.NotNull(CurtainFramingRules.PlanWall(input).Refusal);
        }

        [Fact]
        public void An_explicit_multi_opening_refuse_is_read_as_given()
        {
            JObject spec = JObject.Parse(@"{ ""wall"": { ""method"": ""curtain"", ""curtain_type_id"": 3001, ""placeholder_type_id"": 3002, ""multi_opening"": ""refuse"" } }");
            Assert.Equal("refuse", FramingSpecRules.ParseWall(spec, out var errors).Curtain.MultiOpening);
            Assert.Empty(errors);
        }

        [Fact]
        public void Overlapping_or_outside_openings_and_a_wall_too_small_to_replace_are_refused()
        {
            // Overlapping openings are refused even under the default keep_carrier.
            CurtainWallInput overlap = Wall(5000, 2700, Door("D1", 500, 1400, 2100), Door("D2", 1300, 2000, 2100));
            Assert.Contains("overlap", CurtainFramingRules.PlanWall(overlap).Refusal);
            Assert.Contains("past the wall's ends", CurtainFramingRules.PlanWall(Wall(1000, 2700, Door("D1", 600, 1100, 2100))).Refusal);
            Assert.Contains("not inside the wall's height", CurtainFramingRules.PlanWall(Wall(3000, 2700, Door("D1", 600, 1100, 2900))).Refusal);
            Assert.Contains("nothing in its place", CurtainFramingRules.PlanWall(Wall(30, 2700)).Refusal);
        }

        [Fact]
        public void The_signature_moves_with_a_piece_and_with_the_carrier_action()
        {
            string a = CurtainFramingRules.PlanWall(Wall(3000, 2700, Door("D1", 1000, 1900, 2100))).Signature();
            string b = CurtainFramingRules.PlanWall(Wall(3000, 2700, Door("D1", 1000, 1900, 2150))).Signature();
            CurtainWallInput other = Wall(3000, 2700, Door("D1", 1000, 1900, 2100));
            other.PlaceholderTypeKey = "3009";
            Assert.NotEqual(a, b);
            Assert.NotEqual(a, CurtainFramingRules.PlanWall(other).Signature());
            Assert.Equal(a, CurtainFramingRules.PlanWall(Wall(3000, 2700, Door("D1", 1000, 1900, 2100))).Signature());
        }

        [Fact]
        public void The_ceiling_curtain_spec_reads_layers_and_needs_the_first_angle_for_hangers()
        {
            CeilingFramingSpec spec = FramingSpecRules.ParseCeiling(JObject.Parse(CurtainCeiling), out var errors);
            Assert.Empty(errors);
            Assert.Equal(2, spec.Curtain.Layers.Count);
            Assert.Equal(40, spec.Curtain.TopOffsetMm);
            Assert.Equal(4003, spec.Curtain.HangerTypeId);
            Assert.Equal(2500, spec.Curtain.HangerMaxLengthMm);

            JObject noAngle = JObject.Parse(CurtainCeiling);
            ((JObject)noAngle["ceiling"]["layers"][0]).Remove("angle_deg");
            Assert.Null(FramingSpecRules.ParseCeiling(noAngle, out errors));
            Assert.Contains(errors, e => e.Code == "missing" && e.Path == "spec.ceiling.layers[0].angle_deg");

            JObject tooMany = JObject.Parse(CurtainCeiling);
            var rows = (JArray)tooMany["ceiling"]["layers"];
            while (rows.Count <= FramingSpecRules.MaxCurtainLayers) rows.Add(JObject.Parse(@"{ ""type_id"": 4009, ""offset_mm"": 10 }"));
            Assert.Null(FramingSpecRules.ParseCeiling(tooMany, out errors));
            Assert.Contains(errors, e => e.Code == "above_maximum" && e.Path == "spec.ceiling.layers");
        }

        [Fact]
        public void Hanger_lines_run_parallel_to_the_first_grid_and_centred_in_the_boundary()
        {
            CeilingFramingSpec spec = FramingSpecRules.ParseCeiling(JObject.Parse(CurtainCeiling), out _);
            var loops = new List<List<double[]>> { new List<double[]> { new[] { 0.0, 0 }, new[] { 6000.0, 0 }, new[] { 6000.0, 3600 }, new[] { 0.0, 3600 } } };
            CurtainCeilingPlan plan = CurtainFramingRules.PlanCeiling(loops, spec.Curtain, 0, 1000);
            Assert.Null(plan.Refusal);
            Assert.Equal(3, plan.HangerLines.Count);                     // 3600 / 1200, centred: y = 600, 1800, 3000
            Assert.Equal(new[] { 600.0, 1800, 3000 }, plan.HangerLines.Select(l => System.Math.Round(l[1], 3)).ToArray());
            Assert.All(plan.HangerLines, l => Assert.Equal(6000, System.Math.Round(System.Math.Abs(l[2] - l[0]), 3)));
            string sig = CurtainCeilingPlan.Signature(spec.Curtain, plan.HangerLines);
            spec.Curtain.Layers[1].OffsetMm = 25;
            Assert.NotEqual(sig, CurtainCeilingPlan.Signature(spec.Curtain, plan.HangerLines));
        }

        [Fact]
        public void A_plan_with_openings_but_no_piece_is_refused_so_the_carrier_never_changes_unrecorded()
        {
            // One door across the whole wall, its head 20 mm under the top: no segment, no header, no sill.
            var input = new CurtainWallInput
            {
                Length = 900, Height = 2700, CurtainTypeKey = "3001", HeaderTypeKey = "3001", SillTypeKey = "3001", PlaceholderTypeKey = "3002", MinSegment = 50,
                Openings = new System.Collections.Generic.List<WallOpeningSpan> { new WallOpeningSpan { Id = "d1", Start = 0, End = 900, Sill = 0, Head = 2680 } },
            };
            CurtainWallPlan plan = CurtainFramingRules.PlanWall(input);
            Assert.Contains("nothing would replace the carrier", plan.Refusal);
            Assert.Empty(plan.Pieces);
            Assert.Null(plan.Carrier);
        }

        [Fact]
        public void A_header_or_sill_type_shared_with_the_placeholder_is_a_conflict()
        {
            Assert.Null(FramingSpecRules.ParseWall(JObject.Parse(@"{ ""wall"": { ""method"": ""curtain"", ""curtain_type_id"": 5, ""placeholder_type_id"": 7, ""header_type_id"": 7 } }"), out var errors));
            Assert.Contains(errors, e => e.Code == "conflict" && e.Path == "spec.wall.header_type_id");
            Assert.Null(FramingSpecRules.ParseWall(JObject.Parse(@"{ ""wall"": { ""method"": ""curtain"", ""curtain_type_id"": 5, ""placeholder_type_id"": 7, ""sill_type_id"": 7 } }"), out errors));
            Assert.Contains(errors, e => e.Code == "conflict" && e.Path == "spec.wall.sill_type_id");
            Assert.NotNull(FramingSpecRules.ParseWall(JObject.Parse(@"{ ""wall"": { ""method"": ""curtain"", ""curtain_type_id"": 5, ""placeholder_type_id"": 7, ""header_type_id"": 5, ""sill_type_id"": 5 } }"), out errors));
        }

        [Fact]
        public void A_fixed_distance_grid_holds_only_with_every_interior_spacing_and_no_missing_line()
        {
            // 406.4 mm grid on a 3000 mm wall, justified at the start: 7 lines, last bay 155.2 mm.
            var good = Enumerable.Range(1, 7).Select(k => k * 406.4).ToList();
            Assert.Empty(CurtainFramingRules.CheckFixedSpacing(good, 3000, 406.4, 1));
            var centred = Enumerable.Range(0, 7).Select(k => 280.8 + k * 406.4).ToList();
            Assert.Empty(CurtainFramingRules.CheckFixedSpacing(centred, 3000, 406.4, 1));

            var missing = good.Where((v, i) => i != 3).ToList();
            Assert.Contains(CurtainFramingRules.CheckFixedSpacing(missing, 3000, 406.4, 1), s => s.Contains("812.8 mm apart"));
            Assert.Contains(CurtainFramingRules.CheckFixedSpacing(good.Take(5), 3000, 406.4, 1), s => s.StartsWith("last bay"));
            Assert.Contains(CurtainFramingRules.CheckFixedSpacing(new double[0], 3000, 406.4, 1), s => s.StartsWith("no grid line"));
            Assert.Empty(CurtainFramingRules.CheckFixedSpacing(new double[0], 400, 406.4, 1));
        }
    }
}
