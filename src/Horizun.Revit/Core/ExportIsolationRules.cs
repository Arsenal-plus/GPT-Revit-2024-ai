// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// AN EXPORT IS A DELIVERY OF OUTPUT, NOT A WRITE - AND THE REPLY MUST SAY WHICH.
//
// REPORTED (Comité de obra, 2026-10-01, Revit 2026.4, v2.1.5): horizun_export
// format=nwc on a structural model came back with model_changes.modified = 11, eleven
// rebar elements, and the run concluded the export had modified the model.
//
// MEASURED LIVE the same evening, on fresh copies of the same model:
//   - Revit's Navisworks exporter DOES write while it exports: one transaction,
//     "Navisworks23", adding 12 elements and modifying 12;
//   - with v2.1.5 (no isolation) and with the rolled-back group below alike, the
//     document afterwards reads Document.IsModified = false, and the eleven rebar
//     re-read byte-identical (bounding box, lengths, count, spacing);
//   - and in both, DocumentChanged still lists those eleven rebar as modified: the
//     event that takes the change back does not name them. The "11" was a residue of
//     the bridge's event accounting, not a change to the model.
//
// So two things happen here. The exporters known to write (NWC; IFC, which needs an
// open transaction for its export marks) run inside a TransactionGroup that is rolled
// back once the file is on disk - a belt for an exporter that might not clean up
// after itself. And the WITNESS for "did this call change the model" is Revit's own
// modified flag, read before and after: clean before and clean after is PROOF of no
// change, whatever the events left behind; clean before and dirty after is proof of
// one. A document that already had unsaved changes cannot testify through the flag,
// and is reported as unverified rather than called clean or modified.
//
// Revit-free: the Revit half opens the group, counts and reads the flag; this decides
// what the readings mean and how loudly to say it.
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
        public const string Unverified = "rolled_back_unverified";
        public const string UnavailableModified = "isolation_unavailable_model_modified";
        public const string UnavailableUnchanged = "isolation_unavailable_no_change";

        /// <summary>
        /// The formats whose Revit exporter is known to commit to the document: NWC
        /// (measured: transaction "Navisworks23") and IFC (export marks, and a hard
        /// requirement for an open transaction). The others export from a document
        /// that is not modifiable and have never been measured writing.
        /// </summary>
        public static bool Isolates(string format) =>
            string.Equals(format, "nwc", StringComparison.Ordinal) ||
            string.Equals(format, "ifc", StringComparison.Ordinal);

        /// <summary>The readings the Revit half took.</summary>
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
            /// <summary>Document.IsModified before the export and after the rollback; null when unreadable.</summary>
            public bool? ModifiedBefore, ModifiedAfter;
            /// <summary>What the exporter committed, by DocumentChanged, before the rollback.</summary>
            public int Added, Modified, Deleted;
            /// <summary>What DocumentChanged still lists after the rollback, settled against the model.</summary>
            public int ResidualAdded, ResidualModified, ResidualDeleted;
            public List<string> Transactions = new List<string>();
            /// <summary>A few of the changed elements, described while they still existed.</summary>
            public JArray Sample = new JArray();
            /// <summary>A few of the residual ids.</summary>
            public JArray ResidualSample = new JArray();
        }

        private static int Exporter(Facts f) => f.Added + f.Modified + f.Deleted;
        private static int Residual(Facts f) => f.ResidualAdded + f.ResidualModified + f.ResidualDeleted;

        /// <summary>Revit's own flag proves the call left the document as it found it.</summary>
        public static bool ProvenUnchanged(Facts f) => f.ModifiedBefore == false && f.ModifiedAfter == false;

        /// <summary>Revit's own flag proves the call changed the document.</summary>
        public static bool ProvenChanged(Facts f) => f.ModifiedBefore == false && f.ModifiedAfter == true;

        public static string Status(Facts f)
        {
            if (ProvenUnchanged(f))
                return f.GroupStarted && Exporter(f) > 0 ? RolledBack : f.GroupStarted ? NoModelChange : UnavailableUnchanged;
            if (ProvenChanged(f)) return f.GroupStarted ? RollbackFailed : UnavailableModified;
            // The flag cannot testify (unsaved work before the call, or unreadable): only
            // the events are left, and they are known to over-report.
            if (Exporter(f) == 0 && Residual(f) == 0) return f.GroupStarted ? NoModelChange : UnavailableUnchanged;
            return Unverified;
        }

        /// <summary>true / false when proven; null when it cannot be told.</summary>
        public static bool? ModelLeftModified(Facts f)
        {
            if (ProvenChanged(f)) return true;
            if (ProvenUnchanged(f)) return false;
            return Exporter(f) == 0 && Residual(f) == 0 ? (bool?)false : null;
        }

        public static JObject Report(string format, Facts f)
        {
            string status = Status(f);
            bool? left = ModelLeftModified(f);
            var o = new JObject
            {
                ["status"] = status,
                ["format"] = format,
                ["isolated"] = f.GroupStarted,
                ["model_left_modified"] = left.HasValue ? (JToken)left.Value : JValue.CreateNull(),
                ["proof"] = ProvenUnchanged(f) || ProvenChanged(f) ? "Document.IsModified before and after" : "none: Document.IsModified " +
                            (f.ModifiedBefore == true ? "was already true before the export" : "could not be read") + ", so only change events remain",
                ["is_modified_before"] = f.ModifiedBefore.HasValue ? (JToken)f.ModifiedBefore.Value : JValue.CreateNull(),
                ["is_modified_after"] = f.ModifiedAfter.HasValue ? (JToken)f.ModifiedAfter.Value : JValue.CreateNull(),
                ["exporter_changes"] = new JObject
                {
                    ["added"] = f.Added, ["modified"] = f.Modified, ["deleted"] = f.Deleted,
                    ["transactions"] = new JArray(f.Transactions.Distinct(StringComparer.Ordinal)),
                    ["sample"] = f.Sample ?? new JArray()
                },
                ["event_residue"] = new JObject
                {
                    ["added"] = f.ResidualAdded, ["modified"] = f.ResidualModified, ["deleted"] = f.ResidualDeleted,
                    ["sample"] = f.ResidualSample ?? new JArray(),
                    ["means"] = "ids Revit's DocumentChanged still lists after the rollback. Measured on NWC: eleven rebar " +
                                "listed here re-read byte-identical with Document.IsModified false - the event that undoes " +
                                "the change does not name them. Not evidence of a change on its own."
                }
            };
            if (f.RollbackStatus != null) o["rollback_status"] = f.RollbackStatus;
            if (f.RollbackError != null) o["rollback_error"] = f.RollbackError;
            if (f.UnavailableReason != null) o["unavailable_reason"] = f.UnavailableReason;
            o["means"] = Means(status);
            return o;
        }

        /// <summary>
        /// model_changes as this call may state it: zero, with the proof, when Revit's
        /// flag proves the document unchanged; null (let the event tally stand) otherwise.
        /// </summary>
        public static JObject ProvenModelChanges(Facts f)
        {
            if (!ProvenUnchanged(f)) return null;
            return new JObject
            {
                ["added"] = 0, ["modified"] = 0, ["deleted"] = 0, ["documents"] = new JArray(),
                ["proven_unchanged"] = true,
                ["events_listed"] = new JObject { ["added"] = f.ResidualAdded, ["modified"] = f.ResidualModified, ["deleted"] = f.ResidualDeleted },
                ["source"] = "Document.IsModified was false before this export and false after it, so the document is as it was. " +
                             "events_listed is what Revit's DocumentChanged still named (see model_isolation.event_residue)."
            };
        }

        /// <summary>The sentence that goes first in the reply, or null when nothing needs saying.</summary>
        public static string Headline(string format, Facts f)
        {
            string fmt = format.ToUpperInvariant();
            switch (Status(f))
            {
                case RollbackFailed:
                    return "THE MODEL WAS LEFT MODIFIED: the document was clean before this " + fmt + " export and Revit reports " +
                           "unsaved changes after it, although the exporter's changes were rolled back. The file was written; " +
                           "do not save the model before reviewing model_isolation.";
                case UnavailableModified:
                    return "THE MODEL WAS LEFT MODIFIED: the " + fmt + " export could not be isolated (" + (f.UnavailableReason ?? "unknown reason") +
                           ") and Revit reports unsaved changes after it on a document that was clean before. The file was written; " +
                           "do not save the model before reviewing model_isolation.";
                case Unverified:
                    return "The " + fmt + " exporter wrote " + Exporter(f) + " element(s) while exporting and they were rolled back, but " +
                           "the document " + (f.ModifiedBefore == true ? "already had unsaved changes" : "did not report its modified state") +
                           ", so Revit cannot confirm it is exactly as before; change events still list " + Residual(f) +
                           " element(s) (see model_isolation.event_residue).";
                default:
                    return null;
            }
        }

        private static string Means(string status)
        {
            switch (status)
            {
                case NoModelChange:
                    return "The export ran inside a transaction group that was rolled back, and nothing was changed.";
                case RolledBack:
                    return "Revit's exporter wrote to the document while exporting (exporter_changes). The export ran inside a " +
                           "transaction group rolled back after the file was on disk, and Document.IsModified - false before, false " +
                           "after - proves the model is as it was. The file is unaffected.";
                case RollbackFailed:
                    return "Document.IsModified was false before the export and is true after the rollback: the document was changed by this call.";
                case Unverified:
                    return "The exporter's changes were rolled back, but Document.IsModified cannot testify (it was already true, or " +
                           "unreadable). The events are all that is left, and they are known to list elements the rollback restored.";
                case UnavailableModified:
                    return "The export could not be isolated, and Document.IsModified turned true: the document was changed by this call.";
                default:
                    return "The export could not be isolated in a transaction group; Document.IsModified shows no change.";
            }
        }
    }
}
