// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Bimbridge.AgentHost.Bench;
using Bimbridge.Engine;
using Bimbridge.Engine.Workflow;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>0.9.0 — P0/P1 fixes from 0.8.0 (without Revit).</summary>
    public class Hardening090Tests
    {
        // ---------- P1.6: Hold does not turn itself off when it could not be saved ----------
        [Fact]
        public void FlagFile_UnsavedWrite_KeepsInMemoryValue_AfterRefresh()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "bb-ff-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            File.WriteAllText(tmp, "I am a file, not a folder");
            try
            {
                var f = new FlagFile(Path.Combine(tmp, "home"), "hold.flag");  // the folder cannot be created
                f.Set(true);
                Assert.True(f.Unsaved);
                Thread.Sleep(1100);   // the disk reread interval passes
                Assert.True(f.On, "the in-memory value must not drop by rereading a disk that was never written");
            }
            finally { File.Delete(tmp); }
        }

        [Fact]
        public void FlagFile_MissingHome_IsCreated_AndPersists()
        {
            var home = Path.Combine(Path.GetTempPath(), "bb-ff-" + Guid.NewGuid().ToString("N").Substring(0, 8), "does", "not-exist");
            try
            {
                var f = new FlagFile(home, "hold.flag");
                f.Set(true);
                Assert.False(f.Unsaved);
                Assert.True(new FlagFile(home, "hold.flag").On);
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(home)), true); } catch (IOException) { } }
        }

        // ---------- P0.2: profiles ----------
        private static readonly string LabDir = Path.Combine(Path.GetTempPath(), "bb-lab");

        [Fact]
        public void Profile_UnknownName_IsProduction()
        {
            Assert.False(ProfilePolicy.Parse("anything", null, true, null).IsLab);
            Assert.False(ProfilePolicy.Parse(null, null, true, null).IsLab);
            Assert.True(ProfilePolicy.Parse("experimental", new[] { LabDir }, false, null).IsLab);
        }

        [Fact]
        public void Profile_Production_RejectsExperimental_AllowsAvailable()
        {
            var p = new ProfilePolicy();
            Assert.NotNull(p.DenyRequest("geo", Permissions.Read, "archivo:c:\\x.rvt", CapabilityStates.Experimental));
            Assert.Null(p.DenyRequest("geo", Permissions.Read, "archivo:c:\\x.rvt", CapabilityStates.Available));
            Assert.Null(p.DenyRequest("geo", Permissions.Read, "archivo:c:\\x.rvt", CapabilityStates.NativeTestPending));
        }

        [Fact]
        public void Profile_Lab_OnlyLabFolders_NoCloudNoUncNoTraversal()
        {
            var lab = ProfilePolicy.Parse("lab", new[] { LabDir }, false, null);
            Assert.Null(lab.DenyRequest("query", Permissions.Read, "archivo:" + Path.Combine(LabDir, "copy.rvt"), CapabilityStates.Experimental));
            Assert.NotNull(lab.DenyRequest("query", Permissions.Read, "archivo:" + Path.Combine(Path.GetTempPath(), "other", "x.rvt"), CapabilityStates.Available));
            Assert.NotNull(lab.DenyRequest("query", Permissions.Read, "archivo:" + Path.Combine(LabDir, "..", "outside.rvt"), CapabilityStates.Available));
            Assert.NotNull(lab.DenyRequest("query", Permissions.Read, "nube:abc/def", CapabilityStates.Available));
            Assert.NotNull(lab.DenyRequest("query", Permissions.Read, "archivo:\\\\server\\share\\x.rvt", CapabilityStates.Available));
            // what does not touch a document: context, catalog, no document
            Assert.Null(lab.DenyRequest(Wire.OpContext, Permissions.Read, "nube:abc/def", CapabilityStates.Available));
            Assert.Null(lab.DenyRequest("query", Permissions.Read, null, CapabilityStates.Available));
            // lab without folders = nothing
            Assert.NotNull(ProfilePolicy.Parse("lab", null, false, null).DenyRequest("query", Permissions.Read, "archivo:" + Path.Combine(LabDir, "c.rvt"), CapabilityStates.Available));
        }

        [Fact]
        public void Profile_DynamicCode_ProductionNeedsShaAndScope()
        {
            const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string doc = "archivo:c:\\obra\\a.rvt";
            Assert.NotNull(new ProfilePolicy().DenyDynamicCode(sha, doc));
            Assert.Null(ProfilePolicy.Parse("production", null, false, new[] { sha }).DenyDynamicCode(sha, doc));
            Assert.Null(ProfilePolicy.Parse("production", null, false, new[] { sha + "@" + doc }).DenyDynamicCode(sha, doc));
            Assert.NotNull(ProfilePolicy.Parse("production", null, false, new[] { sha + "@archivo:c:\\obra\\b.rvt" }).DenyDynamicCode(sha, doc));
            Assert.NotNull(ProfilePolicy.Parse("production", null, false, new[] { sha }).DenyDynamicCode(sha.Replace('a', 'b'), doc));
            var lab = ProfilePolicy.Parse("lab", new[] { LabDir }, false, null);
            Assert.Null(lab.DenyDynamicCode(sha, "archivo:" + Path.Combine(LabDir, "c.rvt")));
            Assert.NotNull(lab.DenyDynamicCode(sha, doc));
        }

        [Fact]
        public void EngineConfig_ParsesProfile()
        {
            var dir = Path.Combine(Path.GetTempPath(), "bb-cfg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var f = Path.Combine(dir, "config.json");
                File.WriteAllText(f, "{\"profile\":\"lab\",\"lab\":{\"folders\":[\"" + LabDir.Replace("\\", "\\\\") + "\"],\"unsigned_updates\":true}}");
                var c = EngineConfig.Load(f);
                Assert.True(c.Profile.IsLab);
                Assert.True(c.Profile.LabUnsignedUpdates);
                Assert.Null(c.Profile.DenyUnsignedUpdate());
                File.WriteAllText(f, "{}");
                Assert.False(EngineConfig.Load(f).Profile.IsLab);
                Assert.NotNull(EngineConfig.Load(f).Profile.DenyUnsignedUpdate());
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---------- P0.3: provable independence ----------
        private static string Sha(char c) => new string(c, 64);

        private static IndependentVerifier.Input In(SourceRef exp, params SourceRef[] builds) => new IndependentVerifier.Input
        {
            ExpectedSource = exp, BuildSources = builds.ToList(),
            Expected = { new IndependentVerifier.Item { Key = "D-1" } }, Observed = { new IndependentVerifier.Item { Key = "D-1", ElementId = 1 } },
        };

        [Fact]
        public void Independence_Cases()
        {
            var pdf = new SourceRef { Id = "E-1.3.02@CCD7", Kind = SourceKinds.Pdf, Revision = "CCD7", Sha256 = Sha('a') };
            var markup = new SourceRef { Id = "markup-1", Kind = SourceKinds.Pdf, Sha256 = Sha('b') };
            // independent
            Assert.Null(IndependentVerifier.CheckIndependence(In(pdf, markup), out var proof));
            Assert.Equal("sha256+linaje", proof["method"]);
            // no sha
            Assert.Equal("independence_not_checkable", IndependentVerifier.CheckIndependence(In(new SourceRef { Id = "x", Kind = SourceKinds.Pdf }, markup), out _)?.State);
            // no declared build
            Assert.Equal("independence_not_checkable", IndependentVerifier.CheckIndependence(In(pdf), out _)?.State);
            // same content under another name
            Assert.Equal("not_independent", IndependentVerifier.CheckIndependence(In(pdf, new SourceRef { Id = "renamed", Sha256 = Sha('a') }), out _)?.State);
            // the build was derived from the expected source
            Assert.Equal("not_independent", IndependentVerifier.CheckIndependence(In(pdf, new SourceRef { Id = "plan", Sha256 = Sha('c'), DerivedFrom = { "sha256:" + Sha('a') } }), out _)?.State);
            // the expected source was derived from the build
            var derived = new SourceRef { Id = "table", Kind = SourceKinds.Pdf, Sha256 = Sha('d'), DerivedFrom = { "markup-1" } };
            Assert.Equal("not_independent", IndependentVerifier.CheckIndependence(In(derived, markup), out _)?.State);
            // common origin
            var a = new SourceRef { Id = "a", Kind = SourceKinds.Pdf, Sha256 = Sha('e'), DerivedFrom = { "root" } };
            Assert.Equal("not_independent", IndependentVerifier.CheckIndependence(In(a, new SourceRef { Id = "b", Sha256 = Sha('f'), DerivedFrom = { "root" } }), out _)?.State);
            // kinds that are never independent
            foreach (var k in new[] { SourceKinds.Inference, SourceKinds.Model, SourceKinds.ExecutionPlan })
                Assert.Equal("not_independent", IndependentVerifier.CheckIndependence(In(new SourceRef { Id = "z", Kind = k, Sha256 = Sha('9') }, markup), out _)?.State);
        }

        [Fact]
        public void Independence_ExpectedEqualsPlan_IsACopy()
        {
            var pdf = new SourceRef { Id = "E", Kind = SourceKinds.Pdf, Sha256 = Sha('a') };
            var inp = In(pdf, new SourceRef { Id = "plan", Sha256 = Sha('b') });
            inp.BuildPlanFingerprint = IndependentVerifier.Fingerprint(inp.Expected);
            Assert.Equal("not_independent", IndependentVerifier.CheckIndependence(inp, out _)?.State);
            var r = IndependentVerifier.Verify(inp);
            Assert.Equal("not_independent", r.State);
            // order-independent fingerprint
            var x = new[] { new IndependentVerifier.Item { Key = "a", X = 1 }, new IndependentVerifier.Item { Key = "b", Y = 2 } };
            Assert.Equal(IndependentVerifier.Fingerprint(x), IndependentVerifier.Fingerprint(Enumerable.Reverse(x)));
        }

        // ---------- P0.4: critical receipts with revision and provenance ----------
        private static DeliveryReceipt R(string stage, string rev, params (string, object)[] data)
        {
            var r = new DeliveryReceipt { Stage = stage, DocKey = "archivo:c:\\a.rvt", Revision = rev };
            foreach (var (k, v) in data) r.Data[k] = v;
            return r;
        }

        [Fact]
        public void Delivery_CriticalReceipts_NeedRevisionOriginAndObservedEvidence()
        {
            var t = new DeliveryTrack("e1", "archivo:c:\\a.rvt", new[] { DeliveryStages.Exported, DeliveryStages.ArtifactVerified, DeliveryStages.DeliveryConfirmed });
            Assert.Contains("without a model revision", t.Record(R(DeliveryStages.Exported, null, ("file", "a.pdf"), ("sha256", Sha('a')), ("origin", "export"), ("evidence", Confidence.Verified))));
            Assert.Contains("provenance", t.Record(R(DeliveryStages.Exported, "r5", ("file", "a.pdf"), ("sha256", Sha('a')), ("evidence", Confidence.Verified))));
            Assert.Contains("VERIFIED", t.Record(R(DeliveryStages.Exported, "r5", ("file", "a.pdf"), ("sha256", Sha('a')), ("origin", "export"), ("evidence", "REPORTADO"))));
            Assert.Null(t.Record(R(DeliveryStages.Exported, "r5", ("file", "a.pdf"), ("sha256", Sha('a')), ("origin", "export"), ("evidence", Confidence.Verified))));
            // verifying a revision other than the exported one
            Assert.Contains("revision r5 was exported", t.Record(R(DeliveryStages.ArtifactVerified, "r6", ("file", "a.pdf"), ("sha256", Sha('a')), ("pages", 3), ("origin", "pdf_info"), ("evidence", Confidence.Verified))));
            Assert.Null(t.Record(R(DeliveryStages.ArtifactVerified, "r5", ("file", "a.pdf"), ("sha256", Sha('a')), ("pages", 3), ("origin", "pdf_info"), ("evidence", Confidence.Verified))));
            // delivery is confirmed by a person, not by the agent
            Assert.Contains("person", t.Record(R(DeliveryStages.DeliveryConfirmed, "r5", ("confirms", "ok"), ("origin", "agent"))));
            Assert.Null(t.Record(R(DeliveryStages.DeliveryConfirmed, "r5", ("confirms", "ok"), ("origin", "usuario:operator"))));
            Assert.True(t.Complete);
        }

        // ---------- P1.5: degenerate 2D geometry ----------
        [Fact]
        public void Draw2D_RejectsCollinearOverlap_NonFinite_Huge()
        {
            Draw2D.Segments(new List<double[]> { new[] { 0.0, 0 }, new[] { 10.0, 0 }, new[] { 5.0, 0 } }, false, out var e1);
            Assert.Contains("collinear", e1 ?? "");
            Draw2D.Segments(new List<double[]> { new[] { 0.0, 0 }, new[] { double.NaN, 1 } }, false, out var e2);
            Assert.NotNull(e2);
            Draw2D.Segments(new List<double[]> { new[] { 0.0, 0 }, new[] { 1e9, 1 } }, false, out var e3);
            Assert.NotNull(e3);
            Draw2D.Segments(new List<double[]> { new[] { 0.0, 0 }, new[] { 10.0, 0 }, new[] { 10.0, 5 } }, false, out var ok);
            Assert.Null(ok);
        }

        [Fact]
        public void Draw2D_Classify_CrossTouchOverlap()
        {
            Draw2D.P P(double x, double y) => new Draw2D.P(x, y);
            Assert.Equal(Draw2D.Contact.Cross, Draw2D.Classify(P(0, 0), P(2, 2), P(0, 2), P(2, 0)));
            Assert.Equal(Draw2D.Contact.Touch, Draw2D.Classify(P(0, 0), P(2, 0), P(1, 0), P(1, 5)));
            Assert.Equal(Draw2D.Contact.Overlap, Draw2D.Classify(P(0, 0), P(4, 0), P(2, 0), P(6, 0)));
            Assert.Equal(Draw2D.Contact.None, Draw2D.Classify(P(0, 0), P(4, 0), P(5, 0), P(6, 0)));
            Assert.Equal(Draw2D.Contact.None, Draw2D.Classify(P(0, 0), P(4, 0), P(0, 1), P(4, 1)));
            // figure-eight loop (non-adjacent edges touch) → invalid
            Assert.NotNull(Draw2D.ValidateLoop(new List<double[]> { new[] { 0.0, 0 }, new[] { 2.0, 2 }, new[] { 2.0, 0 }, new[] { 0.0, 2 } }));
            Assert.Null(Draw2D.ValidateLoop(new List<double[]> { new[] { 0.0, 0 }, new[] { 2.0, 0 }, new[] { 2.0, 2 }, new[] { 0.0, 2 } }));
        }

        // ---------- P1.7: benchmark catalog references ----------
        [Fact]
        public void Benchmarks_ItemsExistInProcedure_AndMatchTheirScenario()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "tools", "acceptance", "0.8.0"))) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            var proc = File.ReadAllText(Path.Combine(dir, "tools", "acceptance", "0.8.0", "PROCEDURE_0.8.0.md"));
            var known = new HashSet<string>(Regex.Matches(proc, @"^\| (N8-\d\d)", RegexOptions.Multiline).Cast<Match>().Select(m => m.Groups[1].Value));
            Assert.Equal(13, known.Count);
            foreach (var b in Benchmarks.Catalog)
            {
                foreach (var it in b.Items) Assert.Contains(it, known);
                foreach (Match m in Regex.Matches(b.Harness ?? "", @"N8-\d\d")) Assert.Contains(m.Value, b.Items);
            }
            Assert.Equal(5, Benchmarks.Catalog.Count(b => b.Items.Length > 0));
            Assert.Equal(new[] { "N8-02", "N8-11" }, Benchmarks.Catalog.First(b => b.Id == "B1").Items);
            Assert.Equal(new[] { "N8-06", "N8-13" }, Benchmarks.Catalog.First(b => b.Id == "B8").Items);
        }
    }
}
