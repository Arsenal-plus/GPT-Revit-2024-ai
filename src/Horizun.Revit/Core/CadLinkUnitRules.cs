// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// DID THE LINK END UP DECLARING THE UNIT THAT WAS ASKED FOR?
//
// horizun_manage_cad_links add takes a `units` argument and hands it to
// DWGImportOptions.Unit. The reply then re-reads the link and used to call the
// write verified_applied WITHOUT LOOKING AT THE UNIT AT ALL. Measured in the
// 2026-09-30 dry run: units=millimeter was asked for, the link came back
// declaring inch (IMPORT_DISPLAY_UNITS ordinal 2), and the reply still said
// verified_applied.
//
// WHY THE TWO CAN DIFFER, measured rather than assumed (verify-dwg-cadlink.ps1,
// W1-W3, 2026-08-27):
//
//   * The requested unit DOES reach the geometry: a drawing forced to a unit it
//     is not in lands at exactly that unit ratio (W1, 25.4 for inch->mm).
//   * IMPORT_DISPLAY_UNITS on the CADLinkType records the DRAWING'S OWN unit
//     (its header), not the one the import was forced to (W2: 'inch' on a link
//     created with units=millimeter).
//   * A second link of a file already linked reuses the existing CADLinkType and
//     the options it was created with, so the requested unit changes nothing (W3).
//
// So the unit read back is NOT a re-read of the option that was set, and the
// mapping from name to ImportUnit ordinal is not the culprit (both directions
// go through the same enum, Millimeter=6, Inch=2, in every year 2023-2027). A
// disagreement means one of two things this bridge cannot tell apart from
// inside Revit: the drawing's header is wrong and the forced unit is right, or
// the forced unit is wrong and the geometry is now off by the ratio. Either
// way the requested unit is NOT CONFIRMED by the model, and a reply that says
// verified over it is the silent pass this repository refuses.
//
// NOTHING TO COMPARE when the caller did not ask for a unit ('default', or the
// argument omitted): Revit then reads the header itself, and whatever the link
// declares is simply reported.
//
// Revit-free, so the decision is arguable at a desk.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>The four answers a unit postcondition can give.</summary>
    public enum CadLinkUnitVerdict
    {
        /// <summary>The caller asked for no unit ('default' or omitted). Nothing to compare; not a failure.</summary>
        NotRequested,
        /// <summary>The link declares the unit that was asked for.</summary>
        Agrees,
        /// <summary>The link declares a DIFFERENT unit. The requested unit is not confirmed.</summary>
        Disagrees,
        /// <summary>A unit was asked for and the link declares nothing comparable. Not confirmed either way.</summary>
        Unconfirmable
    }

    public static class CadLinkUnitRules
    {
        /// <summary>Whether a requested unit is one there is anything to compare against.</summary>
        public static bool IsRequested(string requested)
        {
            string r = Normalise(requested);
            return r.Length > 0 && r != "default";
        }

        /// <summary>
        /// Compare the unit the caller asked for with the unit the link declares
        /// after the commit. Names are the ImportUnit names, lower-cased, as both
        /// sides already spell them; two spellings of one length (millimetre and
        /// millimeter) agree. 'default' and 'custom' on the READ side resolve to no
        /// length and so can confirm nothing - except 'custom' asked for and
        /// 'custom' read, which is the same declaration.
        /// </summary>
        public static CadLinkUnitVerdict Compare(string requested, string declared)
        {
            if (!IsRequested(requested)) return CadLinkUnitVerdict.NotRequested;
            string want = Normalise(requested);
            string got = Normalise(declared);
            if (got.Length == 0 || got == "default") return CadLinkUnitVerdict.Unconfirmable;
            if (string.Equals(want, got, StringComparison.Ordinal)) return CadLinkUnitVerdict.Agrees;

            double? wantMm = CadUnits.MillimetresPer(want);
            double? gotMm = CadUnits.MillimetresPer(got);
            if (wantMm.HasValue && gotMm.HasValue)
                return Math.Abs(wantMm.Value - gotMm.Value) <= 1e-9 * Math.Max(1.0, wantMm.Value)
                    ? CadLinkUnitVerdict.Agrees
                    : CadLinkUnitVerdict.Disagrees;
            // One side has a length and the other is 'custom' (or a name this
            // build does not know): different declarations, not a match.
            return CadLinkUnitVerdict.Disagrees;
        }

        /// <summary>Whether this verdict lets the write be counted as verified.</summary>
        public static bool Verifies(CadLinkUnitVerdict verdict) =>
            verdict == CadLinkUnitVerdict.NotRequested || verdict == CadLinkUnitVerdict.Agrees;

        /// <summary>The reply block: what was asked, what was read, and what the difference means.</summary>
        public static JObject Describe(string requested, string declared, string declaredRoute)
        {
            CadLinkUnitVerdict verdict = Compare(requested, declared);
            var o = new JObject
            {
                ["requested"] = IsRequested(requested) ? (JToken)Normalise(requested) : JValue.CreateNull(),
                ["declared_by_link"] = declared,
                ["declared_route"] = declaredRoute,
                ["verdict"] = Name(verdict),
                ["verifies"] = Verifies(verdict)
            };
            switch (verdict)
            {
                case CadLinkUnitVerdict.NotRequested:
                    o["means"] = "no unit was asked for, so Revit read the drawing's own header and there is " +
                                 "nothing to compare. The link's declaration is reported as it is.";
                    break;
                case CadLinkUnitVerdict.Agrees:
                    o["means"] = "the link declares the unit that was asked for.";
                    break;
                case CadLinkUnitVerdict.Disagrees:
                    double? ratio = Ratio(requested, declared);
                    o["ratio"] = ratio;
                    o["means"] =
                        "THE REQUESTED UNIT IS NOT CONFIRMED. Revit records the DRAWING'S OWN unit on the link " +
                        "type, not the one the import was told (measured), and a second link of an already-linked " +
                        "file keeps the first link's options. So either the drawing's header is wrong and the " +
                        "geometry now sits at the requested unit, or the requested unit is wrong and the geometry " +
                        "is off by" + (ratio.HasValue ? " a factor of " + ratio.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : " the unit ratio") +
                        ". Nothing inside Revit tells those apart. Measure a known dimension with horizun_query_cad " +
                        "mode=geometry before building from this link; if it is wrong, delete the instance with " +
                        "horizun_delete_verified. The link was committed and is in the model.";
                    break;
                default:
                    o["means"] = "a unit was asked for and the link declares none this bridge can compare " +
                                 "('" + (declared ?? "nothing") + "'), so the requested unit is not confirmed.";
                    break;
            }
            return o;
        }

        public static string Name(CadLinkUnitVerdict verdict)
        {
            switch (verdict)
            {
                case CadLinkUnitVerdict.NotRequested: return "not_requested";
                case CadLinkUnitVerdict.Agrees: return "agrees";
                case CadLinkUnitVerdict.Disagrees: return "disagrees";
                default: return "unconfirmable";
            }
        }

        private static double? Ratio(string requested, string declared)
        {
            double? a = CadUnits.MillimetresPer(Normalise(requested));
            double? b = CadUnits.MillimetresPer(Normalise(declared));
            if (!a.HasValue || !b.HasValue || a.Value <= 0 || b.Value <= 0) return null;
            return Math.Round(Math.Max(a.Value, b.Value) / Math.Min(a.Value, b.Value), 6, MidpointRounding.AwayFromZero);
        }

        private static string Normalise(string unit) => (unit ?? "").Trim().ToLowerInvariant();
    }
}
