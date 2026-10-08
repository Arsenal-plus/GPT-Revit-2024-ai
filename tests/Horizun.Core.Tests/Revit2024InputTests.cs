using System;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

public sealed class Revit2024InputTests
{
    [Fact] public void Every_declared_operation_is_recognized_by_validation()
    {
        var tool = Contract.All.Single(t => t.Name == "horizun_revit2024");
        foreach (JToken op in (JArray)tool.InputSchema["properties"]["operation"]["enum"])
            Assert.NotEqual("Unknown operation.", Revit2024InputRules.Validate(new JObject { ["operation"] = op.DeepClone() }));
    }
    [Fact] public void Irrelevant_fields_cannot_be_silently_ignored()
    {
        var r = new JObject { ["operation"] = "analytical_member_create", ["points"] = new JArray(new JArray(0, 0, 0), new JArray(1, 0, 0)), ["force"] = new JArray(1, 0, 0) };
        Assert.Contains("does not use 'force'", Revit2024InputRules.Validate(r));
    }
    [Fact] public void Nonfinite_coordinate_is_refused_before_revit()
    {
        var r = new JObject { ["operation"] = "analytical_member_create", ["points"] = new JArray(new JArray(double.NaN, 0, 0), new JArray(1, 0, 0)) };
        Assert.NotNull(Revit2024InputRules.Validate(r));
    }
    [Theory]
    [InlineData("id:2147483648")]
    [InlineData("ID:-1001000")]
    public void Explicit_parameter_id_does_not_fall_through_to_localized_name(string id)
    {
        Assert.Equal(ParameterSpecKind.Id, ParameterResolutionRules.KindOf(id, _ => false));
        var probes = ParameterResolutionRules.Plan(id, _ => false, false);
        Assert.Single(probes);
        Assert.Equal(ParameterSpecKind.Id, probes[0].Kind);
    }
    [Fact] public void Loads_require_explicit_units_even_when_values_are_zero()
    {
        var r = new JObject { ["operation"] = "analytical_area_load", ["host_id"] = 1, ["load_case_id"] = 2, ["force"] = new JArray(0, 0, 0) };
        Assert.Contains("requires force_unit", Revit2024InputRules.Validate(r));
    }
    [Fact] public void Transport_key_reordering_keeps_approval_but_changed_geometry_does_not()
    {
        var preview = JObject.Parse("{ 'operation':'analytical_member_create', 'target_document':'fixture.rvt', 'units':'feet', 'points':[[0,150,0],[10,150,0]] }");
        var apply = new JObject(preview.Properties().Reverse().Select(p => new JProperty(p.Name, p.Value.DeepClone())));
        apply["dry_run"] = false; apply["confirmation_token"] = "token"; apply["idempotency_key"] = "retry";
        string Hash(JObject r) => ConfirmationStore.PlanHash(r, Revit2024InputRules.BoundFields(r));
        var store = new ConfirmationStore();
        var issued = store.Issue("horizun_revit2024", "fixture", Hash(preview));
        Assert.True(store.Validate(issued.Token, "horizun_revit2024", "fixture", Hash(apply)).Ok);
        apply["points"][1][0] = 11;
        Assert.NotEqual(Hash(preview), Hash(apply));
    }
}
