// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// View-set, gbXML and .rfa exports: file names are decided (and collisions refused)
// before anything is written, headers are judged from real signatures, and a gbXML
// with no Space is refused as an empty campus.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.IO;
using System.Text;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ExportFormatRulesTests
    {
        private static List<ExportViewFacts> Views(params ExportViewFacts[] views) { return new List<ExportViewFacts>(views); }
        private static ExportViewFacts View(long id, string name) { return new ExportViewFacts { Id = id, Name = name }; }
        private static ExportViewFacts Sheet(long id, string number, string name) { return new ExportViewFacts { Id = id, Name = name, IsSheet = true, SheetNumber = number }; }

        [Fact]
        public void Ordinal_naming_matches_the_pdf_shape()
        {
            string refusal;
            List<string> stems = ExportFormatRules.SetStems("set", ExportFormatRules.NamingOrdinal, Views(View(12, "A"), View(7, "B")), out refusal);
            Assert.Null(refusal);
            Assert.Equal(new[] { "set-001-12", "set-002-7" }, stems);
        }

        [Fact]
        public void View_name_naming_sanitizes_and_replaces_dots()
        {
            string refusal;
            List<string> stems = ExportFormatRules.SetStems("set", ExportFormatRules.NamingViewName, Views(View(1, "Level 1.5 / North")), out refusal);
            Assert.Null(refusal);
            Assert.Equal("set-Level 1_5 _ North", stems[0]);
        }

        [Fact]
        public void Two_views_that_would_write_one_file_refuse_by_name()
        {
            string refusal;
            List<string> stems = ExportFormatRules.SetStems("set", ExportFormatRules.NamingViewName, Views(View(1, "Plan:A"), View(2, "plan/A")), out refusal);
            Assert.Null(stems);
            Assert.Contains("views 1 and 2", refusal);
        }

        [Fact]
        public void Sheet_number_naming_refuses_a_view_that_is_not_a_sheet()
        {
            string refusal;
            Assert.Null(ExportFormatRules.SetStems("set", ExportFormatRules.NamingSheetNumber, Views(Sheet(3, "A-101", "Plan"), View(4, "3D")), out refusal));
            Assert.Contains("view 4", refusal);
            List<string> stems = ExportFormatRules.SetStems("set", ExportFormatRules.NamingSheetNumber, Views(Sheet(3, "A-101", "Plan")), out refusal);
            Assert.Equal("set-A-101-Plan", stems[0]);
        }

        [Fact]
        public void A_repeated_view_and_an_unknown_rule_refuse()
        {
            string refusal;
            Assert.Null(ExportFormatRules.SetStems("set", ExportFormatRules.NamingOrdinal, Views(View(1, "A"), View(1, "A")), out refusal));
            Assert.Contains("repeats", refusal);
            Assert.Null(ExportFormatRules.SetStems("set", "by_level", Views(View(1, "A")), out refusal));
            Assert.Contains("file_naming", refusal);
        }

        [Fact]
        public void Family_names_that_collide_on_disk_refuse()
        {
            string refusal;
            Assert.Null(ExportFormatRules.FamilyStems(new[] { "Door:Single", "door_single" }, out refusal));
            Assert.Contains("door_single", refusal);
            Assert.Equal(new[] { "Desk", "Chair 900" }, ExportFormatRules.FamilyStems(new[] { "Desk", "Chair 900" }, out refusal));
        }

        [Fact]
        public void Headers_are_judged_from_real_signatures()
        {
            Assert.Equal("AC1032", ExportFormatRules.HeaderKindOf("dwg", Encoding.ASCII.GetBytes("AC1032\0\0")));
            Assert.Null(ExportFormatRules.HeaderKindOf("dwg", Encoding.ASCII.GetBytes("%PDF-1.7")));
            Assert.Equal("dgn_v8_structured_storage", ExportFormatRules.HeaderKindOf("dgn", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }));
            Assert.Equal("dgn_v7", ExportFormatRules.HeaderKindOf("dgn", new byte[] { 0xC8, 0x09, 0xFE, 0x02 }));
            Assert.Null(ExportFormatRules.HeaderKindOf("dgn", Encoding.ASCII.GetBytes("AC1032")));
            Assert.Equal("zip_package", ExportFormatRules.HeaderKindOf("dwfx", new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0, 0, 0 }));
            Assert.Null(ExportFormatRules.HeaderKindOf("dwfx", new byte[0]));
        }

        private static GbXmlCounts Count(string xml)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml))) return ExportFormatRules.CountGbXml(stream);
        }

        [Fact]
        public void A_gbxml_with_spaces_and_zones_holds_up()
        {
            GbXmlCounts counts = Count("<gbXML xmlns=\"http://www.gbxml.org/schema\"><Campus><Building><Space id=\"a\"/><Space id=\"b\"><AdjacentSpaceId/></Space></Building>" +
                                       "<Surface/><Surface><Opening/></Surface></Campus><Zone/></gbXML>");
            Assert.Equal("gbXML", counts.Root);
            Assert.Equal(2, counts.Space);
            Assert.Equal(1, counts.Zone);
            Assert.Equal(2, counts.Surface);
            Assert.Equal(1, counts.Opening);
            Assert.Null(ExportFormatRules.GbXmlProblem(counts));
        }

        [Fact]
        public void An_empty_campus_is_refused_as_no_spaces()
        {
            string problem = ExportFormatRules.GbXmlProblem(Count("<gbXML><Campus><Building/></Campus></gbXML>"));
            Assert.StartsWith("no spaces", problem);
            Assert.Contains("root element", ExportFormatRules.GbXmlProblem(Count("<other><Space/></other>")));
        }
    }
}
