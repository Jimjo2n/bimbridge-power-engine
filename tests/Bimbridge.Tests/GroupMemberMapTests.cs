// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using Bimbridge.Engine.Qa;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    public class GroupMemberMapTests
    {
        [Fact]
        public void VariantOnlyChangesTheTwoSpecifiedTypesAndPreservesTheWallHostedMember()
        {
            var before = Json.ParseObject("""
                {"11":{"type":101,"pos":[1,2,3],"rot":0},"12":{"type":102,"pos":[2,2,3],"rot":90},
                 "13":{"type":103,"pos":[3,2,3],"rot":180,"host":901}}
                """);
            var after = Json.ParseObject(Json.Write(before));
            var changes = Json.ParseObject("{\"11\":201,\"12\":202}");
            foreach (var change in changes)
                ((Dictionary<string, object>)after[change.Key])["type"] = change.Value;
            Assert.True(GroupMemberMap.MatchesVariant(before, after, changes));
            ((Dictionary<string, object>)after["13"])["host"] = 902L;
            Assert.False(GroupMemberMap.MatchesVariant(before, after, changes));
        }

        private static object Row(long id, string uid, string signature) =>
            new Dictionary<string, object> { ["id"] = id, ["uid"] = uid, ["signature"] = signature };

        [Fact]
        public void PreservesStableIdentityAndOnlyMapsUnambiguousRebuiltMembers()
        {
            var previous = new List<object> { Row(1, "uid1", "ex"), Row(2, "uid2", "unique"), Row(3, "uid3", "double") };
            var current = new List<object> { Row(1, "uid1", "other"), Row(20, "uid20", "unique"), Row(30, "uid30", "double"), Row(31, "uid31", "double") };
            var result = GroupMemberMap.Compare(previous, current);
            Assert.Collection((List<object>)result["correspondence"],
                row => Assert.Equal(new object[] { 1L, 1L }, (object[])row),
                row => Assert.Equal(new object[] { 2L, 20L }, (object[])row));
            Assert.Equal(new object[] { 3L }, (List<object>)result["previous_without_match"]);
            Assert.Equal(new object[] { 30L, 31L }, (List<object>)result["current_without_match"]);
            Assert.Equal(false, result["complete"]);
        }
    }
}
