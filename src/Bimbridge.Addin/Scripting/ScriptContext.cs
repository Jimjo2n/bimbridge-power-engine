// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimbridge.Addin.Revit;
using Bimbridge.Engine;

namespace Bimbridge.Addin.Scripting
{
    /// <summary>
    /// What an exec script receives. The body already has: ctx, doc (the target document), uidoc (null if the target is not the active one),
    /// app (Application), uiapp (UIApplication) and args (the call's JSON arguments).
    /// The whole public Revit API is available: there is no allow-list of operations.
    /// </summary>
    public sealed class ScriptContext
    {
        private readonly List<string> _log = new List<string>();
        private readonly NamedSets _sets;
        private readonly string _docHandle;

        internal ScriptContext(UIApplication uiapp, Document doc, IDictionary<string, object> args, string mode, string tx,
            CancellationToken cancel, NamedSets sets, string docHandle)
        {
            UiApp = uiapp;
            App = uiapp?.Application;
            Doc = doc;
            try { var a = uiapp?.ActiveUIDocument; UiDoc = a != null && doc != null && a.Document.Equals(doc) ? a : null; } catch (Exception) { }
            Args = args ?? new Dictionary<string, object>();
            Mode = mode;
            Tx = tx;
            Cancel = cancel;
            _sets = sets;
            _docHandle = docHandle;
        }

        public UIApplication UiApp { get; }
        public Autodesk.Revit.ApplicationServices.Application App { get; }
        /// <summary>Target document (fixed when the request is received; doc=dN selects it).</summary>
        public Document Doc { get; }
        /// <summary>UIDocument of the target only if it is the active one; otherwise null.</summary>
        public UIDocument UiDoc { get; }
        public IDictionary<string, object> Args { get; }
        /// <summary>"consulta" (everything is rolled back at the end) or "ejecutar".</summary>
        public string Mode { get; }
        /// <summary>"auto", "grupo" or "ninguna" (the script opens its own transactions).</summary>
        public string Tx { get; }
        public CancellationToken Cancel { get; }

        /// <summary>Throws OperationCanceledException if the user or the AI cancelled. Call it inside long loops.</summary>
        public void ThrowIfCancelled() => Cancel.ThrowIfCancellationRequested();

        public void Log(object message)
        {
            if (_log.Count < 500) _log.Add(Convert.ToString(message, CultureInfo.InvariantCulture));
            else if (_log.Count == 500) _log.Add("… (log truncated to 500 lines)");
        }

        internal List<string> LogLines => _log;

        // ---------------- task (step-by-step recipes) ----------------

        /// <summary>What a unit leaves in the task memory (decisions, exceptions, artifacts). The engine clears it after each unit.</summary>
        internal sealed class TaskSink
        {
            public string TaskId;
            public string ArtifactsDir;
            public readonly List<string> Decisions = new List<string>();
            public readonly List<Dictionary<string, object>> Exceptions = new List<Dictionary<string, object>>();
            public readonly List<Dictionary<string, object>> Artifacts = new List<Dictionary<string, object>>();
            public void Clear() { Decisions.Clear(); Exceptions.Clear(); Artifacts.Clear(); }
        }

        internal TaskSink Sink;

        /// <summary>Id of the task running this recipe (null in exec).</summary>
        public string TaskId => Sink?.TaskId;

        /// <summary>Notes a decision in the task memory (it appears in the summary for the next agent).</summary>
        public void Decision(string text)
        {
            if (Sink != null) Sink.Decisions.Add(text); else Log("decision: " + text);
        }

        /// <summary>Declares a traceable exception (something deliberately left unresolved, with its reason and evidence).</summary>
        public void Exception(string reason, object detail = null)
        {
            var d = new Dictionary<string, object> { ["motivo"] = reason, ["detalle"] = detail == null ? null : ResultSerializer.ToJson(detail, Doc, out _) };
            if (Sink != null) Sink.Exceptions.Add(d); else Log("exception: " + reason);
        }

        /// <summary>Ends the unit without doing anything (whatever it did is rolled back) and records it as skipped with the reason.</summary>
        public void Skip(string reason) => throw new SkipUnit(reason);

        /// <summary>
        /// Saves a text artifact (CSV/JSON/MD report) in the task folder and records it with its SHA-256. Returns the path.
        /// Outside a task it is saved in resultados\.
        /// </summary>
        public string Artifact(string name, string content)
        {
            var safe = new string((name ?? "artefacto.txt").Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '_').ToArray());
            var dir = Sink?.ArtifactsDir ?? Home.Sub("resultados");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, safe);
            System.IO.File.WriteAllText(path, content ?? "");
            var sha = Home.FileSha256(path);
            var a = new Dictionary<string, object> { ["ruta"] = path, ["sha256"] = sha, ["bytes"] = new System.IO.FileInfo(path).Length, ["tipo"] = System.IO.Path.GetExtension(path).TrimStart('.') };
            Sink?.Artifacts.Add(a);
            return path;
        }

        // ---------------- documents ----------------

        /// <summary>dN handle of an open document (the same one the AI uses with doc=).</summary>
        public string HandleOf(Document d) => DocRegistry.Instance.IdOf(d);

        /// <summary>Another open document by its dN handle; null if it is not open.</summary>
        public Document DocOf(string handle) => DocRegistry.Instance.Resolve(UiApp, handle);

        /// <summary>Open documents (excluding links).</summary>
        public IList<Document> Docs => UiApp.Application.Documents.Cast<Document>().Where(d => !d.IsLinked).ToList();

        // ---------------- elements and ids ----------------

        public ElementId Id(long id) => Ids.ToId(id);
        public long Long(ElementId id) => Ids.ToLong(id);
        public Element E(long id) => Doc.GetElement(Ids.ToId(id));
        public T E<T>(long id) where T : Element => Doc.GetElement(Ids.ToId(id)) as T;

        /// <summary>Ids of a saved set ($name) of this document.</summary>
        public IList<long> Set(string name)
        {
            if (_sets == null) throw new InvalidOperationException("no session with sets");
            if (!_sets.TryGet(name, _docHandle, out var ids, out var error)) throw new ArgumentException(error);
            return ids;
        }

        /// <summary>Saves a set ($name) to use later with ids=$name in q, plan, geo…</summary>
        public void SaveSet(string name, IEnumerable<long> ids) => _sets?.Save(name, ids, _docHandle, Doc?.Title);
        public void SaveSet(string name, IEnumerable<ElementId> ids) => SaveSet(name, ids.Select(Ids.ToLong));

        // ---------------- units (Revit stores feet) ----------------

        public double Mm(double mm) => mm / 304.8;
        public double ToMm(double feet) => feet * 304.8;
        public double In(double inches) => inches / 12.0;
        public double ToIn(double feet) => feet * 12.0;
        public XYZ MmXyz(double x, double y, double z) => new XYZ(x / 304.8, y / 304.8, z / 304.8);

        /// <summary>Readable value of a parameter (text with project units), or null.</summary>
        public string Show(Parameter p) => ShowValue(p);
        internal static string ShowValue(Parameter p) => p == null ? null : (p.StorageType == StorageType.String ? p.AsString() : p.AsValueString());
    }
}
