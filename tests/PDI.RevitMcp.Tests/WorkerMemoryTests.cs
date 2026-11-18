// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using BimBridge.PowerEngine;
using Xunit;
using static BimBridge.PowerEngine.PowerEngineContracts;
using Request = BimBridge.PowerEngine.PowerEngineContracts.Request;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Vigilancia de memoria del WorkerClient con un worker Python falso (contrato pe=1).</summary>
    public sealed class WorkerMemoryTests : IDisposable
    {
        // "comer": reserva ~200 MB y se cuelga; "comer_y_fallar": reserva ~200 MB y responde un error; "nada": responde OK.
        private const string Fake = @"import sys,json,time
keep=None
for l in sys.stdin:
    r=json.loads(l); j=r.get('trabajo'); i=r.get('id')
    def out(c,res=None,err=None):
        d={'pe':1,'id':i,'codigo':c,'resultado':res or {}}
        if err: d['error']=err
        print(json.dumps(d),flush=True)
    if j=='hola': out('OK',{'motor':'python','version':'falso','pe':1,'capacidades':[]})
    elif j=='salir': break
    elif j=='comer':
        keep=b'x'*(200<<20); time.sleep(120)
    elif j=='comer_y_fallar':
        b=b'x'*(200<<20); del b; out('INVALIDO',err='fallo a proposito')
    else: out('OK',{'ok':True})
";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb-mem-" + Guid.NewGuid().ToString("N"));
        private readonly string _script;

        public WorkerMemoryTests()
        {
            Directory.CreateDirectory(_dir);
            _script = Path.Combine(_dir, "falso_mem.py");
            File.WriteAllText(_script, Fake);
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

        private static Request Job(string job) => new Request { Job = job, Limits = { MaxMemoryMb = 64, TimeoutMs = 20000 } };

        [Fact]
        public void MemoriaExcedida_DuranteElTrabajo_SeCortaAntesDelTimeout_YReinicia()
        {
            using var w = new WorkerClient(EnginePython, PowerEngineEnv.Python, new[] { "-I", _script });
            var sw = Stopwatch.StartNew();
            var r = w.Call(Job("comer"));
            sw.Stop();
            Assert.Equal(Codes.MemoryLimit, r.Code);
            Assert.True(sw.ElapsedMilliseconds < 10000, "tardó " + sw.ElapsedMilliseconds + " ms");
            var ok = w.Call(Job("nada"));
            Assert.True(ok.Ok, ok.Code + " " + ok.Error);
            Assert.Equal(1, w.Restarts);
        }

        [Fact]
        public void PicoDeUnTrabajoFallido_NoCondenaAlSiguiente()
        {
            using var w = new WorkerClient(EnginePython, PowerEngineEnv.Python, new[] { "-I", _script });
            Assert.False(w.Call(Job("comer_y_fallar")).Ok);
            var ok = w.Call(Job("nada"));
            Assert.True(ok.Ok, ok.Code + " " + ok.Error);
        }
    }
}
