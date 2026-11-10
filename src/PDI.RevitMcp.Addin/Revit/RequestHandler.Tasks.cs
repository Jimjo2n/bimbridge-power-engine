// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Addin.Revit.Families;
using PDI.RevitMcp.Addin.Scripting;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Tasks;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// 0.6.0 (§4–§6): tareas largas reanudables. Una sola orden (tarea iniciar) entrega una receta y sus argumentos; el motor la recorre
    /// por lotes en el hilo de Revit, deja checkpoints durables, respeta pausa/Retener/diálogos, guarda y exporta como barreras verificadas,
    /// y cualquier cliente (Codex o Claude) la continúa con su id y un resumen compacto. El código de la receta queda copiado en la tarea.
    /// </summary>
    public sealed partial class RequestHandler
    {
        private TaskManager _taskMgr;
        private RecipeRegistry _recipes;
        private string _tasksError;
        private readonly object _tasksInit = new object();

        /// <summary>Crea el gestor de tareas y engancha los eventos de guardado (evidencia de persistencia) y Guardar como (alias).</summary>
        internal void InitTasks()
        {
            lock (_tasksInit)
            {
                if (_taskMgr != null || _tasksError != null) return;
                try
                {
                    var store = new TaskStore();
                    _recipes = new RecipeRegistry(RecipeRegistry.UserFolder, Path.Combine(ScriptHost.AddinDir, "recetas"));
                    _taskMgr = new TaskManager(store, DocRegistry.Instance.InstanceId, t => new RevitTaskBackend(this, t))
                    {
                        // R03: sin almacén de concesiones, el motor niega toda escritura (DenyWritesAuthority).
                        Authority = Ui.Runtime.Grants != null ? (ITaskAuthority)new GrantAuthority(Ui.Runtime.Grants) : DenyWritesAuthority.Instance,
                        Audit = Ui.Runtime.Audit,
                    };
                    _tracker.Saved += (doc, saveAs) => OnPersisted(doc, saveAs ? "guardar_como" : "guardado");
                    _tracker.Synced += doc => OnPersisted(doc, "sync");
                }
                catch (Exception ex) { _tasksError = ex.GetType().Name + ": " + ex.Message; }
            }
        }

        /// <summary>Evento de Revit (hilo de Revit): sólo se toman los datos baratos; el registro en disco va fuera del hilo.</summary>
        private void OnPersisted(Document doc, string kind)
        {
            try
            {
                var id = DocRegistry.Instance.IdentityOf(doc);
                if (id == null) return;
                var ev = new PersistEvidence { Kind = kind, When = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
                try
                {
                    ev.File = doc.PathName;
                    if (!string.IsNullOrEmpty(ev.File) && File.Exists(ev.File))
                    {
                        var fi = new FileInfo(ev.File);
                        ev.Bytes = fi.Length;
                        ev.Mtime = fi.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture);
                    }
                }
                catch (Exception) { }
                var key = id.Key;
                var gen = id.Generation;
                var mgr = _taskMgr;
                if (mgr != null) Task.Run(() => { try { mgr.RecordPersist(key, gen, ev); } catch (Exception) { } });
            }
            catch (Exception) { }
        }

        private void OnRekeyed(string oldKey, string newKey)
        {
            try { _ops?.Alias(oldKey, newKey); } catch (Exception) { }
            var mgr = _taskMgr;
            if (mgr != null) Task.Run(() => { try { mgr.Rekey(oldKey, newKey); } catch (Exception) { } });
        }

        private static string ResponseStatusOf(string taskState)
        {
            switch (taskState)
            {
                case TaskState.Aplicada: return Status.Ok;
                case TaskState.AplicadaConDiferencias: return Status.ConDiferencias;
                case TaskState.Parcial: return Status.Parcial;
                case TaskState.Interrumpida: return Status.Interrumpido;
                case TaskState.Cancelada: return Status.Cancelado;
                case TaskState.CancelacionSolicitada: return Status.CancelacionSolicitada;
                case TaskState.EsperandoContexto: return Status.EsperandoContexto;
                default: return Status.EnProceso;
            }
        }

        private Response TaskResponse(Request request, TaskRecord t, bool continuation, Dictionary<string, object> extra = null)
        {
            _taskMgr.Get(t.Id, out var units);
            var d = TaskSummary.Status(t, units, continuation);
            if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
            return new Response { Id = request.Id, Estado = ResponseStatusOf(t.State), Datos = d };
        }

        // ---------------- op tarea ----------------

        private async Task<Response> TaskOp(Session session, Request request, CancellationToken ct)
        {
            InitTasks();
            if (_taskMgr == null)
                return Response.Fail(request.Id, Status.DiarioNoDisponible, "las tareas no están disponibles: no se pudo preparar su carpeta (" + _tasksError + ")");
            var a = request.Args;
            var verb = OpSemantics.TaskVerb(a);
            var id = Arg.GetString(a, "id") ?? Arg.GetString(a, Wire.TaskArg);
            var who = session.ClientName ?? session.Id;
            if (verb == "recetas") return Recipes(request);
            if (verb == "lista") return TaskList(request);
            if (verb == "iniciar") return await StartTask(session, request, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(id)) return Response.Fail(request.Id, Status.Invalido, "falta id= de la tarea (tarea lista muestra las de este equipo)");
            switch (verb)
            {
                case "estado":
                case "resumen":
                {
                    var t = _taskMgr.Get(id, out _);
                    if (t == null) return Response.Fail(request.Id, Status.Invalido, "no existe la tarea " + id);
                    return TaskResponse(request, t, verb == "resumen");
                }
                case "esperar":
                {
                    var since = Arg.GetLong(a, "desde") ?? -1;
                    var max = Math.Max(1, Math.Min(120, Arg.GetLong(a, "max") ?? 30));
                    var t = await _taskMgr.WaitAsync(id, since, TimeSpan.FromSeconds(max), ct).ConfigureAwait(false);
                    if (t == null) return Response.Fail(request.Id, Status.Invalido, "no existe la tarea " + id);
                    return TaskResponse(request, t, false);
                }
                case "cancelar":
                {
                    var t = _taskMgr.Cancel(id);
                    if (t == null) return Response.Fail(request.Id, Status.Invalido, "no existe la tarea " + id);
                    return TaskResponse(request, t, false);
                }
                case "reanudar":
                {
                    if (!_config.AllowsExec(true)) return Response.Fail(request.Id, Status.Rechazado, "este Revit no permite ejecutar recetas (runner/niveles de la configuración del MCP)");
                    var r = _taskMgr.Resume(id, who, PolicyFrom(a, onlyGiven: true));
                    if (r.Error != null) return Response.Fail(request.Id, r.ErrorStatus ?? Status.Error, r.Error);
                    var t = await WaitBriefly(r.Task, a, ct).ConfigureAwait(false);
                    return TaskResponse(request, t, false, r.AlreadyRunning ? new Dictionary<string, object> { ["ya_en_curso"] = true } : null);
                }
                case "reconciliar":
                {
                    var dec = (Arg.GetString(a, "decision") ?? "").Trim().ToLowerInvariant();
                    bool applied;
                    if (dec == "aplicada" || dec == "aplicado" || dec == "si") applied = true;
                    else if (dec == "no_aplicada" || dec == "no_aplicado" || dec == "no") applied = false;
                    else return Response.Fail(request.Id, Status.Invalido, "reconciliar necesita decision=aplicada (ya está en el modelo: no se repite) o decision=no_aplicada (se ejecutará al reanudar)");
                    var err = _taskMgr.Reconcile(id, Arg.GetStringList(a, "claves"), applied, Arg.GetString(a, "nota"), who);
                    if (err != null) return Response.Fail(request.Id, Status.Invalido, err);
                    return TaskResponse(request, _taskMgr.Get(id, out _), false);
                }
                case "anotar":
                {
                    var text = Arg.GetString(a, "texto");
                    if (string.IsNullOrWhiteSpace(text)) return Response.Fail(request.Id, Status.Invalido, "anotar necesita texto=");
                    var err = _taskMgr.Note(id, text, who);
                    if (err != null) return Response.Fail(request.Id, Status.Invalido, err);
                    return TaskResponse(request, _taskMgr.Get(id, out _), false);
                }
                case "unidades":
                {
                    var rows = _taskMgr.UnitRows(id, Arg.GetString(a, "estado"), (int)(Arg.GetLong(a, "desde") ?? 0), (int)(Arg.GetLong(a, "n") ?? 50), out var total);
                    return new Response
                    {
                        Id = request.Id,
                        Datos = new Dictionary<string, object>
                        {
                            ["cols"] = new List<object> { "clave", "estado", "intentos", "ms", "error", "creados" },
                            ["rows"] = rows.Cast<object>().ToList(), ["total"] = (long)total,
                        },
                    };
                }
                case "olvidar":
                {
                    var err = _taskMgr.Archive(id);
                    if (err != null) return Response.Fail(request.Id, Status.Invalido, err);
                    return new Response { Id = request.Id, Datos = new Dictionary<string, object> { ["tarea"] = id, ["archivada"] = true } };
                }
                default:
                    return Response.Fail(request.Id, Status.Invalido, "tarea: iniciar, estado, resumen, esperar, cancelar, reanudar, reconciliar, anotar, lista, unidades, recetas u olvidar");
            }
        }

        /// <summary>Espera corta tras iniciar/reanudar (espera_s, por defecto 10 s): las tareas pequeñas vuelven ya terminadas en UNA llamada.</summary>
        private async Task<TaskRecord> WaitBriefly(TaskRecord t, IDictionary<string, object> a, CancellationToken ct)
        {
            var wait = Math.Max(0, Math.Min(120, Arg.GetLong(a, "espera_s") ?? 10));
            var until = DateTime.UtcNow.AddSeconds(wait);
            var cur = t;
            while (DateTime.UtcNow < until && _taskMgr.IsRunning(t.Id))
            {
                var left = until - DateTime.UtcNow;
                cur = await _taskMgr.WaitAsync(t.Id, cur.Rev, left, ct).ConfigureAwait(false) ?? cur;
            }
            return _taskMgr.Get(t.Id, out _) ?? cur;
        }

        private static readonly string[] PolicyKeys = { "guardar_cada", "al_final", "en_error", "lote_max", "avisos", "tx" };

        private static Dictionary<string, object> PolicyFrom(IDictionary<string, object> a, bool onlyGiven)
        {
            var p = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var k in PolicyKeys)
                if (a.TryGetValue(k, out var v) && v != null) p[k] = v;
            return onlyGiven && p.Count == 0 ? null : p;
        }

        private async Task<Response> StartTask(Session session, Request request, CancellationToken ct)
        {
            var a = request.Args;
            if (!_config.AllowsExec(true))
                return Response.Fail(request.Id, Status.Rechazado, "este Revit no permite ejecutar recetas que cambien el modelo (runner/niveles " + _config.Levels + " en la configuración del MCP). Dígaselo al usuario.");
            var target = DocTarget.Ambient.Value;
            if (target == null) return Response.Fail(request.Id, Status.DestinoNoDisponible, "no hay un documento abierto (o indique doc=)");
            if (WillHold(target.Key))
                return Response.Fail(request.Id, Status.NecesitaUsuario, "Retener está activo: una tarea no tiene un diff que encolar. Pida al usuario que cambie el Modo a En vivo para esta tarea.");

            // Receta: registrada por nombre (receta=), archivo .cs, script guardado (nombre=) o código en línea.
            string code, source, recipeName, recipeVersion = null, recipeHash;
            var installedRecipe = false;
            var reg = Arg.GetString(a, "receta");
            if (reg != null)
            {
                var e = _recipes.Find(reg);
                if (e == null)
                    return Response.Fail(request.Id, Status.Invalido, "no hay una receta '" + reg + "' (tarea recetas lista las disponibles; las del usuario van en " + RecipeRegistry.UserFolder + ")");
                code = File.ReadAllText(e.Path);
                source = e.Origin + ":" + e.Path;
                installedRecipe = e.Origin == "instalada";
                recipeName = e.Name;
                recipeVersion = e.Version;
            }
            else
            {
                // Código propio del agente (codigo=, archivo=, nombre=) también es inyección de C#: respeta el bloqueo. Las recetas registradas no.
                var blockedTask = DynamicCodePolicy.Decide(Ui.Runtime.BlockDynamicCode);
                if (blockedTask != null) return Response.Fail(request.Id, Status.Rechazado, blockedTask);
                code = ResolveCode(a, out source, out var codeError);
                if (code == null) return Response.Fail(request.Id, Status.Invalido, codeError ?? "falta la receta: receta=nombre, archivo=ruta.cs, nombre= de un script guardado o codigo=");
                recipeName = Arg.GetString(a, "nombre") ?? (Arg.GetString(a, "archivo") is string f ? Path.GetFileNameWithoutExtension(f) : "codigo");
            }
            recipeHash = Home.Sha256Hex(code);

            // R04: una tarea corre sola → perfil restringido también para las DLL extra (se decide antes de cargarlas).
            var refs = Arg.GetStringList(a, "refs");
            var refDenied = ReferencePolicy.Denied(ExecutionProfile.RestrictedAutonomous, refs,
                f => { try { return Home.FileSha256(f); } catch (Exception) { return null; } }, ReferencePolicy.LoadTrusted(), ScriptHost.AddinDir);
            if (refDenied.Count > 0)
                return Response.Fail(request.Id, Status.Rechazado, "referencia_no_confiable: " + string.Join("; ", refDenied.Take(5)) + ". Añada su SHA-256 a " + ReferencePolicy.TrustedListPath + " (lo decide una persona).");
            // Compilar ya (fuera del hilo de Revit) para fallar rápido; la tarea guarda la copia exacta del código.
            var compiled = await Task.Run(() =>
            {
                var c = ScriptHost.Compile(code, _revitVersion, _addinVersion, refs, out var err);
                return Tuple.Create(c, err);
            }, ct).ConfigureAwait(false);
            if (compiled.Item1 == null) return Response.Fail(request.Id, Status.Invalido, compiled.Item2);
            // R03: lo que el agente pide (capacidades=) sólo se ANOTA como solicitado. Lo que nunca se concede se rechaza ya.
            var requested = Arg.GetStringList(a, "capacidades").Where(c => Capability.Grantable.Contains(c)).ToList();
            var denied = CapabilityPolicy.Denied(ExecutionProfile.RestrictedAutonomous, compiled.Item1.Capabilities, Capability.Grantable);
            if (denied.Count > 0) return Response.Fail(request.Id, Status.Rechazado, CapabilityPolicy.Explain(denied));
            var writes = true;
            if (compiled.Item1.RecipeType != null)
            {
                // rc3: NO se instancia (el constructor es código de la receta y aún no hay autorización). Metadatos del código fuente;
                // si no son constantes, se asume lo conservador: escribe (revit_modificar).
                recipeVersion = recipeVersion ?? ScriptHost.RecipeConstant(code, "Version") ?? "1";
                writes = ScriptHost.RecipeConstant(code, "Transaccion") != "Lectura";
                if (compiled.Item1.RecipeType.GetConstructor(Type.EmptyTypes) == null)
                    return Response.Fail(request.Id, Status.Invalido, "la receta necesita un constructor público sin argumentos");
            }

            var userArgs = a.TryGetValue("args", out var ua) && ua is Dictionary<string, object> ud ? ud : new Dictionary<string, object>(StringComparer.Ordinal);
            var policy = PolicyFrom(a, onlyGiven: false);
            if (refs.Count > 0) policy["refs"] = refs.Cast<object>().ToList();
            // Capacidades que las unidades necesitan (las barreras se comprueban al llegar a ellas): se validan en cada punto de ejecución.
            var requires = compiled.Item1.Capabilities.Select(CapabilityPolicy.Kind).Where(k => Capability.Grantable.Contains(k)).ToList();
            if (writes) requires.Add(Capability.Modificar);
            // R04: el conjunto cerrado de lo que corre solo son las recetas instaladas (verificadas por el instalador). Cualquier otro
            // código autónomo —también de sólo lectura— exige que una persona apruebe ESA tarea (que fija el SHA-256 de la receta).
            if (!installedRecipe) requires.Add(Capability.RecetaExterna);
            requires = requires.Distinct().ToList();
            policy["requiere"] = requires.Cast<object>().ToList();
            if (requested.Count > 0) policy["solicitadas"] = requested.Cast<object>().ToList();
            var content = OpSemantics.Canonical(new Dictionary<string, object>
            {
                ["receta"] = recipeHash, ["args"] = userArgs, ["doc"] = target.Key, ["huella_doc"] = target.Print ?? "", ["refs"] = refs.Cast<object>().ToList(),
                ["requiere"] = requires.Cast<object>().ToList(),
            });
            var draft = new TaskRecord
            {
                Id = TaskStore.SafeId(Arg.GetString(a, "id") ?? Arg.GetString(a, Wire.TaskArg) ?? TaskStore.NewId(DateTime.UtcNow)),
                Objective = Arg.GetString(a, "objetivo"),
                Scope = Arg.GetString(a, "alcance"),
                RecipeName = recipeName,
                RecipeVersion = recipeVersion ?? "1",
                RecipeHash = recipeHash,
                RecipeSource = source,
                ContentHash = Home.Sha256Hex(content).Substring(0, 16),
                Args = userArgs,
                Docs = new List<TaskDoc> { new TaskDoc { Key = target.Key, Print = target.Print, Generation = target.Generation, Handle = target.Id, Title = target.Title } },
                Policy = policy,
            };
            var r = _taskMgr.Start(draft, code, session.ClientName ?? session.Id);
            if (r.Error != null) return Response.Fail(request.Id, r.ErrorStatus ?? Status.Error, r.Error);
            var t = await WaitBriefly(r.Task, a, ct).ConfigureAwait(false);
            var extra = new Dictionary<string, object>
            {
                ["receta"] = recipeName + " v" + draft.RecipeVersion, ["huella"] = draft.ContentHash,
                ["compilar_ms"] = compiled.Item1.FromCache ? 0L : compiled.Item1.CompileMs,
            };
            if (!r.Created) extra["ya_existia"] = true;
            if (r.AlreadyRunning) extra["ya_en_curso"] = true;
            // Lo que falta autorizar se deja como SOLICITUD para una persona (botón «Autorizar» en Revit). El agente no puede aprobarla.
            var need = requires.Concat(requested).Distinct().ToList();
            if (Ui.Runtime.Grants != null && need.Count > 0)
            {
                var missing = need.Where(c => Ui.Runtime.Grants.Check(draft.Id, target.Key, target.Print, c, DateTime.UtcNow) != null).ToList();
                if (missing.Count > 0)
                {
                    Ui.Runtime.Grants.Request(draft.Id, target.Key, target.Print, target.Title, missing, session.ClientName ?? session.Id, draft.Objective, DateTime.UtcNow);
                    Ui.Runtime.MarkDirty();
                    extra["autorizacion"] = new Dictionary<string, object>
                    {
                        ["pendiente"] = missing.Cast<object>().ToList(),
                        ["como"] = "una persona debe pulsar «Autorizar» en la pestaña PDI de Revit; después: tarea reanudar " + draft.Id,
                    };
                }
            }
            return TaskResponse(request, t, false, extra);
        }

        private Response Recipes(Request request)
        {
            var rows = _recipes.List().Select(e => (object)new object[] { e.Name, e.Version, e.Origin, e.Description, e.Hash.Substring(0, 12) }).ToList();
            return new Response
            {
                Id = request.Id,
                Datos = new Dictionary<string, object>
                {
                    ["cols"] = new List<object> { "nombre", "version", "origen", "descripcion", "sha" }, ["rows"] = rows,
                    ["carpeta_usuario"] = RecipeRegistry.UserFolder,
                },
            };
        }

        private Response TaskList(Request request)
        {
            var rows = _taskMgr.List().OrderByDescending(t => t.Updated ?? t.Created, StringComparer.Ordinal).Take(50)
                .Select(t => (object)new object[] { t.Id, t.State, t.RecipeName, t.MainDoc?.Title, t.UnitsTotal, t.ConfirmSeq, t.Updated ?? t.Created }).ToList();
            return new Response
            {
                Id = request.Id,
                Datos = new Dictionary<string, object>
                {
                    ["cols"] = new List<object> { "id", "estado", "receta", "doc", "unidades", "confirmadas", "actualizada" }, ["rows"] = rows,
                },
            };
        }

        // ---------------- backend de Revit ----------------

        /// <summary>Un script con Run(ctx) usado como tarea: una sola unidad.</summary>
        private sealed class RunAsRecipe : Receta
        {
            private readonly System.Reflection.MethodInfo _run;
            public RunAsRecipe(System.Reflection.MethodInfo run) { _run = run; }
            public override IEnumerable<Unidad> Unidades(ScriptContext ctx) { yield return new Unidad("unica"); }
            public override object Ejecutar(ScriptContext ctx, Unidad u)
            {
                try { return _run.Invoke(null, new object[] { ctx }); }
                catch (System.Reflection.TargetInvocationException tie) { throw tie.InnerException ?? tie; }
            }
        }

        /// <summary>
        /// Lado Revit del motor (§4): enumera y ejecuta unidades en el hilo de Revit con una transacción por unidad, detecta conflictos con
        /// cambios ajenos (autor distinto de la tarea), verifica lo dudoso por UniqueId o con Verificar de la receta, y corre las barreras
        /// guardar/sync/pdf con verificación de archivos. Toma el documento (DocLocks) mientras la tarea corre y lo suelta al terminar.
        /// </summary>
        private sealed class RevitTaskBackend : ITaskBackend, IDisposable
        {
            private readonly RequestHandler _h;
            private readonly TaskRecord _t;
            private readonly string _owner;
            private readonly NamedSets _sets = new NamedSets();
            private readonly ScriptContext.TaskSink _sink;
            private bool _locked;
            private Receta _recipe;
            private ScriptHost.Compiled _compiled;
            private readonly RecipeGate _gate;
            private UIApplication _gateApp;
            private string _compileError;
            private IEnumerator<Unidad> _enum;
            private bool _enumDone;
            private bool _prepared;
            private readonly List<Unidad> _found = new List<Unidad>();
            private readonly Dictionary<string, Unidad> _byKey = new Dictionary<string, Unidad>(StringComparer.Ordinal);
            private readonly Dictionary<string, Dictionary<long, long>> _snap = new Dictionary<string, Dictionary<long, long>>(StringComparer.Ordinal);
            private static readonly TimeSpan BusyAfter = TimeSpan.FromSeconds(20);

            public RevitTaskBackend(RequestHandler h, TaskRecord t)
            {
                _h = h;
                _t = t;
                _owner = "tarea:" + t.Id;
                _sink = new ScriptContext.TaskSink { TaskId = t.Id, ArtifactsDir = h._taskMgr.Store.ArtifactsDir(t.Id) };
                // rc3: frontera de recetas. Autoridad = último punto seguro del motor (leases + concesiones); sin motor en marcha, nada.
                _gate = new RecipeGate(() => _t.Preflight != null ? _t.Preflight() : "la tarea no está en ejecución", WriteCounter);
            }

            /// <summary>Contador de escrituras de todos los documentos abiertos: revisión del tracker (sube con cada transacción
            /// confirmada) + 1e12 por documento con transacción abierta. Sólo se lee en el hilo de Revit (dentro de OnRevit).</summary>
            private long WriteCounter()
            {
                long n = 0;
                if (_gateApp == null) return 0;
                foreach (Document d in _gateApp.Application.Documents)
                {
                    if (d.IsLinked) continue;
                    n += _h._tracker.For(d)?.Rev ?? 0;
                    if (d.IsModifiable) n += 1000000000000L;
                }
                return n;
            }

            /// <summary>Ejecuta código de la receta dentro de la frontera (en el hilo de Revit). null = bien; si no, el motivo.</summary>
            private string Guard(UIApplication app, string phase, Action body)
            {
                _gateApp = app;
                try { return _gate.Run(phase, body); }
                finally { _gateApp = null; }
            }

            /// <summary>Crea la instancia de la receta (constructor e inicializadores) sólo tras autorizar, en el hilo de Revit.</summary>
            private string EnsureInstance(UIApplication app)
            {
                if (_recipe != null) return null;
                if (_compiled == null) return _compileError ?? "receta no compilada";
                var c = _compiled;
                Receta r = null;
                var why = Guard(app, RecipeGate.Phase.Constructor, () => r = c.RecipeType != null ? (Receta)Activator.CreateInstance(c.RecipeType) : new RunAsRecipe(c.Run));
                if (why != null) return why;
                // rc3: la política real debe cuadrar con lo que se autorizó al iniciar (metadatos leídos del código). Si la receta escribe
                // pero la tarea se autorizó como de lectura (sin revit_modificar), no sigue.
                PoliticaTx tx = PoliticaTx.PorUnidad;
                why = Guard(app, RecipeGate.Phase.Constructor, () => tx = r.Transaccion);
                if (why != null) return why;
                var req = _t.Policy.TryGetValue("requiere", out var rq) && rq is List<object> rl ? rl.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList() : null;
                if (tx != PoliticaTx.Lectura && req != null && !req.Contains(Capability.Modificar))
                    return "la receta declara Transaccion=" + tx + " pero la tarea se autorizó como de sólo lectura: no se ejecuta (reinicie la tarea para pedir revit_modificar)";
                _recipe = r;
                return null;
            }

            public void Dispose()
            {
                if (_locked && _t.MainDoc?.Key != null) _h.Locks.Release(new[] { _t.MainDoc.Key }, _owner);
                _locked = false;
                try { _enum?.Dispose(); } catch (Exception) { }
            }

            private DocRegistry.Info Info => DocRegistry.Instance.Snapshot.FirstOrDefault(i => i.Key == _t.MainDoc?.Key);

            public string CurrentGeneration(TaskRecord task) => Info?.Generation;

            public BackendResult<bool> CheckContext(TaskRecord task)
            {
                if (!_h._config.AllowsExec(true)) return BackendResult<bool>.Fail("la configuración del MCP ya no permite ejecutar recetas en este Revit");
                if (_h._pause != null && _h._pause.On) return BackendResult<bool>.Waiting("el usuario pausó la IA desde Revit", "el usuario pulsa Reanudar IA; la tarea sigue sola");
                var key = _t.MainDoc?.Key;
                if (_h.WillHold(key)) return BackendResult<bool>.Waiting("Retener está activo (o hay planes en la cola de este documento)", "el usuario cambia el Modo a En vivo");
                var info = Info;
                if (info == null) return BackendResult<bool>.Waiting("el documento " + (_t.MainDoc?.Title ?? key) + " no está abierto en este Revit", "abrirlo; la tarea sigue sola");
                if (_t.MainDoc.Print != null && info.Print != null && info.Print != _t.MainDoc.Print)
                    return BackendResult<bool>.Fail("el archivo " + key + " ahora contiene OTRO modelo (huella " + info.Print + " ≠ " + _t.MainDoc.Print + "): la tarea no continúa sobre un documento distinto");
                if (!_locked)
                {
                    if (!_h.Locks.TryAcquire(new[] { key }, _owner, "tarea", _t.Id, out var blocking))
                        return BackendResult<bool>.Waiting("el documento lo tiene " + blocking.Describe(), "esperar a que termine");
                    _locked = true;
                }
                return BackendResult<bool>.Ok(true);
            }

            private Task<WorkResult> OnRevit(Func<UIApplication, Stopwatch, int, bool> step, CancellationToken ct) =>
                _h._main.RunAsync((app, sw, budget) =>
                {
                    using (DocTarget.Enter(null)) return step(app, sw, budget);
                }, false, BusyAfter, ct);

            private static BackendResult<T> FromWork<T>(WorkResult r)
            {
                if (r.Kind == WorkResult.KindBusy) return BackendResult<T>.Waiting("Revit está ocupado (un comando, un diálogo o un modo de edición abierto)", "terminar o cerrar lo que esté abierto en Revit");
                if (r.Kind == WorkResult.KindCancelled) return BackendResult<T>.Waiting("la visita a Revit se canceló", null);
                return BackendResult<T>.Fail("error interno en el hilo de Revit: " + (r.Error?.Message ?? r.Kind));
            }

            private Document FindDoc(UIApplication app)
            {
                foreach (Document d in app.Application.Documents)
                {
                    if (d.IsLinked) continue;
                    var id = DocRegistry.Instance.IdentityOf(d);
                    if (id != null && id.Key == _t.MainDoc.Key) return d;
                }
                return null;
            }

            private ScriptContext Ctx(UIApplication app, Document doc, CancellationToken ct)
            {
                var ctx = new ScriptContext(app, doc, _t.Args, OpSemantics.ModeRun, "tarea", ct, _sets, DocRegistry.Instance.IdOf(doc)) { Sink = _sink };
                return ctx;
            }

            /// <summary>Compila la copia de la receta guardada en la tarea (no el archivo actual: si cambió, la tarea sigue con lo suyo).</summary>
            private bool EnsureRecipe(out string error)
            {
                error = null;
                if (_compiled != null) return true;
                if (_compileError != null) { error = _compileError; return false; }
                var code = _h._taskMgr.Store.RecipeCode(_t.Id);
                if (code == null) { _compileError = "falta la copia de la receta en la carpeta de la tarea"; error = _compileError; return false; }
                if (Home.Sha256Hex(code) != _t.RecipeHash) { _compileError = "la copia de la receta de la tarea no coincide con su huella (archivo alterado)"; error = _compileError; return false; }
                var refs = _t.Policy.TryGetValue("refs", out var rv) && rv is List<object> rl ? rl.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList() : new List<string>();
                var c = ScriptHost.Compile(code, _h._revitVersion, _h._addinVersion, refs, out var err);
                if (c == null) { _compileError = "la receta ya no compila en este Revit: " + err; error = _compileError; return false; }
                // Lo concedible lo decide la autoridad en cada punto de ejecución; aquí sólo se corta lo que nunca se concede.
                var denied = CapabilityPolicy.Denied(ExecutionProfile.RestrictedAutonomous, c.Capabilities, Capability.Grantable);
                if (denied.Count > 0) { _compileError = CapabilityPolicy.Explain(denied); error = _compileError; return false; }
                // rc3: aquí NO se instancia: el constructor es código de la receta y sólo corre tras autorizar (EnsureInstance).
                _compiled = c;
                return true;
            }

            private PoliticaTx TxPolicy
            {
                get
                {
                    var p = (_t.PolicyString("tx") ?? "").ToLowerInvariant();
                    if (p == "ninguna") return PoliticaTx.Ninguna;
                    if (p == "lectura") return PoliticaTx.Lectura;
                    return _recipe?.Transaccion ?? PoliticaTx.PorUnidad;
                }
            }

            /// <summary>Recorre la enumeración perezosa de la receta por tramos (varias visitas si hace falta).</summary>
            private async Task<BackendResult<bool>> EnsureEnumerated(CancellationToken ct)
            {
                if (!EnsureRecipe(out var err)) return BackendResult<bool>.Fail(err);
                while (!_enumDone)
                {
                    string fail = null;
                    var r = await OnRevit((app, watch, budget) =>
                    {
                        var doc = FindDoc(app);
                        if (doc == null) { fail = "cerrado"; return true; }
                        var ctx = Ctx(app, doc, ct);
                        var reg = _h._tracker.For(doc);
                        try
                        {
                            var g = EnsureInstance(app);
                            if (g != null) { fail = g; return true; }
                            if (!_prepared)
                            {
                                g = Guard(app, RecipeGate.Phase.Preparar, () => _recipe.Preparar(ctx));
                                if (g != null) { fail = g; return true; }
                                _prepared = true;
                            }
                            if (_enum == null)
                            {
                                g = Guard(app, RecipeGate.Phase.Enumerar, () => _enum = _recipe.Unidades(ctx).GetEnumerator());
                                if (g != null) { fail = g; return true; }
                            }
                            var limit = Math.Max(20, budget);
                            while (watch.ElapsedMilliseconds < limit)
                            {
                                var more = false;
                                g = Guard(app, RecipeGate.Phase.Enumerar, () => more = _enum.MoveNext());
                                if (g != null) { fail = g; return true; }
                                if (!more) { _enumDone = true; break; }
                                var u = _enum.Current;
                                if (u == null) continue;
                                _found.Add(u);
                                if (!_byKey.ContainsKey(u.Clave)) _byKey[u.Clave] = u;
                                if (u.Ids.Count > 0 && reg != null) _snap[u.Clave] = reg.Snapshot(u.Ids);
                            }
                        }
                        catch (Exception ex) { fail = "la receta falló al enumerar unidades: " + Describe(ex); }
                        return true;
                    }, ct).ConfigureAwait(false);
                    if (!r.Ok) return FromWork<bool>(r);
                    if (fail == "cerrado") return BackendResult<bool>.Waiting("el documento no está abierto", "abrirlo; la tarea sigue sola");
                    if (fail != null) return BackendResult<bool>.Fail(fail);
                }
                return BackendResult<bool>.Ok(true);
            }

            public async Task<BackendResult<List<UnitRef>>> EnumerateAsync(TaskRecord task, CancellationToken ct)
            {
                var e = await EnsureEnumerated(ct).ConfigureAwait(false);
                if (e.Value != true) return new BackendResult<List<UnitRef>> { Wait = e.Wait, Action = e.Action, Fatal = e.Fatal };
                return BackendResult<List<UnitRef>>.Ok(_found.Select(u => new UnitRef
                {
                    Key = u.Clave, Barrier = u.Barrera,
                    BarrierArgs = u.BarreraArgs != null ? new Dictionary<string, object>(u.BarreraArgs) : null,
                }).ToList());
            }

            public async Task<BackendResult<List<UnitOutcome>>> RunBatchAsync(TaskRecord task, IReadOnlyList<UnitRef> units, int budgetMs, CancellationToken ct)
            {
                if (!EnsureRecipe(out var err)) return BackendResult<List<UnitOutcome>>.Fail(err);
                var outcomes = new List<UnitOutcome>();
                string wait = null, waitAction = null;
                var r = await OnRevit((app, watch, budget) =>
                {
                    var doc = FindDoc(app);
                    if (doc == null) { wait = "el documento no está abierto"; waitAction = "abrirlo; la tarea sigue sola"; return true; }
                    var reg = _h._tracker.For(doc);
                    var ctx = Ctx(app, doc, ct);
                    var limit = Math.Max(10, Math.Min(budget, budgetMs));
                    _h._tracker.CurrentWriter = _owner;
                    try
                    {
                        foreach (var uref in units)
                        {
                            if (outcomes.Count > 0 && (watch.ElapsedMilliseconds >= limit || ct.IsCancellationRequested)) break;
                            // R06/R03: último punto seguro, en el hilo de Revit, justo antes de abrir la transacción de esta unidad.
                            if (task.Preflight?.Invoke() is string refusal) { Ui.Runtime.Log("Tarea " + task.Id + ": unidad " + uref.Key + " no ejecutada (" + refusal + ")"); break; }
                            if (!_byKey.TryGetValue(uref.Key, out var u))
                            {
                                outcomes.Add(UnitOutcome.Of(uref.Key, UnitState.Revertida, "la receta ya no enumera esta unidad"));
                                continue;
                            }
                            var o = RunUnit(doc, reg, ctx, u, out var stopWait);
                            if (o == null) { wait = stopWait; waitAction = "cerrar el modo de edición o el comando abierto en Revit"; break; }
                            outcomes.Add(o);
                            if (o.State == UnitState.Parcial || o.State == UnitState.Desconocida) break;
                        }
                    }
                    finally { _h._tracker.CurrentWriter = null; }
                    return true;
                }, ct).ConfigureAwait(false);
                if (!r.Ok && outcomes.Count == 0) return FromWork<List<UnitOutcome>>(r);
                return new BackendResult<List<UnitOutcome>> { Value = outcomes, Wait = wait, Action = waitAction };
            }

            /// <summary>Una unidad: conflicto → transacción → Ejecutar → resultado materializado → commit comprobado. null = no se pudo ni empezar.</summary>
            private UnitOutcome RunUnit(Document doc, VersionRegistry reg, ScriptContext ctx, Unidad u, out string wait)
            {
                wait = null;
                var sw = Stopwatch.StartNew();
                var o = new UnitOutcome { Key = u.Clave };
                if (reg != null && _snap.TryGetValue(u.Clave, out var snap) && snap.Count > 0)
                {
                    var conflicts = reg.FindConflicts(snap).Where(c => c.Author != _owner).ToList();
                    if (conflicts.Count > 0)
                    {
                        o.State = UnitState.Conflicto;
                        o.Error = conflicts.Count + " elementos cambiaron desde la enumeración: " + string.Join("; ", conflicts.Take(3).Select(c => c.Describe(_owner))) +
                                  ". No se pisó; reanudar la vuelve a enumerar sobre el modelo actual.";
                        return o;
                    }
                }
                var rev0 = reg?.Rev ?? 0;
                bool Changed() => (reg?.Rev ?? 0) != rev0;
                var policy = TxPolicy;
                var failures = new FailureLog { DeleteWarnings = !string.Equals(_t.PolicyString("avisos"), "dejar", StringComparison.OrdinalIgnoreCase) };
                Transaction t = null;
                _sink.Clear();
                try
                {
                    if (policy == PoliticaTx.PorUnidad)
                    {
                        t = new Transaction(doc, DocumentTracker.TransactionPrefix + " tarea " + _t.Id + " " + Clip(u.Clave, 60));
                        FailureLog.Attach(t, failures);
                        var ts = t.Start();
                        if (ts != TransactionStatus.Started) { wait = "Revit no permite abrir una transacción ahora (" + ts + ")"; return null; }
                    }
                    var value = _recipe.Ejecutar(ctx, u);
                    object json;
                    try { json = ResultSerializer.ToJson(value, doc, out _, out _); }
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { throw new InvalidOperationException("el resultado de la unidad no se pudo convertir: " + ex.Message, ex); }
                    o.Result = json;
                    if (t != null)
                    {
                        var st = t.Commit();
                        if (st == TransactionStatus.RolledBack)
                        {
                            o.State = UnitState.Revertida;
                            o.Error = "Revit rechazó la transacción y la revirtió: " + string.Join(" | ", failures.Errors.Take(3));
                        }
                        else if (st != TransactionStatus.Committed)
                        {
                            o.State = UnitState.Desconocida;
                            o.Error = "la transacción quedó en estado " + st + " (puede haber un aviso pendiente en pantalla)";
                        }
                        else o.State = UnitState.Confirmada;
                    }
                    else if (policy == PoliticaTx.Lectura && Changed())
                    {
                        o.State = UnitState.Parcial;
                        o.Error = "la receta es de lectura pero el modelo cambió durante la unidad";
                    }
                    else o.State = UnitState.Confirmada;
                    if (failures.Warnings.Count > 0) o.Decisions.Add("avisos de Revit en " + u.Clave + ": " + string.Join(" | ", failures.Warnings.Take(3)));
                }
                catch (OmitirUnidad om)
                {
                    var rolled = RollBack(t);
                    if (Changed() && (policy != PoliticaTx.PorUnidad || !rolled))
                    {
                        o.State = UnitState.Parcial;
                        o.Error = "se pidió omitir, pero quedaron cambios confirmados: " + om.Message;
                    }
                    else { o.State = UnitState.Omitida; o.Error = om.Message; }
                }
                catch (Exception ex)
                {
                    var rolled = RollBack(t);
                    var what = (ex is OperationCanceledException ? "cancelado: " : "") + Describe(ex);
                    if (!Changed() && rolled) { o.State = UnitState.Revertida; o.Error = what + " (no quedó nada aplicado)"; }
                    else { o.State = UnitState.Parcial; o.Error = what + ". ATENCIÓN: quedaron cambios confirmados; revise y reconcilie"; }
                }
                finally
                {
                    try { t?.Dispose(); } catch (Exception) { }
                }
                o.Ms = sw.ElapsedMilliseconds;
                if (reg != null && Changed() && (o.State == UnitState.Confirmada || o.State == UnitState.Parcial))
                {
                    foreach (var id in reg.AddedSince(rev0))
                    {
                        string uid = null;
                        try { uid = doc.GetElement(Ids.ToId(id))?.UniqueId; } catch (Exception) { }
                        if (uid != null) o.Created.Add(new object[] { id, uid });
                    }
                    var cnt = reg.CountSince(rev0);
                    o.Changed = cnt.Key;
                    o.Deleted = cnt.Value;
                }
                o.Decisions.AddRange(_sink.Decisions);
                o.Exceptions.AddRange(_sink.Exceptions.Select(x => new Dictionary<string, object>(x)));
                o.Artifacts.AddRange(_sink.Artifacts.Select(x => new Dictionary<string, object>(x)));
                foreach (var line in ctx.LogLines) if (o.Decisions.Count < 20) o.Decisions.Add("log: " + line);
                ctx.LogLines.Clear();
                _sink.Clear();
                return o;
            }

            private static bool RollBack(Transaction t)
            {
                if (t == null) return true;
                try { return t.GetStatus() != TransactionStatus.Started || t.RollBack() == TransactionStatus.RolledBack; }
                catch (Exception) { return false; }
            }

            public async Task<BackendResult<UnitOutcome>> RunBarrierAsync(TaskRecord task, UnitRef barrier, CancellationToken ct)
            {
                var verb = (barrier.Barrier ?? "").ToLowerInvariant();
                if (verb != "guardar" && verb != "sync" && verb != "sincronizar" && verb != "pdf")
                    return BackendResult<UnitOutcome>.Ok(UnitOutcome.Of(barrier.Key, UnitState.Revertida, "barrera desconocida '" + barrier.Barrier + "' (guardar, sync o pdf)"));
                if (!_h._config.AllowsDocOps)
                    return BackendResult<UnitOutcome>.Ok(UnitOutcome.Of(barrier.Key, UnitState.Revertida, "la configuración del MCP no permite guardar/sincronizar/exportar en este Revit"));
                var sw = Stopwatch.StartNew();
                var diffs = new List<string>();
                var pend = new DocOps.Pending();
                Dictionary<string, object> datos = null;
                string fail = null, wait = null, path = null;
                var bargs = barrier.BarrierArgs ?? new Dictionary<string, object>();
                var r = await OnRevit((app, watch, budget) =>
                {
                    var doc = FindDoc(app);
                    if (doc == null) { wait = "el documento no está abierto"; return true; }
                    _h._tracker.CurrentWriter = _owner;
                    try
                    {
                        switch (verb)
                        {
                            case "guardar": datos = DocOps.Save(app, doc, bargs, pend, diffs); break;
                            case "sync": case "sincronizar": datos = DocOps.Sync(doc, bargs, diffs, _h._tracker); break;
                            case "pdf": datos = DocOps.Pdf(doc, bargs, _sets, DocRegistry.Instance.IdOf(doc), pend, diffs); break;
                        }
                        try { path = doc.IsModelInCloud ? ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetCloudModelPath()) : doc.PathName; } catch (Exception) { path = doc.PathName; }
                    }
                    catch (OpFail ex) { fail = ex.Message; }
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { fail = ex.GetType().Name + ": " + ex.Message; }
                    finally { _h._tracker.CurrentWriter = null; }
                    return true;
                }, ct).ConfigureAwait(false);
                if (!r.Ok) return FromWork<UnitOutcome>(r);
                if (wait != null) return BackendResult<UnitOutcome>.Waiting(wait, "abrirlo; la tarea sigue sola");
                var o = new UnitOutcome { Key = barrier.Key };
                if (fail != null) { o.State = UnitState.Revertida; o.Error = fail; o.Ms = sw.ElapsedMilliseconds; return BackendResult<UnitOutcome>.Ok(o); }
                List<object> files = null;
                if (pend.Files.Count > 0)
                {
                    var expected = datos != null && datos.TryGetValue("n_hojas", out var nh) && nh is long l ? (int)l : 0;
                    files = await Task.Run(() => DocOps.VerifyFiles(pend, expected, diffs), ct).ConfigureAwait(false);
                    datos["archivos"] = files;
                }
                o.State = UnitState.Confirmada;
                o.Result = datos;
                o.Ms = sw.ElapsedMilliseconds;
                foreach (var d in diffs) o.Exceptions.Add(new Dictionary<string, object> { ["motivo"] = "barrera " + verb + ": " + d });
                var first = files?.OfType<Dictionary<string, object>>().FirstOrDefault();
                if (verb == "pdf")
                {
                    foreach (var f in files?.OfType<Dictionary<string, object>>() ?? Enumerable.Empty<Dictionary<string, object>>())
                        o.Artifacts.Add(new Dictionary<string, object>
                        {
                            ["ruta"] = f.TryGetValue("archivo", out var p) ? p : null, ["sha256"] = f.TryGetValue("sha256", out var s) ? s : null,
                            ["bytes"] = f.TryGetValue("bytes", out var b) ? b : null, ["paginas"] = f.TryGetValue("paginas", out var pg) ? pg : null, ["tipo"] = "pdf",
                        });
                }
                else
                {
                    o.Persist = new PersistEvidence
                    {
                        Kind = verb == "guardar" ? "guardar" : "sync", When = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        File = (first != null && first.TryGetValue("archivo", out var fp) ? fp as string : null) ?? path,
                        Bytes = first != null && first.TryGetValue("bytes", out var fb) && fb is long lb ? lb : 0,
                        Mtime = first != null && first.TryGetValue("escrito_utc", out var fm) ? fm as string : null,
                        Sha = first != null && first.TryGetValue("sha256", out var fs) ? fs as string : null,
                    };
                }
                return BackendResult<UnitOutcome>.Ok(o);
            }

            public async Task<BackendResult<List<UnitOutcome>>> VerifyAsync(TaskRecord task, IReadOnlyList<UnitRecord> units, bool reopened, CancellationToken ct)
            {
                // Verificar de la receta necesita las unidades enumeradas (sus datos); sólo si alguna no se puede verificar por UniqueId.
                if (units.Any(u => u.Created.Count == 0))
                {
                    var e = await EnsureEnumerated(ct).ConfigureAwait(false);
                    if (e.Value != true) return new BackendResult<List<UnitOutcome>> { Wait = e.Wait, Action = e.Action, Fatal = e.Fatal };
                }
                var outcomes = new List<UnitOutcome>();
                string wait = null;
                var curGen = CurrentGeneration(task);
                var r = await OnRevit((app, watch, budget) =>
                {
                    var doc = FindDoc(app);
                    if (doc == null) { wait = "el documento no está abierto"; return true; }
                    var ctx = Ctx(app, doc, ct);
                    foreach (var u in units) outcomes.Add(VerifyOne(app, doc, ctx, u, reopened, curGen));
                    return true;
                }, ct).ConfigureAwait(false);
                if (!r.Ok) return FromWork<List<UnitOutcome>>(r);
                if (wait != null) return BackendResult<List<UnitOutcome>>.Waiting(wait, "abrirlo; la tarea sigue sola");
                return BackendResult<List<UnitOutcome>>.Ok(outcomes);
            }

            private UnitOutcome VerifyOne(UIApplication _verifyApp, Document doc, ScriptContext ctx, UnitRecord u, bool reopened, string curGen)
            {
                var o = new UnitOutcome { Key = u.Key };
                // 1) Evidencia en el modelo: los elementos que creó (UniqueId).
                if (u.Created.Count > 0)
                {
                    var exist = u.Created.Count(c => c.Length > 1 && c[1] is string uid && SafeGet(doc, uid) != null);
                    if (exist == u.Created.Count) { o.State = UnitState.Confirmada; o.Error = "verificada: sus " + exist + " elementos creados existen"; o.Created = u.Created.ToList(); return o; }
                    if (exist == 0 && reopened) { o.State = UnitState.Pendiente; o.Error = "verificada: ninguno de sus elementos creados está en el modelo reabierto (se perdió al cerrar sin guardar)"; return o; }
                    o.State = UnitState.Desconocida;
                    o.Error = "existen " + exist + " de " + u.Created.Count + " elementos que creó: revise y reconcilie";
                    return o;
                }
                // 2) Verificar de la receta.
                var v = Verificacion.Desconocida;
                if (_byKey.TryGetValue(u.Key, out var unidad))
                {
                    try
                    {
                        var g = _recipe == null ? "receta no instanciada" : Guard(_verifyApp, RecipeGate.Phase.Verificar, () => v = _recipe.Verificar(ctx, unidad));
                        if (g != null) { v = Verificacion.Desconocida; o.Error = g; }
                    }
                    catch (Exception ex) { o.Error = "Verificar falló: " + Describe(ex); }
                }
                if (v == Verificacion.Aplicada) { o.State = UnitState.Confirmada; o.Error = "verificada por la receta"; return o; }
                if (v == Verificacion.NoAplicada) { o.State = UnitState.Pendiente; o.Error = "la receta verificó que no está aplicada"; return o; }
                // 3) Inferencia segura sólo tras reabrir: empezó después del último guardado de SU apertura → no está en el archivo.
                if (reopened && u.Generation != null && u.Generation != curGen)
                {
                    var saved = _t.PersistedSince(u.Generation, u.Started);
                    if (saved == false)
                    {
                        o.State = UnitState.Pendiente;
                        o.Error = "inferido: empezó (" + u.Started + ") después del último guardado de la apertura " + u.Generation + " y el documento se reabrió sin guardarla";
                        return o;
                    }
                }
                o.State = UnitState.Desconocida;
                o.Error = (o.Error != null ? o.Error + "; " : "") + "no se puede comprobar en el modelo (la receta no implementa Verificar y no creó elementos)";
                return o;
            }

            private static Element SafeGet(Document doc, string uid)
            {
                try { return doc.GetElement(uid); } catch (Exception) { return null; }
            }

            public async Task<BackendResult<Dictionary<string, object>>> PostconditionAsync(TaskRecord task, CancellationToken ct)
            {
                if (!EnsureRecipe(out var err)) return BackendResult<Dictionary<string, object>>.Fail(err);
                Dictionary<string, object> result = null;
                string wait = null;
                var r = await OnRevit((app, watch, budget) =>
                {
                    var doc = FindDoc(app);
                    if (doc == null) { wait = "el documento no está abierto"; return true; }
                    var ctx = Ctx(app, doc, ct);
                    // QC de sólo lectura: dentro de un grupo que siempre se revierte (lo que intente cambiar no queda).
                    TransactionGroup g = null;
                    using (_h._tracker.Suspend())
                    {
                        try
                        {
                            // P0: con cambios sin guardar no se abre el grupo temporal (Revit rechazará cualquier intento de modificar).
                            if (Engine.TrialGuard.MayOpenTemporaryGroup(doc.IsModified))
                            {
                                g = new TransactionGroup(doc, DocumentTracker.TemporaryPrefix + " qc tarea");
                                g.Start();
                            }
                            object value = null;
                            var pg = EnsureInstance(app) ?? Guard(app, RecipeGate.Phase.Postcondicion, () => value = _recipe.Postcondicion(ctx));
                            if (pg != null) throw new InvalidOperationException(pg);
                            var json = value == null ? null : ResultSerializer.ToJson(value, doc, out _, out _);
                            result = json as Dictionary<string, object> ?? (json == null ? new Dictionary<string, object>() : new Dictionary<string, object> { ["resultado"] = json });
                        }
                        catch (Exception ex)
                        {
                            result = new Dictionary<string, object> { ["desviaciones"] = new List<object> { "la postcondición falló: " + Describe(ex) } };
                        }
                        finally
                        {
                            try { if (g != null && g.HasStarted()) g.RollBack(); } catch (Exception) { }
                            try { g?.Dispose(); } catch (Exception) { }
                        }
                    }
                    return true;
                }, ct).ConfigureAwait(false);
                if (!r.Ok) return FromWork<Dictionary<string, object>>(r);
                if (wait != null) return BackendResult<Dictionary<string, object>>.Waiting(wait, "abrirlo; la tarea sigue sola");
                return BackendResult<Dictionary<string, object>>.Ok(result);
            }

            private static string Clip(string s, int n) => s.Length <= n ? s : s.Substring(0, n);
        }
    }
}
