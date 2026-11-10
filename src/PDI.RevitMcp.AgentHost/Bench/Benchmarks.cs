// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Engine;

namespace PDI.RevitMcp.AgentHost.Bench
{
    /// <summary>
    /// 0.8.0: medición de rendimiento y tokens. Cada cifra lleva su procedencia:
    /// MEDIDO_SIMULADO (add-in falso por la tubería real, sin Revit), MEDIDO_NATIVO (Revit real, con evidencia) o NO_MEDIDO.
    /// Las comparaciones contra pyRevit quedan preparadas pero NO_MEDIDO hasta que haya una corrida nativa autorizada.
    /// </summary>
    public static class Benchmarks
    {
        public const string Simulated = "MEDIDO_SIMULADO", Native = "MEDIDO_NATIVO", NotMeasured = "NO_MEDIDO";

        /// <summary>Resumen de las llamadas por cable de un <see cref="Revit.RevitLink"/> (Calls).</summary>
        public static Dictionary<string, object> Wire(IEnumerable<Dictionary<string, object>> calls)
        {
            var list = calls.ToList();
            long L(Dictionary<string, object> c, string k) => c.TryGetValue(k, out var v) ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;
            var req = list.Sum(c => L(c, "bytes_peticion"));
            var resp = list.Sum(c => L(c, "bytes_respuesta"));
            return new Dictionary<string, object>
            {
                ["llamadas_cable"] = (long)list.Count,
                ["por_operacion"] = list.GroupBy(c => Convert.ToString(c["op"], CultureInfo.InvariantCulture)).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)(long)g.Count()),
                ["bytes_peticion"] = req, ["bytes_respuesta"] = resp,
                ["ms_total"] = list.Sum(c => L(c, "ms")),
                ["tokens_estimados_cable"] = TokenEstimate.FromChars(req + resp),
            };
        }

        public sealed class Def
        {
            public string Id, Nombre, Escenario, Metricas, Arnes, Estado = NotMeasured, Bloqueo;
            /// <summary>0.9.0: ítems de la campaña nativa que lo cubren (comprobados contra el procedimiento por una prueba).</summary>
            public string[] Items = new string[0];
            public Dictionary<string, object> ToJson() => new Dictionary<string, object>
            {
                ["id"] = Id, ["nombre"] = Nombre, ["escenario"] = Escenario, ["metricas"] = Metricas, ["arnes"] = Arnes, ["estado"] = Estado, ["bloqueo"] = Bloqueo, ["items"] = Items.Cast<object>().ToList(),
            };
        }

        private const string Blocked = "BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION";

        /// <summary>B1–B8 del informe de flujos reales (§12). Ninguno medido de forma nativa todavía.</summary>
        public static readonly IReadOnlyList<Def> Catalog = new List<Def>
        {
            new Def { Id = "B0", Nombre = "586 luminarias: uno a uno frente a plan", Escenario = "586 elementos sintéticos; leer y cambiar un parámetro de cada uno",
                Metricas = "llamadas del agente, llamadas por cable, bytes, tokens ESTIMADOS, tiempo; commits",
                Arnes = "tests: Bench_B0_586_OneByOne_vs_Plan (add-in falso por la tubería real). Salida JSON con BIMBRIDGE_BENCH_OUT=<carpeta>",
                Estado = Simulated, Bloqueo = "nativo: " + Blocked },
            new Def { Id = "B1", Nombre = "Plantilla y visibilidad", Escenario = "varias vistas con regla heredada que oculta un sistema y un filtro demasiado amplio",
                Metricas = "tiempo, llamadas, correspondencia por vista, cero categorías ajenas, plantilla original preservada",
                Arnes = "campaña ítems N8-02 (vistas y plantillas) y N8-11 (visibilidad) en copia desechable; par pyRevit: script equivalente de lectura+cambio", Items = new[] { "N8-02", "N8-11" }, Bloqueo = Blocked },
            new Def { Id = "B2", Nombre = "Iluminación y soporte", Escenario = "cientos de luces, símbolos parecidos, casi duplicados, rampas, offset ≠ altura física",
                Metricas = "precisión/exhaustividad del emparejamiento y montaje, falsos positivos/negativos frente a verdad de prueba",
                Arnes = "verificar_independiente + geo montaje sobre copia; verdad de prueba sembrada (omisiones y extras); ítems N8-07 y N8-10", Items = new[] { "N8-07", "N8-10" }, Bloqueo = Blocked },
            new Def { Id = "B3", Nombre = "Grupos y anotación", Escenario = "11 hojas de unidades, variantes, más de un panel, salto de nivel no uniforme",
                Metricas = "cobertura tipo/planta, miembros correctos, cero referencias inválidas, legibilidad a escala",
                Arnes = "PREPARADO PARCIAL: lectura de grupos existe; la receta de cobertura por tipo/planta es 0.9.0", Bloqueo = Blocked },
            new Def { Id = "B4", Nombre = "Feeds de dos sistemas", Escenario = "26 plantas con stubs, rutas cruzadas, vínculos y alertas cerca de la tolerancia",
                Metricas = "tiempo, memoria, llamadas; continuidad y separación; cero interferencias incompatibles conocidas",
                Arnes = "NO PREPARADO: routing físico es 0.9.0 (CAP-011)", Bloqueo = Blocked },
            new Def { Id = "B5", Nombre = "Riser 2D", Escenario = "grafo con derivación omitida, rótulo atravesado, ID repetido válido, extremo abierto respaldado",
                Metricas = "exactitud de nodos/aristas, tasas de error, legibilidad, una página",
                Arnes = "PREPARADO PARCIAL: dibujo 2D (detail_*) y verificación independiente existen; el grafo de riser es 0.9.0 (CAP-012)", Bloqueo = Blocked },
            new Def { Id = "B6", Nombre = "Publicación", Escenario = "set ordenado de 26 láminas y un riser, con un PDF obsoleto presente",
                Metricas = "persistencia tras reapertura, orden, papel, color, sello; detectar PDF anterior y verificar el nuevo",
                Arnes = "DeliveryTrack (12 etapas) + campaña N8-05 (láminas y conjunto) y N8-12 (PDF y recibo); exportación sólo con permiso de exportar", Items = new[] { "N8-05", "N8-12" }, Bloqueo = Blocked },
            new Def { Id = "B7", Nombre = "Recuperación", Escenario = "Revit ocupado, segundo agente, cambio de PID, caída tras commit, respuesta truncada",
                Metricas = "cero ejecución concurrente, cero duplicación, cero escritura en modelo equivocado; fases repetidas",
                Arnes = "NRT-03/05/06/07 (simulados en pruebas; NRT-03/05/06 nativos en rc1 14ec21d; NRT-07 sin corrida nativa); ítems N8-09 y N8-13", Items = new[] { "N8-09", "N8-13" }, Bloqueo = Blocked },
            new Def { Id = "B8", Nombre = "Compatibilidad de familia", Escenario = "instalación/manifiesto, abrir familia, conectores, recarga en 2022–2026",
                Metricas = "resultado por combinación versión×operación, reversión y recarga verificables",
                Arnes = "fam info/caras/conector/cargar_archivo + campaña N8-06 (familias externas) y N8-13c (instalación por ZIP) por versión", Items = new[] { "N8-06", "N8-13" }, Bloqueo = Blocked },
        };
    }
}
