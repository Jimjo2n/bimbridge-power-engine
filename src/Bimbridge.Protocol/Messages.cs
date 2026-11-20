// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace Bimbridge.Protocol
{
    /// <summary>Constants of the internal bridge ↔ add-in protocol. The AI never sees them.</summary>
    public static class Wire
    {
        /// <summary>Bumped when the format changes. Bridge and add-in must match.</summary>
        public const int ProtocolVersion = 2;

        public const string PipePrefix = "Bimbridge.";

        // Operations
        public const string OpHello = "hello";
        public const string WriteReceiptFeature = "recibo_escritura";
        public const string LargeResultFeature = "resultado_fuera_de_trama";
        public const string OpPing = "ping";
        public const string OpContext = "context";
        public const string OpQuery = "query";
        public const string OpSets = "sets";
        public const string OpCheck = "check";
        public const string OpPlan = "plan";
        public const string OpCommit = "commit";
        public const string OpJob = "job";
        public const string OpGeo = "geo";
        public const string OpCatalog = "catalog";
        public const string OpShow = "show";
        public const string OpCapture = "capture";
        public const string OpParams = "params";
        /// <summary>General runner: compiles and runs C# against the public Revit API (rolled-back query, run with or without a transaction).</summary>
        public const string OpExec = "exec";
        /// <summary>Open documents (projects and families) with a stable dN handle.</summary>
        public const string OpDocs = "docs";
        /// <summary>Families: info, faces, connectors, parameters, edit, load (typed reads and writes).</summary>
        public const string OpFam = "fam";
        /// <summary>Document operations: save, save as, sync, PDF, close.</summary>
        public const string OpDoc = "doc";
        /// <summary>0.6.0: long resumable tasks (start, status, wait, cancel, resume, reconcile, summary…).</summary>
        public const string OpTask = "tarea";
        /// <summary>Args key with the target document (dN handle, path or title). Without it: the document active when the request arrives, fixed for the whole request.</summary>
        public const string DocArg = "doc";
        /// <summary>Idempotency key: the same key on the same document is not applied again.</summary>
        public const string KeyArg = "clave";
        public const string TaskArg = "tarea";
        /// <summary>Optional args key with the name of the tool the AI saw (e.g. "get"), journal only.</summary>
        public const string ToolArg = "_tool";
    }

    /// <summary>Response statuses (architecture section 6). These are what the AI sees.</summary>
    public static class Status
    {
        public const string Ok = "ok";
        public const string EnCola = "en_cola";
        public const string EnProceso = "en_proceso";
        /// <summary>The change is in the Revit queue; the user will apply it with the "Apply queue" button.</summary>
        public const string Retenido = "retenido";
        public const string Conflicto = "conflicto";
        public const string RevitOcupado = "revit_ocupado";
        public const string Revertido = "revertido";
        public const string Vencido = "vencido";
        public const string Rechazado = "rechazado";
        public const string Invalido = "invalido";
        public const string RevitNoDisponible = "revit_no_disponible";
        public const string Incompatible = "incompatible";
        public const string Error = "error";
        /// <summary>The request deadline passed and the work was cancelled inside Revit.</summary>
        public const string TiempoAgotado = "tiempo_agotado";
        /// <summary>The operation does not apply to this document type or Revit version (says which, and what alternative exists).</summary>
        public const string NoSoportado = "no_soportado";
        /// <summary>The pinned document or Revit is no longer open. Not redirected to another one.</summary>
        public const string DestinoNoDisponible = "destino_no_disponible";
        /// <summary>A user decision is needed (e.g. Hold is on, ambiguous data left unresolved).</summary>
        public const string NecesitaUsuario = "necesita_usuario";
        /// <summary>An operation with that key started and its end is not recorded (Revit closed or was cut off): reconcile before repeating.</summary>
        public const string Interrumpido = "interrumpido";
        /// <summary>Applied, but the follow-up verification found differences from what was requested (they are listed).</summary>
        public const string ConDiferencias = "completado_con_diferencias";

        // 0.6.0: semantic statuses that tell apart what was applied, what was rolled back and what is unknown.
        /// <summary>One part was committed and another was not (e.g. tx=ninguna with a failure halfway, or one document yes and another no). Reconcile before repeating.</summary>
        public const string Parcial = "parcial";
        /// <summary>It cannot be known whether it was applied (the evidence was lost). Reconcile before repeating.</summary>
        public const string Desconocido = "desconocido";
        /// <summary>The changes were committed, but producing the response failed. Nothing was rolled back.</summary>
        public const string AplicadoErrorResultado = "aplicado_error_resultado";
        public const string CancelacionSolicitada = "cancelacion_solicitada";
        /// <summary>Cancellation confirmed: what was committed before cancelling stays applied and is listed.</summary>
        public const string Cancelado = "cancelado";
        /// <summary>The task is waiting on context (user pause, open dialog, closed document). It keeps its progress.</summary>
        public const string EsperandoContexto = "esperando_contexto";
        /// <summary>The operation journal is unavailable: a write with clave= is not applied when its idempotency cannot be guaranteed.</summary>
        public const string DiarioNoDisponible = "diario_no_disponible";
        /// <summary>A name matches several documents or elements: candidates are returned, the first one is not picked.</summary>
        public const string Ambiguo = "ambiguo";
        /// <summary>Another task or barrier holds the document; the write was not applied.</summary>
        public const string DocumentoOcupado = "documento_ocupado";

        /// <summary>Terminal statuses that confirm NOTHING was applied (can be retried with the same key).</summary>
        public static bool ConfirmsNothingApplied(string s) =>
            s == Revertido || s == Rechazado || s == Invalido || s == DestinoNoDisponible || s == NoSoportado || s == NecesitaUsuario ||
            s == Conflicto || s == RevitOcupado || s == Vencido || s == DiarioNoDisponible || s == Ambiguo || s == DocumentoOcupado ||
            s == Incompatible || s == RevitNoDisponible || s == TiempoAgotado;

        /// <summary>Terminal statuses that count as applied (repeating the key returns the stored result).</summary>
        public static bool CountsAsApplied(string s) => s == Ok || s == ConDiferencias || s == Retenido || s == AplicadoErrorResultado;
    }

    public sealed class Request : IJsonWritable
    {
        public long Id;
        public string Op;
        public Dictionary<string, object> Args = new Dictionary<string, object>(StringComparer.Ordinal);
        /// <summary>Maximum time the bridge will wait; the add-in uses it to avoid wasted work.</summary>
        public int DeadlineMs;
        public string AttemptId;

        public object ToJson()
        {
            var data = new Dictionary<string, object> { ["id"] = Id, ["op"] = Op, ["args"] = Args, ["deadline_ms"] = DeadlineMs };
            if (AttemptId != null) data["intento_id"] = AttemptId;
            return data;
        }

        public static Request FromJson(string json)
        {
            var o = Json.ParseObject(json);
            return new Request
            {
                Id = Arg.GetLong(o, "id") ?? 0,
                Op = Arg.GetString(o, "op"),
                AttemptId = Arg.GetString(o, "intento_id"),
                Args = o.TryGetValue("args", out var a) && a is Dictionary<string, object> d
                    ? d
                    : new Dictionary<string, object>(StringComparer.Ordinal),
                DeadlineMs = (int)(Arg.GetLong(o, "deadline_ms") ?? 0),
            };
        }
    }

    public sealed class Response : IJsonWritable
    {
        public long Id;
        public string Estado = Status.Ok;
        public long? Rev;
        public object Datos;
        public string Aviso;

        public object ToJson()
        {
            var d = new Dictionary<string, object> { ["id"] = Id, ["estado"] = Estado };
            if (Rev.HasValue) d["rev"] = Rev.Value;
            if (Datos != null) d["datos"] = Datos;
            if (!string.IsNullOrEmpty(Aviso)) d["aviso"] = Aviso;
            return d;
        }

        public static Response FromJson(string json)
        {
            var o = Json.ParseObject(json);
            o.TryGetValue("datos", out var datos);
            return new Response
            {
                Id = Arg.GetLong(o, "id") ?? 0,
                Estado = Arg.GetString(o, "estado") ?? Status.Error,
                Rev = Arg.GetLong(o, "rev"),
                Datos = datos,
                Aviso = Arg.GetString(o, "aviso"),
            };
        }

        public static Response Fail(long id, string estado, string aviso) =>
            new Response { Id = id, Estado = estado, Aviso = aviso };
    }

    /// <summary>Lenient reading of already-parsed JSON arguments.</summary>
    public static class Arg
    {
        public static string GetString(IDictionary<string, object> o, string key)
        {
            if (o == null || !o.TryGetValue(key, out var v) || v == null) return null;
            return v as string ?? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static long? GetLong(IDictionary<string, object> o, string key)
        {
            if (o == null || !o.TryGetValue(key, out var v) || v == null) return null;
            switch (v)
            {
                case long l: return l;
                case int i: return i;
                case double d when Math.Abs(d - Math.Round(d)) < 1e-9: return (long)Math.Round(d);
                case string s when long.TryParse(s, out var p): return p;
                default: return null;
            }
        }

        public static bool? GetBool(IDictionary<string, object> o, string key)
        {
            if (o == null || !o.TryGetValue(key, out var v) || v == null) return null;
            if (v is bool b) return b;
            if (v is string s && bool.TryParse(s, out var p)) return p;
            return null;
        }

        /// <summary>Accepts "a" or ["a","b"] and always returns a list.</summary>
        public static List<string> GetStringList(IDictionary<string, object> o, string key)
        {
            var result = new List<string>();
            if (o == null || !o.TryGetValue(key, out var v) || v == null) return result;
            if (v is string s)
            {
                foreach (var part in s.Split(','))
                    if (part.Trim().Length > 0) result.Add(part.Trim());
                return result;
            }
            if (v is List<object> l)
            {
                foreach (var item in l)
                    if (item != null) result.Add(Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture));
                return result;
            }
            // A bare number (ids=12345) is also a list of one.
            if (v is long || v is int || v is double) result.Add(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
            return result;
        }
    }
}
