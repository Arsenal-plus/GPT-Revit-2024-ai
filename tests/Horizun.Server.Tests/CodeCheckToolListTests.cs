// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// horizun_code_check gained ONE write (operation=travel_distance with
// travel.create_paths) and is therefore classified MutatingUnlessDryRun. Its
// requirement-set check and its measurement write nothing, so a read_only machine
// must still be OFFERED the tool: the command refuses create_paths there itself.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class CodeCheckToolListTests
    {
        private const string Tool = "horizun_code_check";

        private static JObject Entry(string tool) =>
            (JObject)Tools.List().FirstOrDefault(t => (string)t["name"] == tool);

        [Fact]
        public void A_read_only_machine_is_still_offered_the_check()
        {
            WithDataRoot("{\"permission_profile\":\"read_only\"}", () =>
            {
                Assert.True(Entry(Tool) != null, Tool + " must stay listed under read_only: operation=check writes nothing");
                Assert.Null(Tools.DisabledReason(Tool));
                // ...while a tool that only writes stays hidden.
                Assert.Null(Entry("horizun_fix_planimetry"));
            });
        }

        [Fact]
        public void It_is_listed_under_every_profile_and_declares_its_operations()
        {
            foreach (string profile in new[] { "read_only", "safe_write", "full_write", "unsafe_code" })
                WithDataRoot("{\"permission_profile\":\"" + profile + "\"}", () =>
                {
                    JObject entry = Entry(Tool);
                    Assert.True(entry != null, Tool + " must be advertised under permission_profile=" + profile);
                    var ops = ((JArray)entry["inputSchema"]["properties"]["operation"]["enum"]).Select(t => (string)t).ToArray();
                    Assert.Equal(new[] { "check", "travel_distance", "energy_readiness", "headroom" }, ops);
                });
        }

        private static void WithDataRoot(string settingsJson, Action action)
        {
            string saved = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            string temp = Path.Combine(Path.GetTempPath(), "hz-codecheck-list-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, temp);
                if (settingsJson != null) File.WriteAllText(HorizunPaths.SettingsPath(), settingsJson);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, saved);
                try { Directory.Delete(temp, true); } catch { }
            }
        }
    }
}
