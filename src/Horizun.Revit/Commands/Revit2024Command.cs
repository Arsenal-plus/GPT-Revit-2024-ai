using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    // Explicit public API operations; no reflection-based invocation or Python escape.
    public sealed partial class Revit2024Command : ICommand
    {
        public const string Tool = "horizun_revit2024";
        public string Name => "horizun_revit2024";
        public string Description => "Verified Revit 2024 site, analytical, fabrication and placement operations.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject r = VerifiedModelEdit.Parse(paramsJson, out CommandResult parseError);
            if (r == null) return parseError;
            string validation = Revit2024InputRules.Validate(r);
            if (validation != null) return CommandResult.Fail(validation);
            string op = VerifiedModelEdit.Operation(r);
            if (op == "catalog" || op == "appearance_read")
            {
                Document d = app.ActiveUIDocument?.Document;
                if (d == null) return CommandResult.Fail("Open a project document first.");
                CommandResult guard = DocumentGate.ReadGuard(d, r, Tool);
                if (guard != null) return guard;
                try { return VerifiedModelEdit.ReadReply(op == "catalog" ? Catalog(d) : AppearanceProperties.Read(d, Need<Material>(d, r, "material_id"))); }
                catch (Exception ex) { return CommandResult.Fail(ex.Message); }
            }
            GateResult gate = DocumentGate.ForMutation(app, r, Tool);
            if (!gate.Ok) return gate.Refusal;
            if (gate.Document.IsFamilyDocument) return CommandResult.Fail("These operations require a project document.");
            try
            {
                double scale = Scale(r);
                ModelEdit edit;
                if (op.StartsWith("site_", StringComparison.Ordinal)) edit = Site(gate.Document, r, op, scale);
                else if (op.StartsWith("analytical_", StringComparison.Ordinal)) edit = Analytical(gate.Document, r, op, scale);
                else if (op.StartsWith("fabrication_", StringComparison.Ordinal)) edit = Fabrication(gate.Document, r, op, scale);
                else edit = Placement(gate.Document, r, op, scale);
                edit.Tool = Tool; edit.Operation = op;
                edit.Before["model_state"] = ModelStateFingerprint.Read(gate.Document);
                edit.Plan["request"] = JObject.Parse(ArchitecturalEditRules.ProposedRequest(r));
                edit.TokenNote = "Bound to element identities and versions. Any intervening model edit requires a new preview.";
                return VerifiedModelEdit.Run(app, gate, r, edit, Revit2024InputRules.BoundFields(r));
            }
            catch (Exception ex) { return CommandResult.Fail(op + ": " + ex.Message); }
        }

        private static double Scale(JObject r)
        {
            switch ((r.Value<string>("units") ?? "mm").ToLowerInvariant())
            { case "mm": return 1 / 304.8; case "m": return 1 / 0.3048; case "feet": return 1; default: throw new ArgumentException("units must be mm, m or feet."); }
        }
        private static T Need<T>(Document doc, JObject r, string field) where T : Element => ModelEditRunner.Need<T>(doc, r, field);
        private static List<XYZ> Points(JToken token, double scale, int min = 2)
        {
            if (!(token is JArray array) || array.Count < min || array.Count > 10000) throw new ArgumentException("Expected " + min + "..10000 points.");
            return array.Select((p, i) => ModelEditRunner.Point(p, scale, "points[" + i + "]")).ToList();
        }
        private static Curve CurveFor(JObject r, double scale)
        {
            List<XYZ> p = Points(r["points"], scale);
            if (p.Count == 2) return Line.CreateBound(p[0], p[1]);
            if (p.Count == 3) return Arc.Create(p[0], p[1], p[2]); // start, end, on-arc
            throw new ArgumentException("A curve takes 2 points (line) or 3 (arc: start, end, on-arc).");
        }
        private static CurveLoop Loop(JToken raw, double scale)
        {
            List<XYZ> p = Points(raw, scale, 3);
            if (p[0].IsAlmostEqualTo(p[p.Count - 1])) p.RemoveAt(p.Count - 1);
            if (p.Count < 3) throw new ArgumentException("A loop needs three distinct vertices.");
            return CurveLoop.Create(p.Select((v, i) => (Curve)Line.CreateBound(v, p[(i + 1) % p.Count])).ToList());
        }
        private static bool SameCurve(Curve a, Curve b)
        {
            if (a == null || b == null) return false;
            return Math.Abs(a.Length - b.Length) < 1e-6 && a.Evaluate(0.5, true).DistanceTo(b.Evaluate(0.5, true)) < 1e-6 &&
                ((a.GetEndPoint(0).DistanceTo(b.GetEndPoint(0)) < 1e-6 && a.GetEndPoint(1).DistanceTo(b.GetEndPoint(1)) < 1e-6) ||
                 (a.GetEndPoint(0).DistanceTo(b.GetEndPoint(1)) < 1e-6 && a.GetEndPoint(1).DistanceTo(b.GetEndPoint(0)) < 1e-6));
        }
        private static bool SameLoop(IEnumerable<Curve> a, IEnumerable<Curve> b)
        {
            var remaining = b.ToList();
            foreach (Curve c in a) { int i = remaining.FindIndex(x => SameCurve(c, x)); if (i < 0) return false; remaining.RemoveAt(i); }
            return remaining.Count == 0;
        }
        private static ModelEdit Edit(string op, Element subject = null) => new ModelEdit
        { Subject = subject?.UniqueId ?? ("new:" + op), Category = op, Action = subject == null ? PlannedAction.Create : PlannedAction.Modify };
        private static JObject Created(Document d, ElementId id)
        {
            Element e = id == null ? null : d.GetElement(id);
            return new JObject { ["element_id"] = e == null ? null : (JToken)Rid.Value(e.Id), ["unique_id"] = e?.UniqueId };
        }
        private static XYZ QuantityVector(JObject r, string field, ForgeTypeId spec)
        {
            string unit = r.Value<string>(field + "_unit");
            if (string.IsNullOrEmpty(unit)) throw new ArgumentException(field + "_unit is required; use a UnitTypeId name or internal.");
            XYZ v = ModelEditRunner.Point(r[field], 1, field);
            if (unit == "internal") return v;
            var property = typeof(UnitTypeId).GetProperties().FirstOrDefault(p => string.Equals(p.Name, unit, StringComparison.OrdinalIgnoreCase));
            ForgeTypeId u = property == null ? new ForgeTypeId(unit) : (ForgeTypeId)property.GetValue(null);
            if (!UnitUtils.IsValidUnit(spec, u)) throw new ArgumentException("Invalid unit for " + field + ".");
            return new XYZ(UnitUtils.ConvertToInternalUnits(v.X, u), UnitUtils.ConvertToInternalUnits(v.Y, u), UnitUtils.ConvertToInternalUnits(v.Z, u));
        }
        private static JObject Catalog(Document d)
        {
            var types = new JArray(new FilteredElementCollector(d).WhereElementIsElementType()
                .Where(e => e is FamilySymbol || e.GetType().Name == "ToposolidType" || e is Autodesk.Revit.DB.Electrical.WireType)
                .Take(1000).Select(e => new JObject { ["id"] = Rid.Value(e.Id), ["name"] = e.Name, ["api_class"] = e.GetType().Name }));
            return new JObject { ["types"] = types, ["type_limit"] = 1000,
                ["levels"] = new JArray(new FilteredElementCollector(d).OfClass(typeof(Level)).Cast<Level>()
                    .Select(l => new JObject { ["id"] = Rid.Value(l.Id), ["name"] = l.Name, ["project_elevation_mm"] = l.ProjectElevation * 304.8 })),
                ["fabrication"] = FabricationCatalog(d) };
        }
    }
}
