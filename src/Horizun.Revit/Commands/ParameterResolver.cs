// -----------------------------------------------------------------------------
// Horizun Revit MCP - the Revit half of Core/ParameterResolutionRules.
//
// The ONE parameter lookup horizun_query_model and horizun_quantities share: a
// BuiltInParameter token, a shared-parameter GUID or a display name, instance then
// type - and a type-level name (Type Name, Family Name) through the type first.
// Each probe here reads; the order and what counts as found are the rules class's.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class ParameterResolver
    {
        // Writes stay on the explicitly named element. Implicitly falling back to its
        // type would change every instance. The resolved id can be kept in a plan.
        public static Parameter ForWrite(Element element, string spec)
        {
            string scope, error;
            Parameter p = Resolve(element, null, spec, out scope, out error);
            if (error != null) throw new ArgumentException(error);
            return p;
        }

        public static string Identity(Parameter p) => "id:" + Rid.Value(p.Id).ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// The parameter `spec` names on `element` or its `type` (null when neither has
        /// it). `scope` is "instance" or "type"; `error` is set - and null returned - when
        /// a read threw or a display name is ambiguous on the element.
        /// </summary>
        public static Parameter Resolve(Element element, Element type, string spec, out string scope, out string error)
        {
            scope = null; error = null;
            if (element == null || string.IsNullOrWhiteSpace(spec)) return null;
            bool distinctType = type != null && type.Id != element.Id;
            List<ParameterProbe> plan = ParameterResolutionRules.Plan(spec, IsBuiltIn, distinctType);
            ParameterResolution<Parameter> r = ParameterResolutionRules.Resolve<Parameter>(plan,
                step => Probe(step.Scope == ParameterResolutionRules.Type ? type : element, step));
            if (r.Error != null) { error = r.Error; return null; }
            if (!r.Found) return null;
            scope = r.Scope;
            return r.Parameter;
        }

        private static bool IsBuiltIn(string token)
        {
            BuiltInParameter bip;
            return !string.IsNullOrWhiteSpace(token) && Enum.TryParse(token, true, out bip);
        }

        private static ParameterProbeResult<Parameter> Probe(Element on, ParameterProbe step)
        {
            if (on == null) return ParameterProbeResult<Parameter>.Absent();
            Parameter p;
            switch (step.Kind)
            {
                case ParameterSpecKind.Id:
                {
                    long id;
                    if (!long.TryParse(step.Token.Substring(3), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out id) || !Rid.CanRepresent(id))
                        return ParameterProbeResult<Parameter>.Failed("Invalid parameter id: " + step.Token);
                    p = null;
                    foreach (Parameter candidate in on.Parameters)
                        if (Rid.Value(candidate.Id) == id) { p = candidate; break; }
                    break;
                }
                case ParameterSpecKind.BuiltIn:
                {
                    BuiltInParameter bip;
                    if (!Enum.TryParse(step.Token, true, out bip)) return ParameterProbeResult<Parameter>.Absent();
                    try { p = on.get_Parameter(bip); }
                    catch (Exception ex)
                    { return ParameterProbeResult<Parameter>.Failed("BuiltInParameter '" + step.Token + "' could not be read: " + ex.Message); }
                    break;
                }
                case ParameterSpecKind.Guid:
                    try { p = on.get_Parameter(Guid.Parse(step.Token)); }
                    catch (Exception ex)
                    { return ParameterProbeResult<Parameter>.Failed("shared parameter '" + step.Token + "' could not be read: " + ex.Message); }
                    break;
                default:
                    try
                    {
                        IList<Parameter> found = on.GetParameters(step.Token);
                        if (found.Count > 1)
                            return ParameterProbeResult<Parameter>.Failed(
                                "parameter name '" + step.Token + "' is ambiguous on element " + Rid.Value(on.Id) +
                                " (" + found.Count + " parameters share it); use a BuiltInParameter token or GUID");
                        p = found.Count == 1 ? found[0] : null;
                    }
                    catch (Exception ex)
                    { return ParameterProbeResult<Parameter>.Failed("parameter '" + step.Token + "' could not be read: " + ex.Message); }
                    break;
            }
            if (p == null) return ParameterProbeResult<Parameter>.Absent();
            return ParameterProbeResult<Parameter>.Of(p, HasValue(p));
        }

        private static bool HasValue(Parameter p)
        {
            try
            {
                if (!p.HasValue) return false;
                return p.StorageType != StorageType.String || !string.IsNullOrEmpty(p.AsString());
            }
            catch { return false; }
        }
    }
}
