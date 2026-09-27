// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Room finishes, carbon and room membership, at a desk. The rules under test are
// the ones a bill of quantities is read by: faces stay gross and openings travel in
// their own column; a reading without a factor, a density or a volume is counted by
// reason and never folded into a total as a zero.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class RoomQuantityRulesTests
    {
        private static FinishFaceFact Face(string room, string surface, string key, string type, string mat, double m2, string code = "C1")
            => new FinishFaceFact { RoomKey = room, Surface = surface, BoundingKey = key, TypeName = type, Material = mat, Code = code, GrossM2 = m2 };

        [Theory]
        [InlineData("Side", "wall")]
        [InlineData("Bottom", "floor")]
        [InlineData("Top", "ceiling")]
        [InlineData("Other", null)]
        public void Subface_type_maps_to_its_finish(string subface, string expected)
            => Assert.Equal(expected, RoomFinishRules.SurfaceOf(subface));

        [Fact]
        public void Faces_group_gross_and_openings_stay_in_their_own_column()
        {
            var faces = new[]
            {
                Face("R1", "wall", "host:10", "Wall A", "Plaster", 12.0),
                Face("R1", "wall", "host:11", "Wall A", "Plaster", 8.0),
                Face("R1", "floor", "host:20", "Slab", "Tile", 20.0),
            };
            var openings = new[]
            {
                new OpeningDeductionFact { RoomKey = "R1", BoundingKey = "host:10", InsertId = "100", SizeBasis = "rough", WidthM = 0.9, HeightM = 2.1 },
                new OpeningDeductionFact { RoomKey = "R1", BoundingKey = "host:11", InsertId = "101", SizeBasis = "bounding_box", WidthM = 1.2, HeightM = 1.0 },
            };
            List<OpeningDeductionFact> orphans;
            var groups = RoomFinishRules.Group(faces, openings, out orphans);

            Assert.Empty(orphans);
            Assert.Equal(2, groups.Count);
            var wall = groups.Single(g => g.Surface == "wall");
            Assert.Equal(20.0, wall.GrossM2, 9);                  // gross, never net
            Assert.Equal(0.9 * 2.1 + 1.2, wall.OpeningDeductionM2, 9);
            Assert.Equal(2, wall.Openings);
            Assert.Equal(new[] { "bounding_box", "rough" }, wall.SizeBases.ToArray());
            Assert.Equal(0, groups.Single(g => g.Surface == "floor").Openings);
        }

        [Fact]
        public void An_opening_is_deducted_once_even_when_its_wall_has_two_subfaces_in_the_room()
        {
            var faces = new[] { Face("R1", "wall", "host:10", "Wall A", "Plaster", 5), Face("R1", "wall", "host:10", "Wall A", "Plaster", 5) };
            var o = new OpeningDeductionFact { RoomKey = "R1", BoundingKey = "host:10", InsertId = "100", SizeBasis = "nominal", WidthM = 1, HeightM = 2 };
            List<OpeningDeductionFact> orphans;
            var g = RoomFinishRules.Group(faces, new[] { o, o }, out orphans).Single();
            Assert.Equal(10, g.GrossM2, 9);
            Assert.Equal(2, g.OpeningDeductionM2, 9);
            Assert.Equal(1, g.Openings);
        }

        [Fact]
        public void A_wall_split_across_two_materials_takes_its_deduction_to_the_larger_row()
        {
            var faces = new[] { Face("R1", "wall", "host:10", "Wall A", "Paint", 2), Face("R1", "wall", "host:10", "Wall A", "Tile", 7) };
            var o = new OpeningDeductionFact { RoomKey = "R1", BoundingKey = "host:10", InsertId = "100", SizeBasis = "rough", WidthM = 1, HeightM = 1 };
            List<OpeningDeductionFact> orphans;
            var groups = RoomFinishRules.Group(faces, new[] { o }, out orphans);
            Assert.Equal(1, groups.Single(g => g.Material == "Tile").Openings);
            Assert.Equal(0, groups.Single(g => g.Material == "Paint").Openings);
        }

        [Fact]
        public void An_unsized_opening_is_counted_not_deducted_and_an_orphan_is_returned()
        {
            var faces = new[] { Face("R1", "wall", "host:10", "Wall A", "Paint", 9) };
            var unsized = new OpeningDeductionFact { RoomKey = "R1", BoundingKey = "host:10", InsertId = "100", WidthM = null, HeightM = 2 };
            var orphan = new OpeningDeductionFact { RoomKey = "R2", BoundingKey = "host:10", InsertId = "101", WidthM = 1, HeightM = 2 };
            List<OpeningDeductionFact> orphans;
            var g = RoomFinishRules.Group(faces, new[] { unsized, orphan }, out orphans).Single();
            Assert.Equal(1, g.OpeningsUnsized);
            Assert.Equal(0, g.OpeningDeductionM2, 9);
            Assert.Single(orphans);
            Assert.Equal("101", orphans[0].InsertId);
        }

        [Fact]
        public void An_unbounded_face_is_its_own_row_under_a_named_type()
        {
            List<OpeningDeductionFact> orphans;
            var g = RoomFinishRules.Group(new[] { Face("R1", "ceiling", null, null, null, 20) }, null, out orphans).Single();
            Assert.Equal(RoomFinishRules.NoBoundingElement, g.TypeName);
            Assert.Equal(20, g.GrossM2, 9);
        }

        [Theory]
        [InlineData(1.0, 2.0, 2.0)]
        [InlineData(0.0, 2.0, null)]
        [InlineData(-1.0, 2.0, null)]
        [InlineData(double.NaN, 2.0, null)]
        public void Rectangle_area_is_null_rather_than_zero_when_a_side_is_unusable(double w, double h, double? expected)
            => Assert.Equal(expected, RoomFinishRules.RectangleM2(w, h));

        private static readonly List<CarbonFactor> Table = new List<CarbonFactor>
        {
            new CarbonFactor { Material = "Concrete C30", Per = "m3", Factor = 300 },
            new CarbonFactor { MaterialClass = "Metal", Per = "kg", Factor = 1.5 },
            new CarbonFactor { Material = "Timber", Per = "kg", Factor = -1.2 },
        };

        [Fact]
        public void Factor_table_refuses_ambiguity_and_empty_keys_and_accepts_negative_factors()
        {
            Assert.Null(CarbonRules.Validate(Table));
            Assert.NotNull(CarbonRules.Validate(new List<CarbonFactor>()));
            Assert.Contains("exactly one", CarbonRules.Validate(new List<CarbonFactor> { new CarbonFactor { Per = "m3", Factor = 1 } }));
            Assert.Contains("per", CarbonRules.Validate(new List<CarbonFactor> { new CarbonFactor { Material = "A", Per = "t", Factor = 1 } }));
            Assert.Contains("repeats", CarbonRules.Validate(new List<CarbonFactor>
            {
                new CarbonFactor { Material = "A", Per = "m3", Factor = 1 }, new CarbonFactor { Material = " a ", Per = "kg", Factor = 2 }
            }));
        }

        [Fact]
        public void Name_matches_before_class()
        {
            var t = new List<CarbonFactor>
            {
                new CarbonFactor { MaterialClass = "Concrete", Per = "m3", Factor = 1 },
                new CarbonFactor { Material = "concrete c30", Per = "m3", Factor = 2 },
            };
            string by;
            Assert.Equal(2, CarbonRules.Resolve(t, "Concrete C30", "Concrete", out by).Factor);
            Assert.Equal("material", by);
            Assert.Equal(1, CarbonRules.Resolve(t, "Concrete C40", "Concrete", out by).Factor);
            Assert.Equal("material_class", by);
            Assert.Null(CarbonRules.Resolve(t, "Brick", "Masonry", out by));
        }

        [Fact]
        public void Carbon_counts_what_it_can_and_names_the_rest_by_reason()
        {
            var readings = new[]
            {
                new CarbonReading { ElementId = "1", Material = "Concrete C30", MaterialClass = "Concrete", Code = "A", Level = "L1", VolumeM3 = 2, AreaM2 = 10, DensityKgM3 = 2400 },
                new CarbonReading { ElementId = "2", Material = "Steel", MaterialClass = "Metal", Code = "A", Level = "L1", VolumeM3 = 0.1, AreaM2 = 1, DensityKgM3 = 7850 },
                new CarbonReading { ElementId = "3", Material = "Steel", MaterialClass = "Metal", Code = "A", Level = "L1", VolumeM3 = 0.1, AreaM2 = 1 },
                new CarbonReading { ElementId = "4", Material = "Gypsum", MaterialClass = "Plaster", Code = "A", Level = "L1", VolumeM3 = 0.5 },
                new CarbonReading { ElementId = "5", Material = "Concrete C30", MaterialClass = "Concrete", Code = "A", Level = "L1", VolumeM3 = null },
            };
            var groups = CarbonRules.Group(readings, Table);

            var concrete = groups.Single(g => g.Material == "Concrete C30");
            Assert.Equal(600, concrete.KgCO2e, 9);            // 300 x 2 m3
            Assert.Equal(4800, concrete.MassKg, 9);
            Assert.Equal(1, concrete.UnreadableVolume);
            Assert.False(concrete.Complete);

            var steel = groups.Single(g => g.Material == "Steel");
            Assert.Equal(1.5 * 0.1 * 7850, steel.KgCO2e, 9);  // per kg needs the density
            Assert.Equal(1, steel.NoDensity);
            Assert.Equal("material_class", steel.MatchedBy);
            Assert.Equal(0.2, steel.VolumeM3, 9);             // volume still sums every reading that has one

            var gypsum = groups.Single(g => g.Material == "Gypsum");
            Assert.Equal(1, gypsum.NoFactor);
            Assert.Equal(0, gypsum.KgCO2e, 9);
            Assert.Null(gypsum.Factor);
        }

        [Fact]
        public void Groups_split_by_code_and_level()
        {
            var readings = new[]
            {
                new CarbonReading { Material = "Concrete C30", Code = "A", Level = "L1", VolumeM3 = 1 },
                new CarbonReading { Material = "Concrete C30", Code = "B", Level = "L1", VolumeM3 = 1 },
                new CarbonReading { Material = "Concrete C30", Code = "A", Level = "L2", VolumeM3 = 1 },
            };
            Assert.Equal(3, CarbonRules.Group(readings, Table).Count);
        }

        [Fact]
        public void Negative_factor_yields_a_negative_contribution()
        {
            double? mass, kg;
            var r = new CarbonReading { Material = "Timber", VolumeM3 = 1, DensityKgM3 = 500 };
            Assert.Equal(CarbonRules.Counted, CarbonRules.Evaluate(r, Table[2], out mass, out kg));
            Assert.Equal(-600, kg.Value, 9);
        }

        [Fact]
        public void Internal_density_converts_to_kg_per_m3()
            => Assert.Equal(2400, CarbonRules.KgPerCubicFootToKgPerM3(2400 * CarbonRules.CubicFeetToM3), 9);

        [Theory]
        [InlineData(true, true, true, true, "solid_interior")]
        [InlineData(true, false, true, true, null)]
        [InlineData(false, true, true, false, "location_point")]
        [InlineData(false, false, false, true, "curve_points")]
        [InlineData(false, false, false, false, null)]
        public void Membership_samples_the_right_point(bool wallOrFloor, bool solid, bool point, bool curve, string expected)
            => Assert.Equal(expected, RoomMembershipRules.SampleBasis(wallOrFloor, solid, point, curve));

        [Fact]
        public void No_room_is_named_unassigned()
        {
            Assert.Equal(RoomMembershipRules.Unassigned, RoomMembershipRules.GroupKey(null));
            Assert.Equal("101 Office", RoomMembershipRules.GroupKey("101 Office"));
        }

        [Fact]
        public void Paint_and_face_materials_of_one_name_fall_in_separate_rows()
        {
            var a = Face("r1", "wall", "host:1", "Walls: W", "White", 10); a.MaterialSource = "paint";
            var b = Face("r1", "wall", "host:2", "Walls: W", "White", 4); b.MaterialSource = "face";
            List<OpeningDeductionFact> orphans;
            var g = RoomFinishRules.Group(new[] { a, b }, null, out orphans);
            Assert.Equal(2, g.Count);
            Assert.Equal("paint", g[0].MaterialSource);
            Assert.Equal(10, g[0].GrossM2, 6);
            Assert.Equal("face", g[1].MaterialSource);
            Assert.Equal(4, g[1].GrossM2, 6);
        }

        [Fact]
        public void Each_attributed_opening_is_kept_with_its_own_size_and_basis()
        {
            var faces = new[] { Face("r1", "wall", "host:1", "Walls: W", "M", 20) };
            var ops = new[]
            {
                new OpeningDeductionFact { RoomKey = "r1", BoundingKey = "host:1", InsertId = "10", InsertKind = "door", SizeBasis = "rough", WidthM = 1.0, HeightM = 2.1 },
                new OpeningDeductionFact { RoomKey = "r1", BoundingKey = "host:1", InsertId = "11", InsertKind = "wall_opening", SizeBasis = "opening_rect", WidthM = 0.5, HeightM = 0.5 },
                new OpeningDeductionFact { RoomKey = "r1", BoundingKey = "host:1", InsertId = "10", InsertKind = "door", SizeBasis = "rough", WidthM = 1.0, HeightM = 2.1 }
            };
            List<OpeningDeductionFact> orphans;
            var g = RoomFinishRules.Group(faces, ops, out orphans).Single();
            Assert.Equal(new[] { "10", "11" }, g.OpeningFacts.Select(o => o.InsertId).ToArray());
            Assert.Equal("opening_rect", g.OpeningFacts[1].SizeBasis);
            Assert.Equal(2.35, g.OpeningDeductionM2, 6);
            Assert.Empty(orphans);
        }

        [Theory]
        [InlineData("New", true)]
        [InlineData("Existing", true)]
        [InlineData("None", true)]
        [InlineData("Demolished", false)]
        [InlineData("Past", false)]
        [InlineData("Future", false)]
        [InlineData("Temporary", false)]
        [InlineData(null, false)]
        public void Only_an_insert_in_the_wall_in_the_phase_is_deducted(string status, bool expected)
            => Assert.Equal(expected, RoomFinishRules.ExistsInPhase(status));

        [Fact]
        public void An_unreadable_area_is_counted_and_the_phase_is_a_column()
        {
            var f = new List<CarbonFactor> { new CarbonFactor { Material = "Concrete", Per = "m3", Factor = 100 } };
            var readings = new[]
            {
                new CarbonReading { ElementId = "1", Material = "Concrete", Level = "L1", PhaseCreated = "New", VolumeM3 = 1, AreaM2 = 2 },
                new CarbonReading { ElementId = "2", Material = "Concrete", Level = "L1", PhaseCreated = "New", VolumeM3 = 1, AreaM2 = null },
                new CarbonReading { ElementId = "3", Material = "Concrete", Level = "L1", PhaseCreated = "Existing", PhaseDemolished = "New", VolumeM3 = 1, AreaM2 = 3 }
            };
            var g = CarbonRules.Group(readings, f);
            Assert.Equal(2, g.Count);
            Assert.Equal(2, g[0].AreaM2, 6);
            Assert.Equal(1, g[0].UnreadableArea);
            Assert.False(g[0].AreaComplete);
            Assert.True(g[0].Complete);
            Assert.Equal(200, g[0].KgCO2e, 6);
            Assert.Equal("New", g[1].PhaseDemolished);
            Assert.True(g[1].AreaComplete);
        }

        [Fact]
        public void Samples_in_two_rooms_span_them_and_a_miss_beside_a_hit_is_ignored()
        {
            List<string> rooms;
            Assert.Equal("assigned", RoomMembershipRules.Classify(new[] { null, "7", "7", null }, out rooms));
            Assert.Equal(new[] { "7" }, rooms.ToArray());
            Assert.Equal("7", RoomMembershipRules.KeyOf("assigned", rooms));
            Assert.Equal("spans_rooms", RoomMembershipRules.Classify(new[] { "7", null, "9" }, out rooms));
            Assert.Equal(new[] { "7", "9" }, rooms.ToArray());
            Assert.Equal("(multiple rooms)", RoomMembershipRules.KeyOf("spans_rooms", rooms));
            Assert.Equal("unassigned", RoomMembershipRules.Classify(new string[] { null, null }, out rooms));
            Assert.Empty(rooms);
            Assert.Equal("(unassigned)", RoomMembershipRules.KeyOf("unassigned", rooms));
        }

        [Fact]
        public void A_rollup_key_is_complete_only_when_every_element_in_it_was_measured()
        {
            var t = new QuantityTally();
            Assert.Equal(QuantityState.Measured, RollupRules.Add(t, QuantityState.Measured, 2.5));
            Assert.Equal(QuantityState.Measured, RollupRules.Add(t, QuantityState.Measured, 0));
            Assert.True(RollupRules.Complete(t, 2));
            Assert.Equal(QuantityState.Absent, RollupRules.Add(t, QuantityState.Absent, null));
            Assert.False(RollupRules.Complete(t, 3));
            Assert.Equal(RollupRules.UnreadableBucket, RollupRules.Add(t, "no such state", null));
            Assert.Equal(RollupRules.UnreadableBucket, RollupRules.Add(t, QuantityState.Measured, double.NaN));
            Assert.Equal(QuantityState.Invalid, RollupRules.Add(t, QuantityState.Invalid, null));
            Assert.Equal(QuantityState.Empty, RollupRules.Add(t, QuantityState.Empty, null));
            Assert.Equal(2.5, t.Total, 6);
            Assert.Equal(2, t.Measured);
            Assert.Equal(1, t.Absent);
            Assert.Equal(2, t.Unreadable);
            Assert.Equal(1, t.Invalid);
            Assert.Equal(1, t.Empty);
            Assert.False(RollupRules.Complete(null, 0));
        }
    }
}
