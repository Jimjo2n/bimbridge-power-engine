// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>Definición de una operación del catálogo cerrado. Todo lo que no es parámetro lo fija el sistema, no el plan.</summary>
    public sealed class OpSpec
    {
        public string Name;
        public bool Writes;
        public Dictionary<string, string> Required = new Dictionary<string, string>(StringComparer.Ordinal);   // nombre → "texto" | "numero" | "valor" | "lista"
        public Dictionary<string, string> Optional = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string[]> Allowed = new Dictionary<string, string[]>(StringComparer.Ordinal);
        /// <summary>Claves de alcance admitidas.</summary>
        public string[] ScopeKeys = new string[0];
        /// <summary>Claves de resultado que el plan puede declarar como esperadas (y que el backend devuelve).</summary>
        public string[] ExpectedKeys = new string[0];
        /// <summary>"lectura" (repetir es inocuo) | "convergente" (aplicar dos veces deja el mismo estado) | "no_idempotente".</summary>
        public string Idempotency = "lectura";
        public IReadOnlyList<string> Authorization = new ReadOnlyCollection<string>(new string[0]);
        /// <summary>Postcondiciones que el sistema comprueba contra el estado del modelo (no las declara el agente).</summary>
        public IReadOnlyList<string> Postconditions = new ReadOnlyCollection<string>(new string[0]);
    }

    /// <summary>
    /// Validador del esquema IR 2. Rechaza: versión desconocida (sin "mejor esfuerzo"), campos que sólo fija el sistema, operaciones fuera
    /// del catálogo, parámetros con tipo o valor incorrecto, unidades o coordenadas implícitas, ids que no cuadran con el contenido, ids
    /// repetidos, duplicados semánticos, dependencias desconocidas, ciclos, fuentes no declaradas y precondiciones fuera del catálogo.
    /// Sólo si no hay errores devuelve un <see cref="ValidatedPlan"/> inmutable.
    /// </summary>
    public static class IrValidator
    {
        public const int MaxRetries = 3;
        /// <summary>Entradas máximas de un lote (igual que la página del add-in).</summary>
        public const int MaxBatch = 200;

        public static readonly Dictionary<string, OpSpec> Catalog = new[]
        {
            new OpSpec { Name = "consultar", Required = { ["categoria"] = "texto" }, Optional = { ["filtro"] = "texto" },
                         ExpectedKeys = new[] { "elementos" } },
            new OpSpec { Name = "fijar_parametro", Writes = true, Idempotency = "convergente",
                         Required = { ["parametro"] = "texto", ["valor"] = "valor" }, Optional = { ["unidades"] = "texto", ["tolerancia"] = "numero" },
                         Allowed = { ["unidades"] = IrSchema.LengthUnits.Concat(new[] { "texto", "adimensional" }).ToArray() },
                         ScopeKeys = new[] { "categoria", "nivel", "ids" }, ExpectedKeys = new[] { "elementos", "cambiados" },
                         Authorization = new ReadOnlyCollection<string>(new[] { "escritura_modelo" }),
                         Postconditions = new ReadOnlyCollection<string>(new[] { "todos los objetivos tienen parametro = valor" }) },
            // rc1: escritura por lotes. Un valor de texto por id; un solo plan/commit en el add-in. alcance.ids = claves de valores.
            new OpSpec { Name = "fijar_parametro_lote", Writes = true, Idempotency = "convergente",
                         Required = { ["parametro"] = "texto", ["valores"] = "mapa_texto" },
                         ScopeKeys = new[] { "categoria", "ids" }, ExpectedKeys = new[] { "elementos", "cambiados" },
                         Authorization = new ReadOnlyCollection<string>(new[] { "escritura_modelo" }),
                         Postconditions = new ReadOnlyCollection<string>(new[] { "cada objetivo tiene parametro = su valor del lote" }) },
            new OpSpec { Name = "verificar_qc", Required = { ["receta"] = "texto" }, Allowed = { ["receta"] = new[] { "qc_manifiesto", "montaje_soportes", "qc_luminarias_tags" } },
                         ScopeKeys = new[] { "categoria", "nivel" }, ExpectedKeys = new[] { "desviaciones" },
                         Postconditions = new ReadOnlyCollection<string>(new[] { "desviaciones = 0" }) },
            // 0.8.0 (CAP-008): contraste con una fuente independiente. esperado: clave → valor esperado del atributo (si hay parametro).
            new OpSpec { Name = "verificar_independiente",
                         Required = { ["fuente_id"] = "texto", ["fuente_tipo"] = "texto", ["fuente_sha256"] = "texto", ["fuente_construccion"] = "texto", ["parametro_clave"] = "texto", ["esperado"] = "mapa_texto" },
                         // 0.9.0 (P0): independencia comprobable: contenido (sha256) y linaje de ambas partes; huella del plan para detectar copias.
                         Optional = { ["fuente_revision"] = "texto", ["revision_exigida"] = "texto", ["parametro"] = "texto", ["excepciones"] = "mapa_texto",
                                      ["fuente_derivada_de"] = "texto", ["fuente_construccion_sha256"] = "texto", ["construccion_derivada_de"] = "texto", ["huella_plan"] = "texto" },
                         Allowed = { ["fuente_tipo"] = PDI.RevitMcp.Engine.Workflow.SourceKinds.All.Where(k => !PDI.RevitMcp.Engine.Workflow.SourceKinds.NeverIndependent(k)).ToArray() },
                         ScopeKeys = new[] { "categoria", "nivel" }, ExpectedKeys = new[] { "desviaciones" },
                         Postconditions = new ReadOnlyCollection<string>(new[] { "omitidos + adicionales + duplicados + discrepancias = 0 frente a la fuente independiente" }) },
        }.ToDictionary(s => s.Name, StringComparer.Ordinal);

        public static List<string> Validate(PlanDraft plan) => Validate(plan, out _);

        public static List<string> Validate(PlanDraft plan, out ValidatedPlan validated)
        {
            validated = null;
            var errors = new List<string>();
            if (plan.Ir != IrSchema.Version) errors.Add("version_no_soportada: ir=" + plan.Ir + " (se admite " + IrSchema.Version + ")");
            if (string.IsNullOrWhiteSpace(plan.Objective)) errors.Add("falta objetivo");
            if (string.IsNullOrEmpty(plan.DocKey)) errors.Add("falta documento.clave");
            if (string.IsNullOrEmpty(plan.DocPrint)) errors.Add("falta documento.huella (identidad fuerte del modelo)");
            if (plan.LengthUnit == null || !IrSchema.LengthUnits.Contains(plan.LengthUnit)) errors.Add("unidades.longitud explícita requerida (" + string.Join("|", IrSchema.LengthUnits) + ")");
            if (plan.Coordinates == null || !IrSchema.CoordinateSystems.Contains(plan.Coordinates)) errors.Add("coordenadas explícitas requeridas (" + string.Join("|", IrSchema.CoordinateSystems) + ")");
            if (plan.Ops.Count == 0) errors.Add("el plan no tiene operaciones");
            foreach (var f in plan.Forbidden) errors.Add("campo_no_declarable: plan." + f + " (lo calcula el sistema)");
            var srcIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in plan.Sources)
            {
                if (string.IsNullOrEmpty(s.Id) || !srcIds.Add(s.Id)) errors.Add("fuente sin id o repetida: " + s.Id);
                if (s.Sha256 == null || s.Sha256.Length != 64 || !s.Sha256.All(Uri.IsHexDigit)) errors.Add("fuente " + s.Id + ": sha256 inválido");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var semantic = new Dictionary<string, string>(StringComparer.Ordinal);
            var pres = new Dictionary<OpDraft, List<Precondition>>();
            foreach (var op in plan.Ops)
            {
                var who = op.Id ?? "(sin id)";
                foreach (var f in op.Forbidden) errors.Add(who + ": campo_no_declarable " + f + " (lo calcula el sistema)");
                OpSpec spec = null;
                if (op.Operation == null || !Catalog.TryGetValue(op.Operation, out spec)) errors.Add(who + ": operacion_fuera_de_catalogo '" + op.Operation + "'");
                if (spec != null)
                {
                    CheckParams(who, op, spec, errors);
                    foreach (var k in op.Scope.Keys) if (!spec.ScopeKeys.Contains(k)) errors.Add(who + ": alcance." + k + " no admitido");
                    if (op.Scope.TryGetValue("ids", out var sids))
                    {
                        // alpha3: ids explícitos estrechan el alcance (nunca lo amplían): lista de enteros positivos, 1..500, sin repetir
                        var l = sids as List<object>;
                        if (l == null || l.Count == 0 || l.Count > 500 || l.Any(x => !(x is long n) || n <= 0) || l.Distinct().Count() != l.Count)
                            errors.Add(who + ": alcance.ids debe ser una lista de 1 a 500 ids enteros positivos sin repetir");
                    }
                    foreach (var k in op.Expected.Keys) if (!spec.ExpectedKeys.Contains(k)) errors.Add(who + ": resultado_esperado." + k + " no admitido");
                    if (op.AllowEmpty && !spec.Writes) errors.Add(who + ": permitir_vacio sólo aplica a escrituras");
                }
                if (op.Retries != null && (op.Retries < 0 || op.Retries > MaxRetries)) errors.Add(who + ": reintentos fuera de 0.." + MaxRetries);
                if (op.Source != null)
                {
                    if (string.IsNullOrEmpty(op.Source.Fuente) || !srcIds.Contains(op.Source.Fuente)) errors.Add(who + ": fuente '" + op.Source.Fuente + "' no declarada en el plan");
                    if (op.Source.Pagina != null && op.Source.Pagina < 1) errors.Add(who + ": página inválida");
                }
                pres[op] = ParsePre(who, op, errors);
                var expected = op.ExpectedId(plan);
                if (op.Id == null) errors.Add("operación sin id (esperado " + expected + ")");
                else if (op.Id != expected) errors.Add(who + ": id_no_determinista (esperado " + expected + ")");
                if (op.Id != null && !ids.Add(op.Id)) errors.Add(who + ": id repetido");
                var sk = IrIds.SemanticKey(op.Scope, op.Operation, op.Params);
                if (semantic.TryGetValue(sk, out var first)) errors.Add(who + ": duplicado_semantico de " + first + " (mismo alcance, operación y parámetros)");
                else semantic[sk] = who;
            }
            foreach (var op in plan.Ops)
                foreach (var d in op.DependsOn)
                    if (d == op.Id) errors.Add(op.Id + ": depende de sí misma");
                    else if (!ids.Contains(d)) errors.Add((op.Id ?? "?") + ": dependencia desconocida " + d);
            var cycle = PlanGraph.FindCycle(plan.Ops);
            if (cycle != null) errors.Add("ciclo: " + string.Join(" → ", cycle));
            if (errors.Count > 0) return errors;

            var vops = plan.Ops.Select(o => new ValidatedOp(o, Catalog[o.Operation], new ReadOnlyCollection<Precondition>(pres[o]))).ToList();
            var order = PlanGraph.Order(vops).ToList();
            validated = new ValidatedPlan(plan, vops, order);
            return errors;
        }

        private static List<Precondition> ParsePre(string who, OpDraft op, List<string> errors)
        {
            var res = new List<Precondition>();
            foreach (var p in op.Preconditions)
            {
                var kind = Arg.GetString(p, "tipo");
                if (kind != "objetivos") { errors.Add(who + ": precondición fuera de catálogo '" + kind + "'"); continue; }
                foreach (var k in p.Keys) if (k != "tipo" && k != "min" && k != "max") errors.Add(who + ": precondición objetivos: campo desconocido " + k);
                var pc = new Precondition { Kind = kind, Min = Arg.GetLong(p, "min"), Max = Arg.GetLong(p, "max") };
                if (pc.Min == null && pc.Max == null) errors.Add(who + ": precondición objetivos sin min ni max");
                if (pc.Min != null && pc.Max != null && pc.Min > pc.Max) errors.Add(who + ": precondición objetivos con min > max");
                res.Add(pc);
            }
            return res;
        }

        private static void CheckParams(string who, OpDraft op, OpSpec spec, List<string> errors)
        {
            foreach (var r in spec.Required)
                if (!op.Params.TryGetValue(r.Key, out var v) || v == null) errors.Add(who + ": falta parametros." + r.Key);
                else if (!TypeOk(v, r.Value)) errors.Add(who + ": parametros." + r.Key + " debe ser " + r.Value);
            foreach (var k in op.Params.Keys)
            {
                if (!spec.Required.ContainsKey(k) && !spec.Optional.ContainsKey(k)) { errors.Add(who + ": parámetro desconocido " + k); continue; }
                if (spec.Optional.TryGetValue(k, out var t) && op.Params[k] != null && !TypeOk(op.Params[k], t)) errors.Add(who + ": parametros." + k + " debe ser " + t);
                if (spec.Allowed.TryGetValue(k, out var allowed) && !allowed.Contains(Convert.ToString(op.Params[k], CultureInfo.InvariantCulture)))
                    errors.Add(who + ": parametros." + k + " fuera de " + string.Join("|", allowed));
            }
            if (spec.Name == "fijar_parametro_lote" && op.Params.TryGetValue("valores", out var mv) && mv is IDictionary<string, object> map)
            {
                if (map.Count < 1 || map.Count > MaxBatch) errors.Add(who + ": parametros.valores debe tener entre 1 y " + MaxBatch + " entradas");
                if (map.Keys.Any(k => !long.TryParse(k, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)))
                    errors.Add(who + ": parametros.valores: las claves deben ser ids numéricos");
                var sids = op.Scope.TryGetValue("ids", out var sv) && sv is List<object> sl ? sl.Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)).ToList() : null;
                if (sids == null) errors.Add(who + ": fijar_parametro_lote exige alcance.ids");
                else if (sids.Count != map.Count || sids.Except(map.Keys).Any()) errors.Add(who + ": alcance.ids debe coincidir exactamente con las claves de parametros.valores");
            }
            // unidades explícitas: un valor numérico sin unidades es ambiguo
            if (spec.Name == "fijar_parametro" && op.Params.TryGetValue("valor", out var val) && (val is long || val is double || val is int) && !op.Params.ContainsKey("unidades"))
                errors.Add(who + ": unidades_no_explicitas (valor numérico sin parametros.unidades)");
        }

        private static bool TypeOk(object v, string type)
        {
            switch (type)
            {
                case "texto": return v is string s && s.Length > 0;
                case "numero": return v is long || v is double || v is int;
                case "valor": return v is string || v is long || v is double || v is int || v is bool;
                case "lista": return v is List<object>;
                case "mapa_texto": return v is IDictionary<string, object> m && m.Values.All(x => x is string t && t.Length > 0);
                default: return false;
            }
        }
    }
}
