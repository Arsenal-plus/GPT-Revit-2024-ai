// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// ONE WAY TO TURN A PARAMETER NAME INTO A PARAMETER, for every reader that takes one.
//
// MEASURED (dry run 2026-09): horizun_query_model read HOST_AREA_COMPUTED as a
// BuiltInParameter token, and horizun_quantities mode=takeoff answered "absent" for
// the same token on the same walls - it looked the token up as a display NAME, and
// no parameter is called "HOST_AREA_COMPUTED". Two tools, two meanings for one
// string. And classification_parameter "Type Name" read "(empty)" on every instance:
// the instance was asked first, it answered with an empty parameter of that name,
// and the type - where the type's name actually lives - was never asked.
//
// So the plan is decided here, once:
//   - a spec is a BuiltInParameter token, a shared-parameter GUID, or a display name;
//   - an ordinary spec is read on the instance first, then on its type (unchanged);
//   - a TYPE-LEVEL spec (the type's name, the family's name) is read on the type
//     first, through its canonical BuiltInParameters, and only a probe that holds a
//     value counts there; if none does, the ordinary order still runs, so nothing
//     that resolved before stops resolving.
// The Revit half (Commands/ParameterResolver.cs) performs each probe and decides
// nothing.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public enum ParameterSpecKind { BuiltIn, Guid, Name, Id }

    /// <summary>One read to try: which element, how to look, and whether an empty value counts.</summary>
    public sealed class ParameterProbe
    {
        /// <summary>"instance" or "type".</summary>
        public string Scope;
        public ParameterSpecKind Kind;
        /// <summary>The BuiltInParameter token, GUID or display name to look up.</summary>
        public string Token;
        /// <summary>True: a parameter found but holding no value does not end the search.</summary>
        public bool RequireValue;
    }

    /// <summary>What one probe found. Error is a read that threw or an ambiguous name.</summary>
    public sealed class ParameterProbeResult<T>
    {
        public bool Found;
        public bool HasValue;
        public T Parameter;
        public string Error;

        public static ParameterProbeResult<T> Absent() => new ParameterProbeResult<T>();
        public static ParameterProbeResult<T> Of(T parameter, bool hasValue) =>
            new ParameterProbeResult<T> { Found = true, Parameter = parameter, HasValue = hasValue };
        public static ParameterProbeResult<T> Failed(string error) => new ParameterProbeResult<T> { Error = error };
    }

    public sealed class ParameterResolution<T>
    {
        public bool Found;
        public T Parameter;
        /// <summary>"instance" or "type"; null when not found.</summary>
        public string Scope;
        public string Error;
    }

    public static class ParameterResolutionRules
    {
        public const string Instance = "instance";
        public const string Type = "type";

        // The names and tokens that live on the TYPE, each with the canonical
        // BuiltInParameters that hold it there. English display names only: a
        // BuiltInParameter token is the language-independent way to ask.
        private static readonly string[] TypeNameTokens = { "SYMBOL_NAME_PARAM", "ALL_MODEL_TYPE_NAME" };
        private static readonly string[] FamilyNameTokens = { "SYMBOL_FAMILY_NAME_PARAM", "ALL_MODEL_FAMILY_NAME" };

        private static readonly Dictionary<string, string[]> TypeLevel =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Type Name"] = TypeNameTokens,
                ["SYMBOL_NAME_PARAM"] = TypeNameTokens,
                ["ALL_MODEL_TYPE_NAME"] = new[] { "ALL_MODEL_TYPE_NAME", "SYMBOL_NAME_PARAM" },
                ["Family Name"] = FamilyNameTokens,
                ["SYMBOL_FAMILY_NAME_PARAM"] = FamilyNameTokens,
                ["ALL_MODEL_FAMILY_NAME"] = new[] { "ALL_MODEL_FAMILY_NAME", "SYMBOL_FAMILY_NAME_PARAM" }
            };

        /// <summary>True when the spec names something that lives on the element's type.</summary>
        public static bool IsTypeLevel(string spec) => spec != null && TypeLevel.ContainsKey(spec.Trim());

        /// <summary>
        /// BuiltInParameter token, then GUID, then display name - the order query_model
        /// has always used. `isBuiltIn` is the Revit half's enum test.
        /// </summary>
        public static ParameterSpecKind KindOf(string spec, Func<string, bool> isBuiltIn)
        {
            if (spec != null && spec.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
                return ParameterSpecKind.Id;
            if (isBuiltIn != null && isBuiltIn(spec)) return ParameterSpecKind.BuiltIn;
            Guid guid;
            if (Guid.TryParse(spec, out guid)) return ParameterSpecKind.Guid;
            return ParameterSpecKind.Name;
        }

        /// <summary>The ordered probes for one spec.</summary>
        public static List<ParameterProbe> Plan(string spec, Func<string, bool> isBuiltIn, bool hasDistinctType)
        {
            var plan = new List<ParameterProbe>();
            if (string.IsNullOrWhiteSpace(spec)) return plan;
            ParameterSpecKind kind = KindOf(spec, isBuiltIn);
            string[] canonical;
            if (hasDistinctType && TypeLevel.TryGetValue(spec.Trim(), out canonical))
                foreach (string token in canonical)
                    if (isBuiltIn == null || isBuiltIn(token))
                        plan.Add(new ParameterProbe { Scope = Type, Kind = ParameterSpecKind.BuiltIn, Token = token, RequireValue = true });
            plan.Add(new ParameterProbe { Scope = Instance, Kind = kind, Token = spec });
            if (hasDistinctType) plan.Add(new ParameterProbe { Scope = Type, Kind = kind, Token = spec });
            return plan;
        }

        /// <summary>
        /// Runs the plan. The first probe that finds a parameter wins, except a
        /// RequireValue probe that found an empty one: that is remembered and the search
        /// goes on, and it is returned only if nothing later is found at all. An error
        /// ends the search - an ambiguous or unreadable parameter is not a reason to read
        /// a different one silently.
        /// </summary>
        public static ParameterResolution<T> Resolve<T>(IEnumerable<ParameterProbe> plan,
                                                        Func<ParameterProbe, ParameterProbeResult<T>> probe)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (probe == null) throw new ArgumentNullException(nameof(probe));
            ParameterResolution<T> emptyCandidate = null;
            foreach (ParameterProbe step in plan)
            {
                ParameterProbeResult<T> r = probe(step) ?? ParameterProbeResult<T>.Absent();
                if (r.Error != null) return new ParameterResolution<T> { Error = r.Error };
                if (!r.Found) continue;
                if (step.RequireValue && !r.HasValue)
                {
                    if (emptyCandidate == null)
                        emptyCandidate = new ParameterResolution<T> { Found = true, Parameter = r.Parameter, Scope = step.Scope };
                    continue;
                }
                return new ParameterResolution<T> { Found = true, Parameter = r.Parameter, Scope = step.Scope };
            }
            return emptyCandidate ?? new ParameterResolution<T>();
        }
    }
}
