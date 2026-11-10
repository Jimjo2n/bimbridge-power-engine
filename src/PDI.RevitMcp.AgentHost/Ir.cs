// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// BIM Task IR, esquema 2 (0.7.0-alpha1). Dos capas:
    /// - <see cref="PlanDraft"/>/<see cref="OpDraft"/>: lo que llega de un agente, de un documento o de una prueba. Mutable y NO confiable.
    /// - <see cref="ValidatedPlan"/>/<see cref="ValidatedOp"/>: el resultado de <see cref="IrValidator.Validate"/>. Inmutable (colecciones de
    ///   sólo lectura, copia profunda); incluye lo que el sistema DERIVA (autorización, postcondiciones, idempotencia) y nunca acepta del agente.
    /// El estado de ejecución vive aparte (Exec/ExecState); el plan validado no cambia mientras se ejecuta.
    /// </summary>
    public static class IrSchema
    {
        public const int Version = 2;
        public static readonly string[] CoordinateSystems = { "interno_revit", "proyecto", "compartidas" };
        public static readonly string[] LengthUnits = { "mm", "m", "ft", "in" };
        /// <summary>Campos que sólo el sistema puede fijar. Si vienen en el JSON, el plan se rechaza.</summary>
        public static readonly string[] SystemOnlyOpFields = { "autorizacion", "estado", "postcondiciones", "idempotencia", "intento", "resultado" };
        public static readonly string[] SystemOnlyPlanFields = { "autorizacion", "estado", "hash" };
    }

    /// <summary>Documento técnico de origen declarado por el plan (p. ej. un PDF de especificaciones), identificado por su SHA-256.</summary>
    public sealed class SourceDoc
    {
        public string Id, Nombre, Sha256;
        public Dictionary<string, object> ToJson() => new Dictionary<string, object> { ["id"] = Id, ["nombre"] = Nombre, ["sha256"] = Sha256 };
    }

    /// <summary>Trazabilidad de una operación hasta el requisito que la originó.</summary>
    public sealed class SourceRef
    {
        public string Fuente;      // id de un SourceDoc del plan
        public long? Pagina;
        public string Seccion, Requisito, Cita;

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object> { ["fuente"] = Fuente };
            if (Pagina != null) d["pagina"] = Pagina.Value;
            if (Seccion != null) d["seccion"] = Seccion;
            if (Requisito != null) d["requisito"] = Requisito;
            if (Cita != null) d["cita"] = Cita;
            return d;
        }

        public static SourceRef FromJson(Dictionary<string, object> o) => o == null ? null : new SourceRef
        {
            Fuente = Arg.GetString(o, "fuente"), Pagina = Arg.GetLong(o, "pagina"), Seccion = Arg.GetString(o, "seccion"),
            Requisito = Arg.GetString(o, "requisito"), Cita = Arg.GetString(o, "cita"),
        };
    }

    /// <summary>Operación tal como llega (no confiable).</summary>
    public sealed class OpDraft
    {
        public string Id;
        public string Operation;
        public Dictionary<string, object> Scope = new Dictionary<string, object>(StringComparer.Ordinal);
        public Dictionary<string, object> Params = new Dictionary<string, object>(StringComparer.Ordinal);
        public List<string> DependsOn = new List<string>();
        public List<Dictionary<string, object>> Preconditions = new List<Dictionary<string, object>>();
        public Dictionary<string, object> Expected = new Dictionary<string, object>(StringComparer.Ordinal);
        public long? Retries;
        public bool AllowEmpty;
        public SourceRef Source;
        /// <summary>Campos de sistema encontrados al leer el JSON (se informan como error).</summary>
        public List<string> Forbidden = new List<string>();

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = Id, ["operacion"] = Operation, ["alcance"] = Scope, ["parametros"] = Params,
                ["depende_de"] = DependsOn.Cast<object>().ToList(), ["precondiciones"] = Preconditions.Cast<object>().ToList(),
                ["resultado_esperado"] = Expected, ["permitir_vacio"] = AllowEmpty,
            };
            if (Retries != null) d["reintentos"] = Retries.Value;
            if (Source != null) d["fuente"] = Source.ToJson();
            return d;
        }

        public static OpDraft FromJson(Dictionary<string, object> o)
        {
            var op = new OpDraft { Id = Arg.GetString(o, "id"), Operation = Arg.GetString(o, "operacion"), Retries = Arg.GetLong(o, "reintentos") };
            if (o.TryGetValue("alcance", out var s) && s is Dictionary<string, object> sd) op.Scope = sd;
            if (o.TryGetValue("parametros", out var p) && p is Dictionary<string, object> pd) op.Params = pd;
            if (o.TryGetValue("resultado_esperado", out var e) && e is Dictionary<string, object> ed) op.Expected = ed;
            if (o.TryGetValue("depende_de", out var dep) && dep is List<object> dl) op.DependsOn = dl.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList();
            if (o.TryGetValue("precondiciones", out var pre) && pre is List<object> pl) op.Preconditions = pl.OfType<Dictionary<string, object>>().ToList();
            if (o.TryGetValue("fuente", out var f) && f is Dictionary<string, object> fd) op.Source = SourceRef.FromJson(fd);
            op.AllowEmpty = Arg.GetBool(o, "permitir_vacio") ?? false;
            foreach (var k in IrSchema.SystemOnlyOpFields) if (o.ContainsKey(k)) op.Forbidden.Add(k);
            return op;
        }

        /// <summary>Id que el sistema espera para este contenido dentro de <paramref name="plan"/>.</summary>
        public string ExpectedId(PlanDraft plan) =>
            IrIds.OpId(plan.DocKey, plan.DocPrint, Scope, Operation, Params, plan.SourceIdentity(Source));
    }

    /// <summary>Plan tal como llega (no confiable).</summary>
    public sealed class PlanDraft
    {
        public int Ir = IrSchema.Version;
        public string Objective;
        public string DocKey, DocPrint;
        public string LengthUnit;
        public string Coordinates;
        public List<SourceDoc> Sources = new List<SourceDoc>();
        public List<OpDraft> Ops = new List<OpDraft>();
        public List<string> Forbidden = new List<string>();

        /// <summary>Identidad de la fuente de una operación para su id: sha256 del documento + requisito (+ página). Sin fuente: "".</summary>
        public string SourceIdentity(SourceRef r)
        {
            if (r == null) return "";
            var doc = Sources.FirstOrDefault(s => s.Id == r.Fuente);
            return (doc?.Sha256 ?? ("?" + r.Fuente)) + "|" + (r.Requisito ?? "") + "|" + (r.Pagina?.ToString(CultureInfo.InvariantCulture) ?? "");
        }

        /// <summary>Asigna a cada operación su id determinista. Las dependencias se expresan por índice con "#n" antes de llamar.</summary>
        public PlanDraft AssignIds()
        {
            var ids = Ops.Select(o => o.ExpectedId(this)).ToList();
            for (var i = 0; i < Ops.Count; i++)
            {
                Ops[i].Id = ids[i];
                Ops[i].DependsOn = Ops[i].DependsOn.Select(d => d.StartsWith("#", StringComparison.Ordinal) ? ids[int.Parse(d.Substring(1), CultureInfo.InvariantCulture)] : d).ToList();
            }
            return this;
        }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["ir"] = (long)Ir, ["objetivo"] = Objective,
            ["documento"] = new Dictionary<string, object> { ["clave"] = DocKey, ["huella"] = DocPrint },
            ["unidades"] = new Dictionary<string, object> { ["longitud"] = LengthUnit },
            ["coordenadas"] = Coordinates,
            ["fuentes"] = Sources.Select(s => (object)s.ToJson()).ToList(),
            ["operaciones"] = Ops.Select(x => (object)x.ToJson()).ToList(),
        };

        public static PlanDraft Parse(string json) => FromJson(Json.ParseObject(json));

        public static PlanDraft FromJson(Dictionary<string, object> o)
        {
            var plan = new PlanDraft
            {
                Ir = (int)(Arg.GetLong(o, "ir") ?? 0), Objective = Arg.GetString(o, "objetivo"), Coordinates = Arg.GetString(o, "coordenadas"),
            };
            if (o.TryGetValue("documento", out var d) && d is Dictionary<string, object> doc) { plan.DocKey = Arg.GetString(doc, "clave"); plan.DocPrint = Arg.GetString(doc, "huella"); }
            if (o.TryGetValue("unidades", out var u) && u is Dictionary<string, object> ud) plan.LengthUnit = Arg.GetString(ud, "longitud");
            if (o.TryGetValue("fuentes", out var fs) && fs is List<object> fl)
                foreach (var x in fl.OfType<Dictionary<string, object>>())
                    plan.Sources.Add(new SourceDoc { Id = Arg.GetString(x, "id"), Nombre = Arg.GetString(x, "nombre"), Sha256 = Arg.GetString(x, "sha256") });
            if (o.TryGetValue("operaciones", out var ops) && ops is List<object> list)
                foreach (var x in list.OfType<Dictionary<string, object>>()) plan.Ops.Add(OpDraft.FromJson(x));
            foreach (var k in IrSchema.SystemOnlyPlanFields) if (o.ContainsKey(k)) plan.Forbidden.Add(k);
            return plan;
        }
    }

    public static class IrIds
    {
        /// <summary>
        /// Id determinista de una operación: "op-" + 20 hex de sha256 sobre (documento: clave + huella, alcance, operación, parámetros
        /// canónicos, identidad de la fuente). Incluir la huella evita reutilizar un id contra un modelo sustituido; incluir la fuente evita
        /// confundir dos requisitos distintos que piden lo mismo (esos se detectan aparte como duplicados semánticos).
        /// </summary>
        public static string OpId(string docKey, string docPrint, IDictionary<string, object> scope, string operation, IDictionary<string, object> parameters, string sourceIdentity) =>
            "op-" + Home.Sha256Hex(OpSemantics.Canonical(new Dictionary<string, object>
            {
                ["doc"] = docKey ?? "", ["huella"] = docPrint ?? "", ["alcance"] = scope ?? new Dictionary<string, object>(), ["operacion"] = operation ?? "",
                ["parametros"] = parameters ?? new Dictionary<string, object>(), ["fuente"] = sourceIdentity ?? "",
            }), 20);

        /// <summary>Clave semántica: lo mismo sin la fuente. Dos operaciones del mismo plan con igual clave son un duplicado.</summary>
        public static string SemanticKey(IDictionary<string, object> scope, string operation, IDictionary<string, object> parameters) =>
            Home.Sha256Hex(OpSemantics.Canonical(new Dictionary<string, object> { ["alcance"] = scope, ["operacion"] = operation, ["parametros"] = parameters }), 20);

        /// <summary>Copia profunda de sólo lectura (diccionarios y listas).</summary>
        public static object Freeze(object v)
        {
            switch (v)
            {
                case IDictionary<string, object> d:
                    return new ReadOnlyDictionary<string, object>(d.ToDictionary(kv => kv.Key, kv => Freeze(kv.Value), StringComparer.Ordinal));
                case string _: return v;
                case System.Collections.IEnumerable e:
                    return new ReadOnlyCollection<object>(e.Cast<object>().Select(Freeze).ToList());
                default: return v;
            }
        }

        public static IReadOnlyDictionary<string, object> FreezeDict(IDictionary<string, object> d) =>
            (IReadOnlyDictionary<string, object>)Freeze(d ?? new Dictionary<string, object>());

        /// <summary>Vuelve a una forma mutable serializable (para escribir JSON).</summary>
        public static object Thaw(object v)
        {
            switch (v)
            {
                case IReadOnlyDictionary<string, object> d: return d.ToDictionary(kv => kv.Key, kv => Thaw(kv.Value), StringComparer.Ordinal);
                case IDictionary<string, object> d2: return d2.ToDictionary(kv => kv.Key, kv => Thaw(kv.Value), StringComparer.Ordinal);
                case string _: return v;
                case System.Collections.IEnumerable e: return e.Cast<object>().Select(Thaw).ToList();
                default: return v;
            }
        }
    }

    /// <summary>Precondición del catálogo cerrado. Hoy: número de objetivos dentro de [Min, Max].</summary>
    public sealed class Precondition
    {
        public string Kind;
        public long? Min, Max;
        public override string ToString() => Kind + "[" + (Min?.ToString(CultureInfo.InvariantCulture) ?? "") + "," + (Max?.ToString(CultureInfo.InvariantCulture) ?? "") + "]";
    }

    /// <summary>Operación validada e inmutable. Lo derivado por el sistema está marcado.</summary>
    public sealed class ValidatedOp
    {
        public string Id { get; }
        public string Operation { get; }
        public OpSpec Spec { get; }
        public IReadOnlyDictionary<string, object> Scope { get; }
        public IReadOnlyDictionary<string, object> Params { get; }
        public IReadOnlyList<string> DependsOn { get; }
        public IReadOnlyList<Precondition> Preconditions { get; }
        public IReadOnlyDictionary<string, object> Expected { get; }
        public bool AllowEmpty { get; }
        public int RetryMax { get; }
        public SourceRef Source { get; }
        // derivado por el sistema:
        public bool Writes => Spec.Writes;
        public string Idempotency => Spec.Idempotency;
        public IReadOnlyList<string> Authorization => Spec.Authorization;
        public IReadOnlyList<string> Postconditions => Spec.Postconditions;

        internal ValidatedOp(OpDraft d, OpSpec spec, IReadOnlyList<Precondition> pre)
        {
            Id = d.Id; Operation = d.Operation; Spec = spec;
            Scope = IrIds.FreezeDict(d.Scope); Params = IrIds.FreezeDict(d.Params); Expected = IrIds.FreezeDict(d.Expected);
            DependsOn = new ReadOnlyCollection<string>(d.DependsOn.Distinct(StringComparer.Ordinal).ToList());
            Preconditions = pre; AllowEmpty = d.AllowEmpty; RetryMax = (int)(d.Retries ?? 0);
            Source = d.Source == null ? null : new SourceRef { Fuente = d.Source.Fuente, Pagina = d.Source.Pagina, Seccion = d.Source.Seccion, Requisito = d.Source.Requisito, Cita = d.Source.Cita };
        }

        public string Param(string k) => Params.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        public string ScopeValue(string k) => Scope.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Plan validado e inmutable. <see cref="Hash"/> identifica su contenido canónico.</summary>
    public sealed class ValidatedPlan
    {
        public string Hash { get; }
        public string Objective { get; }
        public string DocKey { get; }
        public string DocPrint { get; }
        public string LengthUnit { get; }
        public string Coordinates { get; }
        public IReadOnlyList<SourceDoc> Sources { get; }
        public IReadOnlyList<ValidatedOp> Ops { get; }
        /// <summary>Orden topológico determinista (empates por id).</summary>
        public IReadOnlyList<string> Order { get; }
        public IReadOnlyDictionary<string, ValidatedOp> ById { get; }
        /// <summary>JSON canónico del borrador validado (lo que se guarda en plan.json).</summary>
        public string CanonicalJson { get; }

        internal ValidatedPlan(PlanDraft d, List<ValidatedOp> ops, List<string> order)
        {
            var draftJson = d.ToJson();
            CanonicalJson = OpSemantics.Canonical(draftJson);
            Hash = Home.Sha256Hex(CanonicalJson, 20);
            Objective = d.Objective; DocKey = d.DocKey; DocPrint = d.DocPrint; LengthUnit = d.LengthUnit; Coordinates = d.Coordinates;
            Sources = new ReadOnlyCollection<SourceDoc>(d.Sources.Select(s => new SourceDoc { Id = s.Id, Nombre = s.Nombre, Sha256 = s.Sha256 }).ToList());
            Ops = new ReadOnlyCollection<ValidatedOp>(ops);
            Order = new ReadOnlyCollection<string>(order);
            ById = new ReadOnlyDictionary<string, ValidatedOp>(ops.ToDictionary(o => o.Id, StringComparer.Ordinal));
        }

        public static string HashOf(PlanDraft d) => Home.Sha256Hex(OpSemantics.Canonical(d.ToJson()), 20);
    }
}
