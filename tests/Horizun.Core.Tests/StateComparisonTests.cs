using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;
namespace Horizun.Core.Tests
{
    public class StateComparisonTests
    {
        [Fact] public void RoundoffPassesButDisplacementIsReported()
        {
            var a = new JObject { ["eye"] = new JArray(10.0,20.0,30.0) };
            Assert.Empty(StateComparison.Differences(a, new JObject { ["eye"] = new JArray(10.0000001,20.0,30.0) }));
            var rows = StateComparison.Differences(a, new JObject { ["eye"] = new JArray(10.01,20.0,30.0) });
            Assert.Single(rows); Assert.Equal("$.eye[0]",rows[0].Value<string>("field"));
        }
        [Fact] public void DirectionsHaveTheirOwnTolerance()
        {
            Assert.Single(StateComparison.Differences(new JObject { ["up"] = new JArray(0.0,1.0,0.0) },
                new JObject { ["up"] = new JArray(0.0000001,1.0,0.0) }));
        }
        [Fact] public void FlagsIdsAndMissingFieldsRemainExact()
        {
            Assert.Equal(3, StateComparison.Differences(JObject.Parse("{id:5,enabled:true,missing:1}"), JObject.Parse("{id:6,enabled:false}")).Count);
        }
        [Fact] public void NonFiniteNumbersAreNotEvidence()
        {
            Assert.Single(StateComparison.Differences(new JValue(double.NaN),new JValue(double.NaN)));
        }
    }
}
