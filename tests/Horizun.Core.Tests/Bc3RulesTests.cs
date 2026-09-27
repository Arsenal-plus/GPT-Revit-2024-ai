// Horizun Revit MCP - original Horizun code.
// Core/Bc3Rules: the FIEBDC-3 writer, reader and verifier behind horizun_budget_compare export_bc3.
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class Bc3RulesTests
    {
        private static Bc3Budget Sample() => new Bc3Budget
        {
            Date = new DateTime(2026, 9, 26),
            Lines = new List<Bc3Line>
            {
                new Bc3Line { Code = "E05.01", Unit = "m3", Description = "Hormigón armado, cimentación €", UnitPrice = 123.45, Quantity = 3.5,
                              Measures = { new Bc3Measure { Comment = "id 101", Value = 1.25 }, new Bc3Measure { Comment = "link 7 id 102", Value = 2.25 } } },
                new Bc3Line { Code = "E08-2", Unit = "m2", Description = "Muro — ladrillo “macizo”", UnitPrice = 10, Quantity = 0.1234567,
                              Measures = { new Bc3Measure { Comment = "id 201", Value = 0.1234567 } } }
            }
        };

        private const string B = "\\";   // one FIEBDC subfield separator, spelled once

        [Fact]
        public void Windows1252_round_trips_every_encodable_character_and_maps_the_high_block()
        {
            string problem;
            var sb = new System.Text.StringBuilder("aZ09 ");
            for (int c = 0xA0; c <= 0xFF; c++) sb.Append((char)c);
            sb.Append("€‚ƒ„…†‡ˆ‰Š‹ŒŽ‘’“”•–—˜™š›œžŸ");
            string text = sb.ToString();
            byte[] bytes = Bc3Rules.Encode1252(text, out problem);
            Assert.Null(problem);
            Assert.Equal(text.Length, bytes.Length);
            Assert.Equal(text, Bc3Rules.Decode1252(bytes));
            Assert.Equal(0x80, Bc3Rules.Encode1252("€", out problem)[0]);
            Assert.Equal(0x97, Bc3Rules.Encode1252("—", out problem)[0]);
            Assert.Equal(0xF1, Bc3Rules.Encode1252("ñ", out problem)[0]);
            Assert.Equal(0x9F, Bc3Rules.Encode1252("Ÿ", out problem)[0]);
        }

        [Fact]
        public void Windows1252_refuses_what_it_cannot_represent_naming_the_character()
        {
            string problem;
            Assert.Null(Bc3Rules.Encode1252("vigas 梁", out problem));
            Assert.Contains("U+6881", problem);
            Assert.Null(Bc3Rules.Encode1252("\u0081", out problem));   // a C1 control, undefined in 1252
            Assert.Equal("�", Bc3Rules.Decode1252(new byte[] { 0x81 }));
        }

        [Theory]
        [InlineData("a|b", "separator")]
        [InlineData("a\\b", "separator")]
        [InlineData("a~b", "separator")]
        [InlineData("a\nb", "control character")]
        [InlineData("Ω", "cannot represent")]
        public void CheckText_refuses_separators_controls_and_unencodable_text(string text, string expected)
        {
            Assert.Contains(expected, Bc3Rules.CheckText(text, "x"));
        }

        [Fact]
        public void CheckCode_reserves_hash_and_whitespace()
        {
            Assert.Null(Bc3Rules.CheckCode("E05.01", "code"));
            Assert.Contains("'#'", Bc3Rules.CheckCode("CAP01#", "code"));
            Assert.Contains("whitespace", Bc3Rules.CheckCode("E 05", "code"));
            Assert.Contains("empty", Bc3Rules.CheckCode(" ", "code"));
        }

        [Fact]
        public void Written_file_carries_the_expected_records()
        {
            string text = Bc3Rules.Write(Sample());
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(8, lines.Length);
            Assert.Equal("~V||FIEBDC-3/2020" + B + "26092026|Horizun Revit MCP||ANSI||2|", lines[0]);
            Assert.Equal(Bc3Rules.KRecord, lines[1]);
            Assert.Equal("~C|HZ_TAKEOFF##||Horizun takeoff|433.30957|26092026|0|", lines[2]);
            Assert.Equal("~C|E05.01|m3|Hormigón armado, cimentación €|123.45|26092026|0|", lines[3]);
            Assert.Equal("~D|HZ_TAKEOFF##|E05.01" + B + "1" + B + "3.5" + B + "E08-2" + B + "1" + B + "0.123457" + B + "|", lines[5]);
            string fourEmpty = B + B + B + B;
            Assert.Equal("~M|HZ_TAKEOFF##" + B + "E05.01|1" + B + "|3.5|" + B + "id 101" + B + "1.25" + fourEmpty +
                         B + "link 7 id 102" + B + "2.25" + fourEmpty + "|", lines[6]);
            Assert.Equal("~M|HZ_TAKEOFF##" + B + "E08-2|2" + B + "|0.123457|" + B + "id 201" + B + "0.123457" + fourEmpty + "|", lines[7]);
        }

        [Fact]
        public void Verify_accepts_what_was_written_after_a_byte_round_trip()
        {
            Bc3Budget b = Sample();
            string problem;
            byte[] bytes = Bc3Rules.Encode1252(Bc3Rules.Write(b), out problem);
            Assert.Null(problem);
            JObject counts;
            List<string> problems = Bc3Rules.Verify(Bc3Rules.Decode1252(bytes), b, out counts);
            Assert.Empty(problems);
            Assert.Equal(1, (int)counts["V"]); Assert.Equal(3, (int)counts["C"]); Assert.Equal(1, (int)counts["D"]); Assert.Equal(2, (int)counts["M"]);
        }

        [Fact]
        public void Verify_catches_a_dropped_element_a_changed_yield_a_twice_listed_code_and_a_measure_outside_the_root()
        {
            // Each edit leaves a file that still parses: only the verifier stands between it and
            // a budget that disagrees with the takeoff it claims to be.
            Bc3Budget b = Sample();
            string text = Bc3Rules.Write(b), fourEmpty = B + B + B + B;
            Func<string, List<string>> verify = t => { JObject c; return Bc3Rules.Verify(t, b, out c); };

            string dropped = text.Replace(B + "link 7 id 102" + B + "2.25" + fourEmpty, "");
            Assert.NotEqual(text, dropped);
            Assert.Contains(verify(dropped), p => p.Contains("~M E05.01 holds 1 line(s); 2 element(s) were written"));

            string yield = text.Replace("E05.01" + B + "1" + B + "3.5" + B, "E05.01" + B + "1" + B + "3.6" + B);
            Assert.NotEqual(text, yield);
            Assert.Contains(verify(yield), p => p.Contains("~D yield for E05.01 is 3.6, the takeoff says 3.5"));

            string twice = text + "~C|E05.01|m3|otra|1|26092026|0|\r\n";
            Assert.Contains(verify(twice), p => p.Contains("~C E05.01 appears twice"));

            string outside = text.Replace("~M|HZ_TAKEOFF##" + B + "E08-2|", "~M|OTRO##" + B + "E08-2|");
            Assert.NotEqual(text, outside);
            List<string> po = verify(outside);
            Assert.Contains(po, p => p.Contains("is not under the root"));
            Assert.Contains(po, p => p.Contains("~M for E08-2 is missing"));
        }

        [Fact]
        public void Verify_catches_a_changed_price_a_changed_measure_a_missing_record_and_a_wrong_charset()
        {
            Bc3Budget b = Sample();
            string text = Bc3Rules.Write(b);
            JObject counts;
            Assert.Contains(Bc3Rules.Verify(text.Replace("|123.45|", "|123.46|"), b, out counts), p => p.Contains("price reads '123.46'"));
            Assert.Contains(Bc3Rules.Verify(text.Replace(B + "id 101" + B + "1.25" + B, B + "id 101" + B + "1.35" + B), b, out counts),
                            p => p.Contains("line 1 reads '1.35'"));
            string noM = string.Join("\r\n", text.Split(new[] { "\r\n" }, StringSplitOptions.None).Where(l => !l.StartsWith("~M|HZ_TAKEOFF##" + B + "E08-2")));
            Assert.Contains(Bc3Rules.Verify(noM, b, out counts), p => p.Contains("~M for E08-2 is missing"));
            Assert.Contains(Bc3Rules.Verify(text.Replace("|ANSI|", "|850|"), b, out counts), p => p.Contains("ANSI"));
            Assert.Contains(Bc3Rules.Verify(text.Replace("E05.01" + B + "1" + B + "3.5" + B, "E05.01" + B + "1" + B + "3.6" + B), b, out counts),
                            p => p.Contains("~D yield for E05.01"));
            Assert.Contains(Bc3Rules.Verify(text.Replace("|433.30957|", "|433.31|"), b, out counts), p => p.Contains("budget total"));
        }

        [Fact]
        public void Verify_catches_a_description_mangled_by_a_wrong_code_page()
        {
            Bc3Budget b = Sample();
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(Bc3Rules.Write(b));   // the classic mistake: UTF-8 bytes labelled ANSI
            JObject counts;
            Assert.Contains(Bc3Rules.Verify(Bc3Rules.Decode1252(utf8), b, out counts), p => p.Contains("summary does not read back"));
        }

        [Fact]
        public void Total_and_numbers_are_computed_over_the_written_values()
        {
            Assert.Equal(3.5 * 123.45 + 0.123457 * 10, Bc3Rules.Total(Sample()), 9);
            Assert.Equal("0.123457", Bc3Rules.Num(0.1234567));
            Assert.Equal("12", Bc3Rules.Num(12.0));
            Assert.Equal("-1.5", Bc3Rules.Num(-1.5));
        }

        [Fact]
        public void ParseRecords_splits_records_fields_and_subfields()
        {
            List<Bc3Record> r = Bc3Rules.ParseRecords("~C|A|m|x|1|01012026|0|\r\n~D|R##|A" + B + "1" + B + "2" + B + "|\r\n");
            Assert.Equal(2, r.Count);
            Assert.Equal("C", r[0].Type);
            Assert.Equal(6, r[0].Fields.Count);
            Assert.Equal(new[] { "A", "1", "2", "" }, r[1].Fields[1]);
        }

        [Theory]
        [InlineData("E05%")]
        [InlineData("E&05")]
        public void CheckCode_refuses_the_FIEBDC_percentage_characters(string code)
        {
            string why = Bc3Rules.CheckCode(code, "apu[0].code");
            Assert.NotNull(why);
            Assert.Contains("percentage", why);
        }

        [Fact]
        public void Write_declares_six_decimals_in_K_and_Verify_names_a_file_without_them()
        {
            var b = new Bc3Budget { Date = new DateTime(2026, 9, 26) };
            var line = new Bc3Line { Code = "A1", Unit = "m2", Description = "x", UnitPrice = 10.125, Quantity = 1.234567 };
            line.Measures.Add(new Bc3Measure { Comment = "id 1", Value = 1.234567 });
            b.Lines.Add(line);
            string text = Bc3Rules.Write(b);
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("~K|6\\6\\6\\6\\6\\6\\6\\6\\\\||6\\6\\6\\6\\6\\6\\6\\6\\6\\6\\6\\6\\\\|", lines[1]);
            JObject counts;
            Assert.Empty(Bc3Rules.Verify(text, b, out counts));
            Assert.Equal(1, (int)counts["K"]);
            string noK = string.Join("\r\n", lines.Where(l => !l.StartsWith("~K"))) + "\r\n";
            Assert.Contains(Bc3Rules.Verify(noK, b, out counts), p => p.Contains("~K is missing"));
            string fewer = text.Replace(lines[1], "~K|2\\2\\2\\3\\2\\2\\2\\2\\\\||");
            Assert.Contains(Bc3Rules.Verify(fewer, b, out counts), p => p.Contains("6 decimals"));
        }
    }
}
