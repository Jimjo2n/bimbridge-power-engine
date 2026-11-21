// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using Bimbridge.Engine;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>AUD-09: a committed commit with differences on reread is not a full functional success.</summary>
    public class PlanVerificationTests
    {
        [Fact]
        public void CommitAndPostconditions_AreSeparate()
        {
            Assert.Equal(PlanVerification.NotVerified, PlanVerification.Postconditions(null));
            Assert.Equal(PlanVerification.Met, PlanVerification.Postconditions(new Dictionary<string, object> { ["reread"] = 3L, ["as_planned"] = 3L }));
            Assert.Equal(PlanVerification.Differences, PlanVerification.Postconditions(new Dictionary<string, object> { ["reread"] = 3L, ["as_planned"] = 2L, ["different_items"] = 1L }));
            var creation = new Dictionary<string, object> { ["creation_verification"] = new Dictionary<string, object> { ["reviewed"] = 2L, ["as_requested"] = 1L, ["problems"] = new List<object> { "x" } } };
            Assert.Equal(PlanVerification.Differences, PlanVerification.Postconditions(creation));
            var allGood = new Dictionary<string, object> { ["creation_verification"] = new Dictionary<string, object> { ["reviewed"] = 2L, ["as_requested"] = 2L } };
            Assert.Equal(PlanVerification.Met, PlanVerification.Postconditions(allGood));
        }
    }
}
