using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class EnergyReadinessRulesTests
    {
        [Theory]
        [InlineData(0, 1, 0)]
        [InlineData(1, 0, 90)]
        [InlineData(0, -1, 180)]
        [InlineData(-1, 0, 270)]
        [InlineData(1, 1, 45)]
        public void The_azimuth_runs_clockwise_from_north_like_Revits_own_definition(double nx, double ny, double want)
        {
            Assert.Equal(want, EnergyReadinessRules.Azimuth(nx, ny).Value, 6);
        }

        [Fact]
        public void A_vertical_normal_has_no_orientation_rather_than_north()
        {
            Assert.Null(EnergyReadinessRules.Azimuth(0, 0));
            Assert.Null(EnergyReadinessRules.Azimuth(1e-9, -1e-9));
        }

        [Theory]
        [InlineData(0, "N")]
        [InlineData(314.999, "W")]
        [InlineData(315, "N")]
        [InlineData(44.999, "N")]
        [InlineData(45, "E")]
        [InlineData(135, "S")]
        [InlineData(225, "W")]
        [InlineData(-90, "W")]
        [InlineData(450, "E")]
        public void Every_azimuth_lands_in_exactly_one_sector_and_a_boundary_goes_clockwise(double azimuth, string want)
        {
            Assert.Equal(want, EnergyReadinessRules.Sector(azimuth));
        }

        [Fact]
        public void A_sector_without_walls_has_no_ratio_while_a_blank_wall_has_zero()
        {
            var walls = new[]
            {
                new EnergyReadinessRules.WallSample { Sector = "N", WallArea = 20, WindowArea = 5, Windows = 2 },
                new EnergyReadinessRules.WallSample { Sector = "N", WallArea = 20, WindowArea = 3, Windows = 1, DoorArea = 2, Doors = 1 },
                new EnergyReadinessRules.WallSample { Sector = "E", WallArea = 12 }
            };
            JObject r = EnergyReadinessRules.ByOrientation(walls);
            JObject[] rows = ((JArray)r["by_orientation"]).Cast<JObject>().ToArray();
            Assert.Equal(new[] { "N", "E", "S", "W" }, rows.Select(x => (string)x["orientation"]).ToArray());
            Assert.Equal(0.2, (double)rows[0]["wwr"], 6);
            Assert.Equal(3, (int)rows[0]["windows"]);
            Assert.Equal(2.0, (double)rows[0]["door_area_m2"], 6);
            Assert.Equal(0.0, (double)rows[1]["wwr"], 6);
            Assert.Equal(JTokenType.Null, rows[2]["wwr"].Type);
            Assert.Equal(JTokenType.Null, rows[3]["wwr"].Type);
            Assert.Equal(8.0 / 52.0, (double)r["total"]["wwr"], 3);
            Assert.Equal(EnergyReadinessRules.SectorRule, (string)r["sector_rule"]);
        }

        [Fact]
        public void No_wall_at_all_reports_every_sector_and_the_total_without_a_ratio()
        {
            JObject r = EnergyReadinessRules.ByOrientation(null);
            Assert.Equal(4, ((JArray)r["by_orientation"]).Count);
            Assert.Equal(JTokenType.Null, r["total"]["wwr"].Type);
            Assert.Equal(0, (int)r["total"]["wall_surfaces"]);
        }
    }
}
