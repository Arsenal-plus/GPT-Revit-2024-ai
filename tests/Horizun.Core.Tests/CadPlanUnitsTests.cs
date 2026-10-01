using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// WHICH UNIT THE UNIT GATE COMPARES.
    ///
    /// MEASURED (dry run, class 4): a DWG linked with units forced to millimetre had its geometry right in
    /// millimetres and still DECLARED inch; horizun_plan_from_cad refused a millimetre set unit_mismatch and
    /// the only way through was to declare inch, which is false. horizun_manage_cad_links now records the
    /// unit Revit actually applied (CadInstanceFacts.AppliedUnits); the gate prefers it and says so.
    /// </summary>
    public class CadPlanUnitsTests
    {
        private const double Mm = 1.0;
        private const double Inch = 25.4;

        [Fact]
        public void Declared_inch_applied_millimetre_requested_millimetre_is_accepted_on_the_applied_unit()
        {
            CadUnitBasis b = CadPlanUnits.Decide("inch", "millimeter", "this bridge's load record (geometry_scale)", Mm);
            Assert.True(b.AgreesWithSet);
            Assert.Equal(CadPlanUnits.AppliedBasis, b.Basis);
            Assert.Equal("millimeter", b.Unit);
            Assert.Equal("applied_units", (string)b.ToJson()["basis"]);
            Assert.Equal("inch", (string)b.ToJson()["declared_units"]);
        }

        [Fact]
        public void The_same_link_against_an_inch_set_is_now_the_mismatch_and_says_why()
        {
            CadUnitBasis b = CadPlanUnits.Decide("inch", "millimeter", null, Inch);
            Assert.False(b.AgreesWithSet);
            Assert.Equal(CadPlanUnits.AppliedBasis, b.Basis);
            Assert.Contains("measured", b.Says);
            Assert.Contains("declares 'inch'", b.Says);
        }

        [Fact]
        public void Without_an_applied_unit_the_declaration_stands_exactly_as_before()
        {
            CadUnitBasis refused = CadPlanUnits.Decide("inch", null, null, Mm);
            Assert.False(refused.AgreesWithSet);
            Assert.Equal(CadPlanUnits.DeclaredBasis, refused.Basis);
            Assert.Equal("the CAD link declares 'inch'", refused.Says);

            CadUnitBasis accepted = CadPlanUnits.Decide("millimeter", null, null, Mm);
            Assert.True(accepted.AgreesWithSet);
            Assert.Equal(CadPlanUnits.DeclaredBasis, accepted.Basis);
        }

        [Fact]
        public void An_applied_unit_that_names_nothing_resolvable_is_not_trusted_over_the_declaration()
        {
            CadUnitBasis b = CadPlanUnits.Decide("millimeter", "custom", null, Mm);
            Assert.Equal(CadPlanUnits.DeclaredBasis, b.Basis);
            Assert.True(b.AgreesWithSet);
        }

        [Fact]
        public void Nothing_resolvable_on_either_side_is_not_agreement()
        {
            CadUnitBasis b = CadPlanUnits.Decide("default", null, null, Mm);
            Assert.False(b.AgreesWithSet);
            Assert.Null(b.MmPerUnit);
        }
    }
}
