// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// AN EXPORT IS A DELIVERY OF OUTPUT, NOT A WRITE.
//
// MEASURED (Comité de obra, 2026-10-01, Revit 2026.4): horizun_export format=nwc on a
// structural model came back with model_changes.modified = 11 - eleven rebar elements
// that Revit's Navisworks exporter regenerated and COMMITTED while it wrote the file.
// The tool is documented as output only; the document was left dirty by a call that
// never asked to change it. The IFC exporter does the same on purpose (export marks)
// and refuses to run without an open transaction, which the handler used to commit.
//
// So the exporters that are known to write run inside a TransactionGroup that is
// ROLLED BACK after the file is on disk. Whatever the exporter committed is taken
// back; the file it wrote stays. The reply says what the exporter changed (counted
// before the rollback) and what was left after it (counted after, against the model)
// - and if the group could not be opened or the rollback did not take, it says the
// model was LEFT MODIFIED instead of letting model_changes be the only witness.
//
// Revit-free: the Revit half opens the group and counts; this decides what the
// counts mean and how loudly to say it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ExportIsolationRules
    {
        public const string NoModelChange = "no_model_change";
        public const string RolledBack = "exporter_changes_rolled_back";
        public const string RollbackFailed = "rollback_failed_model_modified";
        public const string UnavailableModified = "isolation_unavailable_model_modified";
        public const string UnavailableUnchanged = "isolation_unavailable_no_change";

        /// <summary>
        /// The formats whose Revit exporter is known to commit to the document: NWC
        /// (regeneration - measured on rebar) and IFC (export marks, and a hard
        /// requirement for an open transaction). The others export from a document
        /// that is not modifiable and have never been measured writing.
        /// </summary>
        public static bool Isolates(string format) =>
            string.Equals(format, "nwc", StringComparison.Ordinal) ||
            string.Equals(format, "ifc", StringComparison.Ordinal);

        /// <summary>The counts the Revit half measured.</summary>
        public sealed class Facts
        {
            /// <summary>The group was opened around the export.</summary>
            public bool GroupStarted;
            /// <summary>Why it could not be opened; null when it was.</summary>
            public string UnavailableReason;
            /// <summary>TransactionStatus of the rollback as text; null when none ran.</summary>
            public string RollbackStatus;
            /// <summary>The rollback threw; null when it did not.</summary>
            public string RollbackError;
            /// <summary>What the exporter committed, before the rollback.</summary>
            public int Added, Modified, Deleted;
            /// <summary>What is still changed after the rollback, measured against the model.</summary>
            public int ResidualAdded, ResidualModified, ResidualDeleted;
            public List<string> Transactions = new List<string>();
            /// <summary>A few of the changed elements, described while they still existed.</summary>
            public JArray Sample = new JArray();
        }

        public static string Status(Facts f)
        {
            int residual = f.ResidualAdded + f.ResidualModified + f.ResidualDeleted;
            int exporter = f.Added + f.Modified + f.Deleted;
            if (!f.GroupStarted) return residual > 0 || exporter > 0 ? UnavailableModified : UnavailableUnchanged;
            // What the model answers after the rollback wins over what the rollback said.
            if (residual > 0) return RollbackFailed;
            if (exporter == 0) return NoModelChange;
            if (f.RollbackError != null || !string.Equals(f.RollbackStatus, "RolledBack", StringComparison.Ordinal))
                return RollbackFailed;
            return RolledBack;
        }

        /// <summary>True when the call leaves the document changed (or cannot prove it did not).</summary>
        public static bool ModelLeftModified(Facts f)
        {
            string s = Status(f);
            return s == RollbackFailed || s == UnavailableModified;
        }

        public static JObject Report(string format, Facts f)
        {
            string status = Status(f);
            var o = new JObject
            {
                ["status"] = status,
                ["format"] = format,
                ["isolated"] = f.GroupStarted,
                ["model_left_modified"] = ModelLeftModified(f),
                ["exporter_changes"] = new JObject
                {
                    ["added"] = f.Added, ["modified"] = f.Modified, ["deleted"] = f.Deleted,
                    ["transactions"] = new JArray(f.Transactions.Distinct(StringComparer.Ordinal)),
                    ["sample"] = f.Sample ?? new JArray()
                },
                ["after_rollback"] = new JObject
                {
                    ["added"] = f.ResidualAdded, ["modified"] = f.ResidualModified, ["deleted"] = f.ResidualDeleted
                }
            };
            if (f.RollbackStatus != null) o["rollback_status"] = f.RollbackStatus;
            if (f.RollbackError != null) o["rollback_error"] = f.RollbackError;
            if (f.UnavailableReason != null) o["unavailable_reason"] = f.UnavailableReason;
            o["means"] = Means(status, f);
            return o;
        }

        /// <summary>The sentence that goes first in the reply, or null when nothing needs saying.</summary>
        public static string Headline(string format, Facts f)
        {
            string status = Status(f);
            int exporter = f.Added + f.Modified + f.Deleted;
            switch (status)
            {
                case RolledBack:
                    return "The " + format.ToUpperInvariant() + " exporter changed " + exporter + " element(s) of the model " +
                           "while writing the file; those changes were rolled back and the model is as it was " +
                           "(see model_isolation).";
                case RollbackFailed:
                    return "THE MODEL WAS LEFT MODIFIED: the " + format.ToUpperInvariant() + " exporter changed " + exporter +
                           " element(s) and the rollback did not take them all back (" +
                           (f.ResidualAdded + f.ResidualModified + f.ResidualDeleted) + " still changed). The file was written; " +
                           "do not save the model before reviewing model_isolation.";
                case UnavailableModified:
                    return "THE MODEL WAS LEFT MODIFIED: the export could not be isolated (" + (f.UnavailableReason ?? "unknown reason") +
                           ") and the " + format.ToUpperInvariant() + " exporter changed " + exporter + " element(s). " +
                           "The file was written; do not save the model before reviewing model_isolation.";
                default:
                    return null;
            }
        }

        private static string Means(string status, Facts f)
        {
            switch (status)
            {
                case NoModelChange:
                    return "The export ran inside a transaction group that was rolled back, and the exporter changed nothing.";
                case RolledBack:
                    return "Revit's exporter committed changes to the document while writing the file (counted in " +
                           "exporter_changes, before the rollback). The export ran inside a transaction group that was rolled " +
                           "back after the file was on disk, so the model is as it was before the call; the file is unaffected.";
                case RollbackFailed:
                    return "The export ran inside a transaction group, but after its rollback the model still differs " +
                           "(after_rollback) or the rollback did not report RolledBack. Treat the document as modified by this call.";
                case UnavailableModified:
                    return "The export could not be isolated in a transaction group, and the exporter changed the document. " +
                           "Treat it as modified by this call.";
                default:
                    return "The export could not be isolated in a transaction group; the exporter was observed and changed nothing.";
            }
        }
    }
}
