// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_coordination operation=navisworks_readiness (read-only) and
// operation=prepare_navisworks (the one write here besides operation=show):
// whether the view Navisworks will actually read is set up to give it real
// geometry, BEFORE anyone runs an export and wonders why the clash test found
// nothing.
//
// MEASURED 2026-09-26 (see the field-feedback note this records): at
// ViewDetailLevel.Coarse a pipe reaches Navisworks as a single line primitive,
// and a Hard clash test against that line reports ZERO clashes. Fine is not a
// preference; it is the difference between exporting a building and exporting
// a wireframe of one. A hidden MEP category with elements never reaches
// Navisworks AT ALL, regardless of detail level - View.GetCategoryHidden is
// per-category and per-view, and nothing here can see past it (a view filter
// or a category-class override can still hide an element GetCategoryHidden
// calls visible; this reads the one mechanism the API exposes and says so).
//
// WHICH VIEW: Navisworks' own Revit exporter reads a NAMED 3D view when told
// to publish/link one - a view named exactly "Navisworks" if the project has
// one, else the ever-present default "{3D}". Neither is this bridge's
// invention; both are what a coordinator actually points Navisworks at.
//
// unhide=true, CanCategoryBeHidden FIRST: a view template governing category
// visibility, or a dependent view, makes SetCategoryHidden a no-op or a throw
// - CanCategoryBeHidden is the API's own way of saying so before this ever
// opens a transaction, and the post-write re-read (GetCategoryHidden again)
// is what actually decides success, never the call not throwing.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CoordinationCommand
    {
        // The disciplines a coordination clash test actually cares about; a hidden
        // architectural/structural category is a modelling choice, a hidden MEP one
        // with real elements in it is a category that will never clash in Navisworks
        // no matter how carefully the model was routed.
        private static readonly BuiltInCategory[] NavisworksMepCategories =
        {
            BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctAccessory,
            BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_DuctTerminal,
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory,
            BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_Sprinklers,
            BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting,
            BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting,
            BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_PlumbingEquipment,
            BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_LightingFixtures
        };

        private static CommandResult NavisworksReadiness(Document doc, JObject request)
        {
            string howNamed;
            View3D view = FindNavisworksView(doc, out howNamed);
            if (view == null)
                return CommandResult.Fail(
                    "No 3D view named exactly 'Navisworks', and no default '{3D}' view either - those are the " +
                    "two names Navisworks' own Revit exporter looks for when publishing or linking a view. " +
                    "Create one of those two names, or point Navisworks at a different view directly. Nothing was read.");

            ViewDetailLevel detail = view.DetailLevel;
            bool detailFine = detail == ViewDetailLevel.Fine;

            bool sectionBoxActive = false;
            JObject sectionBoxJson = null;
            try
            {
                sectionBoxActive = view.IsSectionBoxActive;
                if (sectionBoxActive)
                {
                    BoundingBoxXYZ box = view.GetSectionBox();
                    if (box != null)
                        sectionBoxJson = new JObject
                        {
                            ["min_mm"] = new JArray(box.Min.X * MmPerFoot3, box.Min.Y * MmPerFoot3, box.Min.Z * MmPerFoot3),
                            ["max_mm"] = new JArray(box.Max.X * MmPerFoot3, box.Max.Y * MmPerFoot3, box.Max.Z * MmPerFoot3)
                        };
                }
            }
            catch { /* section box is reported best-effort; its absence is not a readiness blocker on its own */ }

            string phaseName = SafeViewPhaseName(doc, view);

            JArray hiddenWithElements;
            List<string> hiddenMep;
            CategoriesHiddenWithElements(doc, view, out hiddenWithElements, out hiddenMep);

            bool ready = detailFine && hiddenMep.Count == 0;
            var reasons = new JArray();
            if (!detailFine)
                reasons.Add("detail level is " + detail + ", not Fine - MEASURED 2026-09-26: at Coarse a pipe reaches " +
                            "Navisworks as a single line primitive, and a Hard clash test against that line reports " +
                            "ZERO clashes.");
            foreach (string name in hiddenMep)
                reasons.Add("MEP category '" + name + "' has elements in the model and is hidden in this view - it " +
                            "will not reach Navisworks at all.");

            return CommandResult.Ok(new JObject
            {
                ["document"] = doc.Title,
                ["view_id"] = Rid.Value(view.Id),
                ["view_name"] = SafeViewName(view),
                ["how_named"] = howNamed,
                ["detail_level"] = detail.ToString(),
                ["section_box_active"] = sectionBoxActive,
                ["section_box_mm"] = sectionBoxJson,
                ["visual_phase"] = phaseName,
                ["hidden_categories_with_elements"] = hiddenWithElements,
                ["hidden_mep_categories"] = new JArray(hiddenMep),
                ["verdict"] = ready ? "ready" : "not_ready",
                ["reasons"] = reasons,
                ["means"] =
                    "ready requires Fine detail AND no MEP category that has elements hidden in this view. A " +
                    "hidden NON-MEP category (e.g. furniture) does not block readiness on its own, but is still " +
                    "listed in hidden_categories_with_elements for visibility. This checks View.GetCategoryHidden " +
                    "only - a view filter or a category-class visibility override can still hide an element this " +
                    "call reports as visible; that is a real gap this command cannot see past."
            });
        }

        private static CommandResult PrepareNavisworks(UIApplication app, JObject request)
        {
            GateResult gate = DocumentGate.ForMutation(app, request, "horizun_coordination");
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            string howNamed;
            View3D view = FindNavisworksView(doc, out howNamed);
            if (view == null)
                return CommandResult.Fail(
                    "No 3D view named exactly 'Navisworks' or the default '{3D}' to prepare. Nothing was changed.");

            bool unhide = request.Value<bool?>("unhide") == true;
            List<string> requestedCategories = (request["categories"] as JArray ?? new JArray())
                .Select(t => (string)t).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (requestedCategories.Count > 0 && !unhide)
                return CommandResult.Fail(
                    "categories was given but unhide is not true, so nothing would be unhidden. Pass unhide=true " +
                    "to unhide the listed categories, or drop categories to change only the detail level. Nothing was changed.");

            var byName = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
            foreach (Category c in doc.Settings.Categories)
                if (c.CategoryType == CategoryType.Model) byName[c.Name] = c;

            var toUnhide = new List<Category>();
            var notFound = new List<string>();
            var cannotUnhide = new List<string>();
            if (unhide)
            {
                foreach (string name in requestedCategories)
                {
                    Category cat;
                    if (!byName.TryGetValue(name, out cat)) { notFound.Add(name); continue; }
                    bool canHide;
                    try { canHide = view.CanCategoryBeHidden(cat.Id); } catch { canHide = false; }
                    if (!canHide) { cannotUnhide.Add(name); continue; }
                    toUnhide.Add(cat);
                }
                if (notFound.Count > 0)
                    return CommandResult.Fail("categories names " + notFound.Count + " value(s) that are not a model " +
                        "category in this document: " + string.Join(", ", notFound.Take(5)) + ". Nothing was changed.");
            }

            bool alreadyFine = view.DetailLevel == ViewDetailLevel.Fine;
            string planHash = DocumentGate.PlanHash(request, "categories", "unhide");
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");

            if (dryRun)
            {
                var preview = new JObject
                {
                    ["dry_run"] = true,
                    ["view_id"] = Rid.Value(view.Id),
                    ["view_name"] = SafeViewName(view),
                    ["detail_level_before"] = view.DetailLevel.ToString(),
                    ["detail_level_after"] = "Fine",
                    ["would_change_detail_level"] = !alreadyFine,
                    ["would_unhide"] = new JArray(toUnhide.Select(c => c.Name)),
                    ["cannot_unhide"] = new JArray(cannotUnhide),
                    ["means"] =
                        "cannot_unhide names a category View.CanCategoryBeHidden refuses for THIS view - typically " +
                        "a view template governing category visibility, or a dependent view. Nothing here can " +
                        "override that; change it on the view or its template directly, then re-run this."
                };
                DocumentGate.StampConfirmation(preview, gate, "horizun_coordination", planHash, true, null);
                return CommandResult.Ok(preview);
            }

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, "horizun_coordination", planHash);
            if (refusal != null) return refusal;

            string txName = "Horizun: prepare view for Navisworks";
            using (var tx = new Transaction(doc, txName))
            {
                RevitErrorRecorder said = RevitErrorRecorder.On(tx);
                tx.Start();
                try
                {
                    view.DetailLevel = ViewDetailLevel.Fine;
                    foreach (Category c in toUnhide)
                        try { view.SetCategoryHidden(c.Id, false); } catch { /* re-read below decides, not this */ }
                    Guard.Commit(tx, txName);
                }
                catch (Exception ex)
                {
                    string rb = tx.GetStatus() == TransactionStatus.Started ? Guard.RollBack(tx).StatusName : PlanFailure.NotAttempted;
                    return CommandResult.Fail("prepare_navisworks failed: " + ex.Message + said.Said() + " " +
                        PlanFailure.SingleTransactionOutcome(tx.GetStatus() == TransactionStatus.Started, rb, "nothing was changed"));
                }
            }

            // ---- re-read from the committed model; this decides, not the call not throwing ----
            View3D reread = doc.GetElement(view.Id) as View3D;
            bool detailOk = reread != null && reread.DetailLevel == ViewDetailLevel.Fine;
            var checklist = new PostconditionCheck("detail_level");
            checklist.Compare("detail_level", "Fine", reread?.DetailLevel.ToString());

            var unhidOk = new List<string>();
            var unhidFailed = new List<string>();
            foreach (Category c in toUnhide)
            {
                bool stillHidden = true;
                try { stillHidden = reread != null && reread.GetCategoryHidden(c.Id); } catch { }
                if (stillHidden) unhidFailed.Add(c.Name); else unhidOk.Add(c.Name);
            }

            var applied = new JObject
            {
                ["dry_run"] = false,
                ["view_id"] = Rid.Value(view.Id),
                ["view_name"] = SafeViewName(reread),
                ["detail_level"] = reread?.DetailLevel.ToString(),
                ["unhidden"] = new JArray(unhidOk),
                ["unhide_failed"] = new JArray(unhidFailed),
                ["cannot_unhide"] = new JArray(cannotUnhide),
                ["postconditions"] = checklist.ToJson()
            };
            int failedCount = (detailOk ? 0 : 1) + unhidFailed.Count;
            ApplicationOutcome.StampApplied(applied, ApplicationOutcome.Committed,
                1 + toUnhide.Count, (detailOk ? 1 : 0) + unhidOk.Count, (detailOk ? 1 : 0) + unhidOk.Count,
                0, failedCount, 0);
            if (!detailOk)
                return CommandResult.FailWithDetail(
                    "The detail level was set but re-reading the view does not show Fine. Success is not claimed.", applied);
            return CommandResult.Ok(applied);
        }

        /// <summary>
        /// The 3D view Navisworks' own Revit exporter reads: one named exactly
        /// "Navisworks" if the project has it, else the ever-present default "{3D}".
        /// Templates are excluded - a template cannot be exported or linked.
        /// </summary>
        private static View3D FindNavisworksView(Document doc, out string howNamed)
        {
            List<View3D> all = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .Where(v => { try { return !v.IsTemplate; } catch { return false; } }).ToList();
            View3D named = all.FirstOrDefault(v => string.Equals(SafeViewName(v), "Navisworks", StringComparison.Ordinal));
            if (named != null) { howNamed = "a 3D view named exactly 'Navisworks'"; return named; }
            View3D default3d = all.FirstOrDefault(v => string.Equals(SafeViewName(v), "{3D}", StringComparison.Ordinal));
            if (default3d != null) { howNamed = "the default '{3D}' view (no view named 'Navisworks' exists)"; return default3d; }
            howNamed = null;
            return null;
        }

        private static string SafeViewPhaseName(Document doc, View view)
        {
            try
            {
                Parameter p = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                ElementId phaseId = p?.AsElementId();
                if (phaseId == null || phaseId == ElementId.InvalidElementId) return null;
                return (doc.GetElement(phaseId) as Phase)?.Name;
            }
            catch { return null; }
        }

        /// <summary>
        /// Every MODEL category that (a) has at least one element in the document and
        /// (b) is hidden in `view`, per View.GetCategoryHidden. `hiddenMep` is the subset
        /// NavisworksMepCategories names - the one that decides the readiness verdict.
        /// </summary>
        private static void CategoriesHiddenWithElements(Document doc, View view, out JArray rows, out List<string> hiddenMep)
        {
            rows = new JArray();
            hiddenMep = new List<string>();
            var mepIds = new HashSet<long>(NavisworksMepCategories.Select(b => (long)b));
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat.CategoryType != CategoryType.Model || cat.Id == null) continue;
                bool hidden;
                try { hidden = view.GetCategoryHidden(cat.Id); } catch { continue; }
                if (!hidden) continue;
                bool hasElements;
                try { hasElements = new FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsNotElementType().FirstElement() != null; }
                catch { continue; }
                if (!hasElements) continue;
                bool isMep = mepIds.Contains(Rid.Value(cat.Id));
                rows.Add(new JObject { ["category"] = cat.Name, ["category_id"] = Rid.Value(cat.Id), ["mep"] = isMep });
                if (isMep) hiddenMep.Add(cat.Name);
            }
        }
    }
}
