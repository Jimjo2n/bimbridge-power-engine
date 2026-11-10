// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Addin.Revit;
using PDI.RevitMcp.Engine;

namespace PDI.RevitMcp.Addin.Scripting
{
    /// <summary>
    /// Lo que recibe un script de exec. En el cuerpo ya existen: ctx, doc (el documento destino), uidoc (null si el destino no es el activo),
    /// app (Application), uiapp (UIApplication) y args (los argumentos JSON de la llamada).
    /// Todo lo de la API pública de Revit está disponible: no hay lista de operaciones permitidas.
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
        /// <summary>Documento destino (fijado al recibir la petición; doc=dN lo elige).</summary>
        public Document Doc { get; }
        /// <summary>UIDocument del destino sólo si es el activo; si no, null.</summary>
        public UIDocument UiDoc { get; }
        public IDictionary<string, object> Args { get; }
        /// <summary>"consulta" (todo se revierte al final) o "ejecutar".</summary>
        public string Mode { get; }
        /// <summary>"auto", "grupo" o "ninguna" (el script abre sus propias transacciones).</summary>
        public string Tx { get; }
        public CancellationToken Cancel { get; }

        /// <summary>Lanza OperationCanceledException si el usuario o la IA cancelaron. Llámelo dentro de bucles largos.</summary>
        public void ThrowIfCancelled() => Cancel.ThrowIfCancellationRequested();

        public void Log(object message)
        {
            if (_log.Count < 500) _log.Add(Convert.ToString(message, CultureInfo.InvariantCulture));
            else if (_log.Count == 500) _log.Add("… (registro recortado a 500 líneas)");
        }

        internal List<string> LogLines => _log;

        // ---------------- tarea (recetas por pasos) ----------------

        /// <summary>Lo que una unidad deja en la memoria de la tarea (decisiones, excepciones, artefactos). Lo vacía el motor tras cada unidad.</summary>
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

        /// <summary>Id de la tarea que ejecuta esta receta (null en exec).</summary>
        public string TareaId => Sink?.TaskId;

        /// <summary>Anota una decisión en la memoria de la tarea (aparece en el resumen para el siguiente agente).</summary>
        public void Decision(string texto)
        {
            if (Sink != null) Sink.Decisions.Add(texto); else Log("decisión: " + texto);
        }

        /// <summary>Declara una excepción trazable (algo que se deja sin resolver a propósito, con su motivo y evidencia).</summary>
        public void Excepcion(string motivo, object detalle = null)
        {
            var d = new Dictionary<string, object> { ["motivo"] = motivo, ["detalle"] = detalle == null ? null : ResultSerializer.ToJson(detalle, Doc, out _) };
            if (Sink != null) Sink.Exceptions.Add(d); else Log("excepción: " + motivo);
        }

        /// <summary>Termina la unidad sin hacer nada (se revierte lo que hubiera hecho) y la registra como omitida con el motivo.</summary>
        public void Omitir(string motivo) => throw new OmitirUnidad(motivo);

        /// <summary>
        /// Guarda un artefacto de texto (informe CSV/JSON/MD) en la carpeta de la tarea y lo registra con su SHA-256. Devuelve la ruta.
        /// Fuera de una tarea se guarda en resultados\.
        /// </summary>
        public string Artefacto(string nombre, string contenido)
        {
            var safe = new string((nombre ?? "artefacto.txt").Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '_').ToArray());
            var dir = Sink?.ArtifactsDir ?? Home.Sub("resultados");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, safe);
            System.IO.File.WriteAllText(path, contenido ?? "");
            var sha = Home.FileSha256(path);
            var a = new Dictionary<string, object> { ["ruta"] = path, ["sha256"] = sha, ["bytes"] = new System.IO.FileInfo(path).Length, ["tipo"] = System.IO.Path.GetExtension(path).TrimStart('.') };
            Sink?.Artifacts.Add(a);
            return path;
        }

        // ---------------- documentos ----------------

        /// <summary>Handle dN de un documento abierto (el mismo que usa la IA con doc=).</summary>
        public string HandleOf(Document d) => DocRegistry.Instance.IdOf(d);

        /// <summary>Otro documento abierto por su handle dN; null si no está abierto.</summary>
        public Document DocOf(string handle) => DocRegistry.Instance.Resolve(UiApp, handle);

        /// <summary>Documentos abiertos (sin vínculos).</summary>
        public IList<Document> Docs => UiApp.Application.Documents.Cast<Document>().Where(d => !d.IsLinked).ToList();

        // ---------------- elementos e ids ----------------

        public ElementId Id(long id) => Ids.ToId(id);
        public long Long(ElementId id) => Ids.ToLong(id);
        public Element E(long id) => Doc.GetElement(Ids.ToId(id));
        public T E<T>(long id) where T : Element => Doc.GetElement(Ids.ToId(id)) as T;

        /// <summary>Ids de un conjunto guardado ($nombre) de este documento.</summary>
        public IList<long> Set(string name)
        {
            if (_sets == null) throw new InvalidOperationException("no hay sesión con conjuntos");
            if (!_sets.TryGet(name, _docHandle, out var ids, out var error)) throw new ArgumentException(error);
            return ids;
        }

        /// <summary>Guarda un conjunto ($nombre) para usarlo después con ids=$nombre en q, plan, geo…</summary>
        public void SaveSet(string name, IEnumerable<long> ids) => _sets?.Save(name, ids, _docHandle, Doc?.Title);
        public void SaveSet(string name, IEnumerable<ElementId> ids) => SaveSet(name, ids.Select(Ids.ToLong));

        // ---------------- unidades (Revit guarda pies) ----------------

        public double Mm(double mm) => mm / 304.8;
        public double ToMm(double feet) => feet * 304.8;
        public double In(double inches) => inches / 12.0;
        public double ToIn(double feet) => feet * 12.0;
        public XYZ MmXyz(double x, double y, double z) => new XYZ(x / 304.8, y / 304.8, z / 304.8);

        /// <summary>Valor legible de un parámetro (texto con unidades del proyecto), o null.</summary>
        public string Show(Parameter p) => ShowValue(p);
        internal static string ShowValue(Parameter p) => p == null ? null : (p.StorageType == StorageType.String ? p.AsString() : p.AsValueString());
    }
}
