// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using PDI.RevitMcp.Engine.Qa;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    public class GroupMemberMapTests
    {
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
