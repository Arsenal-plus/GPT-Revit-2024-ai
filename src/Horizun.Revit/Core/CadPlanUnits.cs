// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code.
//
// WHICH UNIT IS THE GEOMETRY AT, for the gate that stops a 200 becoming 200 metres.
//
// The DWG-to-BIM readers (plan_from_cad, plan_cad_update, audit_cad_model) compare
// the unit the CAD link stands in against the unit the requirement set declares,
// because Revit hands the geometry over already scaled and nothing downstream can
// rescale it. They used to read only what the link DECLARES.
//
// MEASURED (dry run, class 4): a DWG whose header says millimetres was linked with
// units forced to millimetre; its geometry was right in millimetres (a building of
// about 66 m) and the link still declared INCH - the declaration does not follow a
// forced unit. A set declaring millimetre was refused unit_mismatch, and the only
// way through was to declare inch, which is false.
//
// horizun_manage_cad_links now measures the unit Revit actually APPLIED, from the
// geometry's scale against the drawing's own extents, and keeps it in this
// machine's load record (CadInstanceFacts.AppliedUnits). When that is known it is
// the stronger statement, and it is what the gate compares; when it is not - a link
// made elsewhere, on another machine, or repointed since - the declaration stands,
// exactly as before. The reply says which of the two was used.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class CadUnitBasis
    {
        /// <summary>applied_units | declared_units.</summary>
        public string Basis;
        /// <summary>The unit the comparison used.</summary>
        public string Unit;
        /// <summary>Millimetres per unit of <see cref="Unit"/>; null when it names no resolvable unit.</summary>
        public double? MmPerUnit;
        public string DeclaredUnits;
        public string AppliedUnits;
        public string AppliedUnitsRoute;
        /// <summary>True when the unit resolves and equals the requirement set's.</summary>
        public bool AgreesWithSet;

        /// <summary>"the CAD link's applied unit 'millimeter' (...)" or "the CAD link declares 'inch'".</summary>
        public string Says =>
            Basis == CadPlanUnits.AppliedBasis
                ? "the CAD link was placed at '" + Unit + "' (the unit this bridge measured Revit applied when it " +
                  "linked the drawing; the link itself declares '" + (DeclaredUnits ?? "(nothing)") + "')"
                : "the CAD link declares '" + (Unit ?? "(nothing)") + "'";

        public JObject ToJson() => new JObject
        {
            ["basis"] = Basis,
            ["unit"] = Unit,
            ["declared_units"] = DeclaredUnits,
            ["applied_units"] = AppliedUnits,
            ["applied_units_route"] = AppliedUnitsRoute,
            ["agrees_with_requirement_set"] = AgreesWithSet,
            ["means"] = Basis == CadPlanUnits.AppliedBasis
                ? "compared the unit this bridge MEASURED the geometry to be at when it linked the drawing. The " +
                  "declaration does not follow a forced unit, so where the two differ the measurement is the " +
                  "one the geometry obeys."
                : "compared the unit the link DECLARES: nothing on this machine measured the unit Revit applied " +
                  "(a link made elsewhere, on another machine, or repointed since). horizun_manage_cad_links " +
                  "add records that measurement."
        };
    }

    public static class CadPlanUnits
    {
        public const string AppliedBasis = "applied_units";
        public const string DeclaredBasis = "declared_units";

        /// <summary>
        /// The unit the geometry is at: the applied one when this bridge measured it and it names a unit,
        /// the declared one otherwise. <paramref name="setMmPerUnit"/> is the requirement set's.
        /// </summary>
        public static CadUnitBasis Decide(string declared, string applied, string appliedRoute, double setMmPerUnit)
        {
            var b = new CadUnitBasis { DeclaredUnits = declared, AppliedUnits = applied, AppliedUnitsRoute = appliedRoute };
            double? appliedMm = string.IsNullOrWhiteSpace(applied) ? null : CadUnits.MillimetresPer(applied);
            if (appliedMm.HasValue)
            {
                b.Basis = AppliedBasis;
                b.Unit = applied;
                b.MmPerUnit = appliedMm;
            }
            else
            {
                b.Basis = DeclaredBasis;
                b.Unit = declared;
                b.MmPerUnit = CadUnits.MillimetresPer(declared);
            }
            b.AgreesWithSet = b.MmPerUnit.HasValue && Math.Abs(b.MmPerUnit.Value - setMmPerUnit) < 1e-9;
            return b;
        }
    }
}
