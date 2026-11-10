// SPDX-License-Identifier: GPL-3.0-only
using System;

namespace PDI.RevitMcp.Engine.Tasks
{
    /// <summary>
    /// Frontera de ejecución de recetas (rc3). TODO código de una receta —constructor e inicializadores estáticos, propiedades,
    /// Preparar, el enumerador de Unidades (cada MoveNext), Ejecutar y Verificar— corre a través de <see cref="Run"/>:
    ///
    /// 1. antes de ejecutar, <c>authorize</c> (lease de tarea, lease de documento y concesiones vigentes) debe devolver null;
    ///    si no, la receta no llega a ejecutarse;
    /// 2. en fases de lectura/planificación (todo salvo Ejecutar), un contador de escrituras del modelo (en Revit: la revisión del
    ///    DocumentTracker, que sube con cada transacción confirmada, más "hay una transacción abierta") debe quedar igual; si cambia,
    ///    la fase falla con <c>escritura_en_fase_de_lectura</c> y la tarea se detiene.
    ///
    /// Esto no es un sandbox: lo que la receta pueda hacer fuera del modelo lo limita el analizador (conjunto cerrado de recetas
    /// instaladas, receta_externa, capacidades siempre denegadas). La frontera garantiza el ORDEN: autorización antes de código.
    /// </summary>
    public sealed class RecipeGate
    {
        private readonly Func<string> _authorize;
        private readonly Func<long> _writeCounter;

        public RecipeGate(Func<string> authorize, Func<long> writeCounter)
        {
            _authorize = authorize ?? (() => "sin autoridad configurada");
            _writeCounter = writeCounter ?? (() => 0);
        }

        /// <summary>Fases en las que la receta NO puede modificar el modelo.</summary>
        public static bool IsReadPhase(string phase) => phase != Phase.Ejecutar;

        public static class Phase
        {
            public const string Constructor = "constructor";
            public const string Preparar = "preparar";
            public const string Enumerar = "enumerar";
            public const string Ejecutar = "ejecutar";
            public const string Verificar = "verificar";
            public const string Postcondicion = "postcondicion";
        }

        /// <summary>Ejecuta <paramref name="body"/> sólo si la autoridad lo permite. Devuelve null o el motivo del rechazo/fallo.
        /// Las excepciones de la receta se propagan (el llamador ya las convierte en fallo de la tarea).</summary>
        public string Run(string phase, Action body)
        {
            var why = _authorize();
            if (why != null) return "autorizacion_requerida (" + phase + "): " + why + ". La receta no se ejecutó.";
            var before = _writeCounter();
            body();
            if (IsReadPhase(phase))
            {
                var after = _writeCounter();
                if (after != before)
                    return "escritura_en_fase_de_lectura: la receta modificó el modelo durante '" + phase + "' (sólo Ejecutar puede escribir, dentro de la transacción del motor). La tarea se detiene.";
            }
            return null;
        }

        /// <summary>Crea la receta (constructor + inicializadores) dentro de la frontera.</summary>
        public string Create<T>(Func<T> factory, out T recipe) where T : class
        {
            T r = null;
            var why = Run(Phase.Constructor, () => r = factory());
            recipe = why == null ? r : null;
            return why;
        }
    }
}
