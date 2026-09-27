// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// FIEBDC-3 (.bc3) - the writer, the reader and the verifier behind
// horizun_budget_compare operation=export_bc3. Pure text and bytes, no Revit,
// no file system: the server writes the bytes this produces and then hands the
// bytes it READ BACK to Verify, so the file on disk is what gets checked.
//
// The subset written is the one a takeoff needs and nothing more:
//   ~V  the file's identity, character set ANSI (= windows-1252);
//   ~C  one concept per code (unit, summary, unit price, date, type 0) plus a
//       root concept "<ROOT>##" whose price is the budget total;
//   ~D  the root's decomposition: every code with factor 1 and its quantity as
//       the yield;
//   ~M  one measurement record per code whose lines are the ELEMENTS - comment
//       "id <element id>" (or "link <instance> id <element id>"), units = the
//       element's quantity - and whose total equals the yield in ~D.
//   ~K  every decimals slot set to 6, the precision Num writes, so a reader that
//       applies the format's defaults (2 or 3 decimals) does not round what was
//       verified; the currency is left out - it is the caller's to state.
//
// NOTHING IS ESCAPED BY GUESSING. The format has no escape for its own
// separators (~ | \) and windows-1252 has no byte for most of Unicode, so text
// carrying either is REFUSED by name before a byte is written - a description
// silently rewritten is a budget line nobody wrote. Control characters are
// refused for the same reason: a line break inside a field is a new record to
// half the readers in the field.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class Bc3Measure
    {
        public string Comment;
        public double Value;
    }

    public sealed class Bc3Line
    {
        public string Code, Unit, Description;
        public double UnitPrice, Quantity;
        public List<Bc3Measure> Measures = new List<Bc3Measure>();
    }

    public sealed class Bc3Budget
    {
        public string RootCode = "HZ_TAKEOFF";
        public string Title = "Horizun takeoff";
        public string Program = "Horizun Revit MCP";
        public DateTime Date;
        public List<Bc3Line> Lines = new List<Bc3Line>();
    }

    public sealed class Bc3Record
    {
        public string Type;
        /// <summary>Fields after the type, each split into its '\' subfields.</summary>
        public List<string[]> Fields = new List<string[]>();
    }

    public static class Bc3Rules
    {
        // windows-1252 0x80..0x9F; '\0' marks the five bytes the code page leaves undefined.
        private static readonly char[] High = {
            '€', '\0', '‚', 'ƒ', '„', '…', '†', '‡',
            'ˆ', '‰', 'Š', '‹', 'Œ', '\0', 'Ž', '\0',
            '\0', '‘', '’', '“', '”', '•', '–', '—',
            '˜', '™', 'š', '›', 'œ', '\0', 'ž', 'Ÿ' };

        public static bool TryEncodeChar(char c, out byte b)
        {
            b = 0;
            if (c < 0x80 || (c >= 0xA0 && c <= 0xFF)) { b = (byte)c; return true; }
            for (int i = 0; i < High.Length; i++)
                if (High[i] != '\0' && High[i] == c) { b = (byte)(0x80 + i); return true; }
            return false;
        }

        /// <summary>windows-1252 bytes, or null with the first character that has none.</summary>
        public static byte[] Encode1252(string text, out string problem)
        {
            problem = null;
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                byte b;
                if (!TryEncodeChar(text[i], out b))
                { problem = "character U+" + ((int)text[i]).ToString("X4") + " at position " + i + " has no windows-1252 byte."; return null; }
                bytes[i] = b;
            }
            return bytes;
        }

        public static string Decode1252(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length);
            foreach (byte b in bytes)
                sb.Append(b >= 0x80 && b <= 0x9F ? (High[b - 0x80] == '\0' ? '�' : High[b - 0x80]) : (char)b);
            return sb.ToString();
        }

        /// <summary>Null when the text can be written as a FIEBDC-3 ANSI field as-is; otherwise why not.</summary>
        public static string CheckText(string text, string what)
        {
            if (text == null) return null;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '~' || c == '|' || c == '\\')
                    return what + " contains '" + c + "', a FIEBDC-3 separator the format cannot escape; remove it at the source.";
                if (c < 0x20 || c == 0x7F)
                    return what + " contains a control character (U+" + ((int)c).ToString("X4") + "); a line break inside a field splits the record for many readers.";
                byte b;
                if (!TryEncodeChar(c, out b))
                    return what + " contains U+" + ((int)c).ToString("X4") + " ('" + c + "'), which windows-1252 (FIEBDC ANSI) cannot represent.";
            }
            return null;
        }

        /// <summary>
        /// A code is text with no whitespace, no '#' (FIEBDC reserves it for chapters and the root)
        /// and no '%' or '&' (FIEBDC reads a child code carrying either as a percentage line).
        /// </summary>
        public static string CheckCode(string code, string what)
        {
            if (string.IsNullOrWhiteSpace(code)) return what + " is empty.";
            if (code.Any(char.IsWhiteSpace)) return what + " '" + code + "' contains whitespace, which FIEBDC codes do not carry.";
            if (code.IndexOf('#') >= 0) return what + " '" + code + "' contains '#', which FIEBDC reserves for chapters and the root.";
            if (code.IndexOf('%') >= 0 || code.IndexOf('&') >= 0)
                return what + " '" + code + "' contains '%' or '&': FIEBDC-3 reads a child code carrying either as a percentage over the " +
                       "lines before it in the decomposition, so its quantity would be read as a percentage.";
            return CheckText(code, what);
        }

        /// <summary>
        /// ~K: every decimals slot of field 1 (DN DD DS DR DI DP DC DM) and of the 2020 field 3
        /// (DRC DC DFS DRS DUO DI DES DN DD DS DSP DEC) set to 6 - the precision Num writes;
        /// DIVISA and field 2's percentages are left empty: a currency is the caller's to state.
        /// </summary>
        public static readonly string KRecord =
            "~K|" + string.Concat(Enumerable.Repeat("6\\", 8)) + "\\||" + string.Concat(Enumerable.Repeat("6\\", 12)) + "\\|";

        public static string Num(double v) => Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);

        private static string Date(DateTime d) => d.ToString("ddMMyyyy", CultureInfo.InvariantCulture);

        /// <summary>The total the root carries: sum of quantity x unit price over the WRITTEN values.</summary>
        public static double Total(Bc3Budget b) =>
            b.Lines.Sum(l => Parse(Num(l.Quantity)) * Parse(Num(l.UnitPrice)));

        private static double Parse(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        public static string Write(Bc3Budget b)
        {
            string root = b.RootCode + "##", date = Date(b.Date);
            var sb = new StringBuilder();
            sb.Append("~V||FIEBDC-3/2020\\").Append(date).Append('|').Append(b.Program).Append("||ANSI||2|\r\n");
            sb.Append(KRecord).Append("\r\n");
            sb.Append("~C|").Append(root).Append("||").Append(b.Title).Append('|').Append(Num(Total(b))).Append('|').Append(date).Append("|0|\r\n");
            foreach (Bc3Line l in b.Lines)
                sb.Append("~C|").Append(l.Code).Append('|').Append(l.Unit).Append('|').Append(l.Description ?? "").Append('|')
                  .Append(Num(l.UnitPrice)).Append('|').Append(date).Append("|0|\r\n");
            sb.Append("~D|").Append(root).Append('|');
            foreach (Bc3Line l in b.Lines) sb.Append(l.Code).Append("\\1\\").Append(Num(l.Quantity)).Append('\\');
            sb.Append("|\r\n");
            for (int i = 0; i < b.Lines.Count; i++)
            {
                Bc3Line l = b.Lines[i];
                sb.Append("~M|").Append(root).Append('\\').Append(l.Code).Append('|').Append(i + 1).Append("\\|").Append(Num(l.Quantity)).Append('|');
                foreach (Bc3Measure m in l.Measures)
                    sb.Append('\\').Append(m.Comment).Append('\\').Append(Num(m.Value)).Append("\\\\\\\\");
                sb.Append("|\r\n");
            }
            return sb.ToString();
        }

        /// <summary>Records by '~', fields by '|', subfields by '\'. The empty field after the final '|' is dropped.</summary>
        public static List<Bc3Record> ParseRecords(string text)
        {
            var records = new List<Bc3Record>();
            foreach (string chunk in (text ?? "").Split('~').Skip(1))
            {
                string body = chunk.TrimEnd('\r', '\n', ' ', '\u001A');
                string[] fields = body.Split('|');
                var r = new Bc3Record { Type = fields[0] };
                int last = fields.Length - 1;
                if (last >= 1 && fields[last].Length == 0) last--;
                for (int i = 1; i <= last; i++) r.Fields.Add(fields[i].Split('\\'));
                records.Add(r);
            }
            return records;
        }

        private static bool Close(double a, double b, double tol) => Math.Abs(a - b) <= tol;

        private static bool TryNum(string s, out double v) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        /// <summary>
        /// Re-reads the text a file holds and holds it to the budget it was written from:
        /// ~V (version, ANSI), ~C (every code once, unit, summary, price; the root total), ~D
        /// (every code, factor 1, yield = quantity) and ~M (one per code, total = yield = the sum
        /// of its lines, one line per element with the element's value). Returns the problems;
        /// an empty list is the only verified answer.
        /// </summary>
        public static List<string> Verify(string text, Bc3Budget expected, out JObject counts)
        {
            var problems = new List<string>();
            List<Bc3Record> recs = ParseRecords(text);
            counts = new JObject
            {
                ["V"] = recs.Count(r => r.Type == "V"), ["K"] = recs.Count(r => r.Type == "K"), ["C"] = recs.Count(r => r.Type == "C"),
                ["D"] = recs.Count(r => r.Type == "D"), ["M"] = recs.Count(r => r.Type == "M")
            };
            string root = expected.RootCode + "##";

            Bc3Record v = recs.FirstOrDefault(r => r.Type == "V");
            if (v == null || recs[0].Type != "V") problems.Add("~V is missing or is not the first record.");
            else
            {
                if (v.Fields.Count < 2 || v.Fields[1][0] != "FIEBDC-3/2020") problems.Add("~V does not declare FIEBDC-3/2020.");
                if (v.Fields.Count < 5 || v.Fields[4][0] != "ANSI") problems.Add("~V does not declare the ANSI character set.");
            }
            Bc3Record kRec = recs.FirstOrDefault(r => r.Type == "K");
            if (kRec == null) problems.Add("~K is missing: a reader would round to the format's default decimals.");
            else if (kRec.Fields.Count < 3 || kRec.Fields[0].Take(8).Count(s => s == "6") != 8 || kRec.Fields[2].Take(12).Count(s => s == "6") != 12)
                problems.Add("~K does not declare the 6 decimals the file is written with.");

            var concepts = new Dictionary<string, Bc3Record>(StringComparer.Ordinal);
            foreach (Bc3Record c in recs.Where(r => r.Type == "C"))
            {
                string code = c.Fields.Count > 0 ? c.Fields[0][0] : "";
                if (concepts.ContainsKey(code)) problems.Add("~C " + code + " appears twice.");
                else concepts[code] = c;
            }
            double tol = 1e-6;
            Bc3Record rootC;
            if (!concepts.TryGetValue(root, out rootC)) problems.Add("the root concept " + root + " is missing.");
            else
            {
                double total;
                if (rootC.Fields.Count < 4 || !TryNum(rootC.Fields[3][0], out total) || !Close(total, Parse(Num(Total(expected))), tol))
                    problems.Add("the root's price is not the budget total " + Num(Total(expected)) + ".");
            }
            if (concepts.Count != expected.Lines.Count + 1)
                problems.Add("~C holds " + concepts.Count + " concept(s); expected " + (expected.Lines.Count + 1) + " (the codes plus the root).");

            Bc3Record d = recs.FirstOrDefault(r => r.Type == "D" && r.Fields.Count > 0 && r.Fields[0][0] == root);
            var yield = new Dictionary<string, double>(StringComparer.Ordinal);
            if (d == null || d.Fields.Count < 2) problems.Add("the root decomposition ~D " + root + " is missing.");
            else
            {
                string[] s = d.Fields[1];
                int n = s.Length - (s.Length > 0 && s[s.Length - 1].Length == 0 ? 1 : 0);
                if (n % 3 != 0) problems.Add("~D " + root + " does not hold code\\factor\\yield triples.");
                for (int i = 0; i + 2 < n; i += 3)
                {
                    double f, y;
                    if (!TryNum(s[i + 1], out f) || !Close(f, 1, tol)) problems.Add("~D factor for " + s[i] + " is not 1.");
                    if (!TryNum(s[i + 2], out y)) { problems.Add("~D yield for " + s[i] + " is not a number."); continue; }
                    if (yield.ContainsKey(s[i])) problems.Add("~D lists " + s[i] + " twice.");
                    yield[s[i]] = y;
                }
            }

            var measures = new Dictionary<string, Bc3Record>(StringComparer.Ordinal);
            foreach (Bc3Record m in recs.Where(r => r.Type == "M"))
            {
                string[] key = m.Fields.Count > 0 ? m.Fields[0] : new string[0];
                if (key.Length != 2 || key[0] != root) { problems.Add("~M " + string.Join("\\", key) + " is not under the root."); continue; }
                measures[key[1]] = m;
            }

            foreach (Bc3Line l in expected.Lines)
            {
                Bc3Record c;
                if (!concepts.TryGetValue(l.Code, out c)) { problems.Add("~C " + l.Code + " is missing."); continue; }
                double price;
                if (c.Fields.Count < 4) { problems.Add("~C " + l.Code + " is truncated."); continue; }
                if (c.Fields[1][0] != l.Unit) problems.Add("~C " + l.Code + " unit reads '" + c.Fields[1][0] + "', written '" + l.Unit + "'.");
                if (c.Fields[2][0] != (l.Description ?? "")) problems.Add("~C " + l.Code + " summary does not read back as written.");
                if (!TryNum(c.Fields[3][0], out price) || !Close(price, Parse(Num(l.UnitPrice)), tol))
                    problems.Add("~C " + l.Code + " price reads '" + c.Fields[3][0] + "', expected " + Num(l.UnitPrice) + ".");

                double y;
                double qty = Parse(Num(l.Quantity));
                if (!yield.TryGetValue(l.Code, out y)) problems.Add("~D does not list " + l.Code + ".");
                else if (!Close(y, qty, tol)) problems.Add("~D yield for " + l.Code + " is " + Num(y) + ", the takeoff says " + Num(qty) + ".");

                Bc3Record m;
                if (!measures.TryGetValue(l.Code, out m)) { problems.Add("~M for " + l.Code + " is missing."); continue; }
                double mTotal;
                if (m.Fields.Count < 3 || !TryNum(m.Fields[2][0], out mTotal)) { problems.Add("~M " + l.Code + " has no total."); continue; }
                if (!Close(mTotal, qty, tol)) problems.Add("~M total for " + l.Code + " is " + Num(mTotal) + ", the takeoff says " + Num(qty) + ".");
                string[] lines = m.Fields.Count > 3 ? m.Fields[3] : new string[0];
                // Each line is TYPE\COMMENT\UNITS\LENGTH\WIDTH\HEIGHT\ - six subfields.
                int count = lines.Length / 6;
                if (count != l.Measures.Count) { problems.Add("~M " + l.Code + " holds " + count + " line(s); " + l.Measures.Count + " element(s) were written."); continue; }
                double sum = 0;
                for (int i = 0; i < count; i++)
                {
                    double u;
                    if (lines[i * 6 + 1] != l.Measures[i].Comment) problems.Add("~M " + l.Code + " line " + (i + 1) + " comment does not read back.");
                    if (!TryNum(lines[i * 6 + 2], out u) || !Close(u, Parse(Num(l.Measures[i].Value)), tol))
                        problems.Add("~M " + l.Code + " line " + (i + 1) + " reads '" + lines[i * 6 + 2] + "', written " + Num(l.Measures[i].Value) + ".");
                    else sum += u;
                }
                // Each written value is rounded to 6 decimals, so the lines may sum to the total
                // only within half a millionth per line - named, not hidden.
                if (!Close(sum, mTotal, 1e-6 * Math.Max(1, count)))
                    problems.Add("~M " + l.Code + " lines sum to " + Num(sum) + " and its total is " + Num(mTotal) + ".");
            }
            if (yield.Count != expected.Lines.Count) problems.Add("~D lists " + yield.Count + " code(s); " + expected.Lines.Count + " were written.");
            if (measures.Count != expected.Lines.Count) problems.Add("~M covers " + measures.Count + " code(s); " + expected.Lines.Count + " were written.");
            return problems;
        }
    }
}
