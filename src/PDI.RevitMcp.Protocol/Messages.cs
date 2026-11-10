// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace PDI.RevitMcp.Protocol
{
    /// <summary>Constantes del protocolo interno puente ↔ add-in. La IA nunca las ve.</summary>
    public static class Wire
    {
        /// <summary>Se sube cuando cambia el formato. Puente y add-in deben coincidir.</summary>
        public const int ProtocolVersion = 2;

        public const string PipePrefix = "PDI.RevitMcp.";

        // Operaciones
        public const string OpHello = "hello";
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
        /// <summary>Runner general: compila y ejecuta C# sobre la API pública de Revit (consulta revertida, ejecución con transacción o sin ella).</summary>
        public const string OpExec = "exec";
        /// <summary>Documentos abiertos (proyectos y familias) con handle estable dN.</summary>
        public const string OpDocs = "docs";
        /// <summary>Familias: info, caras, conectores, parámetros, editar, cargar (lecturas y escrituras tipadas).</summary>
        public const string OpFam = "fam";
        /// <summary>Operaciones de documento: guardar, guardar como, sincronizar, PDF, cerrar.</summary>
        public const string OpDoc = "doc";
        /// <summary>0.6.0: tareas largas reanudables (iniciar, estado, esperar, cancelar, reanudar, reconciliar, resumen…).</summary>
        public const string OpTask = "tarea";
        /// <summary>Clave de args con el documento destino (handle dN, ruta o título). Sin ella: el activo al recibir la petición, fijo para toda la petición.</summary>
        public const string DocArg = "doc";
        /// <summary>Clave de idempotencia: la misma clave en el mismo documento no se vuelve a aplicar.</summary>
        public const string KeyArg = "clave";
        public const string TaskArg = "tarea";
        /// <summary>Clave opcional en args con el nombre de la herramienta que vio la IA (p. ej. "get"), sólo para el diario.</summary>
        public const string ToolArg = "_tool";
    }

    /// <summary>Estados de respuesta (sección 6 de la arquitectura). Son los que ve la IA.</summary>
    public static class Status
    {
        public const string Ok = "ok";
        public const string EnCola = "en_cola";
        public const string EnProceso = "en_proceso";
        /// <summary>El cambio quedó en la cola de Revit; el usuario lo aplicará con el botón "Aplicar cola".</summary>
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
        /// <summary>Se venció el plazo de la petición y el trabajo se canceló dentro de Revit.</summary>
        public const string TiempoAgotado = "tiempo_agotado";
        /// <summary>La operación no aplica a este tipo de documento o a esta versión de Revit (dice cuál y qué alternativa hay).</summary>
        public const string NoSoportado = "no_soportado";
        /// <summary>El documento o el Revit fijado ya no está abierto. No se redirige a otro.</summary>
        public const string DestinoNoDisponible = "destino_no_disponible";
        /// <summary>Hace falta una decisión del usuario (p. ej. Retener activo, dato ambiguo sin resolver).</summary>
        public const string NecesitaUsuario = "necesita_usuario";
        /// <summary>Una operación con esa clave empezó y no consta su final (Revit se cerró o se cortó): reconciliar antes de repetir.</summary>
        public const string Interrumpido = "interrumpido";
        /// <summary>Se aplicó, pero la verificación posterior encontró diferencias con lo pedido (se listan).</summary>
        public const string ConDiferencias = "completado_con_diferencias";

        // 0.6.0: estados semánticos que distinguen lo aplicado, lo revertido y lo que no se sabe.
        /// <summary>Una parte se confirmó y otra no (p. ej. tx=ninguna con un fallo a mitad, o un documento sí y otro no). Reconciliar antes de repetir.</summary>
        public const string Parcial = "parcial";
        /// <summary>No se puede saber si se aplicó (se perdió la evidencia). Reconciliar antes de repetir.</summary>
        public const string Desconocido = "desconocido";
        /// <summary>Los cambios se confirmaron, pero falló producir la respuesta. No se revirtió nada.</summary>
        public const string AplicadoErrorResultado = "aplicado_error_resultado";
        public const string CancelacionSolicitada = "cancelacion_solicitada";
        /// <summary>Cancelación confirmada: lo confirmado antes de cancelar sigue aplicado y se lista.</summary>
        public const string Cancelado = "cancelado";
        /// <summary>La tarea espera algo del contexto (pausa del usuario, diálogo abierto, documento cerrado). Conserva su avance.</summary>
        public const string EsperandoContexto = "esperando_contexto";
        /// <summary>El diario de operaciones no está disponible: no se aplica una escritura con clave= sin poder garantizar su idempotencia.</summary>
        public const string DiarioNoDisponible = "diario_no_disponible";
        /// <summary>Un nombre coincide con varios documentos o elementos: se devuelven candidatos, no se elige el primero.</summary>
        public const string Ambiguo = "ambiguo";
        /// <summary>Otra tarea o barrera tiene el documento; la escritura no se aplicó.</summary>
        public const string DocumentoOcupado = "documento_ocupado";

        /// <summary>Estados terminales que confirman que NO quedó nada aplicado (se puede reintentar con la misma clave).</summary>
        public static bool ConfirmsNothingApplied(string s) =>
            s == Revertido || s == Rechazado || s == Invalido || s == DestinoNoDisponible || s == NoSoportado || s == NecesitaUsuario ||
            s == Conflicto || s == RevitOcupado || s == Vencido || s == DiarioNoDisponible || s == Ambiguo || s == DocumentoOcupado ||
            s == Incompatible || s == RevitNoDisponible || s == TiempoAgotado;

        /// <summary>Estados terminales que cuentan como aplicado (repetir la clave devuelve el resultado guardado).</summary>
        public static bool CountsAsApplied(string s) => s == Ok || s == ConDiferencias || s == Retenido || s == AplicadoErrorResultado;
    }

    public sealed class Request : IJsonWritable
    {
        public long Id;
        public string Op;
        public Dictionary<string, object> Args = new Dictionary<string, object>(StringComparer.Ordinal);
        /// <summary>Tiempo máximo que el puente esperará; el add-in lo usa para no trabajar en vano.</summary>
        public int DeadlineMs;

        public object ToJson() => new Dictionary<string, object>
        {
            ["id"] = Id,
            ["op"] = Op,
            ["args"] = Args,
            ["deadline_ms"] = DeadlineMs,
        };

        public static Request FromJson(string json)
        {
            var o = Json.ParseObject(json);
            return new Request
            {
                Id = Arg.GetLong(o, "id") ?? 0,
                Op = Arg.GetString(o, "op"),
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

    /// <summary>Lectura tolerante de argumentos JSON ya parseados.</summary>
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

        /// <summary>Acepta "a" o ["a","b"] y devuelve siempre una lista.</summary>
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
            // Un número suelto (ids=12345) también es una lista de uno.
            if (v is long || v is int || v is double) result.Add(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
            return result;
        }
    }
}
