// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using PDI.RevitMcp.Engine.Qa;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    public class GroupMemberMapTests
    {
        [Fact]
        public void VariantOnlyChangesTheTwoSpecifiedTypesAndPreservesTheWallHostedMember()
        {
            var before = Json.ParseObject("""
                {"11":{"tipo":101,"pos":[1,2,3],"rot":0},"12":{"tipo":102,"pos":[2,2,3],"rot":90},
                 "13":{"tipo":103,"pos":[3,2,3],"rot":180,"anfitrion":901}}
                """);
            var after = Json.ParseObject(Json.Write(before));
            var changes = Json.ParseObject("{\"11\":201,\"12\":202}");
            foreach (var change in changes)
                ((Dictionary<string, object>)after[change.Key])["tipo"] = change.Value;
            Assert.True(GroupMemberMap.MatchesVariant(before, after, changes));
            ((Dictionary<string, object>)after["13"])["anfitrion"] = 902L;
            Assert.False(GroupMemberMap.MatchesVariant(before, after, changes));
        }

        private static object Row(long id, string uid, string signature) =>
            new Dictionary<string, object> { ["id"] = id, ["uid"] = uid, ["firma"] = signature };

        [Fact]
        public void PreservesStableIdentityAndOnlyMapsUnambiguousRebuiltMembers()
        {
            var previous = new List<object> { Row(1, "uid1", "ex"), Row(2, "uid2", "unico"), Row(3, "uid3", "doble") };
            var current = new List<object> { Row(1, "uid1", "otro"), Row(20, "uid20", "unico"), Row(30, "uid30", "doble"), Row(31, "uid31", "doble") };
            var result = GroupMemberMap.Compare(previous, current);
            Assert.Collection((List<object>)result["correspondencia"],
                row => Assert.Equal(new object[] { 1L, 1L }, (object[])row),
                row => Assert.Equal(new object[] { 2L, 20L }, (object[])row));
            Assert.Equal(new object[] { 3L }, (List<object>)result["anterior_sin_correspondencia"]);
            Assert.Equal(new object[] { 30L, 31L }, (List<object>)result["actual_sin_correspondencia"]);
            Assert.Equal(false, result["completo"]);
        }
    }
}
