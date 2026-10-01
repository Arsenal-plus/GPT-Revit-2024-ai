// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHICH UNIT IS THE LINKED GEOMETRY ACTUALLY AT?
//
// horizun_manage_cad_links add takes a `units` argument and hands it to
// DWGImportOptions.Unit. The reply used to call the write verified_applied
// WITHOUT LOOKING AT THE UNIT AT ALL. Measured in the 2026-09-30 dry run:
// units=millimeter was asked for, the link came back declaring inch
// (IMPORT_DISPLAY_UNITS ordinal 2), and the reply still said verified_applied.
//
// WHY THE DECLARATION CANNOT ANSWER IT, measured (verify-dwg-cadlink.ps1,
// W1-W3, 2026-08-27):
//
//   * The requested unit DOES reach the geometry: a drawing forced to a unit it
//     is not in lands at exactly that unit ratio (W1, 25.4 for inch->mm).
//   * IMPORT_DISPLAY_UNITS on the CADLinkType did NOT follow the forced unit:
//     an inch fixture forced to millimetre declared inch (W2).
//   * A second link of a file already linked reuses the existing CADLinkType and
//     the options it was created with, so the requested unit changes nothing (W3).
//
// AND IT IS NOT RELIABLY THE DRAWING'S HEADER EITHER. W2 was read as "the
// declaration echoes the header"; the dry run's own drawing contradicts that,
// measured 2026-09-30 by reading it with the DWG reader: INSUNITS 4 (millimetres),
// extents 82.4 x 66.4 m - and the link made from it with units=millimeter
// declared inch. In both measurements a millimetre-forced link read ordinal 2.
// Whether the ordinal-to-ImportUnit mapping in CadFacts is wrong for this
// parameter needs a live measurement; until then the declaration is not used as
// evidence of anything.
//
// So comparing the request with the declaration is wrong in BOTH directions: it
// fails legitimate forced units, and it cannot see W3. The evidence that CAN
// answer is the SCALE
// of the linked geometry: the drawing's extents in its own units (read from the
// DWG itself, $EXTMIN/$EXTMAX) against the extents of what Revit placed, in
// millimetres. Their ratio is the millimetres per drawing unit Revit APPLIED,
// and it either matches the requested unit, the header unit, or neither. The
// header is the DWG's own INSUNITS, read in the same pass; the link's declaration
// stands in only when the drawing could not be read.
//
// The ratio is taken over the XY DIAGONAL, which a rotation (orient_to_view)
// does not change and a placement (origin, centred, shared) does not move. It is
// a match within a factor of MatchFactor, because $EXTMIN/$EXTMAX include what
// the Revit harvest does not (points, hatches) and may lag an unsaved regen; the
// units this decides between are at least 10x apart (inch/foot 12x, mm/cm 10x),
// and two candidates closer than DistinctFactor are not told apart at all.
//
// NOTHING TO COMPARE when the caller did not ask for a unit ('default', or the
// argument omitted): Revit then reads the header itself and the geometry is at
// the header's unit.
//
// Revit-free, so the decision is arguable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>The answers a unit postcondition can give.</summary>
    public enum CadLinkUnitVerdict
    {
        /// <summary>The caller asked for no unit ('default' or omitted). Revit read the header; nothing to compare.</summary>
        NotRequested,
        /// <summary>The requested unit is the header's unit, and the geometry (when measured) is at it.</summary>
        Agrees,
        /// <summary>The geometry's scale PROVES the requested unit; the header (IMPORT_DISPLAY_UNITS) names another.</summary>
        AppliedHeaderDiffers,
        /// <summary>The geometry's scale matches the HEADER unit, not the requested one - e.g. a reused link type (W3).</summary>
        NotApplied,
        /// <summary>No measurement could confirm or refute the requested unit.</summary>
        Unconfirmable
    }

    /// <summary>The scale evidence: the drawing's own extents against what Revit placed.</summary>
    public sealed class CadLinkScaleEvidence
    {
        /// <summary>XY diagonal of $EXTMIN..$EXTMAX in the DRAWING'S OWN units. Null when it could not be read.</summary>
        public double? DrawingDiagonalUnits;
        /// <summary>XY diagonal of the linked geometry as Revit placed it, in millimetres.</summary>
        public double? LinkDiagonalMm;
        /// <summary>Why there is no measurement, when there is none. Published as is.</summary>
        public string Unavailable;
        /// <summary>Where the drawing's extents came from (the DWG reader's cache, or a header read).</summary>
        public string DrawingRoute;
        /// <summary>
        /// The DWG's OWN unit, from its INSUNITS as the reader saw it, in ImportUnit names. Null when the
        /// drawing was not read or declares none. When present it is the header the decision uses - see
        /// NeedsMeasurement for why IMPORT_DISPLAY_UNITS is not trusted for that.
        /// </summary>
        public string DrawingHeaderUnit;
    }

    /// <summary>The decision, with what it rests on.</summary>
    public sealed class CadLinkUnitCheck
    {
        public CadLinkUnitVerdict Verdict;
        /// <summary>The unit the linked geometry is AT, or null when nothing establishes it. The field downstream reads.</summary>
        public string AppliedUnit;
        /// <summary>How AppliedUnit is known: measured_scale, header_matches_request, header_default, or null.</summary>
        public string AppliedRoute;
        /// <summary>Millimetres per drawing unit, measured from the geometry. Null when not measured.</summary>
        public double? MeasuredMmPerUnit;
    }

    public static class CadLinkUnitRules
    {
        /// <summary>A measured scale matches a unit when it is within this factor of it.</summary>
        public const double MatchFactor = 1.2;

        /// <summary>Two units closer than this factor cannot be told apart by scale.</summary>
        public const double DistinctFactor = 1.5;

        /// <summary>Below this many drawing units or millimetres of diagonal, a ratio is noise.</summary>
        public const double MinDiagonal = 1e-6;

        /// <summary>Whether a requested unit is one there is anything to compare against.</summary>
        public static bool IsRequested(string requested)
        {
            string r = Normalise(requested);
            return r.Length > 0 && r != "default";
        }

        /// <summary>
        /// Whether the add must MEASURE the scale: whenever a unit was asked for. Not only when the link's
        /// declaration differs from the request - MEASURED on the dry run's own drawing (2026-09-30): its
        /// INSUNITS is 4 (millimetres), and the link made from it with units=millimeter declared INCH
        /// (IMPORT_DISPLAY_UNITS ordinal 2). So the declaration is not reliably the drawing's header either,
        /// and a request that happens to equal it proves nothing. The read is header-only (seconds).
        /// </summary>
        public static bool NeedsMeasurement(string requested, string declaredHeader, bool alreadyLinked) =>
            IsRequested(requested);

        /// <summary>
        /// A DWG INSUNITS code as an ImportUnit name, for the units this bridge can link at; null for
        /// unitless (0) and every unit Revit's import has no member for.
        /// </summary>
        public static string HeaderUnitOfInsUnits(int? insUnits)
        {
            switch (insUnits ?? 0)
            {
                case 1: return "inch";
                case 2: return "foot";
                case 4: return "millimeter";
                case 5: return "centimeter";
                case 6: return "meter";
                case 14: return "decimeter";
                case 21: return "ussurveyfoot";
                default: return null;
            }
        }

        /// <summary>Millimetres per drawing unit from the two diagonals, or null when either is degenerate.</summary>
        public static double? MeasuredMmPerUnit(CadLinkScaleEvidence e)
        {
            if (e == null || !e.DrawingDiagonalUnits.HasValue || !e.LinkDiagonalMm.HasValue) return null;
            double d = e.DrawingDiagonalUnits.Value, l = e.LinkDiagonalMm.Value;
            if (double.IsNaN(d) || double.IsNaN(l) || double.IsInfinity(d) || double.IsInfinity(l)) return null;
            if (d <= MinDiagonal || l <= MinDiagonal) return null;
            return l / d;
        }

        /// <summary>
        /// Decide which unit the linked geometry is at. <paramref name="declaredHeader"/> is the link's
        /// IMPORT_DISPLAY_UNITS name - the DRAWING's header unit (W2), not the import option.
        /// </summary>
        public static CadLinkUnitCheck Decide(string requested, string declaredHeader, CadLinkScaleEvidence evidence,
                                              bool alreadyLinked = false)
        {
            var c = new CadLinkUnitCheck { MeasuredMmPerUnit = MeasuredMmPerUnit(evidence) };
            // THE DRAWING'S OWN INSUNITS when it was read; the link's declaration only when it was not.
            string header = Normalise(evidence?.DrawingHeaderUnit ?? declaredHeader);
            bool headerKnown = header.Length > 0 && header != "default";

            if (!IsRequested(requested))
            {
                c.Verdict = CadLinkUnitVerdict.NotRequested;
                if (headerKnown) { c.AppliedUnit = header; c.AppliedRoute = "header_default"; }
                return c;
            }

            string want = Normalise(requested);
            double? wantMm = CadUnits.MillimetresPer(want);
            double? headerMm = headerKnown ? CadUnits.MillimetresPer(header) : null;
            double? m = c.MeasuredMmPerUnit;

            if (m.HasValue && wantMm.HasValue)
            {
                bool distinct = !headerMm.HasValue || !Within(wantMm.Value, headerMm.Value, DistinctFactor);
                if (Within(m.Value, wantMm.Value, MatchFactor))
                {
                    c.Verdict = SameUnit(want, header) ? CadLinkUnitVerdict.Agrees
                              : distinct ? CadLinkUnitVerdict.AppliedHeaderDiffers
                              : CadLinkUnitVerdict.Unconfirmable;   // header a hair away (foot / US survey foot)
                    if (c.Verdict != CadLinkUnitVerdict.Unconfirmable) { c.AppliedUnit = want; c.AppliedRoute = "measured_scale"; }
                    return c;
                }
                if (headerMm.HasValue && distinct && Within(m.Value, headerMm.Value, MatchFactor))
                {
                    c.Verdict = CadLinkUnitVerdict.NotApplied;
                    c.AppliedUnit = header; c.AppliedRoute = "measured_scale";
                    return c;
                }
                c.Verdict = CadLinkUnitVerdict.Unconfirmable;   // the scale matches neither
                return c;
            }

            // NO MEASUREMENT. The header alone confirms the request only when they name the same unit and
            // the link is new - a reused type (W3) may carry another unit whatever the header says.
            if (SameUnit(want, header) && !alreadyLinked)
            {
                c.Verdict = CadLinkUnitVerdict.Agrees;
                c.AppliedUnit = want; c.AppliedRoute = "header_matches_request";
                return c;
            }
            c.Verdict = CadLinkUnitVerdict.Unconfirmable;
            return c;
        }

        /// <summary>Whether this verdict lets the write be counted as verified.</summary>
        public static bool Verifies(CadLinkUnitVerdict verdict) =>
            verdict == CadLinkUnitVerdict.NotRequested || verdict == CadLinkUnitVerdict.Agrees ||
            verdict == CadLinkUnitVerdict.AppliedHeaderDiffers;

        /// <summary>The reply block: what was asked, what the header says, what was measured, and what the link is AT.</summary>
        public static JObject Describe(string requested, string declaredHeader, string declaredRoute,
                                       CadLinkScaleEvidence evidence, CadLinkUnitCheck check)
        {
            var o = new JObject
            {
                ["requested"] = IsRequested(requested) ? (JToken)Normalise(requested) : JValue.CreateNull(),
                ["declared_by_link"] = declaredHeader,
                ["declared_route"] = declaredRoute,
                ["declared_means"] = "IMPORT_DISPLAY_UNITS does not follow the unit the import was told (measured), " +
                                     "and it is not evidence of what was applied.",
                ["drawing_header"] = evidence?.DrawingHeaderUnit,
                ["applied"] = check.AppliedUnit,
                ["applied_route"] = check.AppliedRoute,
                ["verdict"] = Name(check.Verdict),
                ["verifies"] = Verifies(check.Verdict)
            };
            var scale = new JObject
            {
                ["measured_mm_per_drawing_unit"] = check.MeasuredMmPerUnit.HasValue
                    ? (JToken)Math.Round(check.MeasuredMmPerUnit.Value, 6, MidpointRounding.AwayFromZero) : JValue.CreateNull(),
                ["drawing_diagonal_units"] = Rounded(evidence?.DrawingDiagonalUnits),
                ["link_diagonal_mm"] = Rounded(evidence?.LinkDiagonalMm),
                ["drawing_route"] = evidence?.DrawingRoute,
                ["requested_mm_per_unit"] = Rounded(CadUnits.MillimetresPer(Normalise(requested))),
                ["header_mm_per_unit"] = Rounded(CadUnits.MillimetresPer(Normalise(declaredHeader))),
                ["match_within_factor"] = MatchFactor
            };
            if (evidence?.Unavailable != null) scale["unavailable"] = evidence.Unavailable;
            o["scale_evidence"] = scale;
            if (DeclarationContradictsDrawing(declaredHeader, evidence?.DrawingHeaderUnit))
                o["declared_contradicts_drawing"] =
                    "the link declares '" + declaredHeader + "' and the DWG's own INSUNITS says '" +
                    evidence.DrawingHeaderUnit + "'. Measured on the 2026-09-30 dry run's drawing too (INSUNITS 4 " +
                    "= millimetres, declared inch), so declared_units is not the drawing's header on every link. " +
                    "Read units_check.applied / applied_units, not declared_units.";

            switch (check.Verdict)
            {
                case CadLinkUnitVerdict.NotRequested:
                    o["means"] = "no unit was asked for, so Revit read the drawing's own header and the geometry is " +
                                 "at that unit. Nothing to compare.";
                    break;
                case CadLinkUnitVerdict.Agrees:
                    o["means"] = check.AppliedRoute == "measured_scale"
                        ? "the requested unit is the header's, and the geometry's scale confirms it."
                        : "the requested unit is the header's unit and the link is new, so Revit applied it.";
                    break;
                case CadLinkUnitVerdict.AppliedHeaderDiffers:
                    o["means"] = "VERIFIED BY SCALE: the linked geometry sits at the requested unit, which is not the " +
                                 "drawing's header unit ('" + (evidence?.DrawingHeaderUnit ?? declaredHeader) + "'). The " +
                                 "link's declared_units does not show the applied unit. Downstream tools must read " +
                                 "units_check.applied (persisted as the instance's applied_units), not declared_units.";
                    break;
                case CadLinkUnitVerdict.NotApplied:
                    o["means"] = "THE REQUESTED UNIT WAS NOT APPLIED: the geometry's scale matches the header unit " +
                                 "('" + (evidence?.DrawingHeaderUnit ?? declaredHeader) + "'). The usual cause is a file that was already linked - " +
                                 "Revit reuses that link's type and the options it was created with. The link was " +
                                 "committed and is in the model at the header's unit; delete it with " +
                                 "horizun_delete_verified if that is not what you want.";
                    break;
                default:
                    o["means"] = "THE REQUESTED UNIT IS NOT CONFIRMED: " +
                                 (evidence?.Unavailable != null
                                     ? "the scale could not be measured (" + evidence.Unavailable + ")"
                                     : check.MeasuredMmPerUnit.HasValue
                                         ? "the measured scale (" + check.MeasuredMmPerUnit.Value.ToString("0.####", CultureInfo.InvariantCulture) +
                                           " mm per drawing unit) matches neither the requested unit nor the header's"
                                         : "the requested unit has no fixed length to measure against, or is " +
                                           "indistinguishable by scale from the header's") +
                                 ". The link was committed. Measure a known dimension with horizun_query_cad " +
                                 "mode=geometry before building from it.";
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
                case CadLinkUnitVerdict.AppliedHeaderDiffers: return "applied_header_differs";
                case CadLinkUnitVerdict.NotApplied: return "not_applied";
                default: return "unconfirmable";
            }
        }

        /// <summary>Both known, and naming different lengths.</summary>
        public static bool DeclarationContradictsDrawing(string declared, string drawingHeader)
        {
            string d = Normalise(declared), h = Normalise(drawingHeader);
            if (d.Length == 0 || h.Length == 0 || d == "default") return false;
            return !SameUnit(d, h);
        }

        /// <summary>Two unit names that are the same declaration, or the same length.</summary>
        public static bool SameUnit(string a, string b)
        {
            string x = Normalise(a), y = Normalise(b);
            if (x.Length == 0 || y.Length == 0 || x == "default" || y == "default") return false;
            if (x == y) return true;
            double? xm = CadUnits.MillimetresPer(x), ym = CadUnits.MillimetresPer(y);
            return xm.HasValue && ym.HasValue && Math.Abs(xm.Value - ym.Value) <= 1e-9 * Math.Max(1.0, xm.Value);
        }

        private static bool Within(double a, double b, double factor)
        {
            if (a <= 0 || b <= 0) return false;
            return Math.Abs(Math.Log(a / b)) <= Math.Log(factor);
        }

        private static JToken Rounded(double? v) =>
            v.HasValue ? (JToken)Math.Round(v.Value, 6, MidpointRounding.AwayFromZero) : JValue.CreateNull();

        private static string Normalise(string unit) => (unit ?? "").Trim().ToLowerInvariant();
    }
}
