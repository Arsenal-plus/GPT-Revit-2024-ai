using System;
using System.IO;
using System.Text;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LandXmlTinRulesTests
    {
        private const string Head = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><LandXML xmlns=\"http://www.landxml.org/schema/LandXML-1.2\" version=\"1.2\">";
        // Four corners and a centre, northing easting elevation; point 6 is touched only by an invisible face.
        private const string Pnts = "<P id=\"1\">100 200 10</P><P id=\"2\">100 210 11</P><P id=\"3\">110 210 12</P><P id=\"4\">110 200 13</P>" +
                                    "<P id=\"5\">105 205 15</P><P id=\"6\">500 500 0</P>";
        private const string Faces = "<F>1 2 5</F><F>2 3 5</F><F>3 4 5</F><F>4 1 5</F><F i=\"1\">1 6 2</F>";

        private static string Units(string system, string unit) => "<Units><" + system + " linearUnit=\"" + unit + "\" areaUnit=\"squareMeter\"/></Units>";
        private static string Surface(string name, string pnts, string faces = "", string type = "TIN")
            => "<Surface name=\"" + name + "\"><Definition surfType=\"" + type + "\"><Pnts>" + pnts + "</Pnts>" +
               (faces.Length > 0 ? "<Faces>" + faces + "</Faces>" : "") + "</Definition></Surface>";
        private static string Doc(string units, params string[] surfaces) => Head + units + "<Surfaces>" + string.Concat(surfaces) + "</Surfaces></LandXML>";
        private static string Read(string xml, string surface, out LandXmlTin tin)
        {
            using (var s = new MemoryStream(Encoding.UTF8.GetBytes(xml))) return LandXmlTinRules.Read(s, surface, out tin);
        }
        private static string Read(string xml, string surface = null) => Read(xml, surface, out LandXmlTin _);

        [Fact]
        public void A_tin_reads_east_north_elevation_in_metres_and_visible_faces_choose_the_points()
        {
            Assert.Null(Read(Doc(Units("Metric", "meter"), Surface("EG", Pnts, Faces)), null, out LandXmlTin tin));
            Assert.Equal("EG", tin.Surface);
            Assert.Equal(6, tin.PointsInFile); Assert.Equal(5, tin.PointsMetres.Count); Assert.Equal(1, tin.PointsUnused);
            Assert.Equal(4, tin.FacesVisible); Assert.Equal(1, tin.FacesInvisible);
            Assert.Equal(new[] { "1", "2", "3", "4", "5" }, tin.PointIds);
            Assert.Equal(new[] { 200.0, 100.0, 10.0 }, tin.PointsMetres[0]);
            Assert.Equal(new[] { 205.0, 105.0, 15.0 }, tin.PointsMetres[4]);
        }

        [Fact]
        public void Without_faces_every_point_is_used()
        {
            Assert.Null(Read(Doc(Units("Metric", "meter"), Surface("EG", Pnts)), null, out LandXmlTin tin));
            Assert.Equal(6, tin.PointsMetres.Count);
        }

        [Theory]
        [InlineData("Metric", "millimeter", 0.2)]
        [InlineData("Imperial", "foot", 60.96)]
        [InlineData("Imperial", "USSurveyFoot", 200 * 1200.0 / 3937.0)]
        public void The_declared_unit_converts_to_metres(string system, string unit, double east)
        {
            Assert.Null(Read(Doc(Units(system, unit), Surface("EG", Pnts, Faces)), null, out LandXmlTin tin));
            Assert.Equal(east, tin.PointsMetres[0][0], 9);
        }

        [Fact] public void A_file_without_a_linear_unit_is_refused() => Assert.Contains("no linear unit", Read(Doc("", Surface("EG", Pnts, Faces))));
        [Fact] public void An_unknown_unit_is_refused_by_name() => Assert.Contains("'mile'", Read(Doc(Units("Imperial", "mile"), Surface("EG", Pnts, Faces))));
        [Fact]
        public void A_declared_elevation_unit_scales_the_heights_and_only_the_heights()
        {
            string units = "<Units><Metric linearUnit=\"meter\" elevationUnit=\"millimeter\" areaUnit=\"squareMeter\"/></Units>";
            Assert.Null(Read(Doc(units, Surface("EG", Pnts, Faces)), null, out LandXmlTin tin));
            Assert.Equal(200.0, tin.PointsMetres[0][0], 9);
            Assert.Equal(0.010, tin.PointsMetres[0][2], 9);
            Assert.Equal("millimeter", tin.ElevationUnit);
        }
        [Fact] public void An_unknown_elevation_unit_is_refused_by_name()
            => Assert.Contains("elevationUnit 'furlong'", Read(Doc("<Units><Metric linearUnit=\"meter\" elevationUnit=\"furlong\"/></Units>", Surface("EG", Pnts, Faces))));
        [Fact] public void A_grid_surface_is_refused() => Assert.Contains("grid", Read(Doc(Units("Metric", "meter"), Surface("EG", Pnts, "", "grid"))));
        [Fact] public void A_point_without_elevation_is_refused_by_id() => Assert.Contains("point '7'", Read(Doc(Units("Metric", "meter"), Surface("EG", Pnts + "<P id=\"7\">1 2</P>"))));
        [Fact] public void A_face_naming_an_undefined_point_is_refused() => Assert.Contains("'99'", Read(Doc(Units("Metric", "meter"), Surface("EG", Pnts, "<F>1 2 99</F>"))));
        [Fact] public void A_repeated_point_id_is_refused() => Assert.Contains("twice", Read(Doc(Units("Metric", "meter"), Surface("EG", Pnts + "<P id=\"1\">0 0 0</P>"))));
        [Fact] public void Only_invisible_faces_leave_nothing_to_take() => Assert.Contains("invisible", Read(Doc(Units("Metric", "meter"), Surface("EG", Pnts, "<F i=\"1\">1 2 3</F>"))));
        [Fact] public void Another_root_is_refused() => Assert.Contains("<gpx>", Read("<gpx></gpx>"));

        [Fact]
        public void A_dtd_is_refused_before_anything_expands()
            => Assert.Contains("not readable", Read("<?xml version=\"1.0\"?><!DOCTYPE LandXML [<!ENTITY x \"y\">]><LandXML><Units><Metric linearUnit=\"meter\"/></Units></LandXML>"));

        [Fact]
        public void Several_surfaces_need_a_name_and_the_name_picks_one()
        {
            string xml = Doc(Units("Metric", "meter"), Surface("EG", Pnts, Faces), Surface("FG", "<P id=\"a\">0 0 1</P><P id=\"b\">0 10 2</P><P id=\"c\">10 0 3</P>"));
            string bad = Read(xml);
            Assert.Contains("2 surfaces", bad); Assert.Contains("'EG'", bad); Assert.Contains("'FG'", bad);
            Assert.Null(Read(xml, "FG", out LandXmlTin tin));
            Assert.Equal("FG", tin.Surface); Assert.Equal(3, tin.PointsMetres.Count);
            Assert.Contains("no surface named 'XX'", Read(xml, "XX"));
        }

        [Fact]
        public void Shared_to_internal_undoes_the_project_position()
        {
            double angle = 30 * Math.PI / 180, eastWest = 100, northSouth = 200, elevation = 5, x = 3, y = 4, z = 1;
            double east = x * Math.Cos(angle) - y * Math.Sin(angle) + eastWest, north = x * Math.Sin(angle) + y * Math.Cos(angle) + northSouth;
            double[] back = LandXmlTinRules.SharedToInternal(east, north, z + elevation, angle, eastWest, northSouth, elevation);
            Assert.Equal(x, back[0], 9); Assert.Equal(y, back[1], 9); Assert.Equal(z, back[2], 9);
        }
    }
}
