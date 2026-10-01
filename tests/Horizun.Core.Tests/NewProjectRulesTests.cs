// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_document_session operation=new_project (course dry run 2026-09-30,
// defect #18: no typed route created a blank project, so the run fell back to a
// copy of another model). The Revit half is NewProjectDocument + SaveAs; these are
// the decisions around it - never overwrite, which template, a newer template, and
// what the confirmation token binds. No Revit, no files.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class NewProjectRulesTests
    {
        private const string Target = @"C:\Proyectos\Edificio\Edificio - CAD.rvt";

        // ---- the target --------------------------------------------------------

        [Fact]
        public void An_empty_rvt_path_in_an_existing_folder_is_accepted()
        {
            Assert.Null(NewProjectRules.TargetProblem(Target, exists: false, folderExists: true));
        }

        [Fact]
        public void An_existing_file_is_refused_and_there_is_no_way_to_overwrite_it()
        {
            string why = NewProjectRules.TargetProblem(Target, exists: true, folderExists: true);
            Assert.NotNull(why);
            Assert.Contains("NEVER overwrites", why);
            Assert.Contains("Nothing was created", why);
        }

        [Fact]
        public void A_path_whose_existence_cannot_be_tested_is_refused_like_an_existing_one()
        {
            Assert.Contains("could not be tested", NewProjectRules.TargetProblem(Target, exists: null, folderExists: true));
        }

        [Theory]
        [InlineData(null, "required")]
        [InlineData("", "required")]
        [InlineData(@"relative\Edificio.rvt", "absolute")]
        [InlineData(@"C:\Proyectos\Edificio.rfa", ".rvt")]
        [InlineData(@"C:\Proyectos\Edificio.rte", ".rvt")]
        public void The_target_must_be_an_absolute_rvt(string target, string expected)
        {
            Assert.Contains(expected, NewProjectRules.TargetProblem(target, exists: false, folderExists: true));
        }

        [Fact]
        public void A_missing_folder_is_refused_not_created()
        {
            Assert.Contains("folder does not exist", NewProjectRules.TargetProblem(Target, exists: false, folderExists: false));
        }

        // ---- the template ------------------------------------------------------

        private static Func<string, bool> Files(params string[] present)
        {
            var set = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
            return p => set.Contains(p);
        }

        [Fact]
        public void A_named_template_is_used_and_reported_as_the_argument()
        {
            const string tpl = @"C:\Plantillas\Oficina.rte";
            Assert.Null(NewProjectRules.ResolveTemplate(tpl, @"C:\Default.rte", Files(tpl, @"C:\Default.rte"),
                                                        out string resolved, out string source));
            Assert.Equal(tpl, resolved);
            Assert.Equal(NewProjectRules.FromArgument, source);
        }

        [Fact]
        public void Without_a_template_path_Revits_default_is_used_and_named()
        {
            const string def = @"C:\ProgramData\Autodesk\RVT 2026\Templates\Spanish\Plantilla arquitectonica.rte";
            Assert.Null(NewProjectRules.ResolveTemplate(null, def, Files(def), out string resolved, out string source));
            Assert.Equal(def, resolved);
            Assert.Equal(NewProjectRules.FromRevitDefault, source);
        }

        [Fact]
        public void No_template_anywhere_is_a_refusal_not_a_template_less_project()
        {
            string why = NewProjectRules.ResolveTemplate(null, "", Files(), out string resolved, out _);
            Assert.Null(resolved);
            Assert.Contains("template_path", why);
            Assert.Contains("NO template", why);
        }

        [Fact]
        public void A_configured_default_that_is_not_on_disk_is_refused()
        {
            Assert.Contains("does not exist on disk",
                NewProjectRules.ResolveTemplate(null, @"C:\Gone\Default.rte", Files(), out _, out _));
        }

        [Theory]
        [InlineData(@"C:\Plantillas\Missing.rte", "not found")]
        [InlineData(@"C:\Plantillas\Modelo.rvt", ".rte")]
        [InlineData(@"Plantillas\Oficina.rte", "absolute")]
        public void A_named_template_must_be_an_existing_absolute_rte(string tpl, string expected)
        {
            Assert.Contains(expected, NewProjectRules.ResolveTemplate(tpl, null, Files(@"C:\Plantillas\Modelo.rvt"), out _, out _));
        }

        [Fact]
        public void A_template_from_a_newer_Revit_is_refused_and_an_older_one_is_not()
        {
            Assert.Contains("older Revit cannot read it", NewProjectRules.TemplateVersionProblem("t.rte", "2027", "2026"));
            Assert.Null(NewProjectRules.TemplateVersionProblem("t.rte", "2023", "2026"));
            Assert.Null(NewProjectRules.TemplateVersionProblem("t.rte", "2026", "2026"));
            // Unreadable is not refused here: NewProjectDocument is the proof, and the apply re-reads the result.
            Assert.Null(NewProjectRules.TemplateVersionProblem("t.rte", null, "2026"));
        }

        // ---- what the token binds ---------------------------------------------

        [Fact]
        public void The_plan_hash_binds_the_template_its_stamp_and_the_target()
        {
            string baseline = NewProjectRules.PlanHash(@"C:\T\a.rte", "100@x", Target);
            Assert.Equal(baseline, NewProjectRules.PlanHash(@"c:/t/A.rte", "100@x", Target.ToUpperInvariant()));
            Assert.NotEqual(baseline, NewProjectRules.PlanHash(@"C:\T\b.rte", "100@x", Target));
            Assert.NotEqual(baseline, NewProjectRules.PlanHash(@"C:\T\a.rte", "101@y", Target));   // template replaced
            Assert.NotEqual(baseline, NewProjectRules.PlanHash(@"C:\T\a.rte", "100@x", @"C:\Otro.rvt"));
        }

        [Fact]
        public void A_token_issued_for_one_plan_does_not_create_another()
        {
            var store = new ConfirmationStore();
            string plan = NewProjectRules.PlanHash(@"C:\T\a.rte", "100@x", Target);
            Confirmation issued = store.Issue(NewProjectRules.ConfirmationScope, "host:2026", plan);

            string other = NewProjectRules.PlanHash(@"C:\T\a.rte", "100@x", @"C:\Otro.rvt");
            Assert.False(store.Validate(issued.Token, NewProjectRules.ConfirmationScope, "host:2026", other).Ok);
            Assert.True(store.Validate(issued.Token, NewProjectRules.ConfirmationScope, "host:2026", plan).Ok);
            // Single use.
            Assert.False(store.Validate(issued.Token, NewProjectRules.ConfirmationScope, "host:2026", plan).Ok);
        }
    }
}
