// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A MINIMAL .xlsx WRITER, dependency-free: System.IO.Compression and hand-written
// XML, nothing else - the add-in targets net48 for Revit 2023/2024 and net8/net10
// after that, and a spreadsheet package that differs between them is one more
// thing a year could break.
//
// What it writes is the smallest OPC package Excel opens without repair, with the
// ECMA-376 part names and content types spelled out:
//
//   [Content_Types].xml              Default rels/xml + one Override per part
//   _rels/.rels                      officeDocument -> xl/workbook.xml
//   xl/workbook.xml                  one <sheet> per worksheet, in order
//   xl/_rels/workbook.xml.rels       rId1..rIdN worksheets, rIdN+1 styles
//   xl/styles.xml                    two cell formats: normal and bold
//   xl/worksheets/sheetN.xml         row 1 bold and frozen; text as INLINE strings,
//                                    numbers as numbers, empty cells omitted
//
// TEXT IS WRITTEN SO IT READS BACK EXACTLY. XML cannot carry most control
// characters and a parser folds CR LF into LF, so: & < > are escaped, CR is a
// character reference, a character XML cannot hold is written the way Excel
// itself writes it (_xHHHH_, ST_Xstring), and a LITERAL _xHHHH_ in the value gets
// its underscore escaped (_x005F_) so Excel does not decode it into something the
// model never said. XlsxWorkbookReader undoes exactly this.
//
// NUMBERS are formatted once, invariantly, and that text is the cell's identity:
// the reader returns the <v> text, so "what was planned" and "what is on disk"
// compare as strings, never as doubles that happen to round alike.
//
// Revit-free: sheets in, bytes out. Tested in Horizun.Core.Tests.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Horizun.Revit.Core
{
    public enum XlsxCellKind { Empty, Text, Number }

    /// <summary>One cell: empty, text, or a number carried as the exact text written to &lt;v&gt;.</summary>
    public struct XlsxCell
    {
        public XlsxCellKind Kind;
        public string Value;

        public static readonly XlsxCell Empty = new XlsxCell { Kind = XlsxCellKind.Empty, Value = "" };

        /// <summary>Text; null or "" is an empty cell (an empty string is not written).</summary>
        public static XlsxCell Text(string value) =>
            string.IsNullOrEmpty(value) ? Empty : new XlsxCell { Kind = XlsxCellKind.Text, Value = value };

        /// <summary>A finite number, formatted once by <see cref="XlsxWorkbookWriter.FormatNumber"/>.</summary>
        public static XlsxCell Number(double value) =>
            new XlsxCell { Kind = XlsxCellKind.Number, Value = XlsxWorkbookWriter.FormatNumber(value) };

        /// <summary>A number cell exactly as a file carried it (the reader's side).</summary>
        internal static XlsxCell RawNumber(string text) =>
            string.IsNullOrEmpty(text) ? Empty : new XlsxCell { Kind = XlsxCellKind.Number, Value = text };

        /// <summary>The form two cells are compared and hashed in: kind and value, nothing else.</summary>
        public string Canonical =>
            Kind == XlsxCellKind.Text ? "t" + Value : Kind == XlsxCellKind.Number ? "n" + Value : "";
    }

    /// <summary>A worksheet: a name and its rows, row 0 being the header.</summary>
    public sealed class XlsxSheet
    {
        public string Name;
        public readonly List<XlsxCell[]> Rows = new List<XlsxCell[]>();

        public XlsxSheet() { }
        public XlsxSheet(string name) { Name = name; }

        public XlsxCell Cell(int row, int column) =>
            row >= 0 && row < Rows.Count && column >= 0 && column < Rows[row].Length ? Rows[row][column] : XlsxCell.Empty;
    }

    public static class XlsxWorkbookWriter
    {
        public const string MainNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        public const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        public const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
        public const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";

        public const string WorkbookContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
        public const string WorksheetContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
        public const string StylesContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml";
        public const string RelationshipsContentType = "application/vnd.openxmlformats-package.relationships+xml";

        public const string OfficeDocumentRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
        public const string WorksheetRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";
        public const string StylesRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";

        /// <summary>Excel's own limits (Office "Excel specifications and limits").</summary>
        public const int MaxRows = 1048576, MaxColumns = 16384, MaxCellCharacters = 32767, MaxSheetNameLength = 31;

        /// <summary>
        /// Every entry is stamped with this time, so the same sheets give the same package
        /// bytes on the same runtime. The workbook's own dates live in its cells.
        /// </summary>
        public static readonly DateTimeOffset EntryTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private const string XmlDeclaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
        private const string ForbiddenSheetNameCharacters = "[]:*?/\\";
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// The text a number is written as: round-trip form, invariant culture, negative zero
        /// as 0. NaN and infinities have no spreadsheet form and are refused.
        /// </summary>
        public static string FormatNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException("a spreadsheet number must be finite; got " + value.ToString(CultureInfo.InvariantCulture) + ".");
            if (value == 0) return "0";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>0 -> A, 25 -> Z, 26 -> AA.</summary>
        public static string ColumnName(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            var sb = new StringBuilder();
            int n = index + 1;
            while (n > 0)
            {
                int rem = (n - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                n = (n - 1) / 26;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Everything that would make the package unreadable or silently altered by Excel,
        /// named. Empty when the sheets can be written as they are.
        /// </summary>
        public static List<string> Validate(IList<XlsxSheet> sheets)
        {
            var problems = new List<string>();
            if (sheets == null || sheets.Count == 0) { problems.Add("a workbook needs at least one sheet"); return problems; }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XlsxSheet sheet in sheets)
            {
                string name = sheet?.Name;
                if (string.IsNullOrWhiteSpace(name)) { problems.Add("a sheet has no name"); continue; }
                if (name.Length > MaxSheetNameLength) problems.Add("sheet name '" + name + "' is longer than " + MaxSheetNameLength + " characters");
                if (name.IndexOfAny(ForbiddenSheetNameCharacters.ToCharArray()) >= 0) problems.Add("sheet name '" + name + "' contains one of " + ForbiddenSheetNameCharacters);
                if (name.StartsWith("'", StringComparison.Ordinal) || name.EndsWith("'", StringComparison.Ordinal)) problems.Add("sheet name '" + name + "' starts or ends with an apostrophe");
                if (!seen.Add(name)) problems.Add("sheet name '" + name + "' is used twice (Excel compares sheet names without case)");
                if (sheet.Rows.Count > MaxRows) problems.Add("sheet '" + name + "' has " + sheet.Rows.Count + " rows; Excel holds " + MaxRows);
                for (int r = 0; r < sheet.Rows.Count; r++)
                {
                    XlsxCell[] row = sheet.Rows[r] ?? new XlsxCell[0];
                    if (row.Length > MaxColumns) { problems.Add("sheet '" + name + "' row " + (r + 1) + " has " + row.Length + " columns; Excel holds " + MaxColumns); continue; }
                    for (int c = 0; c < row.Length; c++)
                        if (row[c].Kind == XlsxCellKind.Text && row[c].Value.Length > MaxCellCharacters)
                            problems.Add("sheet '" + name + "' cell " + ColumnName(c) + (r + 1) + " holds " + row[c].Value.Length +
                                         " characters; an Excel cell holds " + MaxCellCharacters);
                }
            }
            return problems;
        }

        /// <summary>Writes a NEW file (never replaces one); refuses sheets <see cref="Validate"/> rejects.</summary>
        public static void WriteFile(string path, IList<XlsxSheet> sheets)
        {
            List<string> problems = Validate(sheets);
            if (problems.Count > 0) throw new ArgumentException("the workbook cannot be written: " + string.Join("; ", problems) + ".");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Write(stream, sheets);
        }

        /// <summary>The package as bytes (tests, and callers that hash before writing).</summary>
        public static byte[] ToBytes(IList<XlsxSheet> sheets)
        {
            List<string> problems = Validate(sheets);
            if (problems.Count > 0) throw new ArgumentException("the workbook cannot be written: " + string.Join("; ", problems) + ".");
            using (var ms = new MemoryStream())
            {
                Write(ms, sheets);
                return ms.ToArray();
            }
        }

        private static void Write(Stream output, IList<XlsxSheet> sheets)
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                Part(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
                Part(zip, "_rels/.rels", XmlDeclaration +
                    "<Relationships xmlns=\"" + PackageRelationshipsNamespace + "\">" +
                    "<Relationship Id=\"rId1\" Type=\"" + OfficeDocumentRelationship + "\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");
                Part(zip, "xl/workbook.xml", Workbook(sheets));
                Part(zip, "xl/_rels/workbook.xml.rels", WorkbookRelationships(sheets.Count));
                Part(zip, "xl/styles.xml", Styles);
                for (int i = 0; i < sheets.Count; i++)
                {
                    ZipArchiveEntry entry = zip.CreateEntry("xl/worksheets/sheet" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".xml", CompressionLevel.Optimal);
                    entry.LastWriteTime = EntryTime;
                    using (var writer = new StreamWriter(entry.Open(), Utf8NoBom))
                        WriteSheet(writer, sheets[i]);
                }
            }
        }

        private static void Part(ZipArchive zip, string name, string xml)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = EntryTime;
            using (Stream s = entry.Open())
            {
                byte[] bytes = Utf8NoBom.GetBytes(xml);
                s.Write(bytes, 0, bytes.Length);
            }
        }

        private static string ContentTypes(int sheetCount)
        {
            var sb = new StringBuilder(XmlDeclaration);
            sb.Append("<Types xmlns=\"").Append(ContentTypesNamespace).Append("\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"").Append(RelationshipsContentType).Append("\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"").Append(WorkbookContentType).Append("\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"").Append(StylesContentType).Append("\"/>");
            for (int i = 1; i <= sheetCount; i++)
                sb.Append("<Override PartName=\"/xl/worksheets/sheet").Append(i.ToString(CultureInfo.InvariantCulture))
                  .Append(".xml\" ContentType=\"").Append(WorksheetContentType).Append("\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string Workbook(IList<XlsxSheet> sheets)
        {
            var sb = new StringBuilder(XmlDeclaration);
            sb.Append("<workbook xmlns=\"").Append(MainNamespace).Append("\" xmlns:r=\"").Append(RelationshipsNamespace).Append("\">");
            sb.Append("<bookViews><workbookView/></bookViews><sheets>");
            for (int i = 0; i < sheets.Count; i++)
                sb.Append("<sheet name=\"").Append(EscapeAttribute(sheets[i].Name)).Append("\" sheetId=\"")
                  .Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("\" r:id=\"rId")
                  .Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("\"/>");
            sb.Append("</sheets></workbook>");
            return sb.ToString();
        }

        private static string WorkbookRelationships(int sheetCount)
        {
            var sb = new StringBuilder(XmlDeclaration);
            sb.Append("<Relationships xmlns=\"").Append(PackageRelationshipsNamespace).Append("\">");
            for (int i = 1; i <= sheetCount; i++)
                sb.Append("<Relationship Id=\"rId").Append(i.ToString(CultureInfo.InvariantCulture)).Append("\" Type=\"")
                  .Append(WorksheetRelationship).Append("\" Target=\"worksheets/sheet").Append(i.ToString(CultureInfo.InvariantCulture)).Append(".xml\"/>");
            sb.Append("<Relationship Id=\"rId").Append((sheetCount + 1).ToString(CultureInfo.InvariantCulture)).Append("\" Type=\"")
              .Append(StylesRelationship).Append("\" Target=\"styles.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        // The smallest stylesheet Excel accepts without repair: the two fills it insists on
        // (none, gray125), one border, and cell format 1 = bold for the header row.
        private const string Styles = XmlDeclaration +
            "<styleSheet xmlns=\"" + MainNamespace + "\">" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
            "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>";

        private static void WriteSheet(TextWriter w, XlsxSheet sheet)
        {
            int width = 1;
            foreach (XlsxCell[] row in sheet.Rows) if (row != null && row.Length > width) width = row.Length;
            int lastRow = Math.Max(1, sheet.Rows.Count);
            w.Write(XmlDeclaration);
            w.Write("<worksheet xmlns=\"" + MainNamespace + "\">");
            w.Write("<dimension ref=\"A1:" + ColumnName(width - 1) + lastRow.ToString(CultureInfo.InvariantCulture) + "\"/>");
            // The header stays in view while the rows scroll.
            if (sheet.Rows.Count > 1)
                w.Write("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            w.Write("<cols><col min=\"1\" max=\"" + width.ToString(CultureInfo.InvariantCulture) + "\" width=\"22\" customWidth=\"1\"/></cols>");
            w.Write("<sheetData>");
            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                string rowNumber = (r + 1).ToString(CultureInfo.InvariantCulture);
                w.Write("<row r=\"" + rowNumber + "\">");
                XlsxCell[] row = sheet.Rows[r] ?? new XlsxCell[0];
                for (int c = 0; c < row.Length; c++)
                {
                    XlsxCell cell = row[c];
                    if (cell.Kind == XlsxCellKind.Empty) continue;
                    string reference = ColumnName(c) + rowNumber;
                    string style = r == 0 ? " s=\"1\"" : "";
                    if (cell.Kind == XlsxCellKind.Number)
                        w.Write("<c r=\"" + reference + "\"" + style + "><v>" + cell.Value + "</v></c>");
                    else
                        w.Write("<c r=\"" + reference + "\"" + style + " t=\"inlineStr\"><is><t xml:space=\"preserve\">" + EscapeText(cell.Value) + "</t></is></c>");
                }
                w.Write("</row>");
            }
            w.Write("</sheetData></worksheet>");
        }

        /// <summary>
        /// Cell text as XML element content that Excel and <see cref="XlsxWorkbookReader"/> both
        /// read back as exactly <paramref name="value"/>.
        /// </summary>
        public static string EscapeText(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length + 16);
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (ch == '_' && LooksLikeEscape(value, i)) { sb.Append("_x005F_"); continue; }
                if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    sb.Append(ch).Append(value[i + 1]);
                    i++;
                    continue;
                }
                if (ch == '&') sb.Append("&amp;");
                else if (ch == '<') sb.Append("&lt;");
                else if (ch == '>') sb.Append("&gt;");
                else if (ch == '\r') sb.Append("&#xD;");
                else if (ch == '\t' || ch == '\n' || (ch >= 0x20 && ch <= 0xD7FF) || (ch >= 0xE000 && ch <= 0xFFFD)) sb.Append(ch);
                else sb.Append("_x").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture)).Append('_');
            }
            return sb.ToString();
        }

        /// <summary>True when value[i..i+6] reads as _xHHHH_, which Excel would decode.</summary>
        internal static bool LooksLikeEscape(string value, int i) =>
            i + 6 < value.Length && value[i] == '_' && value[i + 1] == 'x' &&
            IsHex(value[i + 2]) && IsHex(value[i + 3]) && IsHex(value[i + 4]) && IsHex(value[i + 5]) && value[i + 6] == '_';

        internal static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

        private static string EscapeAttribute(string value) =>
            (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
