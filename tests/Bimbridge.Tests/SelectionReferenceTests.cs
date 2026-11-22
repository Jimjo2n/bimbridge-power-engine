// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Engine;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>"Copy selection ID": a reference the user pastes into any AI conversation and every set-taking command resolves.</summary>
    public class SelectionReferenceTests
    {
        [Fact]
        public void SameElements_SameReference_ResolvedFromAnySession()
        {
            var a = NamedSets.Pin(new long[] { 1203, 1201, 1202, 1350 }, "d1", "Tower A");
            var b = NamedSets.Pin(new long[] { 1350, 1202, 1201, 1203 }, "d1", "Tower A");
            Assert.Equal(a, b);
            Assert.StartsWith(NamedSets.SelectionPrefix, a);

            // A session that never saved it still resolves it (the user pasted it into a new conversation).
            var other = new NamedSets();
            Assert.True(other.TryGet(a, "d1", out var ids, out var error), error);
            Assert.Equal(new long[] { 1201, 1202, 1203, 1350 }, ids);
        }

        [Fact]
        public void PinnedSelection_RejectedInAnotherDocument()
        {
            var r = NamedSets.Pin(new long[] { 77 }, "d1", "Tower A");
            Assert.False(new NamedSets().TryGet(r, "d2", out _, out var error));
            Assert.Contains("d1", error);
        }

        [Fact]
        public void SessionSetWithSameName_WinsOverPinned()
        {
            var r = NamedSets.Pin(new long[] { 5, 6 }, "d1", "Tower A");
            var s = new NamedSets();
            s.Save(r, new long[] { 9 }, "d1");
            Assert.True(s.TryGet(r, "d1", out var ids, out _));
            Assert.Equal(new long[] { 9 }, ids);
        }

        [Fact]
        public void Description_StandsOnItsOwn_AndOmitsIdsWhenLarge()
        {
            var small = NamedSets.DescribeSelection("$sel-abc123", "Tower A", "d1", new List<Tuple<string, List<long>>>
            {
                Tuple.Create("Conduits", new List<long> { 1201, 1202, 1203 }),
                Tuple.Create("Electrical Fixtures", new List<long> { 1350 }),
            });
            Assert.Contains("4 elements", small);
            Assert.Contains("\"Tower A\" (d1)", small);
            Assert.Contains("Conduits ×3 [1201, 1202, 1203]", small);
            Assert.Contains("ids=$sel-abc123", small);

            var big = NamedSets.DescribeSelection("$sel-abc123", "Tower A", "d1", new List<Tuple<string, List<long>>>
            {
                Tuple.Create("Lighting Fixtures", Enumerable.Range(1, NamedSets.MaxInlineIds + 1).Select(i => (long)i).ToList()),
            });
            Assert.DoesNotContain("[", big);
            Assert.Contains("ids are omitted", big);
            Assert.Contains("$sel-abc123", big);
        }
    }
}
