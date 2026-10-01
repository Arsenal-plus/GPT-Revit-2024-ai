// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A LINK ASKED FOR IN MILLIMETRES THAT DECLARES INCHES IS NOT VERIFIED.
//
// The 2026-09-30 dry run: horizun_manage_cad_links add with units=millimeter
// produced a link declaring inch (IMPORT_DISPLAY_UNITS ordinal 2), and the reply
// said verified_applied because nothing compared the two. These tests pin the
// comparison, the counts it feeds into the application declaration, and the
// wiring that makes the add use it.
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
        [Fact]
        public void Millimetre_asked_for_and_inch_read_back_disagrees_and_does_not_verify()
        {
            CadLinkUnitVerdict v = CadLinkUnitRules.Compare("millimeter", "inch");

            Assert.Equal(CadLinkUnitVerdict.Disagrees, v);
            Assert.False(CadLinkUnitRules.Verifies(v));
        }

        [Fact]
        public void And_the_apply_it_feeds_is_partial_not_verified_applied()
        {
            // The exact counts ManageCadLinksCommand stamps for that verdict.
            CadLinkUnitVerdict v = CadLinkUnitRules.Compare("millimeter", "inch");
            ApplicationState state = ApplicationOutcome.Applied("Committed", 1, 1,
                CadLinkUnitRules.Verifies(v) ? 1 : 0, 0,
                v == CadLinkUnitVerdict.Disagrees ? 1 : 0,
                v == CadLinkUnitVerdict.Unconfirmable ? 1 : 0);

            Assert.NotEqual(ApplicationState.VerifiedApplied, state);
            Assert.Equal(ApplicationState.Partial, state);
        }

        [Fact]
        public void The_reply_block_names_the_disagreement_and_the_ratio()
        {
            JObject o = CadLinkUnitRules.Describe("millimeter", "inch", "IMPORT_DISPLAY_UNITS on the type (ordinal 2)");

            Assert.Equal("disagrees", (string)o["verdict"]);
            Assert.False((bool)o["verifies"]);
            Assert.Equal("millimeter", (string)o["requested"]);
            Assert.Equal("inch", (string)o["declared_by_link"]);
            Assert.Equal(25.4, (double)o["ratio"], 6);
            Assert.Contains("NOT CONFIRMED", (string)o["means"]);
        }

        [Theory]
        [InlineData("millimeter", "millimeter")]
        [InlineData("Millimeter", "millimeter")]
        [InlineData("millimetre", "millimeter")]
        [InlineData("inch", "inch")]
        [InlineData("custom", "custom")]
        [InlineData("ussurveyfoot", "ussurveyfoot")]
        public void The_same_unit_agrees_and_verifies(string requested, string declared)
        {
            CadLinkUnitVerdict v = CadLinkUnitRules.Compare(requested, declared);
            Assert.Equal(CadLinkUnitVerdict.Agrees, v);
            Assert.True(CadLinkUnitRules.Verifies(v));
            Assert.Equal(ApplicationState.VerifiedApplied, ApplicationOutcome.Applied("Committed", 1, 1, 1, 0, 0, 0));
        }

        [Theory]
        [InlineData("foot", "ussurveyfoot")]
        [InlineData("meter", "millimeter")]
        [InlineData("custom", "millimeter")]
        [InlineData("millimeter", "custom")]
        public void Different_units_disagree(string requested, string declared)
        {
            Assert.Equal(CadLinkUnitVerdict.Disagrees, CadLinkUnitRules.Compare(requested, declared));
        }

        [Theory]
        [InlineData(null, "inch")]
        [InlineData("", "inch")]
        [InlineData("default", "inch")]
        [InlineData(" DEFAULT ", null)]
        public void No_unit_asked_for_means_nothing_to_compare_and_is_not_a_failure(string requested, string declared)
        {
            // Revit read the header itself; whatever the link declares is just reported.
            CadLinkUnitVerdict v = CadLinkUnitRules.Compare(requested, declared);
            Assert.Equal(CadLinkUnitVerdict.NotRequested, v);
            Assert.True(CadLinkUnitRules.Verifies(v));
            Assert.Equal(JTokenType.Null, CadLinkUnitRules.Describe(requested, declared, null)["requested"].Type);
        }

        [Theory]
        [InlineData("millimeter", null)]
        [InlineData("millimeter", "")]
        [InlineData("millimeter", "default")]
        public void A_unit_asked_for_and_nothing_comparable_read_back_is_unconfirmed(string requested, string declared)
        {
            CadLinkUnitVerdict v = CadLinkUnitRules.Compare(requested, declared);
            Assert.Equal(CadLinkUnitVerdict.Unconfirmable, v);
            Assert.False(CadLinkUnitRules.Verifies(v));
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Applied("Committed", 1, 1, 0, 0, 0, 1));
        }

        [Fact]
        public void The_add_compares_the_units_instead_of_stamping_a_literal_verified()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands",
                                                       "ManageCadLinksCommand.cs"));
            // The literal the add used to stamp: committed, applied, VERIFIED, whatever the units said.
            Assert.DoesNotContain("StampApplied(result, \"Committed\", 1, 1, 1, 0, 0, 0)", src);
            Assert.Contains("CadLinkUnitRules.Compare(requestedUnits, declaredUnits)", src);
            Assert.Contains("[\"host_verified\"] = unitsHold", src);
            Assert.Contains("[\"units_check\"]", src);
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
