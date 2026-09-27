// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// set_view_display: a view's DETAIL LEVEL and/or DISCIPLINE, set because a
// planimetry finding says they are wrong - never on its own initiative.
//
// Two ways a request here could be honoured by nobody, both refused by name
// before any transaction:
//   * the view's template CONTROLS the parameter (it is among
//     GetTemplateParameterIds and not among GetNonControlledTemplateParameterIds).
//     Revit would accept the assignment and the template would overwrite it, so
//     the "fix" would be a write that does not stay. The honest remedies are a
//     template change or an edit of the template itself - both deliberate.
//   * the view kind has no such property (HasDetailLevel / HasViewDiscipline) or
//     Revit says it cannot be modified (CanModifyDetailLevel /
//     CanModifyViewDiscipline).
// And the finding must be ABOUT each property set (observed.field): a detail-level
// finding never licenses a discipline change, a finding about the name neither.
// The property the caller did NOT name is re-read too, so a write that moved it
// as a side effect is a failed postcondition rather than an unnoticed change; a side
// that could not be read is named unreadable, never passed as "unchanged".
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class FixPlanimetryCommand
    {
        private static void PlanSetDisplay(Document doc, JObject a, PlanimetryFixRules.CitedFinding cited, Plan plan)
        {
            View view = Need<View>(doc, a, "view_id");
            RequireTargetInFinding(cited, Rid.Value(view.Id), "view_id");
            if (view.IsTemplate)
                throw new ArgumentException("view_id " + Rid.Value(view.Id) + " is a view template; change the " +
                                            "views that use it, or edit the template deliberately.");
            if (view is ViewSheet || view is ViewSchedule)
                throw new ArgumentException("view_id " + Rid.Value(view.Id) + " is a " + view.GetType().Name +
                                            ", which has no detail level or discipline to set.");

            string error = PlanimetryFixRules.EnumNameError("detail_level", a["detail_level"],
                                                            PlanimetryFixRules.DetailLevels);
            if (error != null) throw new ArgumentException(error);
            error = PlanimetryFixRules.EnumNameError("discipline", a["discipline"], PlanimetryFixRules.Disciplines);
            if (error != null) throw new ArgumentException(error);
            string wantDetail = a.Value<string>("detail_level");
            string wantDiscipline = a.Value<string>("discipline");
            foreach (string property in new[] { wantDetail != null ? "detail_level" : null,
                                                wantDiscipline != null ? "discipline" : null })
            {
                string notAbout = property == null ? null : PlanimetryFixRules.DisplayPropertyError(cited, property);
                if (notAbout != null) throw new ArgumentException(notAbout);
            }

            if (wantDetail != null)
            {
                RefuseTemplateControlled(doc, view, BuiltInParameter.VIEW_DETAIL_LEVEL, "Detail Level");
                if (!view.HasDetailLevel())
                    throw new ArgumentException("view_id " + Rid.Value(view.Id) + " (" + view.ViewType + ") has " +
                                                "no detail level (View.HasDetailLevel is false).");
                if (!view.CanModifyDetailLevel())
                    throw new ArgumentException("Revit reports that the detail level of view_id " +
                        Rid.Value(view.Id) + " cannot be modified (View.CanModifyDetailLevel is false). " +
                        "Nothing was written.");
            }
            if (wantDiscipline != null)
            {
                RefuseTemplateControlled(doc, view, BuiltInParameter.VIEW_DISCIPLINE, "Discipline");
                if (!view.HasViewDiscipline())
                    throw new ArgumentException("view_id " + Rid.Value(view.Id) + " (" + view.ViewType + ") has " +
                                                "no discipline (View.HasViewDiscipline is false).");
                if (!view.CanModifyViewDiscipline())
                    throw new ArgumentException("Revit reports that the discipline of view_id " +
                        Rid.Value(view.Id) + " cannot be modified (View.CanModifyViewDiscipline is false). " +
                        "Nothing was written.");
            }

            plan.TargetId = view.Id;
            plan.TargetUniqueId = SafeUid(view);
            plan.TargetClass = view.GetType().Name;
            plan.DetailLevel = wantDetail;
            plan.Discipline = wantDiscipline;
            plan.Before["detail_level"] = ReadDetailLevel(view);
            plan.Before["discipline"] = ReadDiscipline(view);
        }

        /// <summary>
        /// Refuses when the view's template controls `bip`. Same rule as manage_views'
        /// view_scale: controlled = GetTemplateParameterIds minus the non-controlled ones.
        /// </summary>
        private static void RefuseTemplateControlled(Document doc, View view, BuiltInParameter bip, string label)
        {
            ElementId templateId = view.ViewTemplateId;
            if (templateId == null || templateId == ElementId.InvalidElementId) return;
            var template = doc.GetElement(templateId) as View;
            if (template == null) return;   // a dangling id controls nothing; CanModify* still judges
            var controlled = new HashSet<ElementId>(template.GetTemplateParameterIds());
            foreach (ElementId id in template.GetNonControlledTemplateParameterIds()) controlled.Remove(id);
            if (controlled.Contains(new ElementId(bip)))
                throw new ArgumentException("view template '" + SafeName(template) + "' (" + Rid.Value(templateId) +
                    ") CONTROLS " + label + " on view_id " + Rid.Value(view.Id) + ": an assignment would be " +
                    "overwritten by the template. Apply a template that leaves " + label + " uncontrolled " +
                    "(set_view_template) or edit the template itself. Nothing was written.");
        }

        private static string ReadDetailLevel(View view)
        {
            try { return view.HasDetailLevel() ? view.DetailLevel.ToString() : "<none>"; }
            catch (Exception ex) { return "<unreadable: " + ex.Message + ">"; }
        }

        private static string ReadDiscipline(View view)
        {
            try { return view.HasViewDiscipline() ? view.Discipline.ToString() : "<none>"; }
            catch (Exception ex) { return "<unreadable: " + ex.Message + ">"; }
        }

        private static void ApplyDisplay(Document doc, Plan plan)
        {
            var view = (View)doc.GetElement(plan.TargetId);
            if (plan.DetailLevel != null)
                view.DetailLevel = (ViewDetailLevel)Enum.Parse(typeof(ViewDetailLevel), plan.DetailLevel);
            if (plan.Discipline != null)
                view.Discipline = (ViewDiscipline)Enum.Parse(typeof(ViewDiscipline), plan.Discipline);
        }

        private static PostconditionCheck VerifyDisplay(Document doc, Plan plan)
        {
            var required = new List<string>();
            if (plan.DetailLevel != null) required.Add("detail_level");
            if (plan.Discipline != null) required.Add("discipline");
            var check = new PostconditionCheck(required.ToArray());
            var view = doc.GetElement(plan.TargetId) as View;
            if (view == null)
            {
                foreach (string r in required) check.Unreadable(r, r == "detail_level" ? plan.DetailLevel : plan.Discipline,
                                                                "the view is gone");
                return check;
            }
            // The requested value, and the one NOT requested left exactly as it was.
            if (plan.DetailLevel != null) check.Compare("detail_level", plan.DetailLevel, ReadDetailLevel(view));
            else Unchanged(check, "detail_level_unchanged", plan.Before["detail_level"], ReadDetailLevel(view));
            if (plan.Discipline != null) check.Compare("discipline", plan.Discipline, ReadDiscipline(view));
            else Unchanged(check, "discipline_unchanged", plan.Before["discipline"], ReadDiscipline(view));
            return check;
        }

        // Judged only when BOTH sides were read: the same "<unreadable: ...>" text before and
        // after is not evidence that the property stayed put.
        private static void Unchanged(PostconditionCheck check, string what, string before, string now)
        {
            if (IsUnreadableText(before) || IsUnreadableText(now))
                check.Unreadable(what, before, "before=" + (before ?? "<null>") + " after=" + (now ?? "<null>"));
            else check.Compare(what, before, now);
        }

        private static bool IsUnreadableText(string v) => v == null || v.StartsWith("<unreadable", StringComparison.Ordinal);
    }
}
