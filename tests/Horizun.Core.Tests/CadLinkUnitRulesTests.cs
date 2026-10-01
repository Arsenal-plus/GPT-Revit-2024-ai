// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHICH UNIT THE LINKED GEOMETRY IS AT, DECIDED BY ITS SCALE.
//
// The 2026-09-30 dry run: horizun_manage_cad_links add with units=millimeter
// produced a link declaring inch (IMPORT_DISPLAY_UNITS ordinal 2), and the reply
// said verified_applied because nothing looked. The declaration cannot decide it
// either: it echoes the DWG header (measured, W2), and the dry run's geometry was
// RIGHT in millimetres - a ~66 m building. So these tests hold the decision to
// the scale evidence: forced-and-applied verifies, header-only (the reused-type
// case, W3) fails, and no evidence is unknown - never a pass, never a fail.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadLinkUnitRulesTests
    {
        /// <summary>A 66 m building: drawn in millimetres, so its extents are ~66,000 drawing units across.</summary>
        private const double DrawingDiagonal = 66000.0;

        private static CadLinkScaleEvidence Placed(double mmPerDrawingUnit) => new CadLinkScaleEvidence
        {
            DrawingDiagonalUnits = DrawingDiagonal,
            LinkDiagonalMm = DrawingDiagonal * mmPerDrawingUnit,
            DrawingRoute = "test"
        };

        private static ApplicationState Stamp(CadLinkUnitVerdict v) => ApplicationOutcome.Applied("Committed", 1, 1,
            CadLinkUnitRules.Verifies(v) ? 1 : 0, 0,
            v == CadLinkUnitVerdict.NotApplied ? 1 : 0,
            v == CadLinkUnitVerdict.Unconfirmable ? 1 : 0);

        [Fact]
        public void The_dry_run_case_millimetre_forced_under_an_inch_header_and_placed_in_mm_VERIFIES()
        {
            CadLinkUnitCheck c = CadLinkUnitRules.Decide("millimeter", "inch", Placed(1.0));

            Assert.Equal(CadLinkUnitVerdict.AppliedHeaderDiffers, c.Verdict);
            Assert.True(CadLinkUnitRules.Verifies(c.Verdict));
            Assert.Equal("millimeter", c.AppliedUnit);
            Assert.Equal("measured_scale", c.AppliedRoute);
            Assert.Equal(ApplicationState.VerifiedApplied, Stamp(c.Verdict));
        }

        [Fact]
        public void The_dry_run_drawing_as_measured_INSUNITS_mm_declared_inch_placed_in_mm_AGREES()
        {
            // Read 2026-09-30 from the dry run's own DWG: INSUNITS 4, extents 82.4 x 66.4 m. The link declared
            // inch. The drawing's header decides, the scale confirms, and the contradiction is published.
            CadLinkScaleEvidence e = Placed(1.0);
            e.DrawingHeaderUnit = CadLinkUnitRules.HeaderUnitOfInsUnits(4);
            CadLinkUnitCheck c = CadLinkUnitRules.Decide("millimeter", "inch", e);

            Assert.Equal(CadLinkUnitVerdict.Agrees, c.Verdict);
            Assert.Equal("millimeter", c.AppliedUnit);
            Assert.Equal("measured_scale", c.AppliedRoute);
            JObject o = CadLinkUnitRules.Describe("millimeter", "inch", null, e, c);
            Assert.Equal("millimeter", (string)o["drawing_header"]);
            Assert.NotNull(o["declared_contradicts_drawing"]);
        }

        [Theory]
        [InlineData(1, "inch")]
        [InlineData(2, "foot")]
        [InlineData(4, "millimeter")]
        [InlineData(5, "centimeter")]
        [InlineData(6, "meter")]
        [InlineData(14, "decimeter")]
        [InlineData(21, "ussurveyfoot")]
        [InlineData(0, null)]
        [InlineData(10, null)]
        public void INSUNITS_maps_to_the_import_unit_names(int code, string name)
        {
            Assert.Equal(name, CadLinkUnitRules.HeaderUnitOfInsUnits(code));
        }

        [Fact]
        public void Millimetre_asked_for_but_the_geometry_is_at_the_inch_header_is_NOT_APPLIED()
        {
            // W3: a second link of a linked file reuses the first type, so the request is ignored.
            CadLinkUnitCheck c = CadLinkUnitRules.Decide("millimeter", "inch", Placed(25.4), alreadyLinked: true);

            Assert.Equal(CadLinkUnitVerdict.NotApplied, c.Verdict);
            Assert.False(CadLinkUnitRules.Verifies(c.Verdict));
            Assert.Equal("inch", c.AppliedUnit);
            Assert.Equal(ApplicationState.Partial, Stamp(c.Verdict));
        }

        [Fact]
        public void Millimetre_asked_for_under_an_inch_header_with_NO_measurement_is_unconfirmable_not_a_failure()
        {
            var none = new CadLinkScaleEvidence { Unavailable = "no headless AutoCAD" };
            CadLinkUnitCheck c = CadLinkUnitRules.Decide("millimeter", "inch", none);

            Assert.Equal(CadLinkUnitVerdict.Unconfirmable, c.Verdict);
            Assert.Null(c.AppliedUnit);
            Assert.Equal(ApplicationState.Uncertain, Stamp(c.Verdict));
            Assert.Equal(CadLinkUnitVerdict.Unconfirmable, CadLinkUnitRules.Decide("millimeter", "inch", null).Verdict);
        }

        [Fact]
        public void A_scale_that_matches_neither_unit_is_unconfirmable()
        {
            CadLinkUnitCheck c = CadLinkUnitRules.Decide("millimeter", "inch", Placed(3.0));
            Assert.Equal(CadLinkUnitVerdict.Unconfirmable, c.Verdict);
            Assert.Equal(3.0, c.MeasuredMmPerUnit.Value, 6);
        }

        [Theory]
        [InlineData(1.15)]
        [InlineData(0.87)]
        public void A_scale_within_the_match_factor_still_matches(double off)
        {
            // $EXTMIN/$EXTMAX carry what the Revit harvest does not (points, hatches): not exact.
            Assert.Equal(CadLinkUnitVerdict.AppliedHeaderDiffers,
                         CadLinkUnitRules.Decide("millimeter", "inch", Placed(1.0 * off)).Verdict);
        }

        [Fact]
        public void Degenerate_extents_measure_nothing()
        {
            Assert.Null(CadLinkUnitRules.MeasuredMmPerUnit(new CadLinkScaleEvidence { DrawingDiagonalUnits = 0, LinkDiagonalMm = 100 }));
            Assert.Null(CadLinkUnitRules.MeasuredMmPerUnit(new CadLinkScaleEvidence { DrawingDiagonalUnits = 100, LinkDiagonalMm = 0 }));
            Assert.Null(CadLinkUnitRules.MeasuredMmPerUnit(new CadLinkScaleEvidence { DrawingDiagonalUnits = double.NaN, LinkDiagonalMm = 1 }));
        }

        [Fact]
        public void Units_too_close_to_tell_apart_by_scale_are_unconfirmable()
        {
            // foot and US survey foot differ by 2 ppm: no extent tells them apart.
            Assert.Equal(CadLinkUnitVerdict.Unconfirmable,
                         CadLinkUnitRules.Decide("ussurveyfoot", "foot", Placed(304.8)).Verdict);
        }

        [Fact]
        public void Requested_equal_to_the_declaration_on_a_new_link_agrees_when_nothing_could_be_measured()
        {
            // Measured anyway - the declaration is not reliably the header - but without a reader it stands in.
            Assert.True(CadLinkUnitRules.NeedsMeasurement("inch", "inch", alreadyLinked: false));
            CadLinkUnitCheck c = CadLinkUnitRules.Decide("inch", "inch", null);
            Assert.Equal(CadLinkUnitVerdict.Agrees, c.Verdict);
            Assert.Equal("header_matches_request", c.AppliedRoute);
        }

        [Fact]
        public void Requested_equal_to_the_header_on_an_ALREADY_linked_file_must_be_measured()
        {
            // The reused type may carry another unit whatever the header says.
            Assert.True(CadLinkUnitRules.NeedsMeasurement("inch", "inch", alreadyLinked: true));
            Assert.Equal(CadLinkUnitVerdict.Unconfirmable, CadLinkUnitRules.Decide("inch", "inch", null, alreadyLinked: true).Verdict);
            Assert.Equal(CadLinkUnitVerdict.Agrees, CadLinkUnitRules.Decide("inch", "inch", Placed(25.4), alreadyLinked: true).Verdict);
        }

        [Fact]
        public void A_header_that_differs_from_the_request_must_be_measured()
        {
            Assert.True(CadLinkUnitRules.NeedsMeasurement("millimeter", "inch", alreadyLinked: false));
            Assert.True(CadLinkUnitRules.NeedsMeasurement("millimeter", null, alreadyLinked: false));
        }

        [Theory]
        [InlineData(null, "inch")]
        [InlineData("", "inch")]
        [InlineData("default", "inch")]
        [InlineData(" DEFAULT ", null)]
        public void No_unit_asked_for_means_nothing_to_compare_and_is_not_a_failure(string requested, string declared)
        {
            Assert.False(CadLinkUnitRules.NeedsMeasurement(requested, declared, alreadyLinked: true));
            CadLinkUnitCheck c = CadLinkUnitRules.Decide(requested, declared, null);
            Assert.Equal(CadLinkUnitVerdict.NotRequested, c.Verdict);
            Assert.True(CadLinkUnitRules.Verifies(c.Verdict));
            Assert.Equal(declared, c.AppliedUnit);
            Assert.Equal(JTokenType.Null, CadLinkUnitRules.Describe(requested, declared, null, null, c)["requested"].Type);
        }

        [Fact]
        public void The_reply_block_carries_the_applied_unit_the_header_and_the_scale()
        {
            CadLinkScaleEvidence e = Placed(1.0);
            CadLinkUnitCheck c = CadLinkUnitRules.Decide("millimeter", "inch", e);
            JObject o = CadLinkUnitRules.Describe("millimeter", "inch", "IMPORT_DISPLAY_UNITS on the type (ordinal 2)", e, c);

            Assert.Equal("applied_header_differs", (string)o["verdict"]);
            Assert.True((bool)o["verifies"]);
            Assert.Equal("millimeter", (string)o["applied"]);
            Assert.Equal("inch", (string)o["declared_by_link"]);
            Assert.Equal(1.0, (double)o["scale_evidence"]["measured_mm_per_drawing_unit"], 6);
            Assert.Contains("units_check.applied", (string)o["means"]);
        }

        [Fact]
        public void The_drawing_diagonal_divides_the_header_scale_back_out_and_refuses_empty_extents()
        {
            var r = new CadDwgReading { MmPerUnit = 25.4, ExtMin = new CadPoint(0, 0), ExtMax = new CadPoint(3 * 25.4, 4 * 25.4) };
            Assert.Equal(5.0, CadDwgExtract.DrawingDiagonalUnits(r).Value, 9);

            var empty = new CadDwgReading { MmPerUnit = 1.0, ExtMin = new CadPoint(1e20, 1e20), ExtMax = new CadPoint(-1e20, -1e20) };
            Assert.Null(CadDwgExtract.DrawingDiagonalUnits(empty));
            Assert.Null(CadDwgExtract.DrawingDiagonalUnits(new CadDwgReading()));
        }

        [Fact]
        public void The_header_only_script_reads_back_through_the_extractor_parser()
        {
            string script = CadDwgReader.BuildHeaderScript(@"C:\tmp\h.tsv");
            Assert.Contains("(defun hz-pt ", script);
            Assert.Contains("EXTMIN", script);
            Assert.Contains("(hz-head \"C:/tmp/h.tsv\")", script);

            // What the script writes, as the parser sees it.
            CadDwgReading r = CadDwgExtract.Parse(new[]
            {
                "H\tdwg\tA.dwg", "H\tinsunits\t1", "H\textmin\t0.000000\t0.000000\t0.000000",
                "H\textmax\t300.000000\t400.000000\t0.000000", "H\tdone\t1"
            });
            Assert.True(r.Complete);
            Assert.Equal(500.0, CadDwgExtract.DrawingDiagonalUnits(r).Value, 6);
        }

        [Fact]
        public void The_add_measures_the_scale_instead_of_stamping_a_literal_verified()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands",
                                                       "ManageCadLinksCommand.cs"));
            Assert.DoesNotContain("StampApplied(result, \"Committed\", 1, 1, 1, 0, 0, 0)", src);
            Assert.Contains("CadLinkUnitRules.Decide(requestedUnits, declaredUnits, scale, alreadyLinked)", src);
            Assert.Contains("[\"host_verified\"] = unitsHold", src);
            Assert.Contains("CadLinkLoads.UnitsAppliedRecord(unitsJson)", src);
            string facts = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Core", "CadFacts.cs"));
            Assert.Contains("[\"applied_units\"] = AppliedUnits", facts);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
        }
    }
}
