// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Linq;
using PDI.RevitMcp.Engine.Coverage;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>0.9.1: índice de capacidades con búsqueda, filtros y paginación; contrato completo sólo bajo demanda.</summary>
    public class CapabilityIndexTests
    {
        private static CapabilityIndex Idx => CapabilityIndex.Default;

        [Fact]
        public void LookupById_AndContractOnDemand()
        {
            var c = Idx.Get("f20-circuit-create-001");
            Assert.NotNull(c);
            var k = CapabilityIndex.Contract(c);
            Assert.Equal("circuit_create", ((string)k["estrategia"]).Substring(6));
            Assert.Equal("DIRECT_REVIT_API", k["ejecutor"]);
            Assert.Equal("pendiente", k["verificacion_nativa"]);
            Assert.Null(Idx.Get("F99-NADA-001"));
        }

        [Fact]
        public void Filters_Combine_AndTextSearchIgnoresAccentsAndMatchesPrefixes()
        {
            var (byDomain, total) = Idx.Search(new CapabilityQuery { Domain = "F20", PageSize = 100 });
            Assert.Equal(CapabilityCatalog.All.Count(c => c.Domain == "F20"), total);
            Assert.All(byDomain, c => Assert.Equal("F20", c.Domain));
            var (hits, _) = Idx.Search(new CapabilityQuery { Text = "tuberia" });
            Assert.Contains(hits, c => c.Id == "F18-PIPE-CREATE-001");
            var (pre, _) = Idx.Search(new CapabilityQuery { Text = "circ" });
            Assert.Contains(pre, c => c.Id == "F20-CIRCUIT-CREATE-001");
            var (typedF20, _) = Idx.Search(new CapabilityQuery { Domain = "F20", Strategy = "typed" });
            Assert.All(typedF20, c => Assert.StartsWith("typed:", c.Strategy));
            var (none, n0) = Idx.Search(new CapabilityQuery { Domain = "F20", Text = "zzzzz" });
            Assert.Empty(none); Assert.Equal(0, n0);
            var (unsupported, _) = Idx.Search(new CapabilityQuery { Status = "UNSUPPORTED_BY_PUBLIC_API", PageSize = 100 });
            Assert.All(unsupported, c => Assert.Equal(CoverageStatus.UNSUPPORTED_BY_PUBLIC_API, c.Status));
        }

        [Fact]
        public void Pagination_IsStableAndBounded()
        {
            var (p1, total) = Idx.Search(new CapabilityQuery { PageSize = 10, Page = 1 });
            var (p2, _) = Idx.Search(new CapabilityQuery { PageSize = 10, Page = 2 });
            Assert.Equal(CapabilityCatalog.All.Count, total);
            Assert.Equal(10, p1.Count);
            Assert.Empty(p1.Select(c => c.Id).Intersect(p2.Select(c => c.Id)));
            var (big, _) = Idx.Search(new CapabilityQuery { PageSize = 100000 });
            Assert.True(big.Count <= CapabilityIndex.MaxPageSize);
        }

        [Fact]
        public void Index_RejectsDuplicateIds_AndHasAStableVersion()
        {
            var e = CapabilityCatalog.All[0];
            Assert.Throws<ArgumentException>(() => new CapabilityIndex(new[] { e, e }));
            Assert.Equal(new CapabilityIndex(CapabilityCatalog.All).Version, Idx.Version);
        }
    }
}
