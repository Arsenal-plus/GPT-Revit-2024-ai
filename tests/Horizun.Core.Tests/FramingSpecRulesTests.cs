// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_framing's spec reader: the typed FRAMING SPEC a client fills from a
// detail image, read strictly (unknown keys, names instead of ids, metres in a
// millimetre field and contradictions are refused by path), with geometric
// defaults only, and a hash that does not depend on key order or number spelling.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FramingSpecRulesTests
    {
        private const string Partition = @"{ ""wall"": {
            ""layer"": ""core"",
            ""stud"": { ""type_id"": 1001, ""spacing_mm"": 406, ""start"": ""wall_start"", ""double_at_ends"": true, ""width_mm"": 41.3 },
            ""track"": { ""bottom_type_id"": 1002, ""top_same_as_bottom"": true, ""thickness_mm"": 1.2 },
            ""openings"": { ""king_studs"": 1, ""jack_studs"": true, ""header_type_id"": 1002, ""sill_type_id"": 1002, ""cripple_spacing_mm"": 406 },
            ""blocking"": [ { ""height_mm"": 1200, ""type_id"": 1002 } ] } }";

        private const string Suspended = @"{ ""ceiling"": {
            ""main"": { ""type_id"": 2001, ""spacing_mm"": 1200, ""direction"": ""short"" },
            ""cross"": { ""type_id"": 2002, ""spacing_mm"": 400 },
            ""perimeter"": { ""type_id"": 2003 },
            ""hanger"": { ""type_id"": 2004, ""spacing_mm"": 1200 },
            ""drop_mm"": 25 } }";

        private static List<string> Codes(List<FramingSpecError> errors) => errors.Select(e => e.Code + "@" + e.Path).ToList();

        [Fact]
        public void A_complete_partition_spec_reads_into_the_plan_input()
        {
            WallFramingSpec spec = FramingSpecRules.ParseWall(JObject.Parse(Partition), out var errors);
            Assert.Empty(errors);
            Assert.True(spec.CoreLayer);
            Assert.Equal(1001, spec.StudTypeId);
            Assert.Equal(1002, spec.TopTrackTypeId);
            Assert.Equal(41.3, spec.StudWidthMm);
            Assert.Single(spec.Blocking);
            Assert.Equal(new long[] { 1001, 1002 }, spec.TypeIds().OrderBy(x => x).ToArray());

            WallFramingInput input = spec.ToInput(3000, 2700, 41.3, 1.2, new[] { new WallOpeningSpan { Id = "d", Start = 1000, End = 1900, Sill = 0, Head = 2100 } });
            Assert.Equal(406, input.StudSpacing);
            Assert.Equal("1001", input.StudTypeKey);
            Assert.Equal("1002", input.HeaderTypeKey);
            Assert.True(input.DoubleAtEnds);
            WallFramingPlan plan = WallFramingRules.Plan(input, 5000);
            Assert.Null(plan.Refusal);
            Assert.Contains(plan.Members, m => m.Role == FramingRoles.Header);
        }

        [Fact]
        public void Defaults_are_geometric_only()
        {
            WallFramingSpec spec = FramingSpecRules.ParseWall(JObject.Parse(
                @"{ ""wall"": { ""stud"": { ""type_id"": 5, ""spacing_mm"": 600 }, ""track"": { ""bottom_type_id"": 6 } } }"), out var errors);
            Assert.Empty(errors);
            Assert.True(spec.CoreLayer);
            Assert.Equal("wall_start", spec.StartRule);
            Assert.Equal(1, spec.KingStuds);
            Assert.True(spec.JackStuds);
            Assert.Equal(6, spec.TopTrackTypeId);
            Assert.Null(spec.StudWidthMm);
            Assert.Null(spec.HeaderTypeId);
            Assert.Empty(spec.Blocking);
        }

        [Fact]
        public void A_layer_index_names_that_layer()
        {
            WallFramingSpec spec = FramingSpecRules.ParseWall(JObject.Parse(
                @"{ ""wall"": { ""layer"": 2, ""stud"": { ""type_id"": 5, ""spacing_mm"": 600 }, ""track"": { ""bottom_type_id"": 6 } } }"), out var errors);
            Assert.Empty(errors);
            Assert.False(spec.CoreLayer);
            Assert.Equal(2, spec.LayerIndex);
        }

        [Fact]
        public void Every_problem_is_named_by_path_in_one_reply()
        {
            WallFramingSpec spec = FramingSpecRules.ParseWall(JObject.Parse(
                @"{ ""wall"": { ""layer"": ""finish"", ""stud"": { ""type_id"": ""C-stud 92"", ""spacing_mm"": 0.406, ""spacng_mm"": 406 },
                     ""track"": { ""bottom_type_id"": 6, ""top_type_id"": 7, ""top_same_as_bottom"": true },
                     ""openings"": { ""king_studs"": 3 } } }"), out var errors);
            Assert.Null(spec);
            List<string> codes = Codes(errors);
            Assert.Contains("bad_value@spec.wall.layer", codes);
            Assert.Contains("not_integer@spec.wall.stud.type_id", codes);
            Assert.Contains("below_minimum@spec.wall.stud.spacing_mm", codes);
            Assert.Contains("unknown_field@spec.wall.stud.spacng_mm", codes);
            Assert.Contains("conflict@spec.wall.track.top_type_id", codes);
            Assert.Contains("bad_value@spec.wall.openings.king_studs", codes);
        }

        [Fact]
        public void Missing_required_members_and_a_foreign_root_are_refused()
        {
            Assert.Null(FramingSpecRules.ParseWall(null, out var none));
            Assert.Contains("missing@spec", Codes(none));

            Assert.Null(FramingSpecRules.ParseWall(JObject.Parse(Suspended), out var wrongRoot));
            List<string> codes = Codes(wrongRoot);
            Assert.Contains("unknown_field@spec.ceiling", codes);
            Assert.Contains("missing@spec.wall", codes);

            Assert.Null(FramingSpecRules.ParseWall(JObject.Parse(@"{ ""wall"": { ""track"": { ""bottom_type_id"": 6, ""top_same_as_bottom"": false } } }"), out var partial));
            codes = Codes(partial);
            Assert.Contains("missing@spec.wall.stud", codes);
            Assert.Contains("missing@spec.wall.track.top_type_id", codes);
        }

        [Fact]
        public void Blocking_rows_are_bounded_and_checked_one_by_one()
        {
            Assert.Null(FramingSpecRules.ParseWall(JObject.Parse(
                @"{ ""wall"": { ""stud"": { ""type_id"": 5, ""spacing_mm"": 600 }, ""track"": { ""bottom_type_id"": 6 },
                     ""blocking"": [ { ""height_mm"": 1200 }, { ""type_id"": 9 } ] } }"), out var errors));
            Assert.Equal(new[] { "missing@spec.wall.blocking[1].height_mm" }, Codes(errors));

            string many = "[" + string.Join(",", Enumerable.Range(0, 21).Select(i => @"{ ""height_mm"": 100 }")) + "]";
            Assert.Null(FramingSpecRules.ParseWall(JObject.Parse(
                @"{ ""wall"": { ""stud"": { ""type_id"": 5, ""spacing_mm"": 600 }, ""track"": { ""bottom_type_id"": 6 }, ""blocking"": " + many + " } }"), out var tooMany));
            Assert.Contains("above_maximum@spec.wall.blocking", Codes(tooMany));
        }

        [Fact]
        public void A_suspended_ceiling_spec_reads_with_its_defaults()
        {
            CeilingFramingSpec spec = FramingSpecRules.ParseCeiling(JObject.Parse(Suspended), out var errors);
            Assert.Empty(errors);
            Assert.Equal("short", spec.Direction);
            Assert.Null(spec.DirectionDeg);
            Assert.Equal(3000, spec.HangerMaxLengthMm);
            Assert.Equal(600, spec.HangerEndOffsetMm);
            Assert.Equal(25, spec.DropMm);
            Assert.Equal(new long[] { 2001, 2002, 2003, 2004 }, spec.TypeIds().OrderBy(x => x).ToArray());

            var square = new List<double[]> { new[] { 0.0, 0 }, new[] { 6000.0, 0 }, new[] { 6000.0, 4000 }, new[] { 0.0, 4000 } };
            CeilingFramingInput input = spec.ToInput(new List<List<double[]>> { square });
            Assert.Equal(400, input.CrossSpacing);
            Assert.Equal("2003", input.PerimeterTypeKey);
            CeilingFramingPlan plan = CeilingFramingRules.Plan(input, 5000);
            Assert.Null(plan.Refusal);
            Assert.Contains(plan.Members, m => m.Role == FramingRoles.Hanger);
        }

        [Fact]
        public void A_ceiling_angle_is_degrees_and_foreign_attach_is_refused()
        {
            CeilingFramingSpec spec = FramingSpecRules.ParseCeiling(JObject.Parse(
                @"{ ""ceiling"": { ""main"": { ""type_id"": 1, ""spacing_mm"": 1200, ""direction"": 90 }, ""hanger"": { ""type_id"": 2, ""spacing_mm"": 1200 } } }"), out var errors);
            Assert.Empty(errors);
            Assert.Equal(90, spec.DirectionDeg);
            Assert.Equal(System.Math.PI / 2, spec.ToInput(null).DirectionAngleRad.Value, 9);
            Assert.Null(spec.CrossTypeId);
            Assert.Equal(0, spec.ToInput(null).CrossSpacing);

            Assert.Null(FramingSpecRules.ParseCeiling(JObject.Parse(
                @"{ ""ceiling"": { ""main"": { ""type_id"": 1, ""spacing_mm"": 1200, ""direction"": ""diagonal"" },
                     ""hanger"": { ""type_id"": 2, ""spacing_mm"": 1200, ""attach"": ""wall"" }, ""drop_mm"": -5 } }"), out var bad));
            List<string> codes = Codes(bad);
            Assert.Contains("bad_value@spec.ceiling.main.direction", codes);
            Assert.Contains("bad_value@spec.ceiling.hanger.attach", codes);
            Assert.Contains("below_minimum@spec.ceiling.drop_mm", codes);

            Assert.Null(FramingSpecRules.ParseCeiling(JObject.Parse(@"{ ""ceiling"": { ""main"": { ""type_id"": 1, ""spacing_mm"": 1200 } } }"), out var noHanger));
            Assert.Contains("missing@spec.ceiling.hanger", Codes(noHanger));
        }

        [Fact]
        public void The_spec_hash_ignores_key_order_and_number_spelling_but_not_values()
        {
            string a = FramingSpecRules.Hash(JObject.Parse(@"{ ""wall"": { ""stud"": { ""type_id"": 5, ""spacing_mm"": 406 }, ""track"": { ""bottom_type_id"": 6 } } }"));
            string b = FramingSpecRules.Hash(JObject.Parse(@"{ ""wall"": { ""track"": { ""bottom_type_id"": 6 }, ""stud"": { ""spacing_mm"": 406.0, ""type_id"": 5 } } }"));
            string c = FramingSpecRules.Hash(JObject.Parse(@"{ ""wall"": { ""stud"": { ""type_id"": 5, ""spacing_mm"": 400 }, ""track"": { ""bottom_type_id"": 6 } } }"));
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
            Assert.Equal(64, a.Length);
        }
    }
}
