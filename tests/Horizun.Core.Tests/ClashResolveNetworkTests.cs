// Horizun Revit MCP - original Horizun code. Link-aware boxes and connected-network
// eligibility for horizun_resolve_clash - the pure, Revit-free half.
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ClashResolveLinkBoxTests
    {
        [Fact]
        public void TransformBox_is_identity_with_no_rotation_and_a_pure_translation()
        {
            var box = new ResolveBox(0, 0, 0, 100, 200, 300);
            ResolveBox moved = ClashResolveRules.TransformBox(box,
                new[] { 1.0, 0, 0 }, new[] { 0, 1.0, 0 }, new[] { 0, 0, 1.0 }, new[] { 5000.0, -1000.0, 0.0 });
            Assert.Equal(5000, moved.MinX); Assert.Equal(5100, moved.MaxX);
            Assert.Equal(-1000, moved.MinY); Assert.Equal(-800, moved.MaxY);
            Assert.Equal(0, moved.MinZ); Assert.Equal(300, moved.MaxZ);
        }

        [Fact]
        public void TransformBox_widens_the_AABB_under_a_90_degree_rotation_never_shrinks_it()
        {
            // A 100x200 footprint rotated 90 degrees about Z becomes 200x100 in X/Y - the AABB
            // of the box's 8 corners under basisX=(0,1,0), basisY=(-1,0,0).
            var box = new ResolveBox(0, 0, 0, 100, 200, 50);
            ResolveBox moved = ClashResolveRules.TransformBox(box,
                new[] { 0.0, 1.0, 0 }, new[] { -1.0, 0, 0 }, new[] { 0, 0, 1.0 }, new[] { 0.0, 0.0, 0.0 });
            Assert.Equal(-200, moved.MinX); Assert.Equal(0, moved.MaxX);
            Assert.Equal(0, moved.MinY); Assert.Equal(100, moved.MaxY);
        }

        [Fact]
        public void LinkPairKey_is_never_order_normalized_and_names_the_link()
        {
            string k = ClashResolveRules.LinkPairKey(5, "STRUCTURE.rvt", 9);
            Assert.Equal("link:STRUCTURE.rvt:5~9", k);
            Assert.NotEqual(k, ClashResolveRules.LinkPairKey(9, "STRUCTURE.rvt", 5));
        }
    }

    public class ClashResolveNetworkTests
    {
        private static Dictionary<long, long[]> Chain(int length)
        {
            var d = new Dictionary<long, long[]>();
            for (long i = 0; i < length; i++)
            {
                var n = new List<long>();
                if (i > 0) n.Add(i - 1);
                if (i < length - 1) n.Add(i + 1);
                d[i] = n.ToArray();
            }
            return d;
        }

        [Fact]
        public void CollectNetwork_walks_a_chain_and_is_not_truncated_under_the_cap()
        {
            Dictionary<long, long[]> graph = Chain(5);
            List<long> visited = ClashResolveRules.CollectNetwork(0, id => graph[id], 60, out bool truncated);
            Assert.False(truncated);
            Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, visited.OrderBy(x => x).ToArray());
        }

        [Fact]
        public void CollectNetwork_truncates_a_network_larger_than_the_cap()
        {
            Dictionary<long, long[]> graph = Chain(10);
            List<long> visited = ClashResolveRules.CollectNetwork(0, id => graph[id], 4, out bool truncated);
            Assert.True(truncated);
            Assert.Equal(4, visited.Count);
        }

        [Fact]
        public void CollectNetwork_visits_a_branching_graph_exactly_once_each()
        {
            var graph = new Dictionary<long, long[]>
            {
                [1] = new[] { 2L, 3L }, [2] = new[] { 1L, 4L }, [3] = new[] { 1L }, [4] = new[] { 2L }
            };
            List<long> visited = ClashResolveRules.CollectNetwork(1, id => graph[id], 60, out bool truncated);
            Assert.False(truncated);
            Assert.Equal(new long[] { 1, 2, 3, 4 }, visited.OrderBy(x => x).ToArray());
        }

        private static ClashResolveRules.NetworkMemberFacts Member(long id, bool host = true, bool pinned = false, bool inGroup = false)
            => new ClashResolveRules.NetworkMemberFacts { Id = id, Host = host, Pinned = pinned, InGroup = inGroup };

        [Fact]
        public void EligibleForRunShift_passes_a_clean_all_host_unpinned_ungrouped_network_with_no_boundary_block()
        {
            var members = new[] { Member(1), Member(2), Member(3) };
            Assert.True(ClashResolveRules.EligibleForRunShift(members, new List<ClashResolveRules.BoundaryBlock>(), false, out string code, out _));
            Assert.Null(code);
        }

        [Fact]
        public void EligibleForRunShift_refuses_a_truncated_network_before_any_other_check()
        {
            var members = new[] { Member(1, pinned: true) };   // would ALSO fail on pinned - truncation must win
            Assert.False(ClashResolveRules.EligibleForRunShift(members, null, true, out string code, out string reason));
            Assert.Equal(ClashResolveRules.CodeNetworkTooLarge, code);
            Assert.Contains("60", reason);
        }

        [Fact]
        public void EligibleForRunShift_refuses_a_linked_pinned_or_grouped_member()
        {
            Assert.Equal(ClashResolveRules.CodeLinked,
                Refuse(new[] { Member(1), Member(2, host: false) }));
            Assert.Equal(ClashResolveRules.CodePinned,
                Refuse(new[] { Member(1, pinned: true) }));
            Assert.Equal(ClashResolveRules.CodeInGroup,
                Refuse(new[] { Member(1, inGroup: true) }));
        }

        private static string Refuse(IReadOnlyList<ClashResolveRules.NetworkMemberFacts> members)
        {
            ClashResolveRules.EligibleForRunShift(members, new List<ClashResolveRules.BoundaryBlock>(), false, out string code, out _);
            return code;
        }

        [Fact]
        public void EligibleForRunShift_refuses_a_boundary_connector_open_to_equipment_outside_the_set_and_names_it()
        {
            var members = new[] { Member(1), Member(2) };
            var blocks = new List<ClashResolveRules.BoundaryBlock> { new ClashResolveRules.BoundaryBlock { OwnerId = 2, BlockedByDescription = "OST_MechanicalEquipment 900" } };
            Assert.False(ClashResolveRules.EligibleForRunShift(members, blocks, false, out string code, out string reason));
            Assert.Equal(ClashResolveRules.CodeBoundaryBlocked, code);
            Assert.Contains("900", reason);
        }

        [Fact]
        public void IsNetworkMember_covers_runs_and_their_fittings_but_not_equipment()
        {
            Assert.True(ClashResolveRules.IsNetworkMember("OST_PipeCurves"));
            Assert.True(ClashResolveRules.IsNetworkMember("OST_DuctFitting"));
            Assert.True(ClashResolveRules.IsNetworkMember("OST_CableTrayFitting"));
            Assert.False(ClashResolveRules.IsNetworkMember("OST_MechanicalEquipment"));
            Assert.False(ClashResolveRules.IsNetworkMember(null));
        }
    }
}
