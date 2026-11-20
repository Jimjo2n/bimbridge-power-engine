// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bimbridge.Addin.Scripting
{
    /// <summary>How a recipe's units are committed.</summary>
    public enum PoliticaTx
    {
        /// <summary>Each unit in its own transaction (Undo per unit; a failure rolls back only that unit).</summary>
        PorUnidad,
        /// <summary>The recipe opens its own transactions. The engine measures the actual changes to tell "nothing applied" from "partial".</summary>
        Ninguna,
        /// <summary>Read-only (QC, inventories): opens no transactions; a detected change is reported.</summary>
        Lectura,
    }

    public enum Verificacion { Aplicada, NoAplicada, Desconocida }

    /// <summary>
    /// A unit of work: a stable key (the same when resuming on the same model), data to run it and, optionally, the
    /// elements it reads or changes (if another user changes them between enumeration and run, the unit goes into conflict instead of overwriting).
    /// It can also be a barrier: guardar, sync, pdf, cargar (runs only when everything before it is committed).
    /// </summary>
    public sealed class Unidad
    {
        public Unidad(string clave, object datos = null, IEnumerable<long> ids = null)
        {
            if (string.IsNullOrWhiteSpace(clave)) throw new ArgumentException("every unit needs a stable key");
            Clave = clave;
            Datos = datos;
            Ids = ids?.ToList() ?? new List<long>();
        }

        public string Clave { get; }
        public object Datos { get; }
        public IList<long> Ids { get; }
        public string Barrera { get; private set; }
        public Dictionary<string, object> BarreraArgs { get; private set; }

        /// <summary>Data as a dictionary (if passed that way) to read with D("field").</summary>
        public object D(string campo) => Datos is IDictionary<string, object> d && d.TryGetValue(campo, out var v) ? v : null;

        public static Unidad Guardar(string clave = "guardar") => new Unidad(clave) { Barrera = "guardar", BarreraArgs = new Dictionary<string, object>() };
        public static Unidad Sync(string clave = "sync", string comentario = null) =>
            new Unidad(clave) { Barrera = "sync", BarreraArgs = new Dictionary<string, object> { ["comentario"] = comentario } };
        public static Unidad Pdf(string clave, Dictionary<string, object> opciones) => new Unidad(clave) { Barrera = "pdf", BarreraArgs = opciones ?? new Dictionary<string, object>() };
        public static Unidad Barrier(string clave, string accion, Dictionary<string, object> args = null) =>
            new Unidad(clave) { Barrera = accion, BarreraArgs = args ?? new Dictionary<string, object>() };
    }

    /// <summary>The recipe decided to do nothing on this unit (it was already fine, or a declared exception). It is recorded with the reason.</summary>
    public sealed class OmitirUnidad : Exception
    {
        public OmitirUnidad(string motivo) : base(motivo) { }
    }

    /// <summary>
    /// Step-by-step recipe for the resumable engine (0.6.0, §5). Contract:
    /// - <see cref="Preparar"/>: validates inputs (throws ArgumentException if any are missing) — read-only;
    /// - <see cref="Unidades"/>: enumerates units with STABLE, deterministic keys on the same model; it can be lazy
    ///   (yield): the engine walks it in chunks without blocking Revit;
    /// - <see cref="Ejecutar"/>: applies ONE unit; with PorUnidad the engine opens and commits the transaction and the result is materialized
    ///   before committing;
    /// - <see cref="Verificar"/>: after a crash, was it applied? By default the engine checks whether the created elements exist (UniqueId);
    /// - <see cref="Postcondicion"/>: final read-only QC; return a dictionary with "desviaciones" (list) to flag differences.
    /// Registered by file (recipes\name.cs) or sent as code; no MCP tool per recipe is needed.
    /// </summary>
    public abstract class Receta
    {
        /// <summary>Declared version: part of the task fingerprint.</summary>
        public virtual string Version => "1";
        public virtual string Descripcion => "";
        public virtual PoliticaTx Transaccion => PoliticaTx.PorUnidad;
        /// <summary>Inputs expected in args (only for documentation and presence validation).</summary>
        public virtual IEnumerable<string> Entradas => Enumerable.Empty<string>();

        public virtual void Preparar(ScriptContext ctx)
        {
            var missing = Entradas.Where(e => !ctx.Args.ContainsKey(e)).ToList();
            if (missing.Count > 0) throw new ArgumentException("missing inputs in args: " + string.Join(", ", missing));
        }

        public abstract IEnumerable<Unidad> Unidades(ScriptContext ctx);

        public abstract object Ejecutar(ScriptContext ctx, Unidad u);

        public virtual Verificacion Verificar(ScriptContext ctx, Unidad u) => Verificacion.Desconocida;

        public virtual object Postcondicion(ScriptContext ctx) => null;
    }
}
