// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_resolve_clash: SLEEVES AND STRUCTURAL OPENINGS, for a finding a move
// cannot resolve (ResolveClashCommand.cs covers the move path). Pure geometry
// lives in Core/SleeveRules.cs; this file is the Revit half.
//
//   propose_opening  READ-ONLY. One side of the finding must be an MEP run
//                     (pipe/duct/conduit/cable tray), the other a host wall,
//                     floor, roof, ceiling, or structural framing/column. The
//                     run's centreline is clipped against the host's own
//                     bounding box (Core/SleeveRules.LineBoxIntersect) to find
//                     the crossing; the opening/sleeve size is the run's outer
//                     cross-section plus clearance_mm. A wall/floor/roof/ceiling
//                     gets a cut-opening route; framing/columns get sleeve-only
//                     and the cut is refused BY NAME (host_cannot_be_cut_by_api)
//                     - the API cannot cut a beam or column except with a
//                     void-cutting family instance.
//   apply_opening    dry_run (default) -> token -> ONE TransactionGroup. Either
//                     cuts the host (Document.Create.NewOpening(wall, pt1, pt2)
//                     / NewOpening(host, CurveArray, true)) or, with
//                     sleeve_type_id, places that caller-supplied family
//                     instance at the crossing, oriented along the run
//                     (org-neutral: no family ships with this bridge); a sleeve
//                     whose family carries a void cuts its host through
//                     InstanceVoidCutUtils when the host accepts it. An optional
//                     approval_parameter/approval_value is written on the
//                     created element and re-read. Kept ONLY when, re-read after
//                     the commit: the element exists, its bounds clear the
//                     crossing by clearance_mm, the run no longer meets the host
//                     SOLID (solid re-detection, whenever the host was cut) and
//                     no new clash appeared around the crossing; otherwise the
//                     whole group rolls back. The ledger finding gets an
//                     "opening_requested" history entry and STAYS OPEN - this
//                     operation never marks resolved_by_model; only a detection
//                     run that measures the model does.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ResolveClashCommand
    {
        private static readonly string[] RunCategories =
        {
            "OST_PipeCurves", "OST_DuctCurves", "OST_Conduit", "OST_CableTray", "OST_FlexPipeCurves", "OST_FlexDuctCurves"
        };
        private static bool IsRunCategory(string bic) => bic != null && Array.IndexOf(RunCategories, bic) >= 0;

        private sealed class OpeningPlan
        {
            public string FindingId; public Element Mep; public Element Host; public string HostKind; public string Route;
            public double[] CrossingMm, EntryMm, ExitMm, RunDirection;
            public double RunWidthMm, RunHeightMm;
            public double OpeningWidthMm, OpeningHeightMm; public string Shape;
            // Filled while applying.
            public long CreatedId = -1; public string Kind; public bool HostCut; public string Placement;
        }

        // ---- propose_opening -------------------------------------------------------

        private CommandResult ProposeOpening(UIApplication app, JObject request, double clearance)
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            CommandResult readRefusal = DocumentGate.ReadGuard(doc, request, Name);
            if (readRefusal != null) return readRefusal;
            List<string> ids = (request["finding_ids"] as JArray ?? new JArray()).Select(t => (string)t).ToList();
            if (ids.Count == 0 || ids.Count > 50) return CommandResult.Fail("finding_ids must list 1..50 findings (horizun_coordination list).");
            string ledgerPath = CoordinationLedger.PathFor(doc.Title, UndoCapture.SafePath(doc));
            Dictionary<string, CoordinationFinding> ledger = CoordinationLedger.Load(ledgerPath, out string _);

            var rows = new JArray();
            var actions = new JArray();
            foreach (string id in ids)
            {
                var row = new JObject { ["finding_id"] = id, ["status"] = "report_only" };
                rows.Add(row);
                if (!ledger.TryGetValue(id, out CoordinationFinding f))
                { row["status"] = "unknown_finding"; row["reason"] = "not in this document's ledger"; continue; }
                if (f.Status == CoordinationRules.StatusResolvedByModel || f.Status == CoordinationRules.StatusClosedByDecision)
                { row["status"] = "not_open"; row["reason"] = "finding is " + f.Status; continue; }
                JObject action = PlanOpening(doc, f, clearance, row);
                if (action == null) continue;
                row["status"] = "proposed";
                actions.Add(action);
            }
            var result = new JObject
            {
                ["read_only"] = true, ["proposals"] = rows, ["proposed"] = actions.Count, ["clearance_mm"] = clearance,
                ["note"] = "Candidates only: nothing was written. Wall: rectangular cut; floor/roof/ceiling: boundary cut (round for a round run); " +
                           "framing/columns: the cut is refused by name (host_cannot_be_cut_by_api) and apply_opening needs sleeve_type_id."
            };
            if (actions.Count > 0)
                result["next_arguments"] = new JObject
                {
                    ["operation"] = "apply_opening", ["target_document"] = doc.Title, ["clearance_mm"] = clearance,
                    ["proposals"] = actions, ["dry_run"] = true
                };
            return CommandResult.Ok(result);
        }

        /// <summary>One finding's opening/sleeve proposal, filled onto `row`; null when report-only.</summary>
        private static JObject PlanOpening(Document doc, CoordinationFinding f, double clearance, JObject row)
        {
            if (!ClashResolveRules.ParseSide(f.SideA, out bool hostA, out string uidA) ||
                !ClashResolveRules.ParseSide(f.SideB, out bool hostB, out string uidB))
            { row["code"] = ClashResolveRules.CodeNoGeometry; row["reason"] = "the finding's sides cannot be parsed"; return null; }
            if (!hostA || !hostB)
            { row["code"] = ClashResolveRules.CodeLinked; row["reason"] = "both sides must be host elements to plan an opening - a linked side is resolved in its own model"; return null; }
            Element a = doc.GetElement(uidA), b = doc.GetElement(uidB);
            if (a == null || b == null)
            { row["code"] = ClashResolveRules.CodeNoGeometry; row["reason"] = "an element of the pair no longer exists in the host"; return null; }
            string error = BuildPlan(doc, f.Id, a, b, clearance, out OpeningPlan p, out string code);
            if (p != null) { row["host_kind"] = p.HostKind; row["route"] = p.Route; }
            if (error != null) { row["code"] = code; row["reason"] = error; return null; }
            row["mep_element_id"] = Rid.Value(p.Mep.Id);
            row["host_element_id"] = Rid.Value(p.Host.Id);
            row["crossing_point_mm"] = new JArray(p.CrossingMm);
            row["run_direction"] = new JArray(p.RunDirection);
            row["opening_width_mm"] = p.OpeningWidthMm; row["opening_height_mm"] = p.OpeningHeightMm; row["shape"] = p.Shape;
            if (p.Route == SleeveRules.RouteSleeveOnly)
            {
                row["cut_refused"] = new JObject
                {
                    ["code"] = SleeveRules.CodeCutRefused,
                    ["reason"] = "a " + p.HostKind + " cannot be cut through the Revit API except by a void-cutting family instance; only a sleeve is proposed"
                };
                row["prediction"] = "apply_opening needs sleeve_type_id to place that family at the crossing; the member is never modified except by the family's own void.";
            }
            else
            {
                string footprint = "";
                if (p.Route == SleeveRules.RouteFloorOpening)
                {
                    SleeveRules.FloorFootprint(p.OpeningWidthMm, p.OpeningHeightMm, p.Shape, out double fx, out double fy);
                    row["footprint_mm"] = new JArray(fx, fy);
                    footprint = p.Shape == SleeveRules.ShapeRound ? " (circular boundary)" : " (squared to the larger side: the section's plan rotation is not read)";
                }
                else if (p.Shape == SleeveRules.ShapeRound) footprint = " (a wall opening is always rectangular in the API)";
                row["prediction"] = SleeveRules.Describe(p.HostKind, p.OpeningWidthMm, p.OpeningHeightMm, p.Shape) + footprint +
                                    "; apply re-detects the run against the cut host on solids and checks the opening clears the run by " + (clearance / 2) + " mm per side.";
            }
            return new JObject
            {
                ["finding_id"] = f.Id, ["mep_element_id"] = Rid.Value(p.Mep.Id), ["host_element_id"] = Rid.Value(p.Host.Id),
                ["host_kind"] = p.HostKind, ["route"] = p.Route
            };
        }

        /// <summary>Shared by propose and apply: always derived fresh from the live model.</summary>
        private static string BuildPlan(Document doc, string findingId, Element a, Element b, double clearance, out OpeningPlan plan, out string code)
        {
            plan = null; code = ClashResolveRules.CodeNoGeometry;
            string bicA = CategoryBic(a), bicB = CategoryBic(b);
            bool runA = IsRunCategory(bicA), runB = IsRunCategory(bicB);
            if (runA == runB)
            {
                code = runA ? ClashResolveRules.CodeBothMovable : ClashResolveRules.CodeNotMovable;
                return runA ? "both sides are MEP runs; an opening is planned for a run-against-host pair, not run-against-run"
                            : "neither side is an MEP run (pipe/duct/conduit/cable tray)";
            }
            Element mep = runA ? a : b, host = runA ? b : a;
            string hostBic = runA ? bicB : bicA;
            string hostKind = SleeveRules.HostKindOf(hostBic);
            string route = SleeveRules.RouteFor(hostKind);
            plan = new OpeningPlan { FindingId = findingId, Mep = mep, Host = host, HostKind = hostKind, Route = route };
            if (route == SleeveRules.RouteRefused)
            { code = SleeveRules.CodeHostUnsupported; return "the host category " + (hostBic ?? "unknown") + " has no documented opening or sleeve route"; }
            if (!MepFacts.TryProfile(mep, out string shape, out double wFt, out double hFt))
            { code = SleeveRules.CodeNoProfile; return "the run has no readable profile"; }
            ResolveRun run = Run(mep);
            if (run == null) return "the run has no straight centreline";
            ResolveBox hostBox = Box(host.get_BoundingBox(null));
            if (hostBox == null) { code = SleeveRules.CodeNoHostBox; return "the host has no bounding box in this document"; }
            if (!SleeveRules.LineBoxIntersect(run.Start, run.End, hostBox, out double[] entry, out double[] exit, out string crossCode))
            { code = crossCode; return "the run's centreline does not cross the host's bounding box"; }
            SleeveRules.OpeningSize(wFt * MmPerFoot, hFt * MmPerFoot, clearance, shape, out double openW, out double openH, out string openShape);
            plan.CrossingMm = SleeveRules.Midpoint(entry, exit);
            plan.EntryMm = entry; plan.ExitMm = exit;
            plan.RunDirection = new[] { run.End[0] - run.Start[0], run.End[1] - run.Start[1], run.End[2] - run.Start[2] };
            plan.RunWidthMm = wFt * MmPerFoot; plan.RunHeightMm = (shape == "round" ? wFt : hFt) * MmPerFoot;
            plan.OpeningWidthMm = openW; plan.OpeningHeightMm = openH; plan.Shape = openShape;
            return null;
        }

        // ---- apply_opening ----------------------------------------------------------

        private CommandResult ApplyOpening(UIApplication app, JObject request, double clearance)
        {
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            JArray input = request["proposals"] as JArray;
            if (input == null || input.Count == 0 || input.Count > 50) return CommandResult.Fail("proposals must list 1..50 entries from operation=propose_opening.");
            long? sleeveTypeId = request.Value<long?>("sleeve_type_id");
            string approvalParam = request.Value<string>("approval_parameter");
            string approvalValue = request.Value<string>("approval_value") ?? "";
            string ledgerPath = CoordinationLedger.PathFor(doc.Title, UndoCapture.SafePath(doc));
            Dictionary<string, CoordinationFinding> ledger = CoordinationLedger.Load(ledgerPath, out string ledgerTitle);

            var plans = new List<OpeningPlan>(); var errors = new JArray(); var claimedMep = new HashSet<long>(); var claimedHost = new HashSet<string>();
            for (int i = 0; i < input.Count; i++)
            {
                string error = ParseOpening(doc, input[i] as JObject, ledger, clearance, claimedMep, claimedHost, out OpeningPlan p, out string code);
                if (error != null) errors.Add(new JObject { ["index"] = i, ["code"] = code, ["error"] = error }); else plans.Add(p);
            }
            FamilySymbol sleeveSymbol = null;
            if (sleeveTypeId != null)
            {
                sleeveSymbol = Rid.CanRepresent(sleeveTypeId.Value) ? doc.GetElement(Rid.Make(sleeveTypeId.Value)) as FamilySymbol : null;
                if (sleeveSymbol == null) errors.Add(new JObject { ["code"] = "sleeve_type_not_found", ["error"] = "sleeve_type_id does not resolve to a family type (symbol) in this document." });
            }
            foreach (OpeningPlan p in plans)
                if (p.Route == SleeveRules.RouteSleeveOnly && sleeveSymbol == null)
                    errors.Add(new JObject
                    {
                        ["finding_id"] = p.FindingId, ["code"] = SleeveRules.CodeCutRefused,
                        ["error"] = "a " + p.HostKind + " cannot be cut through the Revit API; pass sleeve_type_id (a sleeve family) instead"
                    });

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "proposals", "clearance_mm", "sleeve_type_id", "approval_parameter", "approval_value");
            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "not_started", ["valid"] = plans.Count, ["invalid"] = errors.Count,
                    ["errors"] = errors,
                    ["plan"] = new JArray(plans.Select(p => (JToken)new JObject
                    {
                        ["finding_id"] = p.FindingId, ["mep_element_id"] = Rid.Value(p.Mep.Id), ["host_element_id"] = Rid.Value(p.Host.Id),
                        ["host_kind"] = p.HostKind, ["route"] = p.Route, ["crossing_point_mm"] = new JArray(p.CrossingMm),
                        ["opening_width_mm"] = p.OpeningWidthMm, ["opening_height_mm"] = p.OpeningHeightMm, ["shape"] = p.Shape,
                        ["placement"] = sleeveSymbol != null ? "sleeve_family" : (p.Route == SleeveRules.RouteSleeveOnly ? "refused_no_cut_route" : "native_opening")
                    })),
                    ["note"] = "Nothing was created. Apply keeps the result only after a solid re-detection around the crossing; the ledger gets an " +
                               "opening_requested entry and the finding stays open - this operation never marks it resolved_by_model."
                };
                ApplicationOutcome.StampRehearsal(result, input.Count, errors.Count, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0 ? "the token binds the proposals, clearance and sleeve/approval arguments" : "no token while any proposal is invalid");
                return CommandResult.Ok(result);
            }
            if (errors.Count > 0) return CommandResult.Fail("Invalid proposals; nothing ran: " + errors.ToString(Formatting.None));
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash);
            if (refusal != null) return refusal;

            // The neighbourhood of every crossing, measured before AND after over the same set:
            // the entry-exit segment grown by the opening's larger side plus clearance.
            var region = new Dictionary<long, Element>();
            foreach (OpeningPlan p in plans)
            {
                double grow = Math.Max(p.OpeningWidthMm, p.OpeningHeightMm) + clearance;
                var seg = new ResolveBox(Math.Min(p.EntryMm[0], p.ExitMm[0]), Math.Min(p.EntryMm[1], p.ExitMm[1]), Math.Min(p.EntryMm[2], p.ExitMm[2]),
                                         Math.Max(p.EntryMm[0], p.ExitMm[0]), Math.Max(p.EntryMm[1], p.ExitMm[1]), Math.Max(p.EntryMm[2], p.ExitMm[2]));
                foreach (Element e in Neighbours(doc, seg, grow)) region[Rid.Value(e.Id)] = e;
                region[Rid.Value(p.Host.Id)] = p.Host; region[Rid.Value(p.Mep.Id)] = p.Mep;
            }
            var runIds = new HashSet<long>(plans.Select(p => Rid.Value(p.Mep.Id)));
            List<string> before = Detect(doc, runIds, region.Keys, out bool completeBefore);

            string txName = "Horizun: resolve clash (opening/sleeve)";
            PostconditionCheck postconditions = null; string verdict = null; var fresh = new List<string>();
            var linksSkipped = new List<string>();
            using (var group = new TransactionGroup(doc, txName))
            {
                RevitErrorRecorder said = null;
                try
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("transaction group did not start");
                    using (var tx = new Transaction(doc, txName))
                    {
                        said = RevitErrorRecorder.On(tx);
                        tx.Start();
                        foreach (OpeningPlan p in plans)
                        {
                            if (!CreateOpeningOrSleeve(doc, p, sleeveSymbol, out string failReason))
                            {
                                Guard.RollBack(tx);
                                Guard.RollBack(group);
                                return CommandResult.Fail("Nothing created for finding " + p.FindingId + ": " + failReason + " " +
                                    PlanFailure.SingleTransactionOutcome(true, ApplicationOutcome.RolledBackStatus, "nothing was created"));
                            }
                            if (!string.IsNullOrEmpty(approvalParam))
                            {
                                Parameter par = doc.GetElement(Rid.Make(p.CreatedId))?.LookupParameter(approvalParam);
                                string why = par == null ? "the created " + p.Kind + " has no parameter named '" + approvalParam + "'"
                                           : par.IsReadOnly ? "'" + approvalParam + "' is read-only on the created " + p.Kind
                                           : par.StorageType != StorageType.String ? "'" + approvalParam + "' is not a text parameter"
                                           : (par.Set(approvalValue) ? null : "Revit refused to set '" + approvalParam + "'");
                                if (why != null)
                                {
                                    Guard.RollBack(tx);
                                    Guard.RollBack(group);
                                    return CommandResult.Fail("Rolled back, nothing kept: the approval mark could not be written - " + why + ".");
                                }
                            }
                        }
                        Guard.Commit(tx, txName);
                    }

                    // Re-read INSIDE the group: every check below can still roll everything back.
                    var keys = new List<string>();
                    foreach (OpeningPlan p in plans)
                    {
                        keys.Add("created:" + p.FindingId); keys.Add("contains_crossing:" + p.FindingId);
                        if (!string.IsNullOrEmpty(approvalParam)) keys.Add("approval:" + p.FindingId);
                        if (p.HostCut) keys.Add("host_cleared:" + p.FindingId);
                    }
                    keys.Add("no_new_clash");
                    postconditions = new PostconditionCheck(keys.ToArray());
                    bool ok = true;
                    // A void-only sleeve has no solid to clash with; asking Detect about it would
                    // only mark the measurement incomplete, so only solid-bearing sleeves are movers.
                    var solidOptions = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
                    var sleeveIds = new HashSet<long>(plans.Where(p => p.Kind == "sleeve" &&
                        (Solids(doc, p.CreatedId, solidOptions, new Dictionary<long, List<Solid>>())?.Count ?? 0) > 0).Select(p => p.CreatedId));
                    var movers = new HashSet<long>(runIds.Concat(sleeveIds));
                    var regionAfter = region.Keys.Concat(sleeveIds).Distinct().ToList();
                    List<string> after = Detect(doc, movers, regionAfter, out bool completeAfter);
                    if (sleeveIds.Count > 0)
                    {
                        after = after.Concat(DetectLinks(doc, sleeveIds, out bool completeLinks, out linksSkipped)).ToList();
                        completeAfter &= completeLinks;
                    }
                    foreach (OpeningPlan p in plans)
                    {
                        Element el = doc.GetElement(Rid.Make(p.CreatedId));
                        bool exists = el != null;
                        postconditions.Record("created:" + p.FindingId, p.Kind, exists ? p.Kind : "missing", exists);
                        ok &= exists;
                        ResolveBox bounds = exists ? CreatedBounds(el) : null;
                        bool contains = SleeveRules.ContainsCrossingWithClearance(bounds, p.CrossingMm, p.RunDirection,
                            p.RunWidthMm / 2, p.RunHeightMm / 2, clearance / 2);
                        postconditions.Record("contains_crossing:" + p.FindingId, "clears the run by >= " + (clearance / 2) + " mm",
                            bounds == null ? "no readable bounds" : "bounds " + BoundsText(bounds), contains);
                        ok &= contains;
                        if (!string.IsNullOrEmpty(approvalParam))
                        {
                            string found = el?.LookupParameter(approvalParam)?.AsString();
                            bool marked = found == approvalValue;
                            postconditions.Record("approval:" + p.FindingId, approvalValue, found, marked);
                            ok &= marked;
                        }
                        if (p.HostCut)
                        {
                            string key = ClashResolveRules.PairKey(Rid.Value(p.Mep.Id), Rid.Value(p.Host.Id));
                            bool gone = !after.Contains(key);
                            if (completeAfter) postconditions.Record("host_cleared:" + p.FindingId, "absent", gone ? "absent" : "present", gone);
                            else postconditions.Unreadable("host_cleared:" + p.FindingId, "absent", "solid re-detection incomplete");
                            ok &= completeAfter && gone;
                        }
                    }
                    fresh = ClashResolveRules.NewPairs(before, after);
                    foreach (OpeningPlan p in plans.Where(x => x.Kind == "sleeve"))
                        fresh = SleeveRules.UnintendedNewPairs(new string[0], fresh, p.CreatedId, Rid.Value(p.Host.Id));
                    if (completeAfter && completeBefore) postconditions.Record("no_new_clash", new JArray(), new JArray(fresh), fresh.Count == 0);
                    else postconditions.Unreadable("no_new_clash", new JArray(), "solid detection before or after was incomplete");
                    ok &= completeAfter && completeBefore && fresh.Count == 0;
                    if (!ok)
                    {
                        Guard.RollbackResult rb = Guard.RollBack(group);
                        return CommandResult.FailWithDetail("Rolled back, nothing kept: a postcondition failed after the opening/sleeve was built.", new JObject
                        {
                            ["state"] = rb.Confirmed ? "rolled_back" : "uncertain", ["rollback_status"] = rb.StatusName,
                            ["new_clashes"] = new JArray(fresh), ["postconditions"] = postconditions.ToJson(),
                            ["links_skipped"] = new JArray(linksSkipped)
                        });
                    }
                    verdict = "every opening/sleeve exists, clears the crossing by the declared clearance, " +
                              (plans.Any(p => p.HostCut) ? "the cut host no longer meets the run on solids, " : "") + "and no new clash appeared";
                    Guard.Assimilate(group, txName);
                }
                catch (Exception ex)
                {
                    string rb = PlanFailure.NotAttempted; bool attempted = false;
                    if (group.GetStatus() == TransactionStatus.Started) { attempted = true; rb = Guard.RollBack(group).StatusName; }
                    return CommandResult.Fail("Opening/sleeve creation failed: " + ex.Message + (said == null ? "" : said.Said()) + " " +
                        PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing was created"));
                }
            }

            foreach (OpeningPlan p in plans)
                if (doc.GetElement(Rid.Make(p.CreatedId)) == null)
                    return CommandResult.FailWithDetail("The group was kept but element " + p.CreatedId + " does not re-read; inspect the model.",
                        new JObject { ["state"] = "uncertain" });

            JObject undo = UndoCapture.Record(doc, Name, new List<UndoEntry>
            {
                UndoCapture.Entry(doc, "created", plans.Select(p => p.CreatedId), new JObject(), new JObject())
            });

            // The ledger learns that an opening was REQUESTED - never that the clash is resolved.
            string ledgerNote = null; var requested = new JArray();
            try
            {
                string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                foreach (OpeningPlan p in plans)
                    if (ledger.TryGetValue(p.FindingId, out CoordinationFinding f))
                    {
                        CoordinationRules.AppendEvent(f, "opening_requested",
                            "horizun_resolve_clash apply_opening created " + p.Kind + " " + p.CreatedId + " (" + p.Placement + ") at the crossing with host " +
                            Rid.Value(p.Host.Id) + (p.HostCut ? "; the host is cut" : "; the host is NOT cut") +
                            "; the finding stays open until a detection run measures it", now);
                        requested.Add(p.FindingId);
                    }
                CoordinationLedger.Save(ledgerPath, ledgerTitle ?? doc.Title, ledger);
            }
            catch (Exception ex) { ledgerNote = "the model change is kept and verified, but the ledger could not be updated: " + ex.Message; }

            var applied = new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = ApplicationOutcome.Committed, ["transaction_name"] = txName,
                ["verdict"] = verdict, ["postconditions"] = postconditions.ToJson(),
                ["created"] = new JArray(plans.Select(p => (JToken)new JObject
                {
                    ["finding_id"] = p.FindingId, ["created_element_id"] = p.CreatedId, ["kind"] = p.Kind, ["placement"] = p.Placement,
                    ["host_kind"] = p.HostKind, ["host_cut"] = p.HostCut
                })),
                ["findings_opening_requested"] = requested, ["findings_resolved_by_model"] = new JArray(),
                ["ledger_note"] = ledgerNote, ["undo"] = undo, ["neighbourhood_elements"] = region.Count,
                ["links_skipped"] = new JArray(linksSkipped),
                ["next_step"] = "run horizun_clash with record_findings=true: only that measurement can resolve the finding"
            };
            ApplicationOutcome.StampApplied(applied, ApplicationOutcome.Committed, plans.Count, plans.Count, plans.Count, 0, 0, 0);
            return CommandResult.Ok(applied);
        }

        /// <summary>Re-derives everything fresh from the live model - never trusted from a propose call that may be minutes old.</summary>
        private static string ParseOpening(Document doc, JObject o, Dictionary<string, CoordinationFinding> ledger, double clearance,
                                           HashSet<long> claimedMep, HashSet<string> claimedHost, out OpeningPlan plan, out string code)
        {
            plan = null; code = "invalid_proposal";
            if (o == null) return "entry is not an object";
            string fid = o.Value<string>("finding_id");
            if (string.IsNullOrEmpty(fid) || !ledger.TryGetValue(fid, out CoordinationFinding f)) return "finding_id is not in this document's ledger";
            if (f.Status == CoordinationRules.StatusResolvedByModel || f.Status == CoordinationRules.StatusClosedByDecision) return "finding " + fid + " is " + f.Status;
            long mepRaw = o.Value<long?>("mep_element_id") ?? -1;
            long hostRaw = o.Value<long?>("host_element_id") ?? -1;
            if (!Rid.CanRepresent(mepRaw) || !Rid.CanRepresent(hostRaw)) return "mep_element_id/host_element_id are invalid";
            Element mep = doc.GetElement(Rid.Make(mepRaw)); Element host = doc.GetElement(Rid.Make(hostRaw));
            if (mep == null || host == null) return "the run or the host no longer exists in this document";
            // One opening per run-host crossing: the same pair twice would cut twice.
            if (!claimedHost.Add(mepRaw.ToString(CultureInfo.InvariantCulture) + "|" + hostRaw.ToString(CultureInfo.InvariantCulture)))
                return "the pair " + mepRaw + "/" + hostRaw + " appears twice; one opening per crossing";
            claimedMep.Add(mepRaw);
            ClashResolveRules.ParseSide(f.SideA, out bool hostA, out string uidA);
            ClashResolveRules.ParseSide(f.SideB, out bool hostB, out string uidB);
            bool matches = (hostA && uidA == mep.UniqueId && hostB && uidB == host.UniqueId) ||
                           (hostB && uidB == mep.UniqueId && hostA && uidA == host.UniqueId);
            if (!matches) return "the pair does not match finding " + fid;
            string error = BuildPlan(doc, fid, mep, host, clearance, out OpeningPlan p, out code);
            if (error != null) return error;
            if (Rid.Value(p.Mep.Id) != mepRaw) return "mep_element_id names the host side of finding " + fid;
            plan = p;
            return null;
        }

        /// <summary>Either the caller-supplied sleeve family (any host), or the native cut (wall / floor-roof-ceiling only).</summary>
        private static bool CreateOpeningOrSleeve(Document doc, OpeningPlan p, FamilySymbol sleeveSymbol, out string failReason)
        {
            failReason = null;
            var crossingFt = new XYZ(p.CrossingMm[0] / MmPerFoot, p.CrossingMm[1] / MmPerFoot, p.CrossingMm[2] / MmPerFoot);
            if (sleeveSymbol != null)
            {
                FamilyInstance sleeve = PlaceSleeve(doc, p, sleeveSymbol, crossingFt, out failReason);
                if (sleeve == null) return false;
                p.CreatedId = Rid.Value(sleeve.Id); p.Kind = "sleeve";
                // A sleeve family that carries a void can cut its host (the only API route that
                // cuts a beam/column). Not every family has one, and not every host accepts it:
                // HostCut records what actually happened so the solid check is only asked of a cut.
                try
                {
                    if (InstanceVoidCutUtils.CanBeCutWithVoid(p.Host) && InstanceVoidCutUtils.IsVoidInstanceCuttingElement(sleeve))
                    {
                        InstanceVoidCutUtils.AddInstanceVoidCut(doc, p.Host, sleeve);
                        p.HostCut = InstanceVoidCutUtils.InstanceVoidCutExists(p.Host, sleeve);
                    }
                }
                catch { p.HostCut = false; }
                return true;
            }
            if (p.Route == SleeveRules.RouteWallOpening && p.Host is Wall wall)
            {
                XYZ dir = WallDirection(wall);
                double halfW = (p.OpeningWidthMm / 2) / MmPerFoot, halfH = (p.OpeningHeightMm / 2) / MmPerFoot;
                XYZ pt1 = crossingFt - dir * halfW - XYZ.BasisZ * halfH;
                XYZ pt2 = crossingFt + dir * halfW + XYZ.BasisZ * halfH;
                try
                {
                    Opening opening = doc.Create.NewOpening(wall, pt1, pt2);
                    if (opening == null) { failReason = "NewOpening returned null"; return false; }
                    p.CreatedId = Rid.Value(opening.Id); p.Kind = "opening"; p.HostCut = true; p.Placement = "wall_rectangular_opening";
                    return true;
                }
                catch (Exception ex) { failReason = "wall opening failed: " + ex.Message; return false; }
            }
            if (p.Route == SleeveRules.RouteFloorOpening && p.Host is HostObject hostObj)
            {
                SleeveRules.FloorFootprint(p.OpeningWidthMm, p.OpeningHeightMm, p.Shape, out double fx, out double fy);
                double hx = (fx / 2) / MmPerFoot, hy = (fy / 2) / MmPerFoot, z = crossingFt.Z;
                var loop = new CurveArray();
                if (p.Shape == SleeveRules.ShapeRound)
                {
                    var c = new XYZ(crossingFt.X, crossingFt.Y, z);
                    loop.Append(Arc.Create(c, hx, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                    loop.Append(Arc.Create(c, hx, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                }
                else
                {
                    var c0 = new XYZ(crossingFt.X - hx, crossingFt.Y - hy, z); var c1 = new XYZ(crossingFt.X + hx, crossingFt.Y - hy, z);
                    var c2 = new XYZ(crossingFt.X + hx, crossingFt.Y + hy, z); var c3 = new XYZ(crossingFt.X - hx, crossingFt.Y + hy, z);
                    loop.Append(Line.CreateBound(c0, c1)); loop.Append(Line.CreateBound(c1, c2));
                    loop.Append(Line.CreateBound(c2, c3)); loop.Append(Line.CreateBound(c3, c0));
                }
                try
                {
                    Opening opening = doc.Create.NewOpening(hostObj, loop, true);
                    if (opening == null) { failReason = "NewOpening returned null"; return false; }
                    p.CreatedId = Rid.Value(opening.Id); p.Kind = "opening"; p.HostCut = true;
                    p.Placement = p.Shape == SleeveRules.ShapeRound ? "boundary_opening_circular" : "boundary_opening_rectangular";
                    return true;
                }
                catch (Exception ex) { failReason = "floor/roof/ceiling opening failed: " + ex.Message; return false; }
            }
            failReason = SleeveRules.CodeCutRefused + ": host route " + p.Route + " has no cut without sleeve_type_id";
            return false;
        }

        private static XYZ WallDirection(Wall wall)
        {
            if ((wall.Location as LocationCurve)?.Curve is Line line)
            {
                XYZ d = line.GetEndPoint(1) - line.GetEndPoint(0);
                if (d.GetLength() > 1e-9) return d.Normalize();
            }
            return XYZ.BasisX;
        }

        /// <summary>
        /// Places the caller's sleeve family by its own placement type: face-based on the host
        /// face the run enters, line-based along the entry-exit segment, anything else at the
        /// crossing point (rotated about Z to the run for a horizontal run). A family whose
        /// placement cannot be satisfied fails with the reason - never a guessed placement.
        /// MEASURE LIVE: the orientation of a point-placed family depends on how its author
        /// modelled the axis; the containment postcondition is what catches a wrong one.
        /// </summary>
        private static FamilyInstance PlaceSleeve(Document doc, OpeningPlan p, FamilySymbol symbol, XYZ crossingFt, out string failReason)
        {
            failReason = null;
            try { if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); } } catch { /* activation failure surfaces in the placement below */ }
            var entryFt = new XYZ(p.EntryMm[0] / MmPerFoot, p.EntryMm[1] / MmPerFoot, p.EntryMm[2] / MmPerFoot);
            var exitFt = new XYZ(p.ExitMm[0] / MmPerFoot, p.ExitMm[1] / MmPerFoot, p.ExitMm[2] / MmPerFoot);
            XYZ runDir = (exitFt - entryFt).GetLength() > 1e-9 ? (exitFt - entryFt).Normalize() : XYZ.BasisX;
            FamilyPlacementType placement = FamilyPlacementType.Invalid;
            try { placement = symbol.Family.FamilyPlacementType; } catch { }
            FamilyInstance instance = null;
            try
            {
                if (placement == FamilyPlacementType.OneLevelBasedHosted)
                {
                    instance = doc.Create.NewFamilyInstance(crossingFt, symbol, p.Host, StructuralType.NonStructural);
                    p.Placement = "hosted_on_host_at_crossing";
                }
                else if (placement == FamilyPlacementType.WorkPlaneBased)
                {
                    Face face = EntryFace(p.Host, entryFt, out XYZ normal);
                    if (face == null) { failReason = "no planar face of the host contains the run's entry point, so a face-based sleeve has nowhere to sit"; return null; }
                    XYZ refDir = Math.Abs(normal.Z) > 0.9 ? XYZ.BasisX : XYZ.BasisZ.CrossProduct(normal).Normalize();
                    instance = doc.Create.NewFamilyInstance(face, entryFt, refDir, symbol);
                    p.Placement = "face_based_on_entry_face";
                }
                else if (placement == FamilyPlacementType.CurveBased)
                {
                    Level level = doc.GetElement(p.Mep.LevelId) as Level ?? doc.GetElement(p.Host.LevelId) as Level;
                    if (level == null) { failReason = "a line-based sleeve needs a level and neither the run nor the host has one"; return null; }
                    instance = doc.Create.NewFamilyInstance(Line.CreateBound(entryFt, exitFt), symbol, level, StructuralType.NonStructural);
                    p.Placement = "line_based_entry_to_exit";
                }
                else
                {
                    instance = doc.Create.NewFamilyInstance(crossingFt, symbol, StructuralType.NonStructural);
                    p.Placement = "point_at_crossing";
                    if (instance != null && Math.Abs(runDir.Z) < 0.5)
                    {
                        double angle = Math.Atan2(runDir.Y, runDir.X);
                        if (Math.Abs(angle) > 1e-6)
                            ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(crossingFt, crossingFt + XYZ.BasisZ), angle);
                        p.Placement = "point_at_crossing_rotated_to_run";
                    }
                }
            }
            catch (Exception ex)
            {
                failReason = "the sleeve family (placement " + placement + ") could not be placed at the crossing: " + ex.Message;
                return null;
            }
            if (instance == null) failReason = "NewFamilyInstance returned null";
            return instance;
        }

        /// <summary>The host's planar face that contains `pointFt` (within 1 mm), with its outward normal.</summary>
        private static Face EntryFace(Element host, XYZ pointFt, out XYZ normal)
        {
            normal = XYZ.BasisZ;
            var options = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
            var solids = new List<Solid>();
            try { GeometryElement g = host.get_Geometry(options); if (g != null) Harvest(g, solids); } catch { return null; }
            foreach (Solid s in solids)
                foreach (Face f in s.Faces)
                {
                    if (!(f is PlanarFace pf) || pf.Reference == null) continue;
                    IntersectionResult hit = pf.Project(pointFt);
                    if (hit != null && hit.Distance < 1.0 / MmPerFoot) { normal = pf.FaceNormal; return pf; }
                }
            return null;
        }

        /// <summary>
        /// The created element's measurable extent: an Opening's own boundary (rectangle corners
        /// or boundary curves - exact), otherwise the element's bounding box.
        /// </summary>
        private static ResolveBox CreatedBounds(Element el)
        {
            var pts = new List<XYZ>();
            if (el is Opening op)
            {
                try
                {
                    if (op.IsRectBoundary) { IList<XYZ> r = op.BoundaryRect; if (r != null) pts.AddRange(r); }
                    else if (op.BoundaryCurves != null) foreach (Curve c in op.BoundaryCurves) pts.AddRange(c.Tessellate());
                }
                catch { pts.Clear(); }
            }
            if (pts.Count >= 2)
                return new ResolveBox(pts.Min(q => q.X) * MmPerFoot, pts.Min(q => q.Y) * MmPerFoot, pts.Min(q => q.Z) * MmPerFoot,
                                      pts.Max(q => q.X) * MmPerFoot, pts.Max(q => q.Y) * MmPerFoot, pts.Max(q => q.Z) * MmPerFoot);
            return Box(el.get_BoundingBox(null));
        }

        private static string BoundsText(ResolveBox b) => string.Format(CultureInfo.InvariantCulture,
            "[{0:0},{1:0},{2:0}]..[{3:0},{4:0},{5:0}] mm", b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ);
    }
}
