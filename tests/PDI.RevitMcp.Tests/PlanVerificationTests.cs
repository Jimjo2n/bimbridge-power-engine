// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using PDI.RevitMcp.Engine;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>AUD-09: un commit confirmado con diferencias en la relectura no es un éxito funcional pleno.</summary>
    public class PlanVerificationTests
    {
        [Fact]
        public void CommitAndPostconditions_AreSeparate()
        {
            Assert.Equal(PlanVerification.NotVerified, PlanVerification.Postconditions(null));
            Assert.Equal(PlanVerification.Met, PlanVerification.Postconditions(new Dictionary<string, object> { ["releidos"] = 3L, ["como_se_planifico"] = 3L }));
            Assert.Equal(PlanVerification.Differences, PlanVerification.Postconditions(new Dictionary<string, object> { ["releidos"] = 3L, ["como_se_planifico"] = 2L, ["distintos"] = 1L }));
            var creation = new Dictionary<string, object> { ["verificacion_creacion"] = new Dictionary<string, object> { ["revisados"] = 2L, ["como_se_pidio"] = 1L, ["problemas"] = new List<object> { "x" } } };
            Assert.Equal(PlanVerification.Differences, PlanVerification.Postconditions(creation));
            var allGood = new Dictionary<string, object> { ["verificacion_creacion"] = new Dictionary<string, object> { ["revisados"] = 2L, ["como_se_pidio"] = 2L } };
            Assert.Equal(PlanVerification.Met, PlanVerification.Postconditions(allGood));
        }
    }
}
