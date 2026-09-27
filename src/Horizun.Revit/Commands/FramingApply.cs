// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_framing: the verified write (wall, remove).
// Original Horizun code.
//
// THE SEQUENCE is ModelEditRunner's (gate, resolve without a transaction, rehearse
// by default, spend a single-use confirmation, write inside a TransactionGroup,
// re-read through a PostconditionCheck and ROLL BACK when it disagrees, re-read once
// more after the group assimilated). It is repeated here rather than reused because
// the confirmation must bind the RESOLVED PLAN - every member's role, type and both
// endpoints (FramingPlanSignature) - and not only the request's arguments: a wall
// that moved, gained a door or changed type between the rehearsal and the apply
// yields another signature, so the token no longer matches and nothing is written.
//
// IDEMPOTENCE. A source whose marked members carry the same spec hash AND plan
// signature is 'already_applied': nothing is created, and its existing members are
// re-read against the plan exactly as new ones would be. A source that carries
// framing from ANOTHER spec or plan is refused by name - remove it first - because
// silently adding a second layout on top of the first is the one outcome nobody
// asked for.
//
// VERIFICATION reads the model, never the calls that did not throw: members found
// by marker, their type, both endpoints within 1 mm of the plan, |y| inside the
// chosen layer, no vertical member entering an opening, counts per role equal to
// the plan, each hosted insert (door, window, opening) with the same type and
// location as before the write, and no member geometry-joined with (cutting) its
// source wall - a join Revit made on its own is undone inside the write.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    /// <summary>One source element resolved into the members it will carry.</summary>
    internal sealed class FramingSourcePlan
    {
        public Element Source;
        public string Operation;
        public FramedWall Wall;
        public FramedCeiling Ceiling;
        public List<FramingMember> Members = new List<FramingMember>();
        public readonly List<string> Warnings = new List<string>();
        public string Signature;
        public string SpecHash;
        public double StudWidthMm;
        public bool AlreadyApplied;
        /// <summary>Plan index -> the element that carries it (filled by the apply, or by an earlier apply).</summary>
        public readonly Dictionary<int, long> MemberIds = new Dictionary<int, long>();
        public readonly List<long> WorkPlaneIds = new List<long>();
        /// <summary>Insert id -> "type|x,y,z" before the write.</summary>
        public readonly Dictionary<long, string> InsertsBefore = new Dictionary<long, string>();
        /// <summary>Model axis of plan member i.</summary>
        public Func<FramingMember, Line> Axis;
        public XYZ PlaneSpan;
        /// <summary>Per-member work-plane span for line-based members (ceilings); PlaneSpan when null.</summary>
        public Func<FramingMember, XYZ> Span;
        /// <summary>Type key -> symbol, and role|type key -> how it places (shared across the call's sources).</summary>
        public Dictionary<string, FamilySymbol> Symbols;
        public Dictionary<string, FramingPlacementKind> Kinds;
        /// <summary>The curtain method's plan (FramingCurtain.cs); null for the member method.</summary>
        public CurtainSourceState Curtain;
        /// <summary>spec.ceiling.method = 'curtain' (FramingCurtainCeiling.cs); null otherwise.</summary>
        public CurtainCeilingState CurtainCeiling;
    }

    public sealed partial class FramingCommand
    {
        private const int MaxMembersPerSource = 5000, MaxMembersTotal = 20000, SummaryMemberCap = 300;
        private const double EndpointToleranceMm = 1.0;

        private static readonly string[] HashScope = { "operation", "element_ids", "view_id", "spec", "target_document" };

        /// <summary>operation wall | ceiling | remove (the ceiling's reading, rays and checks are in FramingCeiling.cs).</summary>
        private CommandResult ApplyFraming(UIApplication app, JObject request, string op)
        {
            WallFramingSpec wallSpec = null;
            string specHash = "";
            if (op == "wall")
            {
                wallSpec = FramingSpecRules.ParseWall(request["spec"], out List<FramingSpecError> errors);
                if (wallSpec == null || errors.Count > 0)
                    return CommandResult.FailWithDetail("spec.wall is invalid: " + string.Join("; ", errors.Select(e => e.ToString())) + ". Nothing was read or written.",
                        new JObject { ["code"] = "invalid_spec", ["write_started"] = false, ["errors"] = new JArray(errors.Select(e => new JObject { ["path"] = e.Path, ["code"] = e.Code, ["detail"] = e.Detail })) });
                specHash = FramingSpecRules.Hash(request["spec"]);
            }
            CeilingFramingSpec ceilingSpec = null;
            if (op == "ceiling")
            {
                ceilingSpec = FramingSpecRules.ParseCeiling(request["spec"], out List<FramingSpecError> errors);
                if (ceilingSpec == null || errors.Count > 0)
                    return CommandResult.FailWithDetail("spec.ceiling is invalid: " + string.Join("; ", errors.Select(e => e.ToString())) + ". Nothing was read or written.",
                        new JObject { ["code"] = "invalid_spec", ["write_started"] = false, ["errors"] = new JArray(errors.Select(e => new JObject { ["path"] = e.Path, ["code"] = e.Code, ["detail"] = e.Detail })) });
                specHash = FramingSpecRules.Hash(request["spec"]);
            }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            List<FramingSourcePlan> plans;
            List<KeyValuePair<Element, FramingMark>> toRemove = null, foreignCopies = null;
            List<long> cascade = null;
            List<CurtainRestore> restores = null;
            var skipped = new List<string>();
            string signature;
            try
            {
                if (op == "remove")
                {
                    HashSet<long> ids = SourceIds(request);
                    if (ids == null || ids.Count == 0) throw new ArgumentException("remove needs element_ids: the walls or ceilings whose framing goes.");
                    toRemove = FramingMarker.Find(doc, ids);
                    foreignCopies = FramingMarker.FindForeign(doc, ids);
                    // The curtain method's carriers come back (FramingCurtainRemove.cs); read before the cascade's rolled-back delete.
                    restores = PlanCurtainRestores(doc, toRemove);
                    cascade = MeasureRemoveCascade(doc, toRemove);
                    plans = new List<FramingSourcePlan>();
                    // The token binds the named members AND the cascade Revit measured for them: a
                    // token that binds only the named ids would still authorise an unbounded dependent
                    // cascade (the rule DeleteCommand keeps for horizun_delete_verified).
                    signature = string.Join(",", toRemove.Select(p => Rid.Value(p.Key.Id).ToString(CultureInfo.InvariantCulture)))
                                + "|cascade:" + string.Join(",", cascade.Select(id => id.ToString(CultureInfo.InvariantCulture))) + CurtainRestoreKey(restores);
                }
                else
                {
                    plans = op == "ceiling" ? (ceilingSpec.Curtain != null ? PlanCurtainCeilings(doc, request, ceilingSpec.Curtain, specHash, skipped) : PlanCeilings(doc, request, ceilingSpec, specHash, skipped))
                          : wallSpec.Curtain != null ? PlanCurtainWalls(doc, request, wallSpec.Curtain, specHash, skipped)
                          : PlanWalls(doc, request, wallSpec, specHash, skipped);
                    signature = string.Join(",", plans.Select(p => Rid.Value(p.Source.Id).ToString(CultureInfo.InvariantCulture) + ":" + p.Signature)) + CurtainCascadeKey(plans);
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FailWithDetail(ex.Message + " Nothing was written.", new JObject { ["code"] = "framing_refused", ["write_started"] = false });
            }

            var resolved = new ResolvedPlan
            {
                Command = Name, DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (FramingSourcePlan p in plans)
            {
                PlannedElement pe = ModelEditRunner.Planned(p.Source, CurtainSourceAction(p), request);
                pe.ProposedValues["plan_signature"] = p.Signature;
                resolved.Elements.Add(pe);
                foreach (PlannedElement dependent in CurtainCascadeRows(doc, p, request)) resolved.Elements.Add(dependent);
            }
            if (toRemove != null)
                foreach (KeyValuePair<Element, FramingMark> p in toRemove)
                    resolved.Elements.Add(ModelEditRunner.Planned(p.Key, PlannedAction.Delete, request));
            if (cascade != null)
                foreach (long id in cascade)
                {
                    Element dependent = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    if (dependent != null) resolved.Elements.Add(ModelEditRunner.Planned(dependent, PlannedAction.Delete, request));
                }
            if (restores != null)
                foreach (PlannedElement row in CurtainRestoreRows(doc, restores, request)) resolved.Elements.Add(row);
            string hash = DocumentGate.PlanHash(request, HashScope) + "|" + FramingPlanSignature.Of(new[] { new FramingMember { Role = op, TypeKey = signature } });

            JObject summary = op == "remove" ? RemoveSummary(doc, toRemove, cascade, foreignCopies) : op == "ceiling" ? (ceilingSpec.Curtain != null ? CurtainCeilingSummary(plans) : CeilingSummary(plans)) : wallSpec.Curtain != null ? CurtainWallSummary(doc, plans) : WallSummary(plans);
            if (restores != null && restores.Count > 0) summary["carrier_restores"] = CurtainRestoreSummary(restores);
            if (skipped.Count > 0) summary["skipped"] = new JArray(skipped.ToArray());
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["operation"] = op, ["transaction_status"] = "not_started", ["plan"] = summary,
                    ["note"] = "Nothing was written. The token binds every member's role, type and endpoints; the apply re-reads each one and rolls back on any disagreement."
                };
                DocumentGate.RecordResolvedPlan(resolved);
                int requested = op == "remove" ? toRemove.Count : plans.Sum(p => p.Members.Count);
                ApplicationOutcome.StampRehearsal(result, requested, 0, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, hash, true,
                    "the token binds the operation, the sources, the spec and the resolved plan of every member");
                return CommandResult.Ok(result);
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash, resolved, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            string txName = op == "remove" ? "Horizun: remove framing" : "Horizun: framing";
            var evidence = new JObject();
            List<long> removedIds = toRemove?.Select(p => Rid.Value(p.Key.Id)).ToList();
            var cascadedNow = new List<long>();
            Func<Document, PostconditionCheck> verify = op == "remove"
                ? (Func<Document, PostconditionCheck>)(d => VerifyRemoved(d, removedIds, SourceIds(request), cascade, cascadedNow, foreignCopies.Count, evidence, restores))
                : op == "ceiling" ? (Func<Document, PostconditionCheck>)(d => ceilingSpec.Curtain != null ? VerifyCurtainCeilings(d, plans, evidence) : VerifyCeilings(d, plans, evidence))
                : wallSpec.Curtain != null ? (Func<Document, PostconditionCheck>)(d => VerifyCurtainWalls(d, plans, evidence))
                : d => VerifyWalls(d, plans, evidence);
            PostconditionCheck check;
            using (var group = new TransactionGroup(doc, txName))
            {
                bool started = false;
                string said = "";
                try
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("the transaction group did not start");
                    using (var tx = new Transaction(doc, txName))
                    {
                        RevitErrorRecorder recorder = RevitErrorRecorder.On(tx);
                        if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("the transaction did not start");
                        started = true;
                        try
                        {
                            if (op == "remove" && toRemove.Count > 0)
                            {
                                var named = new HashSet<long>(removedIds);
                                cascadedNow.Clear();
                                cascadedNow.AddRange(doc.Delete(toRemove.Select(p => p.Key.Id).ToList()).Select(Rid.Value).Where(id => !named.Contains(id)).OrderBy(id => id));
                                RestoreCarriers(doc, restores);
                            }
                            else if (op != "remove") foreach (FramingSourcePlan p in plans.Where(x => !x.AlreadyApplied)) PlaceSource(doc, p);
                            doc.Regenerate();
                            if (op == "wall" && wallSpec.Curtain == null && UnjoinFromSources(doc, plans, evidence) > 0) doc.Regenerate();
                            Guard.Commit(tx, txName);
                        }
                        catch { said = recorder.Said(); throw; }
                    }
                    check = verify(doc);
                    if (!check.AllVerified)
                    {
                        var rolled = Guard.RollBack(group);
                        return CommandResult.FailWithDetail(
                            "The committed model disagreed with the plan, so the whole edit was rolled back. " + ModelEditRunner.FailedText(check, evidence),
                            new JObject
                            {
                                ["code"] = "postcondition_failed", ["write_started"] = true,
                                ["changes_applied"] = rolled.Confirmed ? (JToken)false : JValue.CreateNull(),
                                ["rollback_status"] = rolled.StatusName, ["postconditions"] = check.ToJson(), ["evidence"] = evidence
                            });
                    }
                    Guard.Assimilate(group, txName);
                }
                catch (Exception ex)
                {
                    string rb = "not_attempted";
                    try { if (group.GetStatus() == TransactionStatus.Started) rb = Guard.RollBack(group).StatusName; }
                    catch (Exception e2) { rb = "failed: " + e2.Message; }
                    return CommandResult.FailWithDetail(Name + " failed: " + ex.Message + said, new JObject
                    {
                        ["code"] = "revit_edit_failed", ["write_started"] = started,
                        ["changes_applied"] = !started ? (JToken)false : (rb == "RolledBack" ? (JToken)false : JValue.CreateNull()),
                        ["rollback_status"] = rb
                    });
                }
            }
            check = verify(doc);
            if (!check.AllVerified)
                return CommandResult.FailWithDetail("Committed, but the re-read after the group assimilated disagrees; inspect the model.",
                    new JObject { ["code"] = "postcommit_verification_failed", ["write_started"] = true, ["changes_applied"] = true,
                                  ["postconditions"] = check.ToJson(), ["evidence"] = evidence });
            int total = op == "remove" ? toRemove.Count : plans.Sum(p => p.Members.Count);
            int created = op == "remove" ? toRemove.Count : plans.Where(p => !p.AlreadyApplied).Sum(p => p.Members.Count);
            var done = new JObject
            {
                ["dry_run"] = false, ["operation"] = op, ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["already_applied"] = op != "remove" && plans.Count > 0 && plans.All(p => p.AlreadyApplied),
                ["postconditions"] = check.ToJson(), ["evidence"] = evidence
            };
            // What THIS call wrote: members of sources already framed by the same plan were
            // re-read, not written, so they are reported apart and never counted as applied
            // (verified > applied would declare an honest idempotent apply 'uncertain').
            if (op != "remove") { done["members_created"] = created; done["members_reverified_existing"] = total - created; }
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, created, created, created, 0, 0, 0);
            return CommandResult.Ok(done);
        }

        // ---- resolving ------------------------------------------------------------------

        private static HashSet<long> SourceIds(JObject request)
        {
            if (!(request["element_ids"] is JArray a) || a.Count == 0) return null;
            var ids = new HashSet<long>();
            foreach (JToken t in a)
            {
                if (t.Type != JTokenType.Integer) throw new ArgumentException("element_ids must be integers.");
                ids.Add((long)t);
            }
            return ids;
        }

        /// <summary>
        /// The sources named by element_ids (each must qualify, or the call refuses) or visible in
        /// view_id. A view scope shows whatever the model has, so there an element viewFilter
        /// rejects (a curtain or curved wall) is listed in 'skipped' with its reason, not refused.
        /// </summary>
        private static List<T> Sources<T>(Document doc, JObject request, string what, Func<T, string> viewFilter = null, List<string> skipped = null) where T : Element
        {
            HashSet<long> ids = SourceIds(request);
            long? viewId = request.Value<long?>("view_id");
            if ((ids == null) == (viewId == null)) throw new ArgumentException("Name the " + what + "s with element_ids OR a view_id scope (exactly one).");
            var found = new List<T>();
            if (ids != null)
            {
                foreach (long id in ids.OrderBy(i => i))
                {
                    Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    if (!(e is T t)) throw new ArgumentException("element " + id + " is " + (e == null ? "not an element of this document" : "a " + (e.Category?.Name ?? e.GetType().Name) + ", not a " + what) + ".");
                    found.Add(t);
                }
                return found;
            }
            if (!Rid.CanRepresent(viewId.Value) || !(doc.GetElement(Rid.Make(viewId.Value)) is View view) || view.IsTemplate)
                throw new ArgumentException("view_id " + viewId + " is not a view of this document.");
            foreach (T e in new FilteredElementCollector(doc, view.Id).OfClass(typeof(T)).Cast<T>().OrderBy(e => Rid.Value(e.Id)))
            {
                string why = viewFilter?.Invoke(e);
                if (why == null) found.Add(e);
                else skipped?.Add(what + " " + Rid.Value(e.Id) + ": " + why);
            }
            if (found.Count == 0) throw new ArgumentException("view " + viewId + " shows no " + what + " this operation can frame" + (skipped?.Count > 0 ? " (" + skipped.Count + " skipped: " + string.Join("; ", skipped.Take(5)) + ")" : "") + ".");
            return found;
        }

        private static List<FramingSourcePlan> PlanWalls(Document doc, JObject request, WallFramingSpec spec, string specHash, List<string> skipped)
        {
            var symbols = new Dictionary<string, FamilySymbol>(StringComparer.Ordinal);
            foreach (long id in spec.TypeIds())
            {
                FamilySymbol s = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as FamilySymbol : null;
                if (s == null) throw new ArgumentException("type id " + id + " in spec.wall is not a family type of this document.");
                symbols[id.ToString(CultureInfo.InvariantCulture)] = s;
            }
            FamilySymbol stud = symbols[spec.StudTypeId.ToString(CultureInfo.InvariantCulture)];
            double? studWidth = spec.StudWidthMm ?? TypeWidthMm(stud);
            if (studWidth == null || !(studWidth > 0))
                throw new ArgumentException("the stud type publishes no section width; give spec.wall.stud.width_mm.");

            var kinds = new Dictionary<string, FramingPlacementKind>(StringComparer.Ordinal);
            var plans = new List<FramingSourcePlan>();
            int total = 0;
            foreach (Wall wall in Sources<Wall>(doc, request, "wall", WallOutOfScope, skipped))
            {
                FramedWall fw = ReadWall(doc, wall, spec, out string refusal);
                if (fw == null) throw new ArgumentException(refusal);
                var p = new FramingSourcePlan { Source = wall, Operation = "wall", Wall = fw, SpecHash = specHash, StudWidthMm = studWidth.Value, PlaneSpan = fw.Normal, Symbols = symbols, Kinds = kinds };
                p.Warnings.AddRange(fw.Warnings);
                // No section parameter says which of a track type's sizes is the thickness under a
                // stud, so the caller states it; a guess would put half of each track outside the wall.
                if (spec.TrackThicknessMm == null) throw new ArgumentException("give spec.wall.track.thickness_mm (the track's thickness under the studs, e.g. 0.9); it is not guessed from the type.");
                double track = spec.TrackThicknessMm.Value;
                WallFramingPlan plan = WallFramingRules.Plan(spec.ToInput(fw.LengthMm, fw.HeightMm, studWidth.Value, track, fw.OpeningsMm), MaxMembersPerSource);
                if (!string.IsNullOrEmpty(plan.Refusal)) throw new ArgumentException("wall " + Rid.Value(wall.Id) + ": " + plan.Refusal);
                p.Members = plan.Members;
                p.Warnings.AddRange(plan.Warnings);
                total += p.Members.Count;
                if (total > MaxMembersTotal) throw new ArgumentException("the plan exceeds " + MaxMembersTotal + " members across its walls; frame fewer walls per call.");
                foreach (FramingMember m in p.Members)
                {
                    string key = m.Role + "|" + m.TypeKey;
                    if (kinds.ContainsKey(key)) continue;
                    if (m.TypeKey == null || !symbols.TryGetValue(m.TypeKey, out FamilySymbol sym)) throw new ArgumentException(m.Role + " has no type in the spec.");
                    string why = ClassifyType(sym, FramingRoles.IsVertical(m.Role), m.Role, out FramingPlacementKind kind);
                    if (why != null) throw new ArgumentException(why);
                    kinds[key] = kind;
                }
                FramedWall frame = fw;
                p.Axis = m => Line.CreateBound(frame.ToModel(m.X0, m.Y0, m.Z0), frame.ToModel(m.X1, m.Y1, m.Z1));
                p.Signature = FramingPlanSignature.Of(p.Members);
                foreach (long insert in fw.InsertIds)
                    p.InsertsBefore[insert] = InsertState(doc, insert);
                ClaimExisting(doc, p);
                plans.Add(p);
            }
            return plans;
        }

        /// <summary>Why a wall a view shows is not one this operation frames (curtain, stacked, curved), or null.</summary>
        private static string WallOutOfScope(Wall w)
        {
            if (w.WallType == null || w.WallType.Kind != WallKind.Basic) return "not a Basic wall";
            return w.Location is LocationCurve lc && lc.Curve is Line ? null : "not straight";
        }

        /// <summary>Earlier framing on this source: the same spec and plan is already applied; anything else refuses.</summary>
        private static void ClaimExisting(Document doc, FramingSourcePlan p)
        {
            long sid = Rid.Value(p.Source.Id);
            List<KeyValuePair<Element, FramingMark>> existing = FramingMarker.Find(doc, new HashSet<long> { sid });
            if (existing.Count == 0) return;
            var members = existing.Where(x => x.Value.Role != FramingMarker.WorkPlaneRole).ToList();
            bool same = members.All(x => x.Value.SpecHash == p.SpecHash && x.Value.PlanSignature == p.Signature)
                        && members.Select(x => x.Value.Index).Distinct().Count() == p.Members.Count && members.Count == p.Members.Count;
            if (!same)
            {
                // Same spec and plan but not the same members: some were deleted (or copied) by hand.
                bool samePlan = members.Count > 0 && members.All(x => x.Value.SpecHash == p.SpecHash && x.Value.PlanSignature == p.Signature);
                throw new ArgumentException(p.Operation + " " + sid + " already carries " + members.Count + " horizun_framing member(s) " +
                                            (samePlan ? "of this same plan, which places " + p.Members.Count + " (members were deleted or copied since)"
                                                      : "from another spec or plan (spec " + string.Join(",", members.Select(x => x.Value.SpecHash).Distinct()) + ")") +
                                            "; run operation=remove for it first.");
            }
            p.AlreadyApplied = true;
            foreach (KeyValuePair<Element, FramingMark> x in members) p.MemberIds[x.Value.Index] = Rid.Value(x.Key.Id);
            p.WorkPlaneIds.AddRange(existing.Where(x => x.Value.Role == FramingMarker.WorkPlaneRole).Select(x => Rid.Value(x.Key.Id)));
        }

        private static string InsertState(Document doc, long id)
        {
            Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
            if (e == null) return "missing";
            string where = "";
            if (e.Location is LocationPoint lp) where = Fmt(lp.Point);
            else if (e is Opening o && o.IsRectBoundary && o.BoundaryRect != null && o.BoundaryRect.Count > 1) where = Fmt(o.BoundaryRect[0]) + ";" + Fmt(o.BoundaryRect[1]);
            return Rid.Value(e.GetTypeId()).ToString(CultureInfo.InvariantCulture) + "|" + where;
        }

        private static string Fmt(XYZ p) => string.Join(",", new[] { p.X, p.Y, p.Z }.Select(v => Math.Round(v * 304.8, 2).ToString("0.00", CultureInfo.InvariantCulture)));

        // ---- writing --------------------------------------------------------------------

        private static void PlaceSource(Document doc, FramingSourcePlan p)
        {
            if (p.Curtain != null) { PlaceCurtainSource(doc, p); return; }
            if (p.CurtainCeiling != null) { PlaceCurtainCeilingSource(doc, p); return; }
            string sourceUid = p.Source.UniqueId;
            long sid = Rid.Value(p.Source.Id);
            Level level = p.Wall?.Level ?? p.Ceiling?.Level;
            for (int i = 0; i < p.Members.Count; i++)
            {
                FramingMember m = p.Members[i];
                FamilySymbol sym = p.Symbols[m.TypeKey];
                FramingPlacementKind kind = p.Kinds[m.Role + "|" + m.TypeKey];
                FamilyInstance fi;
                Element plane;
                try { fi = PlaceMember(doc, sym, kind, p.Axis(m), level, p.Span?.Invoke(m) ?? p.PlaneSpan, null, out plane); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
                {
                    // Name the member Revit refused: role, index, type, placement and both ends.
                    Line ax = p.Axis(m);
                    throw new InvalidOperationException(m.Role + " " + i + " (" + kind + ", type " + Rid.Value(sym.Id) + ", " + Fmt(ax.GetEndPoint(0)) + " -> " + Fmt(ax.GetEndPoint(1)) + " mm): " + ex.Message, ex);
                }
                if (fi == null) throw new InvalidOperationException(m.Role + " " + i + ": Revit returned no instance.");
                var mark = new FramingMark { SourceId = sid, SourceUniqueId = sourceUid, Role = m.Role, Index = i, SpecHash = p.SpecHash, PlanSignature = p.Signature, Operation = p.Operation };
                FramingMarker.Write(fi, mark);
                p.MemberIds[i] = Rid.Value(fi.Id);
                if (plane != null)
                {
                    FramingMarker.Write(plane, new FramingMark { SourceId = sid, SourceUniqueId = sourceUid, Role = FramingMarker.WorkPlaneRole, Index = i, SpecHash = p.SpecHash, PlanSignature = p.Signature, Operation = p.Operation });
                    p.WorkPlaneIds.Add(Rid.Value(plane.Id));
                }
            }
        }

        /// <summary>
        /// Revit may join a column placed inside a wall with that wall and cut the wall by it
        /// (it does so for some column materials; which ones is measured live). A stud never
        /// cuts the partition it frames: that would change the wall's own volume and area, so
        /// every such join is undone here and the count is reported (evidence.source_joins_undone).
        /// </summary>
        private static int UnjoinFromSources(Document doc, List<FramingSourcePlan> plans, JObject evidence)
        {
            int undone = 0;
            var refused = new JArray();
            foreach (FramingSourcePlan p in plans.Where(x => !x.AlreadyApplied))
                foreach (long id in p.MemberIds.Values)
                {
                    Element m = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    try
                    {
                        if (m == null || !JoinGeometryUtils.AreElementsJoined(doc, p.Source, m)) continue;
                        JoinGeometryUtils.UnjoinGeometry(doc, p.Source, m);
                        undone++;
                    }
                    // Named, not thrown: the source_unjoined postcondition re-reads the join and
                    // rolls the whole edit back when one survived.
                    catch (Exception ex) { refused.Add(id + ": " + ex.Message); }
                }
            evidence["source_joins_undone"] = undone;
            if (refused.Count > 0) evidence["source_unjoin_refused"] = refused;
            return undone;
        }

        /// <summary>Members of this plan still geometry-joined with their source wall (re-read; must be 0).</summary>
        private static int JoinedToSource(Document doc, FramingSourcePlan p)
        {
            int n = 0;
            foreach (long id in p.MemberIds.Values)
            {
                Element m = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                try { if (m != null && JoinGeometryUtils.AreElementsJoined(doc, p.Source, m)) n++; } catch { }
            }
            return n;
        }

        // ---- verifying ------------------------------------------------------------------

        /// <summary>A member's axis as the committed model reports it, and how it was read.</summary>
        internal static XYZ[] MemberEnds(Document doc, Element e, out string method)
        {
            method = null;
            if (e?.Location is LocationCurve lc && lc.Curve != null)
            {
                XYZ c0 = lc.Curve.GetEndPoint(0), c1 = lc.Curve.GetEndPoint(1);
                // A line-based family on a level keeps its location curve ON the level plane and
                // carries its height as an offset (MEASURED 2026-09-26, Revit 2026: curve Z = level
                // elevation, offset 2498.2 mm, bounding box centred at the planned height) - the
                // same rule as a wall's location line. Its real axis is the curve plus that offset.
                if (e is FamilyInstance lb && lb.Symbol?.Family?.FamilyPlacementType == FamilyPlacementType.CurveBased)
                {
                    Parameter off = lb.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM) ?? lb.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                    if (off != null && off.StorageType == StorageType.Double && Math.Abs(off.AsDouble()) > 1e-9)
                    {
                        // The curve lies on its level (the level comes as Host, and LevelId may be
                        // invalid), or, when no level resolves, the solid says the offset is real:
                        // the bounding box centre is nearer curve + offset than the curve itself.
                        Level onLevel = lb.Host as Level ?? doc.GetElement(lb.LevelId) as Level;
                        bool onPlane = onLevel != null && Math.Abs(c0.Z - onLevel.ProjectElevation) < 1e-6 && Math.Abs(c1.Z - onLevel.ProjectElevation) < 1e-6;
                        bool solidSays = false;
                        if (!onPlane)
                        {
                            BoundingBoxXYZ bb = lb.get_BoundingBox(null);
                            if (bb != null)
                            {
                                double mid = (bb.Min.Z + bb.Max.Z) / 2;
                                solidSays = Math.Abs(c0.Z + off.AsDouble() - mid) + 1e-6 < Math.Abs(c0.Z - mid);
                            }
                        }
                        if (onPlane || solidSays)
                        {
                            XYZ up = new XYZ(0, 0, off.AsDouble());
                            method = "location_curve_plus_level_offset";
                            return new[] { c0 + up, c1 + up };
                        }
                    }
                }
                method = "location_curve";
                return new[] { c0, c1 };
            }
            // A vertical column reports a point; its ends are its base and top constraints.
            if (e is FamilyInstance fi && fi.Location is LocationPoint lp)
            {
                Level b = doc.GetElement(fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
                Level t = doc.GetElement(fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
                if (b == null || t == null) return null;
                double z0 = b.ProjectElevation + (fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0);
                double z1 = t.ProjectElevation + (fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0);
                method = "column_constraints";
                return new[] { new XYZ(lp.Point.X, lp.Point.Y, z0), new XYZ(lp.Point.X, lp.Point.Y, z1) };
            }
            return null;
        }

        /// <summary>
        /// How a line-based member's height is carried, read back: its location curve's Z, the
        /// offset parameters it exposes and its bounding box. Evidence for a height that did not
        /// match the plan, never a verdict of its own.
        /// </summary>
        internal static JObject HeightRead(Element e, double plannedZmm)
        {
            var o = new JObject { ["element_id"] = Rid.Value(e.Id), ["planned_z_mm"] = Math.Round(plannedZmm, 2) };
            if (e.Location is LocationCurve lc && lc.Curve != null) o["curve_z_mm"] = Math.Round(lc.Curve.GetEndPoint(0).Z * 304.8, 2);
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_OFFSET_POS_PARAM })
            {
                Parameter p = e.get_Parameter(bip);
                if (p != null && p.StorageType == StorageType.Double) o[bip.ToString()] = Math.Round(p.AsDouble() * 304.8, 2) + (p.IsReadOnly ? " (read-only)" : "");
            }
            BoundingBoxXYZ bb = e.get_BoundingBox(null);
            if (bb != null) o["bbox_z_mm"] = new JArray(Math.Round(bb.Min.Z * 304.8, 2), Math.Round(bb.Max.Z * 304.8, 2));
            return o;
        }

        /// <summary>A member's solid in the wall frame, mm {xmin, xmax, ymin, ymax}, from its edges; null when it has none.</summary>
        internal static double[] SolidExtentInFrame(Element e, FramedWall fw)
        {
            GeometryElement ge;
            try { ge = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
            if (ge == null) return null;
            double[] r = { double.MaxValue, double.MinValue, double.MaxValue, double.MinValue };
            bool any = false;
            foreach (Solid s in SolidsOf(ge))
                foreach (Edge ed in s.Edges)
                    foreach (XYZ q in ed.Tessellate())
                    {
                        double[] f = fw.ToFrame(q);
                        r[0] = Math.Min(r[0], f[0]); r[1] = Math.Max(r[1], f[0]); r[2] = Math.Min(r[2], f[1]); r[3] = Math.Max(r[3], f[1]);
                        any = true;
                    }
            return any ? r : null;
        }

        private static IEnumerable<Solid> SolidsOf(GeometryElement ge)
        {
            foreach (GeometryObject g in ge)
            {
                if (g is Solid s && s.Volume > 1e-9) yield return s;
                else if (g is GeometryInstance gi)
                    foreach (Solid t in SolidsOf(gi.GetInstanceGeometry())) yield return t;
            }
        }

        /// <summary>
        /// A beam member's settings re-read: 0 when it is centred on its axis (Z_JUSTIFICATION) and
        /// joins are off at both ends; else how many disagree. Placement sets both in swallowed
        /// try/catch blocks, and neither moves the LocationCurve the endpoint check reads.
        /// </summary>
        internal static int BeamSettingsOff(Element e)
        {
            if (!(e is FamilyInstance fi)) return 1;
            int off = 0;
            Parameter z = fi.get_Parameter(BuiltInParameter.Z_JUSTIFICATION);
            if (z == null || !z.HasValue || z.AsInteger() != (int)ZJustification.Center) off++;
            for (int end = 0; end < 2; end++)
                try { if (StructuralFramingUtils.IsJoinAllowedAtEnd(fi, end)) off++; } catch (Autodesk.Revit.Exceptions.ApplicationException) { off++; }
            return off;
        }

        private static PostconditionCheck VerifyWalls(Document doc, List<FramingSourcePlan> plans, JObject evidence)
        {
            var check = new PostconditionCheck("member_count", "member_types", "member_endpoints", "counts_by_role", "inside_layer", "no_stud_through_opening",
                                               "inside_wall_length", "section_along_wall", "beam_settings", "inserts_untouched", "source_unjoined");
            int planned = 0, found = 0, wrongType = 0, unreadable = 0, crossings = 0, insertsChanged = 0, joined = 0, solidRead = 0, declaredRead = 0, misoriented = 0, beamOff = 0;
            double maxDev = 0, maxExcess = 0, maxBeyondLength = 0;
            JObject worstLayer = null;
            var heightReads = new JArray();
            var plannedRoles = new JObject();
            var foundRoles = new JObject();
            var perSource = new JArray();
            var methods = new HashSet<string>();
            foreach (FramingSourcePlan p in plans)
            {
                FramedWall fw = p.Wall;
                long sid = Rid.Value(p.Source.Id);
                var byIndex = FramingMarker.Find(doc, new HashSet<long> { sid })
                    .Where(x => x.Value.Role != FramingMarker.WorkPlaneRole && x.Value.SpecHash == p.SpecHash && x.Value.PlanSignature == p.Signature)
                    .GroupBy(x => x.Value.Index).ToDictionary(g => g.Key, g => g.ToList());
                int srcFound = 0, srcCross = 0;
                double srcDev = 0;
                for (int i = 0; i < p.Members.Count; i++)
                {
                    FramingMember m = p.Members[i];
                    planned++;
                    plannedRoles[m.Role] = (plannedRoles.Value<int?>(m.Role) ?? 0) + 1;
                    if (!byIndex.TryGetValue(i, out var list) || list.Count != 1) continue;
                    Element e = list[0].Key;
                    if (list[0].Value.Role != m.Role) continue;
                    found++; srcFound++;
                    foundRoles[m.Role] = (foundRoles.Value<int?>(m.Role) ?? 0) + 1;
                    if (Rid.Value(e.GetTypeId()).ToString(CultureInfo.InvariantCulture) != m.TypeKey) wrongType++;
                    XYZ[] ends = MemberEnds(doc, e, out string method);
                    if (ends == null) { unreadable++; continue; }
                    methods.Add(method);
                    Line axis = p.Axis(m);
                    XYZ a = axis.GetEndPoint(0), b = axis.GetEndPoint(1);
                    double dev = Math.Min(Math.Max(ends[0].DistanceTo(a), ends[1].DistanceTo(b)), Math.Max(ends[0].DistanceTo(b), ends[1].DistanceTo(a))) * 304.8;
                    srcDev = Math.Max(srcDev, dev);
                    if (dev > EndpointToleranceMm && p.Kinds[m.Role + "|" + m.TypeKey] == FramingPlacementKind.LineBased && heightReads.Count < 3)
                        heightReads.Add(HeightRead(e, a.Z * 304.8));
                    double[] f0 = fw.ToFrame(ends[0]), f1 = fw.ToFrame(ends[1]);
                    // The section as Revit BUILT it: the member's solid in the wall frame. Only a
                    // member with no solid falls back to its axis and the declared width (counted).
                    // Along its own axis the length is member_endpoints' job (the location line).
                    double[] sx = SolidExtentInFrame(e, fw);
                    double xLo, xHi;
                    if (sx != null)
                    {
                        solidRead++; xLo = sx[0]; xHi = sx[1];
                        double excess = Math.Max(0, Math.Max(Math.Abs(sx[2]), Math.Abs(sx[3])) - fw.LayerWidthMm / 2);
                        if (excess > maxExcess + 1e-9 && excess > EndpointToleranceMm)
                            worstLayer = new JObject { ["member_id"] = Rid.Value(e.Id), ["role"] = m.Role, ["index"] = i, ["excess_mm"] = Math.Round(excess, 2),
                                                       ["solid_y_mm"] = new JArray(Math.Round(sx[2], 2), Math.Round(sx[3], 2)), ["layer_mm"] = Math.Round(fw.LayerWidthMm, 2) };
                        maxExcess = Math.Max(maxExcess, excess);
                    }
                    else
                    {
                        declaredRead++;
                        double half = FramingRoles.IsVertical(m.Role) ? p.StudWidthMm / 2 : 0;
                        xLo = Math.Min(f0[0], f1[0]) - half; xHi = Math.Max(f0[0], f1[0]) + half;
                        maxExcess = Math.Max(maxExcess, Math.Max(0, Math.Max(Math.Abs(f0[1]), Math.Abs(f1[1])) - fw.LayerWidthMm / 2));
                    }
                    maxBeyondLength = Math.Max(maxBeyondLength, Math.Max(0, Math.Max(-xLo, xHi - fw.LengthMm)));
                    FramingPlacementKind kind = p.Kinds[m.Role + "|" + m.TypeKey];
                    if (kind == FramingPlacementKind.Column && e is FamilyInstance col)
                    {
                        XYZ hand = col.HandOrientation;
                        if (hand == null || Math.Abs(hand.X * fw.Dir.Y - hand.Y * fw.Dir.X) > Math.Sin(Math.PI / 180) * hand.GetLength()) misoriented++;
                    }
                    if (kind == FramingPlacementKind.Beam) beamOff += BeamSettingsOff(e);
                    // A cripple sits inside the opening's width but above its head or below its sill,
                    // so the same test (the void's x AND z ranges) holds for every vertical role. The
                    // slack is the endpoint tolerance: a jack flush with the jamb, read back a hair
                    // inside it, is round-off; one that really entered the void fails member_endpoints too.
                    if (FramingRoles.IsVertical(m.Role) &&
                        WallFramingRules.CrossesOpening((xLo + xHi) / 2, Math.Min(f0[2], f1[2]), Math.Max(f0[2], f1[2]), xHi - xLo, fw.OpeningsMm, EndpointToleranceMm))
                    { crossings++; srcCross++; }
                }
                maxDev = Math.Max(maxDev, srcDev);
                int changed = p.InsertsBefore.Count(kv => InsertState(doc, kv.Key) != kv.Value);
                insertsChanged += changed;
                int srcJoined = JoinedToSource(doc, p);
                joined += srcJoined;
                perSource.Add(new JObject
                {
                    ["source_id"] = sid, ["already_applied"] = p.AlreadyApplied, ["planned"] = p.Members.Count, ["found"] = srcFound,
                    ["max_endpoint_deviation_mm"] = Math.Round(srcDev, 3), ["stud_crossings"] = srcCross,
                    ["inserts_checked"] = p.InsertsBefore.Count, ["inserts_changed"] = changed, ["joined_to_source"] = srcJoined,
                    ["member_ids"] = new JArray(p.MemberIds.OrderBy(kv => kv.Key).Select(kv => kv.Value)),
                    ["work_plane_ids"] = new JArray(p.WorkPlaneIds)
                });
            }
            check.Compare("member_count", planned, found);
            check.Compare("member_types", 0, wrongType);
            if (unreadable > 0) check.Unreadable("member_endpoints", 0, unreadable + " member(s) report neither a location curve nor column constraints");
            else check.Measure("member_endpoints", 0, maxDev, EndpointToleranceMm, "mm", "max over members of the farther end's distance to the planned axis end");
            check.Record("counts_by_role", plannedRoles, foundRoles, JToken.DeepEquals(plannedRoles, foundRoles));
            check.Measure("inside_layer", 0, maxExcess, EndpointToleranceMm, "mm", "max |y| of a member's solid (its axis when it has none) beyond half the carrying layer's thickness");
            check.Compare("no_stud_through_opening", 0, crossings);
            check.Measure("inside_wall_length", 0, maxBeyondLength, EndpointToleranceMm, "mm", "max x of a member's solid beyond the layer's extent between its joins");
            check.Compare("section_along_wall", 0, misoriented);
            check.Compare("beam_settings", 0, beamOff);
            evidence["section_read"] = new JObject { ["solid"] = solidRead, ["axis_and_declared_width"] = declaredRead };
            if (worstLayer != null) evidence["worst_inside_layer"] = worstLayer;
            if (heightReads.Count > 0) evidence["line_based_height"] = heightReads;
            check.Compare("inserts_untouched", 0, insertsChanged);
            check.Compare("source_unjoined", 0, joined);
            evidence["sources"] = perSource;
            evidence["endpoint_read"] = new JArray(methods.OrderBy(s => s, StringComparer.Ordinal).ToArray());
            return check;
        }

        /// <summary>
        /// Re-reads by the ids captured BEFORE the delete: a deleted Element's wrapper is no longer a
        /// valid object, so reading its Id after the commit throws instead of answering "gone".
        /// </summary>
        private static PostconditionCheck VerifyRemoved(Document doc, List<long> removedIds, HashSet<long> sources, List<long> cascadeMeasured,
                                                        List<long> cascadedNow, int foreignKept, JObject evidence, List<CurtainRestore> restores = null)
        {
            // The curtain method's carriers this remove restored are re-read too (FramingCurtainRemove.cs).
            bool restoring = restores != null && restores.Any(r => r.Refusal == null);
            var check = restoring
                ? new PostconditionCheck("members_absent", "markers_absent", "cascade_absent", "cascade_as_measured", "carrier_restored", "carrier_inserts_restored")
                : new PostconditionCheck("members_absent", "markers_absent", "cascade_absent", "cascade_as_measured");
            int still = removedIds.Count(id => doc.GetElement(Rid.Make(id)) != null);
            int marked = FramingMarker.Find(doc, sources).Count;
            check.Compare("members_absent", 0, still);
            check.Compare("markers_absent", 0, marked);
            // The dependents the rehearsal measured must be gone, and Revit must have taken no
            // other: a cascade the token did not bind rolls the whole remove back.
            check.Compare("cascade_absent", 0, cascadeMeasured.Count(id => doc.GetElement(Rid.Make(id)) != null));
            var measured = new HashSet<long>(cascadeMeasured);
            int differs = cascadedNow.Count(id => !measured.Contains(id)) + cascadeMeasured.Count(id => !cascadedNow.Contains(id));
            check.Compare("cascade_as_measured", 0, differs);
            evidence["removed_ids"] = new JArray(removedIds);
            evidence["cascaded_ids"] = new JArray(cascadedNow);
            evidence["cascade_measured_in_rehearsal"] = new JArray(cascadeMeasured);
            evidence["foreign_copies_kept"] = foreignKept;
            evidence["sources"] = new JArray(sources.OrderBy(s => s));
            if (restores != null && restores.Count > 0)
            {
                VerifyCurtainRestores(doc, restores, out int restoreProblems, out int insertProblems, evidence);
                if (restoring) { check.Compare("carrier_restored", 0, restoreProblems); check.Compare("carrier_inserts_restored", 0, insertProblems); }
            }
            return check;
        }

        /// <summary>
        /// What Revit deletes along with the named members (tags, dimensions, anything hosted on
        /// the tool's work planes), measured in a rolled-back transaction: the ids Delete returned
        /// AND that no longer resolve, minus the named ones. Deterministic for one model state, so
        /// the rehearsal and the apply measure the same set and the token can bind it.
        /// </summary>
        private static List<long> MeasureRemoveCascade(Document doc, List<KeyValuePair<Element, FramingMark>> toRemove)
        {
            var cascade = new List<long>();
            if (toRemove == null || toRemove.Count == 0) return cascade;
            var named = new HashSet<long>(toRemove.Select(p => Rid.Value(p.Key.Id)));
            using (var tx = new Transaction(doc, "Horizun: measure framing remove (rolled back)"))
            {
                if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("the remove's cascade could not be measured: no transaction could start.");
                try
                {
                    ICollection<ElementId> gone = doc.Delete(toRemove.Select(p => p.Key.Id).ToList());
                    foreach (ElementId id in gone)
                    {
                        long v = Rid.Value(id);
                        if (!named.Contains(v) && doc.GetElement(id) == null) cascade.Add(v);
                    }
                }
                finally
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                }
            }
            cascade.Sort();
            return cascade;
        }

        // ---- summaries ------------------------------------------------------------------

        private static JObject WallSummary(List<FramingSourcePlan> plans)
        {
            var rows = new JArray();
            int listed = 0;
            foreach (FramingSourcePlan p in plans)
            {
                FramedWall fw = p.Wall;
                var members = new JArray();
                for (int i = 0; i < p.Members.Count && listed < SummaryMemberCap; i++, listed++)
                {
                    FramingMember m = p.Members[i];
                    members.Add(new JObject
                    {
                        ["i"] = i, ["role"] = m.Role, ["type_id"] = long.Parse(m.TypeKey, CultureInfo.InvariantCulture),
                        ["from"] = new JArray(Math.Round(m.X0, 1), Math.Round(m.Z0, 1)), ["to"] = new JArray(Math.Round(m.X1, 1), Math.Round(m.Z1, 1))
                    });
                }
                var counts = new JObject();
                foreach (KeyValuePair<string, int> kv in FramingPlanSignature.CountByRole(p.Members).OrderBy(k => k.Key, StringComparer.Ordinal)) counts[kv.Key] = kv.Value;
                rows.Add(new JObject
                {
                    ["source_id"] = Rid.Value(p.Source.Id), ["status"] = p.AlreadyApplied ? "already_applied" : "planned",
                    ["length_mm"] = Math.Round(fw.LengthMm, 1), ["height_mm"] = Math.Round(fw.HeightMm, 1),
                    ["layer"] = new JObject { ["index"] = fw.LayerIndex, ["choice"] = fw.LayerChoice, ["width_mm"] = Math.Round(fw.LayerWidthMm, 1) },
                    ["openings"] = new JArray(fw.OpeningsMm.Select((o, k) => new JObject
                    {
                        ["id"] = o.Id, ["start"] = Math.Round(o.Start, 1), ["end"] = Math.Round(o.End, 1),
                        ["sill"] = Math.Round(o.Sill, 1), ["head"] = Math.Round(o.Head, 1), ["read_from"] = fw.OpeningSources[k]
                    })),
                    ["count_by_role"] = counts, ["member_count"] = p.Members.Count,
                    ["plan_signature"] = p.Signature, ["spec_hash"] = p.SpecHash,
                    ["warnings"] = new JArray(p.Warnings.ToArray()),
                    ["members_frame"] = "x along the wall from its start, z up from its base, mm",
                    ["members"] = members
                });
            }
            return new JObject
            {
                ["sources"] = rows, ["member_count"] = plans.Sum(p => p.Members.Count),
                ["members_listed"] = listed, ["truncated"] = listed < plans.Sum(p => p.Members.Count)
            };
        }

        private static JObject RemoveSummary(Document doc, List<KeyValuePair<Element, FramingMark>> found, List<long> cascade,
                                             List<KeyValuePair<Element, FramingMark>> foreign)
        {
            var bySource = new JArray();
            foreach (IGrouping<long, KeyValuePair<Element, FramingMark>> g in found.GroupBy(p => p.Value.SourceId))
            {
                var counts = new JObject();
                foreach (IGrouping<string, KeyValuePair<Element, FramingMark>> r in g.GroupBy(p => p.Value.Role).OrderBy(r => r.Key, StringComparer.Ordinal)) counts[r.Key] = r.Count();
                bySource.Add(new JObject { ["source_id"] = g.Key, ["count_by_role"] = counts, ["element_count"] = g.Count() });
            }
            var byCategory = new JObject();
            foreach (IGrouping<string, long> g in cascade.GroupBy(id => CategoryLabel(doc, id)).OrderBy(g => g.Key, StringComparer.Ordinal)) byCategory[g.Key] = g.Count();
            var copies = new JArray();
            foreach (KeyValuePair<Element, FramingMark> c in foreign.Take(SummaryMemberCap))
                copies.Add(new JObject { ["id"] = Rid.Value(c.Key.Id), ["names_source_id"] = c.Value.SourceId, ["role"] = c.Value.Role });
            return new JObject
            {
                ["sources"] = bySource, ["element_count"] = found.Count, ["nothing_to_remove"] = found.Count == 0,
                // Deleted WITH the members by Revit itself; bound by the token, re-read after the commit.
                ["cascade"] = new JObject { ["count"] = cascade.Count, ["by_category"] = byCategory, ["ids"] = new JArray(cascade.Take(SummaryMemberCap)) },
                // Copies of members (copy/paste or array of a framed wall): their marker names a source
                // in the call, but they are not the members it made, so they stay.
                ["foreign_copies_kept"] = copies, ["foreign_copy_count"] = foreign.Count
            };
        }

        private static string CategoryLabel(Document doc, long id)
        {
            Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
            if (e == null) return "(unreadable)";
            try { if (e.Category != null) return e.Category.Name; } catch { }
            return e.GetType().Name;
        }
    }
}
