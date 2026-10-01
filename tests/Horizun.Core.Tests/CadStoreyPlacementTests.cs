using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// A PLAN DRAWING'S Z IS NOT A HEIGHT.
    ///
    /// MEASURED (dry run, class 4): a DWG linked into a plan of a level at +30 000 mm handed its geometry
    /// over at Z = 0, and horizun_plan_from_cad emitted 75 walls at start/end Z = 0. horizun_create_elements
    /// derives a wall's base offset as Z minus the level's elevation, so those walls would have been built
    /// with a base offset of -30 000 mm - on the storey underneath - and verified there.
    ///
    /// These fix the rule the plan now follows: an element resolved to a storey stands on that storey, at its
    /// elevation plus the rule's offset, and the consumer's own derivation then gives back exactly that offset.
    /// </summary>
    public class CadStoreyPlacementTests
    {
        // Revit's internal unit is the decimal foot: +30 000 mm is 98.4251968... ft.
        private const double LevelFeet = 30000.0 / 304.8;

        private static double LevelMm => CadUnits.FeetToMm(LevelFeet);

        private static JObject Wall(double z, double? offset = null)
        {
            var row = new JObject
            {
                ["kind"] = "wall",
                ["start"] = new JArray(1000.0, 2000.0, z),
                ["end"] = new JArray(6000.0, 2000.0, z),
                ["height"] = 3000.0,
                ["level_id"] = 275900L
            };
            if (offset.HasValue) row["offset"] = offset.Value;
            return row;
        }

        /// <summary>What horizun_create_elements does with a wall row: base offset = Z - level elevation.</summary>
        private static double DerivedBaseOffsetMm(JObject row) => ((JArray)row["start"])[2].Value<double>() - LevelMm;

        [Fact]
        public void The_level_is_thirty_metres_up_in_feet_as_Revit_keeps_it()
        {
            Assert.Equal(98.4252, LevelFeet, 4);
            Assert.Equal(30000.0, LevelMm, 6);
        }

        [Fact]
        public void A_wall_drawn_at_z_zero_on_a_level_at_thirty_metres_is_placed_on_the_level()
        {
            JObject row = Wall(0.0);
            // BEFORE: the consumer would have derived a base offset of -30 000 mm.
            Assert.Equal(-30000.0, DerivedBaseOffsetMm(row), 6);

            double? drawn;
            Assert.True(CadConversionPlanRules.PlaceOnStorey(row, LevelMm, out drawn));
            Assert.Equal(0.0, drawn.Value, 6);
            Assert.Equal(30000.0, ((JArray)row["start"])[2].Value<double>(), 6);
            Assert.Equal(30000.0, ((JArray)row["end"])[2].Value<double>(), 6);
            Assert.Equal(0.0, DerivedBaseOffsetMm(row), 6);
            // plan coordinates are untouched: only the height moved
            Assert.Equal(1000.0, ((JArray)row["start"])[0].Value<double>(), 6);
            Assert.Equal(2000.0, ((JArray)row["end"])[1].Value<double>(), 6);
        }

        [Fact]
        public void A_declared_offset_is_the_offset_the_consumer_derives_and_agrees_with()
        {
            // create_elements refuses an 'offset' that disagrees with Z - level ("Supply consistent coordinates
            // and offset"); with the drawing's Z that held only on a level at 0.
            JObject row = Wall(0.0, offset: 150.0);
            double? drawn;
            Assert.True(CadConversionPlanRules.PlaceOnStorey(row, LevelMm, out drawn));
            Assert.Equal(30150.0, ((JArray)row["start"])[2].Value<double>(), 6);
            Assert.Equal(row.Value<double>("offset"), DerivedBaseOffsetMm(row), 6);
        }

        [Fact]
        public void A_link_that_already_sits_at_the_level_is_not_lifted_twice()
        {
            JObject row = Wall(30000.0);
            double? drawn;
            Assert.False(CadConversionPlanRules.PlaceOnStorey(row, LevelMm, out drawn));
            Assert.Equal(30000.0, drawn.Value, 6);
            Assert.Equal(30000.0, ((JArray)row["start"])[2].Value<double>(), 6);
        }

        [Fact]
        public void A_wall_on_a_ground_level_at_zero_is_unchanged()
        {
            JObject row = Wall(0.0);
            double? drawn;
            Assert.False(CadConversionPlanRules.PlaceOnStorey(row, 0.0, out drawn));
            Assert.Equal(0.0, ((JArray)row["start"])[2].Value<double>(), 6);
        }

        [Fact]
        public void A_drawn_slope_is_left_as_drawn()
        {
            var row = new JObject
            {
                ["kind"] = "wall",
                ["start"] = new JArray(0.0, 0.0, 0.0),
                ["end"] = new JArray(5000.0, 0.0, 300.0)
            };
            double? drawn;
            Assert.False(CadConversionPlanRules.PlaceOnStorey(row, LevelMm, out drawn));
            Assert.Null(drawn);
            Assert.Equal(300.0, ((JArray)row["end"])[2].Value<double>(), 6);
        }

        [Fact]
        public void A_floor_profile_is_placed_on_the_level_with_its_offset()
        {
            var row = new JObject
            {
                ["kind"] = "floor",
                ["offset"] = -50.0,
                ["profile"] = new JArray
                {
                    new JArray(new JArray(0.0, 0.0, 0.0), new JArray(4000.0, 0.0, 0.0), new JArray(4000.0, 3000.0, 0.0)),
                    new JArray(new JArray(1000.0, 1000.0, 0.0), new JArray(1500.0, 1000.0, 0.0), new JArray(1500.0, 1500.0, 0.0))
                }
            };
            double? drawn;
            Assert.True(CadConversionPlanRules.PlaceOnStorey(row, LevelMm, out drawn));
            foreach (JArray loop in (JArray)row["profile"])
                foreach (JArray p in loop)
                    Assert.Equal(29950.0, p[2].Value<double>(), 6);
        }

        [Fact]
        public void An_absolute_column_point_stands_on_the_level_and_a_level_offset_point_is_already_relative()
        {
            var column = new JObject
            {
                ["kind"] = "structural_column",
                ["coordinate_mode"] = "absolute",
                ["point"] = new JArray(500.0, 500.0, 0.0)
            };
            double? drawn;
            Assert.True(CadConversionPlanRules.PlaceOnStorey(column, LevelMm, out drawn));
            Assert.Equal(30000.0, ((JArray)column["point"])[2].Value<double>(), 6);

            var device = new JObject
            {
                ["kind"] = "family_instance",
                ["coordinate_mode"] = "level_offset",
                ["point"] = new JArray(500.0, 500.0, 1200.0)
            };
            Assert.False(CadConversionPlanRules.PlaceOnStorey(device, LevelMm, out drawn));
            Assert.Equal(1200.0, ((JArray)device["point"])[2].Value<double>(), 6);
        }

        [Fact]
        public void An_mep_run_keeps_its_own_rule()
        {
            var duct = new JObject
            {
                ["kind"] = "duct",
                ["start"] = new JArray(0.0, 0.0, 0.0),
                ["end"] = new JArray(1000.0, 0.0, 0.0)
            };
            double? drawn;
            Assert.False(CadConversionPlanRules.PlaceOnStorey(duct, LevelMm, out drawn));
            Assert.Equal(0.0, ((JArray)duct["start"])[2].Value<double>(), 6);
        }
    }
}
