// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code.
//
// A PLAN THE CLIENT CAN READ, AND THE WHOLE PLAN KEPT WHERE THE APPLY CAN FIND IT.
//
// MEASURED (dry run, classes 4 and 6): horizun_plan_from_cad answered 232 kB for 75
// walls on one layer. The client truncated it to a file, and in Claude Desktop that
// broke the flow - the one part a person reads (counts, coverage, what was deferred
// and why) arrived in the same reply as the 75 rows nobody reads by eye, the
// candidate index, the layer map and every reasoning trail.
//
// So the reply can be a SUMMARY, in the repository's existing response_mode
// convention (QueryResponseOptions, ProgressiveResponse): full stays the default and
// keeps every row (it gains only plan_id and stored_plan); summary keeps every
// scalar, every count, the
// coverage, the warnings, the coherence verdict and the apply_binding WHOLE, and
// shortens each list of rows to a sample, naming every shortened list by JSON
// pointer with its real length. Nothing measured is dropped - only rows are.
//
// The rows a summary leaves out are not lost: every plan is kept whole under its
// plan_id on this machine, and horizun_apply_cad_plan takes that id instead of the
// copied actions. The apply still re-measures everything it always did; the stored
// plan replaces the client's clipboard, not the checks.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadPlanResponse
    {
        public const string Full = "full";
        public const string Summary = "summary";
        public const int SampleRows = 3;

        /// <summary>
        /// Never shortened, whatever their size: the binding is what the apply re-measures and must travel
        /// verbatim, and the warnings and the coherence are the part of a plan a person must not miss.
        /// </summary>
        private static readonly HashSet<string> KeptWhole = new HashSet<string>(StringComparer.Ordinal)
        {
            "/apply_binding", "/warnings", "/coherence", "/coverage", "/storey_placement"
        };

        /// <summary>
        /// What was NOT planned, and why, is the half a reviewer reads - so those lists keep more rows than a
        /// list of things that will simply be built.
        /// </summary>
        private static readonly Dictionary<string, int> LargerSample = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["/deferred_detail"] = 25,
            ["/withdrawn/rows"] = 25,
            ["/blocked_detail"] = 25
        };

        /// <summary>Null when the mode is one this tool answers; otherwise the refusal text.</summary>
        public static string ValidateMode(string mode)
        {
            if (mode == null || mode == Full || mode == Summary) return null;
            return "response_mode must be 'full' or 'summary'. NOTHING was planned.";
        }

        /// <summary>
        /// The id a plan is kept under: the plan it is AND the exact actions it emitted, so two plans of one
        /// drawing resolved against two models never share a slot.
        /// </summary>
        public static string PlanId(string planFingerprint, string actionsFingerprint)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes((planFingerprint ?? "") + "|" + (actionsFingerprint ?? "")));
                return "cadplanid:" + string.Concat(h.Take(16).Select(b => b.ToString("x2")));
            }
        }

        /// <summary>
        /// The summary of a full plan reply. The input is not modified. Every list of OBJECTS longer than the
        /// sample is cut to the sample and named in response_omissions; lists of numbers (a coordinate, a
        /// profile ring) and of strings are values, not rows, and are never cut.
        /// </summary>
        public static JObject Summarize(JObject full, int sample = SampleRows)
        {
            var copy = (JObject)full.DeepClone();
            var omissions = new JArray();
            Shorten(copy, "", Math.Max(1, sample), omissions);
            copy["response_mode"] = Summary;
            copy["response_omissions"] = omissions;
            copy["response_detail_complete"] = omissions.Count == 0;
            copy["response_means"] =
                "a SUMMARY: every count, the coverage, the warnings, the coherence and apply_binding are whole; each " +
                "list named in response_omissions is cut to a sample (" + sample + " rows; deferred and withdrawn " +
                "rows, 25) and says how many it had. The whole plan is kept on this machine under plan_id: horizun_apply_cad_plan takes plan_id " +
                "in place of apply_binding and actions, and response_mode='full' answers with every row.";
            return copy;
        }

        private static void Shorten(JToken token, string pointer, int sample, JArray omissions)
        {
            if (KeptWhole.Contains(pointer)) return;
            if (token is JObject obj)
            {
                foreach (JProperty p in obj.Properties().ToList())
                    Shorten(p.Value, pointer + "/" + p.Name.Replace("~", "~0").Replace("/", "~1"), sample, omissions);
                return;
            }
            var array = token as JArray;
            if (array == null) return;
            bool rows = array.Count > 0 && array.All(x => x is JObject);
            int keep;
            if (!LargerSample.TryGetValue(pointer, out keep)) keep = sample;
            if (rows && array.Count > keep)
            {
                int total = array.Count;
                while (array.Count > keep) array.RemoveAt(array.Count - 1);
                omissions.Add(new JObject
                {
                    ["json_pointer"] = pointer,
                    ["total"] = total,
                    ["shown"] = array.Count,
                    ["omitted"] = total - array.Count
                });
            }
            for (int i = 0; i < array.Count; i++) Shorten(array[i], pointer + "/" + i, sample, omissions);
        }
    }

    /// <summary>Whole plans, kept on this machine under their plan_id. One file each.</summary>
    public static class CadPlanStore
    {
        /// <summary>How long a kept plan is offered back. A plan older than this is re-planned, not applied.</summary>
        public static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);

        public static string DefaultRoot => Path.Combine(HorizunPaths.DataRoot(), "cad-plans");

        public static string PathFor(string root, string planId)
        {
            string safe = new string((planId ?? "").Where(ch => char.IsLetterOrDigit(ch)).ToArray());
            if (safe.Length == 0) throw new ArgumentException("plan_id is empty.");
            return Path.Combine(root, safe + ".json");
        }

        /// <summary>Writes the plan; returns the path, or null when it could not be kept (the reply says so).</summary>
        public static string Save(string root, string planId, JObject plan)
        {
            try
            {
                Directory.CreateDirectory(root);
                Prune(root);
                string path = PathFor(root, planId);
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, plan.ToString(Formatting.None), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return path;
            }
            catch { return null; }
        }

        /// <summary>The kept plan, or null when there is none under that id (never kept, expired or removed).</summary>
        public static JObject Load(string root, string planId)
        {
            try
            {
                string path = PathFor(root, planId);
                if (!File.Exists(path)) return null;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > KeepFor) return null;
                var plan = JObject.Parse(File.ReadAllText(path));
                // A FILE IS DATA. The id inside must be the id asked for, or it is somebody else's plan.
                return string.Equals(plan.Value<string>("plan_id"), planId, StringComparison.Ordinal) ? plan : null;
            }
            catch { return null; }
        }

        private static void Prune(string root)
        {
            foreach (string f in Directory.GetFiles(root, "*.json"))
            {
                try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > KeepFor) File.Delete(f); }
                catch { }
            }
        }
    }
}
