// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine.Workflow
{
    /// <summary>
    /// 0.8.0 (contrato de entrega; incidentes del informe: "se sincronizó una rama y se abrió otra", "la lámina se revisó pero el PDF
    /// era de una revisión anterior"). Una entrega avanza por hitos DISTINTOS, cada uno con su recibo. No se salta un hito, no se
    /// declara uno posterior por inferencia y un recibo de otro documento o de otra revisión no vale.
    /// </summary>
    public static class DeliveryStages
    {
        public const string PlanGenerado = "plan_generado", PlanValidado = "plan_validado", PlanAutorizado = "plan_autorizado",
            Ejecutada = "operacion_ejecutada", CommitConfirmado = "commit_confirmado", EstadoObservado = "estado_observado",
            Guardado = "cambios_guardados", Sincronizado = "modelo_sincronizado", SetPersistido = "set_laminas_persistido",
            Exportado = "archivo_exportado", ArtefactoVerificado = "artefacto_verificado", EntregaConfirmada = "entrega_confirmada";

        /// <summary>Orden canónico. Guardado/Sincronizado/Set son opcionales según la entrega (ver <see cref="DeliveryTrack.Required"/>).</summary>
        public static readonly string[] Order =
        {
            PlanGenerado, PlanValidado, PlanAutorizado, Ejecutada, CommitConfirmado, EstadoObservado,
            Guardado, Sincronizado, SetPersistido, Exportado, ArtefactoVerificado, EntregaConfirmada,
        };

        /// <summary>
        /// 0.9.0 (P0): hitos críticos. Su recibo exige la revisión del modelo y procedencia: "origen" (qué operación o persona lo
        /// produjo) y "evidencia" = COMPROBADO (observado por BIMBRIDGE). Un resultado REPORTADO nunca cierra un hito crítico,
        /// salvo la confirmación de entrega, que es una declaración de una persona ("origen" = "usuario:…").
        /// </summary>
        public static readonly HashSet<string> Critical = new HashSet<string>
        {
            CommitConfirmado, EstadoObservado, Guardado, Sincronizado, SetPersistido, Exportado, ArtefactoVerificado, EntregaConfirmada,
        };

        /// <summary>Qué debe traer el recibo de cada hito (además de documento y revisión).</summary>
        public static readonly Dictionary<string, string[]> ReceiptFields = new Dictionary<string, string[]>
        {
            [PlanGenerado] = new[] { "plan_id" },
            [PlanValidado] = new[] { "plan_id", "validador" },
            [PlanAutorizado] = new[] { "plan_id", "autoriza" },
            [Ejecutada] = new[] { "plan_id" },
            [CommitConfirmado] = new[] { "plan_id", "rev" },
            [EstadoObservado] = new[] { "rev", "lectura" },
            [Guardado] = new[] { "huella_disco" },
            [Sincronizado] = new[] { "central", "huella_central" },
            [SetPersistido] = new[] { "set", "laminas" },
            [Exportado] = new[] { "archivo", "sha256" },
            [ArtefactoVerificado] = new[] { "archivo", "sha256", "paginas" },
            [EntregaConfirmada] = new[] { "confirma" },
        };
    }

    public sealed class DeliveryReceipt
    {
        public string Stage;
        public string DocKey;       // identidad del documento (clave + huella)
        public string Revision;     // revisión/snapshot del modelo al que se refiere
        public string At;
        public Dictionary<string, object> Data = new Dictionary<string, object>();

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["hito"] = Stage, ["documento"] = DocKey, ["revision"] = Revision, ["en"] = At, ["datos"] = Data,
        };

        public static DeliveryReceipt FromJson(Dictionary<string, object> d) => new DeliveryReceipt
        {
            Stage = Arg.GetString(d, "hito"), DocKey = Arg.GetString(d, "documento"), Revision = Arg.GetString(d, "revision"), At = Arg.GetString(d, "en"),
            Data = d.TryGetValue("datos", out var x) && x is Dictionary<string, object> dd ? dd : new Dictionary<string, object>(),
        };
    }

    /// <summary>Seguimiento de una entrega concreta sobre UN documento.</summary>
    public sealed class DeliveryTrack
    {
        public string Id;
        public string DocKey;
        public List<string> Required;   // hitos que esta entrega exige, en orden canónico
        public readonly List<DeliveryReceipt> Receipts = new List<DeliveryReceipt>();

        public DeliveryTrack(string id, string docKey, IEnumerable<string> required = null)
        {
            Id = id; DocKey = docKey;
            var req = new HashSet<string>(required ?? DeliveryStages.Order);
            Required = DeliveryStages.Order.Where(req.Contains).ToList();
        }

        public string Current => Receipts.Count == 0 ? null : Receipts[Receipts.Count - 1].Stage;
        public string Next => Required.FirstOrDefault(s => !Receipts.Any(r => r.Stage == s));
        public bool Complete => Next == null;

        /// <summary>Registra un hito. Devuelve null si se acepta, o el motivo del rechazo.</summary>
        public string Record(DeliveryReceipt r)
        {
            if (r == null || string.IsNullOrEmpty(r.Stage)) return "recibo vacío";
            if (!Required.Contains(r.Stage)) return "el hito " + r.Stage + " no forma parte de esta entrega";
            if (Receipts.Any(x => x.Stage == r.Stage)) return "el hito " + r.Stage + " ya tiene recibo";
            if (r.Stage != Next) return "fuera de orden: el siguiente hito es " + Next + ", no " + r.Stage + " (no se declara un hito posterior por inferencia)";
            if (!string.Equals(r.DocKey, DocKey, StringComparison.OrdinalIgnoreCase))
                return "el recibo es de otro documento (" + r.DocKey + "); un resultado en un documento no prueba nada en " + DocKey;
            foreach (var f in DeliveryStages.ReceiptFields[r.Stage])
                if (!r.Data.TryGetValue(f, out var v) || v == null || (v is string s && s.Length == 0)) return r.Stage + ": falta " + f + " en el recibo";
            if (DeliveryStages.Critical.Contains(r.Stage))
            {
                if (string.IsNullOrWhiteSpace(r.Revision)) return r.Stage + ": hito crítico sin revisión del modelo; no se sabe a qué estado se refiere";
                var origin = Arg.GetString(r.Data, "origen");
                if (string.IsNullOrWhiteSpace(origin)) return r.Stage + ": falta la procedencia (origen: operación o persona que lo produjo)";
                var ev = Arg.GetString(r.Data, "evidencia");
                if (r.Stage == DeliveryStages.EntregaConfirmada)
                {
                    if (!origin.StartsWith("usuario:", StringComparison.OrdinalIgnoreCase)) return r.Stage + ": la confirmación de entrega la da una persona (origen=usuario:…), no el agente";
                }
                else if (ev != Confidence.Comprobado)
                    return r.Stage + ": la evidencia es " + (ev ?? "(sin indicar)") + "; un hito crítico exige COMPROBADO (observado por BIMBRIDGE), no un resultado reportado";
            }
            // 0.9.0: el artefacto exportado y su verificación deben ser de la MISMA revisión (no sólo no anterior)
            if (r.Stage == DeliveryStages.ArtefactoVerificado)
            {
                var exp0 = Receipts.FirstOrDefault(x => x.Stage == DeliveryStages.Exportado);
                if (exp0 != null && exp0.Revision != null && !string.Equals(exp0.Revision, r.Revision, StringComparison.Ordinal))
                    return "artefacto_verificado: se verificó la revisión " + r.Revision + " pero se exportó la " + exp0.Revision;
            }
            // la revisión no puede retroceder: un PDF de una revisión anterior al commit no vale
            var lastRev = Receipts.LastOrDefault(x => x.Revision != null)?.Revision;
            if (lastRev != null && r.Revision != null && CompareRev(r.Revision, lastRev) < 0)
                return r.Stage + ": el recibo es de la revisión " + r.Revision + ", anterior a " + lastRev + " (artefacto obsoleto)";
            if (r.Stage == DeliveryStages.ArtefactoVerificado)
            {
                var exp = Receipts.FirstOrDefault(x => x.Stage == DeliveryStages.Exportado);
                if (exp != null && !string.Equals(Arg.GetString(exp.Data, "sha256"), Arg.GetString(r.Data, "sha256"), StringComparison.OrdinalIgnoreCase))
                    return "artefacto_verificado: el archivo verificado no es el exportado (sha256 distinto)";
                var set = Receipts.FirstOrDefault(x => x.Stage == DeliveryStages.SetPersistido);
                var expected = set == null ? null : Arg.GetLong(set.Data, "laminas");
                var pages = Arg.GetLong(r.Data, "paginas");
                if (expected.HasValue && pages.HasValue && expected.Value != pages.Value)
                    return "artefacto_verificado: el PDF tiene " + pages + " páginas y el set " + expected + " láminas";
            }
            r.At = r.At ?? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            Receipts.Add(r);
            return null;
        }

        private static int CompareRev(string a, string b)
        {
            if (long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) && long.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) return x.CompareTo(y);
            return string.CompareOrdinal(a, b);
        }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["id"] = Id, ["documento"] = DocKey, ["hitos"] = Required.Cast<object>().ToList(), ["actual"] = Current, ["siguiente"] = Next,
            ["completa"] = Complete, ["recibos"] = Receipts.Select(r => (object)r.ToJson()).ToList(),
        };
    }
}
