// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using PDI.RevitMcp.Addin.Revit;

namespace PDI.RevitMcp.Addin.Scripting
{
    /// <summary>
    /// Convierte lo que devuelve un script en datos JSON simples, DENTRO del hilo de Revit (los objetos de la API no salen de él).
    /// ElementId → número; Element → {id, cat, nombre}; XYZ → [x,y,z] en pies; Document → {doc, titulo}; colecciones y objetos anónimos.
    /// Límites: profundidad 6 y 20.000 elementos; lo que exceda se marca, no se pierde en silencio.
    /// </summary>
    public static class ResultSerializer
    {
        public const int MaxItems = 20000;
        public const int MaxDepth = 6;

        [ThreadStatic] private static Document _doc;
        [ThreadStatic] private static List<string> _errors;

        public static object ToJson(object value, Document doc, out bool truncated) => ToJson(value, doc, out truncated, out _);

        /// <summary>
        /// 0.6.0: una enumeración diferida que lanza (LINQ perezoso, colector sobre un documento cerrado…) no tumba la respuesta ni deja el
        /// trabajo colgado: se corta ahí, se marca en el resultado y se informa en <paramref name="errors"/>.
        /// </summary>
        public static object ToJson(object value, Document doc, out bool truncated, out List<string> errors)
        {
            _doc = doc;
            _errors = new List<string>();
            var budget = new int[] { MaxItems };
            var t = false;
            object r;
            try { r = Convert(value, 0, budget, ref t); }
            finally { errors = _errors; _errors = null; _doc = null; }
            truncated = t;
            return r;
        }

        private static string Fail(Exception ex, string where)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            var msg = "error al " + where + ": " + inner.GetType().Name + ": " + inner.Message;
            if (_errors != null && _errors.Count < 20) _errors.Add(msg);
            return msg;
        }

        private static object Convert(object v, int depth, int[] budget, ref bool truncated)
        {
            if (v == null) return null;
            if (--budget[0] < 0) { truncated = true; return "…"; }
            switch (v)
            {
                case string s: return s;
                case bool b: return b;
                case int i: return (long)i;
                case long l: return l;
                case short sh: return (long)sh;
                case byte by: return (long)by;
                case uint ui: return (long)ui;
                case double d: return Round(d);
                case float f: return Round(f);
                case decimal m: return (double)m;
                case Guid g: return g.ToString();
                case DateTime dt: return dt.ToString("o", CultureInfo.InvariantCulture);
                case Enum e: return e.ToString();
                case ElementId id: return Ids.ToLong(id);
                case XYZ p: return new List<object> { Round(p.X), Round(p.Y), Round(p.Z) };
                case UV uv: return new List<object> { Round(uv.U), Round(uv.V) };
                case Document doc: return new Dictionary<string, object> { ["doc"] = DocRegistry.Instance.IdOf(doc), ["titulo"] = doc.Title };
                case Parameter p: return new Dictionary<string, object> { ["nombre"] = p.Definition?.Name, ["valor"] = ScriptContext.ShowValue(p) };
                case Reference r:
                    try { return _doc != null ? r.ConvertToStableRepresentation(_doc) : Ids.ToLong(r.ElementId).ToString(CultureInfo.InvariantCulture); }
                    catch (Exception) { return Ids.ToLong(r.ElementId).ToString(CultureInfo.InvariantCulture); }
                case Element e: return Element(e);
                case BoundingBoxXYZ bb: return new Dictionary<string, object> { ["min"] = Convert(bb.Min, depth + 1, budget, ref truncated), ["max"] = Convert(bb.Max, depth + 1, budget, ref truncated) };
            }
            if (depth >= MaxDepth) { truncated = true; return v.ToString(); }
            if (v is IDictionary dict)
            {
                var o = new Dictionary<string, object>();
                try
                {
                    foreach (DictionaryEntry kv in dict)
                    {
                        o[System.Convert.ToString(kv.Key, CultureInfo.InvariantCulture) ?? ""] = Convert(kv.Value, depth + 1, budget, ref truncated);
                        if (budget[0] < 0) break;
                    }
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { o["_error"] = Fail(ex, "enumerar el diccionario"); }
                return o;
            }
            if (v is IEnumerable seq)
            {
                var list = new List<object>();
                try
                {
                    foreach (var item in seq)
                    {
                        list.Add(Convert(item, depth + 1, budget, ref truncated));
                        if (budget[0] < 0) break;
                    }
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { list.Add(Fail(ex, "enumerar el elemento " + list.Count)); }
                return list;
            }
            var type = v.GetType();
            if (type.IsPrimitive) return v.ToString();
            // Objetos anónimos o clases propias del script: propiedades públicas.
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).ToArray();
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (props.Length == 0 && fields.Length == 0) return v.ToString();
            var obj = new Dictionary<string, object>();
            foreach (var p in props)
            {
                object pv;
                try { pv = p.GetValue(v); } catch (Exception ex) { pv = "error: " + (ex.InnerException ?? ex).Message; }
                obj[p.Name] = Convert(pv, depth + 1, budget, ref truncated);
            }
            foreach (var f in fields)
            {
                try { obj[f.Name] = Convert(f.GetValue(v), depth + 1, budget, ref truncated); }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { obj[f.Name] = Fail(ex, "leer " + f.Name); }
            }
            return obj;
        }

        private static object Element(Element e)
        {
            var d = new Dictionary<string, object> { ["id"] = Ids.ToLong(e.Id) };
            try { if (e.Category != null) d["cat"] = e.Category.Name; } catch (Exception) { }
            try { d["nombre"] = e.Name; } catch (Exception) { }
            return d;
        }

        private static double Round(double d) => double.IsNaN(d) || double.IsInfinity(d) ? 0 : Math.Round(d, 9);
    }
}
