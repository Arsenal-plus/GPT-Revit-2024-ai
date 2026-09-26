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
// the plan, and each hosted insert (door, window, opening) with the same type and
// location as before the write.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
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
        /// <summary>Type key -> symbol, and role|type key -> how it places (shared across the call's sources).</summary>
        public Dictionary<string, FamilySymbol> Symbols;
        public Dictionary<string, FramingPlacementKind> Kinds;
    }

    public sealed partial class FramingCommand
    {
        private const int MaxMembersPerSource = 5000, MaxMembersTotal = 20000, SummaryMemberCap = 300;
        private const double EndpointToleranceMm = 1.0;

        private static readonly string[] HashScope = { "operation", "element_ids", "view_id", "spec", "target_document" };

        /// <summary>operation wall | remove (ceiling lands in FramingCeiling.cs).</summary>
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

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            List<FramingSourcePlan> plans;
            List<KeyValuePair<Element, FramingMark>> toRemove = null;
            string signature;
            try
            {
                if (op == "remove")
                {
                    HashSet<long> ids = SourceIds(request);
                    if (ids == null || ids.Count == 0) throw new ArgumentException("remove needs element_ids: the walls or ceilings whose framing goes.");
                    toRemove = FramingMarker.Find(doc, ids);
                    plans = new List<FramingSourcePlan>();
                    signature = string.Join(",", toRemove.Select(p => Rid.Value(p.Key.Id).ToString(CultureInfo.InvariantCulture)));
                }
                else
                {
                    plans = PlanWalls(doc, request, wallSpec, specHash);
                    signature = string.Join(",", plans.Select(p => Rid.Value(p.Source.Id).ToString(CultureInfo.InvariantCulture) + ":" + p.Signature));
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
                PlannedElement pe = ModelEditRunner.Planned(p.Source, PlannedAction.Modify, request);
                pe.ProposedValues["plan_signature"] = p.Signature;
                resolved.Elements.Add(pe);
            }
            if (toRemove != null)
                foreach (KeyValuePair<Element, FramingMark> p in toRemove)
                    resolved.Elements.Add(ModelEditRunner.Planned(p.Key, PlannedAction.Delete, request));
            string hash = DocumentGate.PlanHash(request, HashScope) + "|" + FramingPlanSignature.Of(new[] { new FramingMember { Role = op, TypeKey = signature } });

            JObject summary = op == "remove" ? RemoveSummary(toRemove) : WallSummary(plans);
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
            Func<Document, PostconditionCheck> verify = op == "remove"
                ? (Func<Document, PostconditionCheck>)(d => VerifyRemoved(d, toRemove, SourceIds(request), evidence))
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
                            if (op == "remove") doc.Delete(toRemove.Select(p => p.Key.Id).ToList());
                            else foreach (FramingSourcePlan p in plans.Where(x => !x.AlreadyApplied)) PlaceSource(doc, p);
                            doc.Regenerate();
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
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, total, created, total, 0, 0, 0);
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

        private static List<T> Sources<T>(Document doc, JObject request, string what) where T : Element
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
            found.AddRange(new FilteredElementCollector(doc, view.Id).OfClass(typeof(T)).Cast<T>().OrderBy(e => Rid.Value(e.Id)));
            if (found.Count == 0) throw new ArgumentException("view " + viewId + " shows no " + what + ".");
            return found;
        }

        private static List<FramingSourcePlan> PlanWalls(Document doc, JObject request, WallFramingSpec spec, string specHash)
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
            foreach (Wall wall in Sources<Wall>(doc, request, "wall"))
            {
                FramedWall fw = ReadWall(doc, wall, spec, out string refusal);
                if (fw == null) throw new ArgumentException(refusal);
                var p = new FramingSourcePlan { Source = wall, Operation = "wall", Wall = fw, SpecHash = specHash, StudWidthMm = studWidth.Value, PlaneSpan = fw.Normal, Symbols = symbols, Kinds = kinds };
                p.Warnings.AddRange(fw.Warnings);
                double track = spec.TrackThicknessMm ?? 0;
                if (spec.TrackThicknessMm == null) p.Warnings.Add("wall " + Rid.Value(wall.Id) + ": no track thickness_mm; studs run base to top of wall");
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
                throw new ArgumentException(p.Operation + " " + sid + " already carries " + members.Count + " horizun_framing member(s) from another spec or plan (spec " +
                                            string.Join(",", members.Select(x => x.Value.SpecHash).Distinct()) + "); run operation=remove for it first.");
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
            View planeView = null;
            string sourceUid = p.Source.UniqueId;
            long sid = Rid.Value(p.Source.Id);
            Level level = p.Wall?.Level;
            for (int i = 0; i < p.Members.Count; i++)
            {
                FramingMember m = p.Members[i];
                FamilySymbol sym = p.Symbols[m.TypeKey];
                FramingPlacementKind kind = p.Kinds[m.Role + "|" + m.TypeKey];
                if (kind == FramingPlacementKind.LineBased && planeView == null) planeView = WorkPlaneView(doc);
                FamilyInstance fi = PlaceMember(doc, sym, kind, p.Axis(m), level, p.PlaneSpan, planeView, out ReferencePlane plane);
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

        // ---- verifying ------------------------------------------------------------------

        /// <summary>A member's axis as the committed model reports it, and how it was read.</summary>
        internal static XYZ[] MemberEnds(Document doc, Element e, out string method)
        {
            method = null;
            if (e?.Location is LocationCurve lc && lc.Curve != null) { method = "location_curve"; return new[] { lc.Curve.GetEndPoint(0), lc.Curve.GetEndPoint(1) }; }
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

        private static PostconditionCheck VerifyWalls(Document doc, List<FramingSourcePlan> plans, JObject evidence)
        {
            var check = new PostconditionCheck("member_count", "member_types", "member_endpoints", "counts_by_role", "inside_layer", "no_stud_through_opening", "inserts_untouched");
            int planned = 0, found = 0, wrongType = 0, unreadable = 0, crossings = 0, insertsChanged = 0;
            double maxDev = 0, maxExcess = 0;
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
                    double[] f0 = fw.ToFrame(ends[0]), f1 = fw.ToFrame(ends[1]);
                    maxExcess = Math.Max(maxExcess, Math.Max(0, Math.Max(Math.Abs(f0[1]), Math.Abs(f1[1])) - fw.LayerWidthMm / 2));
                    // A cripple sits inside the opening's width but above its head or below its sill,
                    // so the same test (the void's x AND z ranges) holds for every vertical role.
                    if (FramingRoles.IsVertical(m.Role) &&
                        WallFramingRules.CrossesOpening((f0[0] + f1[0]) / 2, Math.Min(f0[2], f1[2]), Math.Max(f0[2], f1[2]), p.StudWidthMm, fw.OpeningsMm))
                    { crossings++; srcCross++; }
                }
                maxDev = Math.Max(maxDev, srcDev);
                int changed = p.InsertsBefore.Count(kv => InsertState(doc, kv.Key) != kv.Value);
                insertsChanged += changed;
                perSource.Add(new JObject
                {
                    ["source_id"] = sid, ["already_applied"] = p.AlreadyApplied, ["planned"] = p.Members.Count, ["found"] = srcFound,
                    ["max_endpoint_deviation_mm"] = Math.Round(srcDev, 3), ["stud_crossings"] = srcCross,
                    ["inserts_checked"] = p.InsertsBefore.Count, ["inserts_changed"] = changed,
                    ["member_ids"] = new JArray(p.MemberIds.OrderBy(kv => kv.Key).Select(kv => kv.Value)),
                    ["work_plane_ids"] = new JArray(p.WorkPlaneIds)
                });
            }
            check.Compare("member_count", planned, found);
            check.Compare("member_types", 0, wrongType);
            if (unreadable > 0) check.Unreadable("member_endpoints", 0, unreadable + " member(s) report neither a location curve nor column constraints");
            else check.Measure("member_endpoints", 0, maxDev, EndpointToleranceMm, "mm", "max over members of the farther end's distance to the planned axis end");
            check.Record("counts_by_role", plannedRoles, foundRoles, JToken.DeepEquals(plannedRoles, foundRoles));
            check.Measure("inside_layer", 0, maxExcess, EndpointToleranceMm, "mm", "max |y| beyond half the carrying layer's thickness");
            check.Compare("no_stud_through_opening", 0, crossings);
            check.Compare("inserts_untouched", 0, insertsChanged);
            evidence["sources"] = perSource;
            evidence["endpoint_read"] = new JArray(methods.OrderBy(s => s, StringComparer.Ordinal).ToArray());
            return check;
        }

        private static PostconditionCheck VerifyRemoved(Document doc, List<KeyValuePair<Element, FramingMark>> removed, HashSet<long> sources, JObject evidence)
        {
            var check = new PostconditionCheck("members_absent", "markers_absent");
            int still = removed.Count(p => doc.GetElement(p.Key.Id) != null);
            int marked = FramingMarker.Find(doc, sources).Count;
            check.Compare("members_absent", 0, still);
            check.Compare("markers_absent", 0, marked);
            evidence["removed_ids"] = new JArray(removed.Select(p => Rid.Value(p.Key.Id)));
            evidence["sources"] = new JArray(sources.OrderBy(s => s));
            return check;
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

        private static JObject RemoveSummary(List<KeyValuePair<Element, FramingMark>> found)
        {
            var bySource = new JArray();
            foreach (IGrouping<long, KeyValuePair<Element, FramingMark>> g in found.GroupBy(p => p.Value.SourceId))
            {
                var counts = new JObject();
                foreach (IGrouping<string, KeyValuePair<Element, FramingMark>> r in g.GroupBy(p => p.Value.Role).OrderBy(r => r.Key, StringComparer.Ordinal)) counts[r.Key] = r.Count();
                bySource.Add(new JObject { ["source_id"] = g.Key, ["count_by_role"] = counts, ["element_count"] = g.Count() });
            }
            return new JObject { ["sources"] = bySource, ["element_count"] = found.Count, ["nothing_to_remove"] = found.Count == 0 };
        }
    }
}
