// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// ModalProbe's Win32 P/Invoke needs a real HWND and is not exercised here (it
// cannot be, offline). What IS Revit-free is ModalDialogInfo's own rendering:
// the exact single-line summary DescribeModal() has always returned, and the
// structured ToJson() horizun_health's blocked-UI-thread report uses. Both are
// pure functions over already-captured fields.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ModalDialogInfoTests
    {
        [Fact]
        public void Summary_line_for_an_unenumerable_dialog_names_that_explicitly()
        {
            var info = new ModalDialogInfo { DialogWindowFound = false };
            Assert.Equal("(the Revit main window is disabled by a modal window this probe could not enumerate)",
                info.ToSummaryLine());
        }

        [Fact]
        public void Summary_line_for_a_titled_dialog_matches_the_pre_existing_DescribeModal_format()
        {
            var info = new ModalDialogInfo { DialogWindowFound = true, Title = "New Project", ClassName = "#32770" };
            Assert.Equal("'New Project' [#32770]", info.ToSummaryLine());
        }

        [Fact]
        public void Summary_line_for_an_untitled_dialog_says_so()
        {
            var info = new ModalDialogInfo { DialogWindowFound = true, Title = "", ClassName = "#32770" };
            Assert.Equal("(an untitled window) [#32770]", info.ToSummaryLine());
        }

        [Fact]
        public void Summary_line_omits_the_class_bracket_when_the_class_could_not_be_read()
        {
            var info = new ModalDialogInfo { DialogWindowFound = true, Title = "Warning", ClassName = null };
            Assert.Equal("'Warning'", info.ToSummaryLine());
        }

        [Fact]
        public void Json_carries_title_text_buttons_and_module_when_all_were_read()
        {
            var info = new ModalDialogInfo
            {
                DialogWindowFound = true,
                Title = "Warning",
                ClassName = "#32770",
                MainText = "One or more elements have no valid geometry.",
                AllText = new List<string> { "One or more elements have no valid geometry.", "Do not show me this again" },
                Buttons = new List<string> { "OK", "Cancel" },
                OwningModule = "RevitAPIUI.dll"
            };
            var json = info.ToJson();
            Assert.True((bool)json["dialog_window_found"]);
            Assert.Equal("Warning", (string)json["title"]);
            Assert.Equal("One or more elements have no valid geometry.", (string)json["main_text"]);
            Assert.Equal(2, ((JArray)json["all_text"]).Count);
            Assert.Equal("OK", (string)json["buttons"][0]);
            Assert.Equal("Cancel", (string)json["buttons"][1]);
            Assert.Equal("RevitAPIUI.dll", (string)json["owning_module"]);
            Assert.Equal("'Warning' [#32770]", (string)json["summary"]);
        }

        [Fact]
        public void Json_never_omits_a_field_just_because_it_could_not_be_read()
        {
            // A probe that could not enumerate anything beyond the disabled main
            // window still owes the caller an explicit, complete shape - null fields,
            // not missing keys, so a client does not have to guess which ones exist.
            var info = new ModalDialogInfo { DialogWindowFound = false };
            var json = info.ToJson();
            Assert.False((bool)json["dialog_window_found"]);
            Assert.Null(json["title"].Value<string>());
            Assert.Null(json["owning_module"].Value<string>());
            Assert.Empty(json["all_text"]);
            Assert.Empty(json["buttons"]);
        }
    }
}
