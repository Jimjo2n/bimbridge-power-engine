// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDI.RevitMcp.Addin.Scripting
{
    /// <summary>Cómo se confirman las unidades de una receta.</summary>
    public enum PoliticaTx
    {
        /// <summary>Cada unidad en su propia transacción (Undo por unidad; un fallo revierte sólo esa unidad).</summary>
        PorUnidad,
        /// <summary>La receta abre sus propias transacciones. El motor mide los cambios reales para distinguir "nada aplicado" de "parcial".</summary>
        Ninguna,
        /// <summary>Sólo lectura (QC, inventarios): no abre transacciones; un cambio detectado se informa.</summary>
        Lectura,
    }

    public enum Verificacion { Aplicada, NoAplicada, Desconocida }

    /// <summary>
    /// Una unidad de trabajo: clave estable (la misma al reanudar sobre el mismo modelo), datos para ejecutarla y, opcionalmente, los
    /// elementos que lee o cambia (si otro usuario los cambia entre la enumeración y la ejecución, la unidad queda en conflicto, no se pisa).
    /// También puede ser una barrera: guardar, sync, pdf, cargar (corre sólo cuando todo lo anterior está confirmado).
    /// </summary>
    public sealed class Unidad
    {
        public Unidad(string clave, object datos = null, IEnumerable<long> ids = null)
        {
            if (string.IsNullOrWhiteSpace(clave)) throw new ArgumentException("cada unidad necesita una clave estable");
            Clave = clave;
            Datos = datos;
            Ids = ids?.ToList() ?? new List<long>();
        }

        public string Clave { get; }
        public object Datos { get; }
        public IList<long> Ids { get; }
        public string Barrera { get; private set; }
        public Dictionary<string, object> BarreraArgs { get; private set; }

        /// <summary>Datos como diccionario (si se pasaron así) para leerlos con D("campo").</summary>
        public object D(string campo) => Datos is IDictionary<string, object> d && d.TryGetValue(campo, out var v) ? v : null;

        public static Unidad Guardar(string clave = "guardar") => new Unidad(clave) { Barrera = "guardar", BarreraArgs = new Dictionary<string, object>() };
        public static Unidad Sync(string clave = "sync", string comentario = null) =>
            new Unidad(clave) { Barrera = "sync", BarreraArgs = new Dictionary<string, object> { ["comentario"] = comentario } };
        public static Unidad Pdf(string clave, Dictionary<string, object> opciones) => new Unidad(clave) { Barrera = "pdf", BarreraArgs = opciones ?? new Dictionary<string, object>() };
        public static Unidad Barrier(string clave, string accion, Dictionary<string, object> args = null) =>
            new Unidad(clave) { Barrera = accion, BarreraArgs = args ?? new Dictionary<string, object>() };
    }

    /// <summary>La receta decidió no hacer nada en esta unidad (ya estaba bien, o una excepción declarada). Se registra con el motivo.</summary>
    public sealed class OmitirUnidad : Exception
    {
        public OmitirUnidad(string motivo) : base(motivo) { }
    }

    /// <summary>
    /// Receta por pasos para el motor reanudable (0.6.0, §5). Contrato:
    /// - <see cref="Preparar"/>: valida entradas (lanza ArgumentException si faltan) — sólo lectura;
    /// - <see cref="Unidades"/>: enumera unidades con claves ESTABLES y deterministas sobre el mismo modelo; puede ser perezosa
    ///   (yield): el motor la recorre por tramos sin bloquear Revit;
    /// - <see cref="Ejecutar"/>: aplica UNA unidad; con PorUnidad el motor abre y confirma la transacción y el resultado se materializa
    ///   antes de confirmar;
    /// - <see cref="Verificar"/>: tras una caída, ¿quedó aplicada? Por defecto el motor comprueba si existen los elementos creados (UniqueId);
    /// - <see cref="Postcondicion"/>: QC final de sólo lectura; devuelva un diccionario con "desviaciones" (lista) para marcar diferencias.
    /// Se registra por archivo (recetas\nombre.cs) o se envía como código; no hace falta una herramienta MCP por receta.
    /// </summary>
    public abstract class Receta
    {
        /// <summary>Versión declarada: entra en la huella de la tarea.</summary>
        public virtual string Version => "1";
        public virtual string Descripcion => "";
        public virtual PoliticaTx Transaccion => PoliticaTx.PorUnidad;
        /// <summary>Entradas que espera en args (sólo para documentación y validación de presencia).</summary>
        public virtual IEnumerable<string> Entradas => Enumerable.Empty<string>();

        public virtual void Preparar(ScriptContext ctx)
        {
            var missing = Entradas.Where(e => !ctx.Args.ContainsKey(e)).ToList();
            if (missing.Count > 0) throw new ArgumentException("faltan entradas en args: " + string.Join(", ", missing));
        }

        public abstract IEnumerable<Unidad> Unidades(ScriptContext ctx);

        public abstract object Ejecutar(ScriptContext ctx, Unidad u);

        public virtual Verificacion Verificar(ScriptContext ctx, Unidad u) => Verificacion.Desconocida;

        public virtual object Postcondicion(ScriptContext ctx) => null;
    }
}
