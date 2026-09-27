// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// SYNCHRONIZE WITH CENTRAL - the decisions, without Revit.
//
// A sync is the one write this bridge makes that can be neither rehearsed nor
// rolled back: it publishes the local's changes into a file other people work
// from and hands ownership back to the server. So everything that CAN be decided
// before the call is decided here, in code a test can hold:
//
//   * who may ask: the machine owner's grant, and the workshared read-only policy;
//   * what the document must be: workshared, and not a detached copy;
//   * what the preview is: an ESTIMATE - ownership counts plus a SAMPLE of
//     per-element update statuses, because WorksharingUtils.GetModelUpdatesStatus
//     answers per (Document, ElementId) and there is no document-level
//     "current with central" in the API;
//   * what "it worked" means for each relinquish choice, measured afterwards.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public enum SyncRelinquish { All, KeepBorrowed, None }

    public sealed class SyncRefusal
    {
        public SyncRefusal(string code, string message) { Code = code; Message = message; }
        public string Code { get; }
        public string Message { get; }
    }

    /// <summary>Tri-state: true = held, false = measured and did not hold, null = could not be measured.</summary>
    public sealed class SyncVerdict
    {
        public bool? Verified;
        public readonly List<long> UnexpectedlyOwned = new List<long>();
        public readonly List<long> UnexpectedlyReleased = new List<long>();
        public readonly List<string> Problems = new List<string>();
    }

    public static class SyncWithCentralRules
    {
        public const string ConfirmationScope = "horizun_document_session:sync_with_central";
        public const string CurrentWithCentral = "CurrentWithCentral";
        public const string Unreadable = "unreadable";
        public const int SpreadSampleSize = 150;
        public const int OwnedSampleSize = 50;

        /// <summary>
        /// What the confirmation token is bound to. The token binds the ESTIMATE, not only
        /// the request: if ownership, IsModified or the sampled statuses move between the
        /// preview and the apply, the plan the person approved is no longer the one that
        /// would run, and the apply is refused.
        /// </summary>
        public static readonly string[] EstimateFields =
        {
            "document", "comment", "relinquish", "compact", "owned_worksets", "owned_elements",
            "borrowed_elements", "is_modified", "sample_status_counts"
        };

        public static bool TryParseRelinquish(string value, out SyncRelinquish choice)
        {
            switch ((value ?? "all").Trim().ToLowerInvariant())
            {
                case "all": choice = SyncRelinquish.All; return true;
                case "keep_borrowed": choice = SyncRelinquish.KeepBorrowed; return true;
                case "none": choice = SyncRelinquish.None; return true;
                default: choice = SyncRelinquish.All; return false;
            }
        }

        public static string Name(SyncRelinquish choice)
        {
            return choice == SyncRelinquish.All ? "all" : choice == SyncRelinquish.KeepBorrowed ? "keep_borrowed" : "none";
        }

        /// <summary>
        /// Where the owner turns it on. Said in every refusal, because a refusal that does
        /// not say who can change it invites the caller to look for a way around it.
        /// </summary>
        public static string HowOwnerEnables(string settingsPath)
        {
            return "Only the machine owner enables it, inside Revit: Horizun Hub tab > Advanced options > " +
                   "Synchronize with central. The choice is stored in " + (settingsPath ?? "settings.json") +
                   ", which no MCP call writes; do not edit it on the owner's behalf.";
        }

        /// <summary>The document's shape. null = it has a central this call could synchronize with.</summary>
        public static SyncRefusal DocumentRefusal(bool? workshared, bool? detached)
        {
            if (workshared == false)
                return new SyncRefusal("not_workshared",
                    "is not workshared: there is no central model to synchronize with. Nothing ran.");
            if (workshared == null)
                return new SyncRefusal("workshared_state_unreadable",
                    "could not report whether it is workshared (Document.IsWorkshared threw). An unknown " +
                    "collaboration state is not a central to write to. Nothing ran.");
            if (detached == true)
                return new SyncRefusal("detached_copy",
                    "is a DETACHED copy: it was cut loose from its central and has nothing to synchronize with. " +
                    "Nothing ran. Save it with save/save_as if it must be kept.");
            if (detached == null)
                return new SyncRefusal("detached_state_unreadable",
                    "could not report whether it is detached (Document.IsDetached threw). Nothing ran.");
            return null;
        }

        /// <summary>
        /// Who allowed it. The workshared read-only policy wins over the grant: an owner
        /// who said "look only at shared models" has said no to this as well, and the two
        /// switches must never be read as "the newer one wins".
        /// </summary>
        public static SyncRefusal AuthorisationRefusal(bool ownerEnabled, bool forceReadOnlyOnWorkshared,
                                                       string settingsPath)
        {
            if (forceReadOnlyOnWorkshared)
                return new SyncRefusal("force_read_only_on_workshared",
                    "This machine protects shared models (force_read_only_on_workshared=true): the assistant may " +
                    "only look at them, and a synchronize with central is a write to the central. Nothing ran. " +
                    "The owner removes that protection in Revit: Horizun Hub tab > Advanced options.");
            if (!ownerEnabled)
                return new SyncRefusal("sync_not_authorised",
                    "Synchronize with central is OFF on this machine, which is the default: it publishes into a " +
                    "file other people work from and cannot be undone. Nothing ran. " + HowOwnerEnables(settingsPath));
            return null;
        }

        /// <summary>
        /// A deterministic sample of element ids: an even spread over the whole sorted id
        /// range (so an estimate is not only the oldest or newest elements) plus the first
        /// owned elements (where not-yet-in-central changes live). Deterministic so the
        /// preview and the apply sample the same ids and the token can bind their statuses.
        /// </summary>
        public static List<long> Sample(IEnumerable<long> allIds, IEnumerable<long> ownedIds, int spread, int owned)
        {
            var sorted = (allIds ?? Enumerable.Empty<long>()).Distinct().OrderBy(x => x).ToList();
            var picked = new SortedSet<long>();
            if (spread > 0 && sorted.Count > 0)
            {
                if (sorted.Count <= spread) foreach (long id in sorted) picked.Add(id);
                else
                    for (int i = 0; i < spread; i++)
                        picked.Add(sorted[(int)((long)i * sorted.Count / spread)]);
            }
            if (owned > 0)
                foreach (long id in (ownedIds ?? Enumerable.Empty<long>()).Distinct().OrderBy(x => x).Take(owned))
                    picked.Add(id);
            return picked.ToList();
        }

        /// <summary>Count of each status name, ordinal-sorted so it hashes the same every time.</summary>
        public static SortedDictionary<string, int> Counts(IEnumerable<string> statuses)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (string s in statuses ?? Enumerable.Empty<string>())
            {
                string key = s ?? "gone";
                counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
            }
            return counts;
        }

        /// <summary>
        /// Did the ownership left behind match the relinquish choice? Measured, per choice:
        ///   all           no workset and no element left owned by this user;
        ///   keep_borrowed no workset owned, and exactly the elements borrowed before
        ///                 (still existing) still owned;
        ///   none          the same number of worksets owned, and exactly the elements
        ///                 owned before (still existing) still owned.
        /// An unreadable count or element is UNMEASURED (null), never a pass.
        /// </summary>
        public static SyncVerdict VerifyOwnership(SyncRelinquish choice,
            int? ownedWorksetsBefore, ICollection<long> ownedElementsBefore, ICollection<long> borrowedBefore,
            int? ownedWorksetsAfter, ICollection<long> ownedElementsAfter, int unreadableAfter,
            Func<long, bool> stillExists)
        {
            var v = new SyncVerdict();
            Func<long, bool> exists = stillExists ?? (_ => true);
            bool unmeasured = false;

            if (ownedWorksetsAfter == null) { unmeasured = true; v.Problems.Add("owned workset count after the sync could not be read"); }
            if (unreadableAfter > 0) { unmeasured = true; v.Problems.Add(unreadableAfter + " element(s) did not report a checkout status after the sync"); }

            int? expectedWorksets = choice == SyncRelinquish.None ? ownedWorksetsBefore : 0;
            if (choice == SyncRelinquish.None && ownedWorksetsBefore == null)
            { unmeasured = true; v.Problems.Add("owned workset count before the sync was not read"); }

            IEnumerable<long> expectedSource =
                choice == SyncRelinquish.All ? Enumerable.Empty<long>()
                : choice == SyncRelinquish.KeepBorrowed ? (borrowedBefore ?? new long[0])
                : (ownedElementsBefore ?? new long[0]);
            var expected = new HashSet<long>(expectedSource.Where(exists));
            var actual = new HashSet<long>(ownedElementsAfter ?? new long[0]);

            v.UnexpectedlyOwned.AddRange(actual.Where(id => !expected.Contains(id)).OrderBy(x => x));
            v.UnexpectedlyReleased.AddRange(expected.Where(id => !actual.Contains(id)).OrderBy(x => x));

            bool worksetsHeld = ownedWorksetsAfter != null && expectedWorksets != null && ownedWorksetsAfter == expectedWorksets;
            if (ownedWorksetsAfter != null && expectedWorksets != null && !worksetsHeld)
                v.Problems.Add("expected " + expectedWorksets + " owned workset(s) after relinquish=" + Name(choice) +
                               ", measured " + ownedWorksetsAfter);
            if (v.UnexpectedlyOwned.Count > 0)
                v.Problems.Add(v.UnexpectedlyOwned.Count + " element(s) still owned that relinquish=" + Name(choice) + " should have released");
            if (v.UnexpectedlyReleased.Count > 0)
                v.Problems.Add(v.UnexpectedlyReleased.Count + " element(s) released that relinquish=" + Name(choice) + " should have kept");

            bool measuredMismatch = (ownedWorksetsAfter != null && expectedWorksets != null && !worksetsHeld) ||
                                    v.UnexpectedlyOwned.Count > 0 || v.UnexpectedlyReleased.Count > 0;
            v.Verified = measuredMismatch ? false : (unmeasured ? (bool?)null : true);
            return v;
        }

        /// <summary>
        /// After a sync every sampled element that still exists must read CurrentWithCentral:
        /// its local changes went up and the central's came down. An element that no longer
        /// exists (deleted in central, reloaded) is not a failure; an unreadable one is
        /// unmeasured. Keys are element ids; a null value means the element is gone.
        /// </summary>
        public static SyncVerdict VerifyUpdates(IDictionary<long, string> after)
        {
            var v = new SyncVerdict();
            bool unmeasured = false;
            foreach (var kv in (after ?? new Dictionary<long, string>()).OrderBy(k => k.Key))
            {
                if (kv.Value == null) continue;
                if (kv.Value == Unreadable) { unmeasured = true; continue; }
                if (!string.Equals(kv.Value, CurrentWithCentral, StringComparison.Ordinal))
                {
                    v.UnexpectedlyOwned.Add(kv.Key);
                    v.Problems.Add("element " + kv.Key + " reads " + kv.Value + " after the sync");
                }
            }
            if (unmeasured) v.Problems.Add("some sampled elements did not report an update status");
            v.Verified = v.UnexpectedlyOwned.Count > 0 ? false : (unmeasured ? (bool?)null : true);
            return v;
        }
    }
}
