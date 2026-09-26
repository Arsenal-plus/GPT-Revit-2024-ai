// Horizun Revit MCP - original Horizun code.
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    // BuiltInCategory and BuiltInParameter are Int32-backed in Revit 2023 and Int64-backed
    // from Revit 2024 (MEASURED 2026-09-26 by reflection on each year's RevitAPI.dll).
    // Enum.IsDefined(typeof(BuiltInCategory), (int)v) compiles against every year and THROWS
    // at run time on 2024-2027 ("Enum underlying type and the object must be same type").
    // Behind a catch it silently became "no category": every requirement-set rule with a
    // category selector matched nothing in horizun_code_check on 2024-2027. The enum
    // itself - (BuiltInCategory)v - works on both widths.
    public sealed class BuiltInEnumWidthTests
    {
        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return dir;
        }

        [Fact]
        public void No_built_in_enum_is_tested_or_named_through_a_boxed_int()
        {
            var bad = new Regex(@"(IsDefined|GetName)\s*\(\s*typeof\s*\(\s*(Autodesk\.Revit\.DB\.)?BuiltIn(Category|Parameter)\s*\)\s*,\s*\(\s*int\s*\)");
            string src = Path.Combine(RepoRoot(), "src");
            var hits = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
                .SelectMany(f => File.ReadAllLines(f).Select((line, i) => new { f, i, line }))
                .Where(x => bad.IsMatch(x.line))
                .Select(x => Path.GetFileName(x.f) + ":" + (x.i + 1))
                .ToList();
            Assert.True(hits.Count == 0, "boxed int passed for a BuiltIn enum (throws on Revit 2024+): " + string.Join(", ", hits));
        }
    }
}
