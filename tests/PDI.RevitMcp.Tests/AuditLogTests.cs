// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Linq;
using PDI.RevitMcp.Engine;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>0.6.1 fase 2: auditoría durable encadenada, separada de la telemetría.</summary>
    public class AuditLogTests
    {
        private static string NewPath() => Path.Combine(Path.GetTempPath(), "pdiaudit_" + Guid.NewGuid().ToString("N"), "auditoria.jsonl");

        [Fact]
        public void Audit_ChainVerifies_AndSurvivesReopen()
        {
            var p = NewPath();
            var a = new AuditLog(p);
            a.Append("s1", "exec_codigo", "ejecutar");
            a.Append("s1", "aplicar_cola", "aplicado");
            var b = new AuditLog(p); // otro proceso / reinicio continúa la cadena
            b.Append("s2", "exec_codigo", "consulta");
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            Assert.Equal(3, v.Lines);
        }

        [Fact]
        public void Audit_DeletedOrEditedLine_IsDetected()
        {
            var p = NewPath();
            var a = new AuditLog(p);
            for (var i = 0; i < 4; i++) a.Append("s", "op" + i, "ok");
            var lines = File.ReadAllLines(p).ToList();
            lines.RemoveAt(1);
            File.WriteAllLines(p, lines);
            Assert.False(AuditLog.Verify(p).Ok);

            var q = NewPath(); var c = new AuditLog(q);
            for (var i = 0; i < 3; i++) c.Append("s", "op" + i, "ok");
            File.WriteAllText(q, File.ReadAllText(q).Replace("\"op1\"", "\"opX\""));
            Assert.False(AuditLog.Verify(q).Ok);
        }

        [Fact]
        public void Audit_TruncatedTail_IsReported_NotHidden()
        {
            var p = NewPath(); var a = new AuditLog(p);
            a.Append("s", "op", "ok");
            File.AppendAllText(p, "{\"seq\":2,\"t\":\"x");
            var v = AuditLog.Verify(p);
            Assert.False(v.Ok);
        }

        [Fact]
        public void Audit_Unwritable_ThrowsVisible_AndCountsFailure()
        {
            var p = NewPath(); var a = new AuditLog(p);
            Directory.Delete(Path.GetDirectoryName(p), true);
            File.WriteAllText(Path.GetDirectoryName(p), "bloquea el directorio"); // la ruta del directorio ahora es un archivo
            Assert.Throws<JournalUnavailableException>(() => a.Append("s", "exec_codigo", "ejecutar"));
            Assert.Equal(1, a.Failures);
        }

        [Fact]
        public void Telemetry_DropsAreCounted_NotSilent()
        {
            var d = Path.Combine(Path.GetTempPath(), "pditel_" + Guid.NewGuid().ToString("N"));
            using var j = new Journal(d, 7);
            for (var i = 0; i < 50; i++) j.Log("s", "op", "ok", 0);
            Assert.True(j.Dropped >= 0);
            Assert.Equal(0, j.WriteErrors);
        }
    }
}

namespace PDI.RevitMcp.Tests
{
    public class PipeServerCheckTests
    {
        [Fact]
        public void PipeServer_MustBeAnnouncedRevitProcess()
        {
            Assert.Null(PDI.RevitMcp.Bridge.PipeServerCheck.Judge(100, 100, "Revit"));
            Assert.NotNull(PDI.RevitMcp.Bridge.PipeServerCheck.Judge(101, 100, "Revit"));
            Assert.NotNull(PDI.RevitMcp.Bridge.PipeServerCheck.Judge(100, 100, "evil"));
        }
    }
}
