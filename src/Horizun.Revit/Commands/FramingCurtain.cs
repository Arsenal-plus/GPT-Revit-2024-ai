// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_framing, spec.wall.method = 'curtain': the Revit half.
// Original Horizun code.
//
// WHAT IT BUILDS. The partition core as Curtain Walls of the caller's type (its grid
// and mullions ARE the studs and tracks), cut around the carrier's openings, with a
// header curtain wall above each opening and a sill curtain wall below each window
// (Core/CurtainFramingRules.cs plans them). The pieces run on the carrier's CORE
// CENTRELINE (the core's two faces, WALL_KEY_REF_PARAM-aware, the same arithmetic
// ReadWall uses for a layer), in the carrier's direction so a grid justified at the
// beginning starts where the wall starts.
//
// REVIT API, confirmed in RevitAPI.xml 2023 and 2026 (identical entries):
//   Wall.Create(Document, Curve, ElementId, ElementId, Double, Double, Boolean, Boolean)
//   WallUtils.DisallowWallJoinAtEnd(Wall, Int32)   - a piece keeps its planned ends
//   Element.ChangeTypeId(ElementId); LocationCurve.Curve (set)   - the carrier
//   Wall.CurtainGrid; CurtainGrid.GetUGridLineIds / GetVGridLineIds / GetMullionIds;
//   CurtainGridLine.FullCurve; Mullion.MullionType; Mullion.LocationCurve
//   BuiltInParameter SPACING_LAYOUT_VERT, SPACING_LENGTH_VERT, AUTO_MULLION_* (type)
// A curtain wall's grid is laid out FROM ITS TYPE when it is created (the type's
// layout, spacing and justification per direction, and the AUTO_MULLION_* types on
// the interior and border lines); nothing here edits a grid. The grid is read back
// after doc.Regenerate(). The numeric value of SPACING_LAYOUT_VERT is not documented
// by the API: 1 is read as Fixed Distance (the order of the type dialog's list) and
// the value string is reported beside it, so the live probe confirms the mapping.
//
// THE CARRIER keeps its identity and its inserts (no public API re-hosts a door):
// trimmed to its one opening's span and set to the placeholder type, deleted when it
// had no opening (after the pieces exist), or kept full length under the pieces.
// An insert whose span cannot be measured, or an edited wall profile, refuses the
// wall: trimming or deleting it could take an element nobody planned for with it.
//
// THE RECORD. Each piece carries the FramingMarker (role, index, spec hash, plan
// signature) AND, in its own schema, the RESOLVED PLAN as JSON: the frame, every
// piece, the carrier action, and the carrier's ORIGINAL type, curve, level and
// constraints. A second apply of the same spec re-verifies from that record (the
// carrier it would re-read has already been trimmed or deleted), and remove restores
// the carrier from it. The FramingMarker field list is frozen, hence the new schema.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    /// <summary>The curtain method's resolved plan for one carrier (feet internally, mm in the plan).</summary>
    internal sealed class CurtainSourceState
    {
        public long CarrierId;
        public string CarrierUniqueId;
        public CurtainWallPlan Plan;
        public XYZ Origin, Dir, Normal;
        public double CoreOffsetFt, BaseZFt, BaseOffsetFt, LengthMm, HeightMm;
        public long LevelId, TopLevelId = -1;
        public double TopOffsetFt, UnconnectedFt;
        public long OriginalTypeId;
        public XYZ OriginalStart, OriginalEnd, NewStart, NewEnd;
        public bool Flipped;
        public int KeyRef;
        public List<WallOpeningSpan> Openings = new List<WallOpeningSpan>();
        public List<long> CarrierDeleteCascade = new List<long>();

        /// <summary>A point of the frame on the core centreline, at the base level's elevation.</summary>
        public XYZ At(double xMm, double levelZ) => new XYZ(Origin.X, Origin.Y, levelZ) + Dir * (xMm / 304.8) + Normal * CoreOffsetFt;

        public JObject ToRecord(string specHash, string signature, IDictionary<long, string> inserts) => new JObject
        {
            ["v"] = 1, ["carrier_id"] = CarrierId, ["carrier_uid"] = CarrierUniqueId, ["spec_hash"] = specHash, ["signature"] = signature,
            ["origin"] = P(Origin), ["dir"] = P(Dir), ["normal"] = P(Normal), ["core_offset_ft"] = CoreOffsetFt, ["base_z_ft"] = BaseZFt,
            ["base_offset_ft"] = BaseOffsetFt, ["length_mm"] = LengthMm, ["height_mm"] = HeightMm, ["level_id"] = LevelId,
            ["top_level_id"] = TopLevelId, ["top_offset_ft"] = TopOffsetFt, ["unconnected_ft"] = UnconnectedFt,
            ["original_type_id"] = OriginalTypeId, ["original_curve"] = new JArray(P(OriginalStart), P(OriginalEnd)),
            ["new_curve"] = NewStart == null ? null : new JArray(P(NewStart), P(NewEnd)), ["flipped"] = Flipped, ["key_ref"] = KeyRef,
            ["pieces"] = new JArray(Plan.Pieces.Select(m => new JObject { ["role"] = m.Role, ["type"] = m.TypeKey, ["x0"] = m.X0, ["x1"] = m.X1, ["z0"] = m.Z0, ["z1"] = m.Z1, ["src"] = m.Source })),
            ["carrier"] = new JObject { ["action"] = Plan.Carrier.Action, ["x0"] = Plan.Carrier.X0, ["x1"] = Plan.Carrier.X1, ["type"] = Plan.Carrier.TypeKey, ["opening"] = Plan.Carrier.OpeningId },
            ["inserts"] = new JObject(inserts.Select(kv => new JProperty(kv.Key.ToString(CultureInfo.InvariantCulture), kv.Value))),
        };

        public static CurtainSourceState FromRecord(JObject r)
        {
            var plan = new CurtainWallPlan();
            foreach (JObject m in r["pieces"] as JArray ?? new JArray())
                plan.Pieces.Add(new FramingMember { Role = (string)m["role"], TypeKey = (string)m["type"], X0 = (double)m["x0"], X1 = (double)m["x1"], Z0 = (double)m["z0"], Z1 = (double)m["z1"], Source = (int)m["src"] });
            JObject c = (JObject)r["carrier"];
            plan.Carrier = new CurtainCarrierAction { Action = (string)c["action"], X0 = (double)c["x0"], X1 = (double)c["x1"], TypeKey = (string)c["type"], OpeningId = (string)c["opening"] };
            JArray nc = r["new_curve"] as JArray;
            return new CurtainSourceState
            {
                CarrierId = (long)r["carrier_id"], CarrierUniqueId = (string)r["carrier_uid"], Plan = plan,
                Origin = X(r["origin"]), Dir = X(r["dir"]), Normal = X(r["normal"]), CoreOffsetFt = (double)r["core_offset_ft"], BaseZFt = (double)r["base_z_ft"],
                BaseOffsetFt = (double)r["base_offset_ft"], LengthMm = (double)r["length_mm"], HeightMm = (double)r["height_mm"], LevelId = (long)r["level_id"],
                TopLevelId = (long)r["top_level_id"], TopOffsetFt = (double)r["top_offset_ft"], UnconnectedFt = (double)r["unconnected_ft"],
                OriginalTypeId = (long)r["original_type_id"], OriginalStart = X(r["original_curve"][0]), OriginalEnd = X(r["original_curve"][1]),
                NewStart = nc == null ? null : X(nc[0]), NewEnd = nc == null ? null : X(nc[1]), Flipped = (bool)r["flipped"], KeyRef = (int)r["key_ref"],
            };
        }

        private static JArray P(XYZ p) => new JArray(p.X, p.Y, p.Z);
        private static XYZ X(JToken t) => new XYZ((double)t[0], (double)t[1], (double)t[2]);
    }

    /// <summary>The resolved curtain plan on every piece (see the header): read by idempotence and remove.</summary>
    internal static class FramingCurtainStore
    {
        public static readonly Guid SchemaGuid = new Guid("5e0c7a93-2d41-4b8f-a6c3-8f19d2b07e54");
        public const string SchemaName = "HorizunFramingCurtainV1";
        private const string FVersion = "SchemaVersion", FCarrier = "CarrierId", FRecord = "Record", FMemberUid = "MemberUniqueId";
        private static Schema _cached;

        private static Schema GetOrCreate()
        {
            if (_cached != null && _cached.IsValidObject) return _cached;
            Schema existing = Schema.Lookup(SchemaGuid);
            if (existing != null) { _cached = existing; return _cached; }
            var b = new SchemaBuilder(SchemaGuid);
            b.SetSchemaName(SchemaName);
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Vendor);
            b.SetVendorId(CadProvenanceStore.VendorId);
            b.SetDocumentation("Horizun framing (curtain method): the resolved plan and the carrier's original state, for re-verification and remove.");
            b.AddSimpleField(FVersion, typeof(int));
            foreach (string f in new[] { FCarrier, FRecord, FMemberUid }) b.AddSimpleField(f, typeof(string));
            _cached = b.Finish();
            return _cached;
        }

        public static void Write(Element e, long carrierId, JObject record)
        {
            var entity = new Entity(GetOrCreate());
            entity.Set(FVersion, 1);
            entity.Set(FCarrier, carrierId.ToString(CultureInfo.InvariantCulture));
            entity.Set(FRecord, record.ToString(Newtonsoft.Json.Formatting.None));
            entity.Set(FMemberUid, e.UniqueId ?? "");
            e.SetEntity(entity);
        }

        /// <summary>The record on an element that is ours (its own UniqueId matches), else null.</summary>
        public static JObject Read(Element e)
        {
            try
            {
                Schema s = Schema.Lookup(SchemaGuid);
                if (s == null || e == null) return null;
                Entity en = e.GetEntity(s);
                if (en == null || !en.IsValid() || !string.Equals(en.Get<string>(FMemberUid), e.UniqueId, StringComparison.Ordinal)) return null;
                return JObject.Parse(en.Get<string>(FRecord));
            }
            catch { return null; }
        }
    }

    public sealed partial class FramingCommand
    {
        private const int MaxCurtainPiecesTotal = 2000;

        private static List<FramingSourcePlan> PlanCurtainWalls(Document doc, JObject request, CurtainWallFramingSpec spec, string specHash, List<string> skipped)
        {
            foreach (long id in spec.TypeIds())
            {
                WallType t = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as WallType : null;
                bool placeholder = id == spec.PlaceholderTypeId;
                if (t == null) throw new ArgumentException("type id " + id + " in spec.wall is not a wall type of this document.");
                if (placeholder && t.Kind != WallKind.Basic) throw new ArgumentException("placeholder_type_id " + id + " is a " + t.Kind + " wall type; the placeholder is a Basic wall type.");
                if (!placeholder && t.Kind != WallKind.Curtain) throw new ArgumentException("type id " + id + " is a " + t.Kind + " wall type; curtain/header/sill types are Curtain Wall types.");
            }
            var plans = new List<FramingSourcePlan>();
            int total = 0;
            HashSet<long> ids = SourceIds(request);
            var walls = new List<Wall>();
            if (ids != null)
            {
                foreach (long id in ids.OrderBy(i => i))
                {
                    Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    if (e is Wall w) { walls.Add(w); continue; }
                    // A carrier the curtain method deleted: its pieces carry the plan it was replaced by.
                    FramingSourcePlan fromRecord = e == null ? CurtainFromRecord(doc, id, null, specHash) : null;
                    if (fromRecord == null) throw new ArgumentException("element " + id + " is " + (e == null ? "not an element of this document" : "not a wall") + ".");
                    plans.Add(fromRecord);
                }
            }
            else walls = Sources<Wall>(doc, request, "wall", WallOutOfScope, skipped);

            foreach (Wall wall in walls)
            {
                long sid = Rid.Value(wall.Id);
                FramingSourcePlan existing = CurtainFromRecord(doc, sid, wall, specHash);
                if (existing != null) { plans.Add(existing); continue; }
                string who = "wall " + sid;
                if (wall.SketchId != ElementId.InvalidElementId) throw new ArgumentException(who + " has an edited profile; the curtain method trims or deletes the carrier and refuses a profile it cannot keep.");
                FramedWall fw = ReadWall(doc, wall, new WallFramingSpec(), out string refusal);
                if (fw == null) throw new ArgumentException(refusal);
                if (fw.InsertIds.Count != fw.OpeningsMm.Count)
                    throw new ArgumentException(who + " hosts " + fw.InsertIds.Count + " insert(s) but only " + fw.OpeningsMm.Count + " have a measurable span; the carrier cannot be trimmed or deleted safely.");
                var s = new CurtainSourceState
                {
                    CarrierId = sid, CarrierUniqueId = wall.UniqueId, Origin = fw.Origin, Dir = fw.Dir, Normal = fw.Normal,
                    BaseZFt = fw.BaseZ, LengthMm = fw.LengthMm, HeightMm = fw.HeightMm, LevelId = Rid.Value(fw.Level.Id),
                    BaseOffsetFt = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0,
                    OriginalTypeId = Rid.Value(wall.GetTypeId()), Flipped = wall.Flipped,
                    KeyRef = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? 0,
                    UnconnectedFt = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0,
                    Openings = fw.OpeningsMm.ToList(),
                };
                Line original = (Line)((LocationCurve)wall.Location).Curve;
                s.OriginalStart = original.GetEndPoint(0);
                s.OriginalEnd = original.GetEndPoint(1);
                ElementId topId = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId;
                if (topId != ElementId.InvalidElementId && doc.GetElement(topId) is Level)
                {
                    s.TopLevelId = Rid.Value(topId);
                    s.TopOffsetFt = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0;
                }
                var p = new FramingSourcePlan { Source = wall, Operation = "wall", Wall = fw, SpecHash = specHash, Curtain = s };
                p.Warnings.AddRange(fw.Warnings);
                double? core = CoreCentreOffset(wall);
                if (core.HasValue) s.CoreOffsetFt = core.Value;
                else { s.CoreOffsetFt = fw.LayerOffset; p.Warnings.Add(who + ": the type has no core; the pieces run on the thickest layer's centre"); }

                CurtainWallPlan plan = CurtainFramingRules.PlanWall(spec.ToInput(fw.LengthMm, fw.HeightMm, fw.OpeningsMm));
                if (!string.IsNullOrEmpty(plan.Refusal)) throw new ArgumentException(who + ": " + plan.Refusal);
                s.Plan = plan;
                if (plan.Carrier.Action == CurtainFramingRoles.CarrierTrim)
                {
                    // The trimmed carrier stays on its own location line (no core offset), at its own z.
                    s.NewStart = new XYZ(fw.Origin.X, fw.Origin.Y, s.OriginalStart.Z) + fw.Dir * (plan.Carrier.X0 / 304.8);
                    s.NewEnd = new XYZ(fw.Origin.X, fw.Origin.Y, s.OriginalStart.Z) + fw.Dir * (plan.Carrier.X1 / 304.8);
                }
                if (!spec.HeaderTypeId.HasValue && plan.Pieces.Any(m => m.Role == CurtainFramingRoles.Header)) p.Warnings.Add("header_type_id not given: headers use the curtain type");
                if (!spec.SillTypeId.HasValue && plan.Pieces.Any(m => m.Role == CurtainFramingRoles.Sill)) p.Warnings.Add("sill_type_id not given: sills use the curtain type");
                if (plan.Carrier.Action != CurtainFramingRoles.CarrierDelete)
                    p.Warnings.Add("a curtain type with Automatically Embed on is embedded by Revit in a wall it overlaps (the placeholder under a header); read the carrier after the apply");
                p.Warnings.AddRange(plan.Warnings);
                p.Members = plan.Pieces;
                p.Signature = plan.Signature();
                total += p.Members.Count;
                if (total > MaxCurtainPiecesTotal) throw new ArgumentException("the plan exceeds " + MaxCurtainPiecesTotal + " curtain walls; frame fewer walls per call.");
                foreach (long insert in fw.InsertIds) p.InsertsBefore[insert] = InsertState(doc, insert);
                plans.Add(p);
            }
            foreach (FramingSourcePlan p in plans)
                if (p.Curtain.Plan.Skipped.Count > 0) skipped.AddRange(p.Curtain.Plan.Skipped.Select(x => "wall " + p.Curtain.CarrierId + ": " + x));
            return plans;
        }

        /// <summary>The core's centre from the location line along Wall.Orientation (feet), or null when the type has no core.</summary>
        private static double? CoreCentreOffset(Wall wall)
        {
            CompoundStructure cs = wall.WallType?.GetCompoundStructure();
            IList<CompoundStructureLayer> layers = cs?.GetLayers();
            if (layers == null || layers.Count == 0) return null;
            int first = cs.GetFirstCoreLayerIndex(), last = cs.GetLastCoreLayerIndex();
            if (first < 0 || last < first || last >= layers.Count) return null;
            double total = cs.GetWidth();
            Func<int, double> faceAfter = k => total / 2 - layers.Take(k).Sum(l => l.Width);
            double coreExt = faceAfter(first), coreInt = faceAfter(last + 1);
            int key = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? 0;
            double loc = key == 1 ? (coreExt + coreInt) / 2 : key == 2 ? total / 2 : key == 3 ? -total / 2 : key == 4 ? coreExt : key == 5 ? coreInt : 0;
            return (coreExt + coreInt) / 2 - loc;
        }

        /// <summary>
        /// Earlier curtain framing of this carrier: the same spec re-verifies from the record its
        /// pieces carry (already_applied); another spec, or pieces missing, refuses - remove first.
        /// Null when the carrier carries none.
        /// </summary>
        private static FramingSourcePlan CurtainFromRecord(Document doc, long carrierId, Wall carrier, string specHash)
        {
            List<KeyValuePair<Element, FramingMark>> members = FramingMarker.Find(doc, new HashSet<long> { carrierId })
                .Where(x => x.Value.Role != FramingMarker.WorkPlaneRole).ToList();
            if (members.Count == 0) return null;
            JObject record = members.Select(x => FramingCurtainStore.Read(x.Key)).FirstOrDefault(r => r != null);
            if (record == null || members.Any(x => x.Value.SpecHash != specHash) || (string)record["spec_hash"] != specHash)
                throw new ArgumentException("wall " + carrierId + " already carries " + members.Count + " horizun_framing member(s) from another spec or method; run operation=remove for it first.");
            CurtainSourceState s = CurtainSourceState.FromRecord(record);
            string signature = (string)record["signature"];
            if (members.Any(x => x.Value.PlanSignature != signature) || members.Count != s.Plan.Pieces.Count || members.Select(x => x.Value.Index).Distinct().Count() != members.Count)
                throw new ArgumentException("wall " + carrierId + " carries " + members.Count + " curtain piece(s) of this spec where its plan placed " + s.Plan.Pieces.Count + " (pieces were deleted or copied since); run operation=remove for it first.");
            var p = new FramingSourcePlan
            {
                Source = (Element)carrier ?? members[0].Key, Operation = "wall", SpecHash = specHash, Curtain = s,
                Members = s.Plan.Pieces, Signature = signature, AlreadyApplied = true
            };
            foreach (KeyValuePair<Element, FramingMark> x in members) p.MemberIds[x.Value.Index] = Rid.Value(x.Key.Id);
            if (record["inserts"] is JObject inserts)
                foreach (JProperty kv in inserts.Properties()) p.InsertsBefore[long.Parse(kv.Name, CultureInfo.InvariantCulture)] = (string)kv.Value;
            return p;
        }

        // ---- writing --------------------------------------------------------------------

        private static void PlaceCurtainSource(Document doc, FramingSourcePlan p)
        {
            CurtainSourceState s = p.Curtain;
            Level level = doc.GetElement(Rid.Make(s.LevelId)) as Level ?? throw new InvalidOperationException("the carrier's level is gone");
            JObject record = s.ToRecord(p.SpecHash, p.Signature, p.InsertsBefore);
            for (int i = 0; i < p.Members.Count; i++)
            {
                FramingMember m = p.Members[i];
                Line line = Line.CreateBound(s.At(m.X0, level.ProjectElevation), s.At(m.X1, level.ProjectElevation));
                Wall w;
                try { w = Wall.Create(doc, line, Rid.Make(long.Parse(m.TypeKey, CultureInfo.InvariantCulture)), level.Id, (m.Z1 - m.Z0) / 304.8, s.BaseOffsetFt + m.Z0 / 304.8, false, false); }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
                { throw new InvalidOperationException(m.Role + " " + i + " (type " + m.TypeKey + ", x " + Math.Round(m.X0, 1) + " -> " + Math.Round(m.X1, 1) + " mm): " + ex.Message, ex); }
                if (w == null) throw new InvalidOperationException(m.Role + " " + i + ": Revit returned no wall.");
                // Segments and headers end where the carrier ends: its top constraint, not a copied number.
                if (m.Role != CurtainFramingRoles.Sill && s.TopLevelId > 0)
                {
                    w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(Rid.Make(s.TopLevelId));
                    w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.Set(s.TopOffsetFt);
                }
                WallUtils.DisallowWallJoinAtEnd(w, 0);
                WallUtils.DisallowWallJoinAtEnd(w, 1);
                FramingMarker.Write(w, new FramingMark { SourceId = s.CarrierId, SourceUniqueId = s.CarrierUniqueId, Role = m.Role, Index = i, SpecHash = p.SpecHash, PlanSignature = p.Signature, Operation = "wall" });
                FramingCurtainStore.Write(w, s.CarrierId, record);
                p.MemberIds[i] = Rid.Value(w.Id);
            }
            Wall carrier = doc.GetElement(Rid.Make(s.CarrierId)) as Wall ?? throw new InvalidOperationException("the carrier wall is gone");
            string action = s.Plan.Carrier.Action;
            if (action == CurtainFramingRoles.CarrierDelete)
            {
                s.CarrierDeleteCascade.Clear();
                s.CarrierDeleteCascade.AddRange(doc.Delete(carrier.Id).Select(Rid.Value).Where(id => id != s.CarrierId).OrderBy(id => id));
                return;
            }
            carrier.ChangeTypeId(Rid.Make(long.Parse(s.Plan.Carrier.TypeKey, CultureInfo.InvariantCulture)));
            if (action == CurtainFramingRoles.CarrierTrim)
                ((LocationCurve)carrier.Location).Curve = Line.CreateBound(s.NewStart, s.NewEnd);
        }

        // ---- verifying ------------------------------------------------------------------

        private static PostconditionCheck VerifyCurtainWalls(Document doc, List<FramingSourcePlan> plans, JObject evidence)
        {
            var check = new PostconditionCheck("curtain_wall_count", "curtain_types", "curtain_location", "curtain_base_top", "grid_spacing", "mullion_types", "carrier", "inserts_untouched");
            int planned = 0, found = 0, wrongType = 0, gridProblems = 0, mullionProblems = 0, carrierProblems = 0, insertsChanged = 0;
            double maxLoc = 0, maxBaseTop = 0;
            var rows = new JArray();
            foreach (FramingSourcePlan p in plans)
            {
                CurtainSourceState s = p.Curtain;
                var byIndex = FramingMarker.Find(doc, new HashSet<long> { s.CarrierId })
                    .Where(x => x.Value.SpecHash == p.SpecHash && x.Value.PlanSignature == p.Signature)
                    .GroupBy(x => x.Value.Index).ToDictionary(g => g.Key, g => g.ToList());
                Level level = doc.GetElement(Rid.Make(s.LevelId)) as Level;
                var pieces = new JArray();
                for (int i = 0; i < p.Members.Count; i++)
                {
                    FramingMember m = p.Members[i];
                    planned++;
                    if (!byIndex.TryGetValue(i, out var list) || list.Count != 1 || list[0].Value.Role != m.Role || !(list[0].Key is Wall w)) continue;
                    found++;
                    var row = new JObject { ["i"] = i, ["role"] = m.Role, ["id"] = Rid.Value(w.Id) };
                    if (Rid.Value(w.GetTypeId()).ToString(CultureInfo.InvariantCulture) != m.TypeKey || w.WallType?.Kind != WallKind.Curtain) wrongType++;
                    if (level != null && w.Location is LocationCurve lc && lc.Curve is Line l)
                    {
                        XYZ a = s.At(m.X0, 0), b = s.At(m.X1, 0);
                        XYZ ra = Flat(l.GetEndPoint(0)), rb = Flat(l.GetEndPoint(1));
                        double dev = Math.Max(ra.DistanceTo(Flat(a)), rb.DistanceTo(Flat(b))) * 304.8;
                        maxLoc = Math.Max(maxLoc, dev);
                        row["location_deviation_mm"] = Math.Round(dev, 3);
                        double baseDev = Math.Abs(BaseElevation(doc, w) - (s.BaseZFt + m.Z0 / 304.8)) * 304.8;
                        double topDev = Math.Abs(TopElevation(doc, w) - (s.BaseZFt + m.Z1 / 304.8)) * 304.8;
                        maxBaseTop = Math.Max(maxBaseTop, Math.Max(baseDev, topDev));
                        row["base_deviation_mm"] = Math.Round(baseDev, 3);
                        row["top_deviation_mm"] = Math.Round(topDev, 3);
                        JObject grid = ReadCurtainWallGrid(doc, w, l, out int gp, out int mp);
                        gridProblems += gp; mullionProblems += mp;
                        row["grid"] = grid;
                    }
                    else { maxLoc = double.PositiveInfinity; row["location"] = "unreadable"; }
                    pieces.Add(row);
                }
                // The carrier: gone, or its planned type and curve, its inserts still its own.
                Element carrierElement = doc.GetElement(Rid.Make(s.CarrierId));
                var carrierRow = new JObject { ["id"] = s.CarrierId, ["action"] = s.Plan.Carrier.Action };
                int changed = 0;
                if (s.Plan.Carrier.Action == CurtainFramingRoles.CarrierDelete)
                {
                    carrierRow["deleted"] = carrierElement == null;
                    if (carrierElement != null) carrierProblems++;
                    if (s.CarrierDeleteCascade.Count > 0) carrierRow["deleted_with_it"] = new JArray(s.CarrierDeleteCascade);
                }
                else if (!(carrierElement is Wall carrier) || !(carrier.Location is LocationCurve clc) || !(clc.Curve is Line cl)) { carrierProblems++; carrierRow["found"] = false; }
                else
                {
                    bool typeOk = Rid.Value(carrier.GetTypeId()).ToString(CultureInfo.InvariantCulture) == s.Plan.Carrier.TypeKey;
                    XYZ ea = s.NewStart ?? s.OriginalStart, eb = s.NewEnd ?? s.OriginalEnd;
                    double dev = Math.Max(Flat(cl.GetEndPoint(0)).DistanceTo(Flat(ea)), Flat(cl.GetEndPoint(1)).DistanceTo(Flat(eb))) * 304.8;
                    carrierRow["type_ok"] = typeOk;
                    carrierRow["curve_deviation_mm"] = Math.Round(dev, 3);
                    if (!typeOk || dev > EndpointToleranceMm) carrierProblems++;
                    foreach (KeyValuePair<long, string> kv in p.InsertsBefore)
                    {
                        Element ins = Rid.CanRepresent(kv.Key) ? doc.GetElement(Rid.Make(kv.Key)) : null;
                        long host = ins is FamilyInstance fi && fi.Host != null ? Rid.Value(fi.Host.Id) : ins is Opening o && o.Host != null ? Rid.Value(o.Host.Id) : -1;
                        if (InsertState(doc, kv.Key) != kv.Value || host != s.CarrierId) changed++;
                    }
                }
                insertsChanged += changed;
                carrierRow["inserts_checked"] = p.InsertsBefore.Count;
                carrierRow["inserts_changed"] = changed;
                rows.Add(new JObject
                {
                    ["source_id"] = s.CarrierId, ["already_applied"] = p.AlreadyApplied, ["pieces"] = pieces, ["carrier"] = carrierRow,
                    ["piece_ids"] = new JArray(p.MemberIds.OrderBy(kv => kv.Key).Select(kv => kv.Value))
                });
            }
            check.Compare("curtain_wall_count", planned, found);
            check.Compare("curtain_types", 0, wrongType);
            check.Measure("curtain_location", 0, maxLoc, EndpointToleranceMm, "mm", "max over pieces of an end's horizontal distance to the planned core-centreline end");
            check.Measure("curtain_base_top", 0, maxBaseTop, EndpointToleranceMm, "mm", "max over pieces of |base or top elevation - plan|, read from the level and offset parameters");
            check.Compare("grid_spacing", 0, gridProblems);
            check.Compare("mullion_types", 0, mullionProblems);
            check.Compare("carrier", 0, carrierProblems);
            check.Compare("inserts_untouched", 0, insertsChanged);
            evidence["sources"] = rows;
            return check;
        }

        private static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);

        private static double BaseElevation(Document doc, Wall w)
        {
            ElementId baseId = w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? w.LevelId;
            double z = (doc.GetElement(baseId) as Level ?? doc.GetElement(w.LevelId) as Level)?.ProjectElevation ?? 0;
            return z + (w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0);
        }

        private static double TopElevation(Document doc, Wall w)
        {
            ElementId topId = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId;
            if (topId != ElementId.InvalidElementId && doc.GetElement(topId) is Level top)
                return top.ProjectElevation + (w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0);
            return BaseElevation(doc, w) + (w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0);
        }

        /// <summary>
        /// A curtain wall's grid and mullions read back: vertical line positions along the wall,
        /// the fixed-distance check when the type's vertical layout is Fixed Distance, and every
        /// mullion's type against the type's AUTO_MULLION_* for its role (interior / border,
        /// vertical / horizontal). With no horizontal grid line each vertical line carries one
        /// interior mullion, so that count is checked too; other counts are reported.
        /// </summary>
        private static JObject ReadCurtainWallGrid(Document doc, Wall w, Line axis, out int gridProblems, out int mullionProblems)
        {
            gridProblems = 0; mullionProblems = 0;
            var result = new JObject();
            CurtainGrid grid = w.CurtainGrid;
            if (grid == null) { gridProblems++; result["read"] = "no curtain grid"; return result; }
            XYZ start = Flat(axis.GetEndPoint(0));
            XYZ dir = Flat(axis.GetEndPoint(1)) - start;
            double hostMm = dir.GetLength() * 304.8;
            dir = dir.Normalize();
            var vertical = new List<double>();
            int horizontal = 0;
            foreach (ElementId id in grid.GetVGridLineIds().Concat(grid.GetUGridLineIds()))
            {
                Curve c = (doc.GetElement(id) as CurtainGridLine)?.FullCurve;
                if (c == null) continue;
                XYZ d = c.GetEndPoint(1) - c.GetEndPoint(0);
                if (Math.Abs(d.Z) > 0.99 * d.GetLength()) vertical.Add((Flat(c.Evaluate(0.5, true)) - start).DotProduct(dir) * 304.8);
                else horizontal++;
            }
            vertical.Sort();
            ElementType type = doc.GetElement(w.GetTypeId()) as ElementType;
            int layout = type?.get_Parameter(BuiltInParameter.SPACING_LAYOUT_VERT)?.AsInteger() ?? -1;
            double spacingMm = (type?.get_Parameter(BuiltInParameter.SPACING_LENGTH_VERT)?.AsDouble() ?? 0) * 304.8;
            result["vertical_lines"] = vertical.Count;
            result["horizontal_lines"] = horizontal;
            result["layout_vert"] = layout;
            result["layout_vert_text"] = type?.get_Parameter(BuiltInParameter.SPACING_LAYOUT_VERT)?.AsValueString();
            if (vertical.Count > 0) { result["first_mm"] = Math.Round(vertical[0], 1); result["last_mm"] = Math.Round(vertical[vertical.Count - 1], 1); }
            if (layout == 1)
            {
                List<string> problems = CurtainFramingRules.CheckFixedSpacing(vertical, hostMm, spacingMm, EndpointToleranceMm);
                result["spacing_mm"] = Math.Round(spacingMm, 2);
                result["spacing_problems"] = new JArray(problems.Take(10).ToArray());
                gridProblems += problems.Count;
            }
            else result["spacing_check"] = "not fixed distance: count and first/last reported only";

            // Mullions by role, against the type's automatic mullion per role.
            Func<BuiltInParameter, long> auto = bip => { ElementId t = type?.get_Parameter(bip)?.AsElementId(); return t == null || t == ElementId.InvalidElementId ? -1 : Rid.Value(t); };
            var expected = new Dictionary<string, long>
            {
                ["vertical_interior"] = auto(BuiltInParameter.AUTO_MULLION_INTERIOR_VERT),
                ["vertical_border"] = -2,   // border 1 / 2 may differ; both are accepted
                ["horizontal_interior"] = auto(BuiltInParameter.AUTO_MULLION_INTERIOR_HORIZ),
                ["horizontal_border"] = -2,
            };
            var borderV = new HashSet<long> { auto(BuiltInParameter.AUTO_MULLION_BORDER1_VERT), auto(BuiltInParameter.AUTO_MULLION_BORDER2_VERT) };
            var borderH = new HashSet<long> { auto(BuiltInParameter.AUTO_MULLION_BORDER1_HORIZ), auto(BuiltInParameter.AUTO_MULLION_BORDER2_HORIZ) };
            var counts = new JObject();
            double baseZ = BaseElevation(doc, w), topZ = TopElevation(doc, w);
            foreach (ElementId id in grid.GetMullionIds())
            {
                if (!(doc.GetElement(id) is Mullion mu) || !(mu.LocationCurve is Curve mc)) { mullionProblems++; continue; }
                XYZ d = mc.GetEndPoint(1) - mc.GetEndPoint(0);
                bool isVertical = Math.Abs(d.Z) > 0.99 * d.GetLength();
                string role;
                if (isVertical)
                {
                    double x = (Flat(mc.Evaluate(0.5, true)) - start).DotProduct(dir) * 304.8;
                    role = x < EndpointToleranceMm || x > hostMm - EndpointToleranceMm ? "vertical_border" : "vertical_interior";
                }
                else
                {
                    double z = mc.Evaluate(0.5, true).Z;
                    role = Math.Abs(z - baseZ) * 304.8 < EndpointToleranceMm || Math.Abs(z - topZ) * 304.8 < EndpointToleranceMm ? "horizontal_border" : "horizontal_interior";
                }
                counts[role] = (counts.Value<int?>(role) ?? 0) + 1;
                long mt = mu.MullionType == null ? -1 : Rid.Value(mu.MullionType.Id);
                bool ok = role == "vertical_border" ? borderV.Contains(mt) : role == "horizontal_border" ? borderH.Contains(mt) : expected[role] == mt;
                if (!ok) mullionProblems++;
            }
            result["mullions_by_role"] = counts;
            if (horizontal == 0 && expected["vertical_interior"] > 0 && (counts.Value<int?>("vertical_interior") ?? 0) != vertical.Count)
            {
                mullionProblems++;
                result["interior_mullion_count_problem"] = "one interior vertical mullion per vertical line expected with no horizontal line";
            }
            return result;
        }

        private static JObject CurtainWallSummary(List<FramingSourcePlan> plans)
        {
            var rows = new JArray();
            foreach (FramingSourcePlan p in plans)
            {
                CurtainSourceState s = p.Curtain;
                rows.Add(new JObject
                {
                    ["source_id"] = s.CarrierId, ["status"] = p.AlreadyApplied ? "already_applied" : "planned", ["method"] = "curtain",
                    ["length_mm"] = Math.Round(s.LengthMm, 1), ["height_mm"] = Math.Round(s.HeightMm, 1),
                    ["core_offset_mm"] = Math.Round(s.CoreOffsetFt * 304.8, 1),
                    ["openings"] = new JArray(s.Openings.Select(o => new JObject
                    {
                        ["id"] = o.Id, ["start"] = Math.Round(o.Start, 1), ["end"] = Math.Round(o.End, 1), ["sill"] = Math.Round(o.Sill, 1), ["head"] = Math.Round(o.Head, 1)
                    })),
                    ["pieces"] = new JArray(p.Members.Select((m, i) => new JObject
                    {
                        ["i"] = i, ["role"] = m.Role, ["type_id"] = long.Parse(m.TypeKey, CultureInfo.InvariantCulture),
                        ["from"] = new JArray(Math.Round(m.X0, 1), Math.Round(m.Z0, 1)), ["to"] = new JArray(Math.Round(m.X1, 1), Math.Round(m.Z1, 1))
                    })),
                    ["skipped"] = new JArray(s.Plan.Skipped.ToArray()),
                    ["carrier"] = new JObject
                    {
                        ["action"] = s.Plan.Carrier.Action, ["original_type_id"] = s.OriginalTypeId,
                        ["type_id"] = s.Plan.Carrier.TypeKey == null ? null : (JToken)long.Parse(s.Plan.Carrier.TypeKey, CultureInfo.InvariantCulture),
                        ["span"] = s.Plan.Carrier.Action == CurtainFramingRoles.CarrierDelete ? null : new JArray(Math.Round(s.Plan.Carrier.X0, 1), Math.Round(s.Plan.Carrier.X1, 1)),
                        ["opening_id"] = s.Plan.Carrier.OpeningId,
                        ["replaced_by"] = s.Plan.Carrier.Action == CurtainFramingRoles.CarrierDelete ? "every curtain_segment piece" : null,
                    },
                    ["count_by_role"] = JObject.FromObject(FramingPlanSignature.CountByRole(p.Members)),
                    ["plan_signature"] = p.Signature, ["spec_hash"] = p.SpecHash,
                    ["warnings"] = new JArray(p.Warnings.ToArray()),
                    ["pieces_frame"] = "x along the carrier from its start, z up from its base, mm; on the core centreline"
                });
            }
            return new JObject { ["method"] = "curtain", ["sources"] = rows, ["member_count"] = plans.Sum(p => p.Members.Count) };
        }
    }
}
