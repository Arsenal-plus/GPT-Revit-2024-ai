// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_cad_connect's OWN VERDICT ARITHMETIC, kept Revit-free so it can be
// unit tested - the same reasoning as PlanLedger, CorrectionApplyLoop and
// CompositeVerdict: CadConnectCommand cannot be constructed without a
// UIApplication, so the decisions here were, until now, unreachable by any
// test that did not also fake a whole Revit session.
//
// TWO QUESTIONS THIS FILE ANSWERS, both about a DELEGATED CHILD's own
// declared verdict rather than r.Success alone:
//
//   ChildVerdictBacksUp - does horizun_connect_mep's or horizun_create_elements'
//   OWN application block back up "joined"/"created" for THIS call? A child
//   that answered success over a partial write, a rollback, or with no
//   declaration at all must not be counted as landed just because it did not
//   throw - exactly the composite-verdict defect closed one level down by
//   ApplicationOutcome and one level up by CompositeVerdict.
//
//   RowChild - re-declares a junction ROW's already-decided final state
//   ("joined"/"created"/"would_join"/"would_create"/"already_connected"/
//   "uncertain"/"refused") as a CompositeChild, so the command's OWN
//   application block can be built by feeding every row through
//   CompositeVerdict - the same aggregator every other composite tool in
//   this tree uses - rather than CadConnectCommand inventing a second
//   opinion about numbers it already computed once.
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadConnectVerdict
    {
        /// <summary>
        /// Whether the delegated child's OWN declared verdict backs up "joined"/"created" -
        /// never r.Success alone. Mirrors CompositeVerdict.Classify's convention: a dry run
        /// expects a clean ApplicationState.Rehearsed, a real write expects
        /// ApplicationState.VerifiedApplied; a legitimate NoOp counts as landed either way,
        /// and anything else - Partial, RolledBack, Uncertain, or no declaration at all
        /// (ApplicationOutcome.Read is fail-closed on both) - does not, whatever r.Success
        /// says. horizun_connect_mep and horizun_create_elements both stamp this block (see
        /// WriteVerificationCatalog), so every direct join and every elbow/tee/cross fitting
        /// can be checked against it the same way.
        /// </summary>
        public static bool ChildVerdictBacksUp(CommandResult r, bool dryRun, out ApplicationState state)
        {
            state = r != null && r.Success ? ApplicationOutcome.Read(r.Data) : ApplicationState.Failed;
            if (r == null || !r.Success) return false;
            if (state == ApplicationState.NoOp) return true;
            return dryRun ? state == ApplicationState.Rehearsed : state == ApplicationState.VerifiedApplied;
        }

        /// <summary>
        /// The declaration a synthetic, already-decided row state re-states as - one unit,
        /// consistent with what ApplicationOutcome.Read would corroborate for each state (see
        /// ApplicationOutcome.Corroborated): VerifiedApplied and Rehearsed each open Read's
        /// gate and must pass its arithmetic check; NoOp is the same "nothing requested"
        /// shape either way; anything else (here, Uncertain) is taken as declared without
        /// corroboration, so its counts do not need to mean anything beyond "not landed".
        /// </summary>
        public static JObject SyntheticDeclaration(ApplicationState state)
        {
            switch (state)
            {
                case ApplicationState.VerifiedApplied:
                    return ApplicationOutcome.DeclareApplied(ApplicationOutcome.Committed, 1, 1, 1, 0, 0, 0);
                case ApplicationState.Rehearsed:
                    return ApplicationOutcome.DeclareRehearsal(1, 0, 0, 0);
                case ApplicationState.NoOp:
                    return ApplicationOutcome.DeclareApplied(ApplicationOutcome.Committed, 0, 0, 0, 0, 0, 0);
                default:
                    return ApplicationOutcome.Declare(state, ApplicationOutcome.Committed, 1, 0, 0, 0, 1, 0);
            }
        }

        /// <summary>
        /// A junction row's final state, re-declared as a CompositeChild. The state was ALREADY
        /// decided by ChildVerdictBacksUp (for "joined"/"created"/"would_join"/"would_create"/
        /// "uncertain") or by CadConnectCommand's own upfront business rules ("already_connected",
        /// "refused") before this ever sees it; this only turns that decision back into the shape
        /// CompositeVerdict.Aggregate/AggregateRehearsal can fold into ONE composite verdict.
        /// </summary>
        public static CompositeChild RowChild(JObject row)
        {
            switch (row?.Value<string>("state"))
            {
                case "joined":
                case "created":
                    return CompositeChild.Of(true, Declared(ApplicationState.VerifiedApplied));
                case "would_join":
                case "would_create":
                    return CompositeChild.Of(true, Declared(ApplicationState.Rehearsed));
                case "already_connected":
                    return CompositeChild.Of(true, Declared(ApplicationState.NoOp));
                case "uncertain":
                    return CompositeChild.Of(true, Declared(ApplicationState.Uncertain));
                default:
                    // "refused", for any reason - domain, geometry, an ambiguous or occupied
                    // connector, or a call that never got a chance to run. Never landed.
                    return CompositeChild.Of(false, null);
            }
        }

        /// <summary>
        /// A CadRefit.cs refit row's final state, re-declared as a CompositeChild the same
        /// way RowChild does for a junction row - CadRefit.Run is the other operation
        /// horizun_cad_connect answers (operation=refit), so its reply gets the same
        /// composite treatment.
        /// </summary>
        public static CompositeChild RefitRowChild(JObject row)
        {
            switch (row?.Value<string>("state"))
            {
                case "refitted":
                    return CompositeChild.Of(true, Declared(ApplicationState.VerifiedApplied));
                case "would_refit":
                    return CompositeChild.Of(true, Declared(ApplicationState.Rehearsed));
                case "uncertain":
                    return CompositeChild.Of(true, Declared(ApplicationState.Uncertain));
                default:
                    // "not_viable" (refused before anything ran), "not_rehearsed" (the group
                    // could not even open) and "rolled_back" (every step ran and the group
                    // was deliberately undone) are all, alike, not landed.
                    return CompositeChild.Of(false, null);
            }
        }

        private static JObject Declared(ApplicationState state)
        {
            var payload = new JObject();
            ApplicationOutcome.Stamp(payload, SyntheticDeclaration(state));
            return payload;
        }
    }
}
