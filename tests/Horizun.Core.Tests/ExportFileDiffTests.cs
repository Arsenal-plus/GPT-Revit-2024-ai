// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// horizun_export decides which files it may report as PRODUCED from a before/
// after snapshot diff. The defect this covers: a file that existed but could
// not be stat'ed (locked, a permission blip) BEFORE the export must never
// read as "new" just because it is missing from the 'before' map - it must
// read as UNMEASURED, and stay out of the produced count either way.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ExportFileDiffTests
    {
        private static ExportFileStamp Readable(long size, long mtime, string hash)
            => new ExportFileStamp { Existed = true, Readable = true, Size = size, Mtime = mtime, Hash = hash };

        private static ExportFileStamp Unreadable()
            => new ExportFileStamp { Existed = true, Readable = false };

        [Fact]
        public void A_path_absent_before_is_produced()
        {
            var before = new Dictionary<string, ExportFileStamp>();
            var after = new Dictionary<string, ExportFileStamp> { ["out.dwg"] = Readable(100, 1, "h1") };
            var (produced, unmeasured) = ExportFileDiff.Diff(before, after);
            Assert.Contains("out.dwg", produced);
            Assert.Empty(unmeasured);
        }

        [Fact]
        public void A_path_that_existed_and_was_unreadable_before_is_unmeasured_not_produced()
        {
            // THE DEFECT: overwrite=true, the destination already existed and was
            // briefly locked when 'before' was taken. The old code dropped it from
            // the snapshot entirely and the diff then called it new.
            var before = new Dictionary<string, ExportFileStamp> { ["out.dwg"] = Unreadable() };
            var after = new Dictionary<string, ExportFileStamp> { ["out.dwg"] = Readable(100, 1, "h1") };
            var (produced, unmeasured) = ExportFileDiff.Diff(before, after);
            Assert.DoesNotContain("out.dwg", produced);
            Assert.Contains("out.dwg", unmeasured);
        }

        [Fact]
        public void A_path_unreadable_after_the_export_is_unmeasured()
        {
            var before = new Dictionary<string, ExportFileStamp> { ["out.dwg"] = Readable(100, 1, "h1") };
            var after = new Dictionary<string, ExportFileStamp> { ["out.dwg"] = Unreadable() };
            var (produced, unmeasured) = ExportFileDiff.Diff(before, after);
            Assert.DoesNotContain("out.dwg", produced);
            Assert.Contains("out.dwg", unmeasured);
        }

        [Fact]
        public void An_unchanged_readable_file_is_neither_produced_nor_unmeasured()
        {
            var before = new Dictionary<string, ExportFileStamp> { ["out.dwg"] = Readable(100, 1, "h1") };
            var after = new Dictionary<string, ExportFileStamp> { ["out.dwg"] = Readable(100, 1, "h1") };
            var (produced, unmeasured) = ExportFileDiff.Diff(before, after);
            Assert.Empty(produced);
            Assert.Empty(unmeasured);
        }

        [Fact]
        public void A_size_change_is_produced()
        {
            var before = new Dictionary<string, ExportFileStamp> { ["out.dwg"] = Readable(100, 1, "h1") };
            var after = new Dictionary<string, ExportFileStamp> { ["out.dwg"] = Readable(200, 1, "h1") };
            Assert.Contains("out.dwg", ExportFileDiff.Diff(before, after).Produced);
        }

        [Fact]
        public void A_mtime_change_alone_is_produced()
        {
            var before = new Dictionary<string, ExportFileStamp> { ["out.dwg"] = Readable(100, 1, "h1") };
            var after = new Dictionary<string, ExportFileStamp> { ["out.dwg"] = Readable(100, 2, "h1") };
            Assert.Contains("out.dwg", ExportFileDiff.Diff(before, after).Produced);
        }

        [Fact]
        public void A_hash_change_with_identical_size_and_mtime_is_still_produced()
        {
            // Content changed but the filesystem's size/mtime happened to read the
            // same (coarse mtime resolution, or a byte-identical-length rewrite) -
            // the hash is what catches this the stat pair alone would miss.
            var before = new Dictionary<string, ExportFileStamp> { ["out.dwg"] = Readable(100, 1, "h1") };
            var after = new Dictionary<string, ExportFileStamp> { ["out.dwg"] = Readable(100, 1, "h2") };
            Assert.Contains("out.dwg", ExportFileDiff.Diff(before, after).Produced);
        }

        [Fact]
        public void An_empty_zero_byte_after_file_is_unmeasured_not_produced()
        {
            var before = new Dictionary<string, ExportFileStamp>();
            var after = new Dictionary<string, ExportFileStamp> { ["out.dwg"] = Readable(0, 1, "h1") };
            var (produced, unmeasured) = ExportFileDiff.Diff(before, after);
            Assert.DoesNotContain("out.dwg", produced);
            Assert.Contains("out.dwg", unmeasured);
        }

        [Fact]
        public void Expected_single_file_present_and_alone_has_no_missing_or_extra()
        {
            var (missing, extra) = ExportFileDiff.AgainstExpectedSingleFile(new List<string> { "out.dwg" }, "out.dwg");
            Assert.Empty(missing);
            Assert.Empty(extra);
        }

        [Fact]
        public void Expected_single_file_absent_is_named_missing()
        {
            var (missing, extra) = ExportFileDiff.AgainstExpectedSingleFile(new List<string>(), "out.dwg");
            Assert.Contains("out.dwg", missing);
            Assert.Empty(extra);
        }

        [Fact]
        public void An_extra_produced_file_beyond_the_expected_one_is_named()
        {
            var (missing, extra) = ExportFileDiff.AgainstExpectedSingleFile(
                new List<string> { "out.dwg", "out-2.dwg" }, "out.dwg");
            Assert.Empty(missing);
            Assert.Contains("out-2.dwg", extra);
        }
    }
}
