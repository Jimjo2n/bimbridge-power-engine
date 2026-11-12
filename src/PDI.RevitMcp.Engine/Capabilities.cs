// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Capacidades declaradas por operación, tipo de documento y versión (R002, R059, R060, R070). ctx las publica para el documento
    /// destino y la matriz MATRIZ_CAPACIDADES.md se genera desde aquí (una prueba la compara), así la documentación no se desincroniza.
    /// "evidencia" dice qué se comprobó de verdad: no se declara probado en Revit lo que sólo compila.
    /// </summary>
    public static class Capabilities
    {
        /// <summary>Niveles de evidencia, de menor a mayor.</summary>
        public const string Compila = "compila 2022–2026";
        public const string Unitaria = "compila + prueba sin Revit";
        public const string Fake = "compila + prueba con Revit simulado (motor/transporte)";
        public const string RevitLectura2024 = "probado en Revit 2024 (lectura, 0.2.x)";
        public const string RevitLectura2026 = "probado en Revit 2026 (lectura, 0.5.0, una muestra)";

        public sealed class Row
        {
            public string Orden;
            public string Que;
            public string Proyecto;
            public string Familia;
            public string Nivel;
            public int Desde = 2022;
            public string Evidencia;
            public string Notas;
        }

        public const string Si = "sí";
        public const string No = "no";

        public static readonly List<Row> All = new List<Row>
        {
            R("ctx / docs", "contexto, documentos abiertos con handle dN, capacidades", Si, Si, "R", RevitLectura2026, "lista RVT y RFA, tipo, ruta, modificado, sólo lectura, activo"),
            R("cat", "catálogo (niveles, vistas, tipos, familias…)", Si, "parcial", "R", RevitLectura2024, "en familias sólo tipos/familias/vistas"),
            R("q / get", "consultas por categoría, nivel, tipo, conjunto", Si, Si, "R", RevitLectura2024, "documento fijado en todos los tramos desde 0.5.0"),
            R("chk lighting_audit", "QC de luminarias y etiquetas por vista", Si, No, "R", RevitLectura2024, "perfil de etiquetas por tarea: tags=uno|ninguno|ignorar"),
            R("geo", "geometría: montaje, choques, rangos de vista, cuartos", Si, No, "R", RevitLectura2024, "vínculos con transformación fallida se excluyen y se informan"),
            R("show / cap", "señalar y capturar vistas", Si, Si, "R", RevitLectura2024, "sólo sobre el documento activo (necesita la interfaz)"),
            R("chk model_health", "salud del modelo: avisos agrupados, duplicados, parámetros requeridos vacíos, conectores MEP libres, láminas vacías y vistas sin lámina", Si, No, "R", Compila, "sólo lectura, por tramos; no reproduce las comprobaciones estructurales de Revit"),
            R("chk historial", "historial de operaciones largas con filtros, progreso de las que siguen y consejo de reintento", Si, Si, "R", Compila, "persistente en %LOCALAPPDATA%; nunca aconseja repetir lo incierto"),
            R("chk cobertura", "descubrimiento del catálogo F01–F37 bajo demanda: filas compactas paginadas y contrato completo por id", Si, Si, "R", Compila, "índice inmutable en memoria; no entra al hilo de Revit ni lee ensamblados"),
            R("chk copy_monitor", "Copy/Monitor: preflight e inventario del vínculo, traspaso manual veraz y verificación posterior (sólo lectura)", Si, No, "R", Compila, "no hay API pública de Copia por lotes: nunca declara 'copiado'; correspondencia origen→copia inferida por tipo y posición"),
            R("plan / commit", "44 operaciones tipadas con diff y un Deshacer (0.8.0: dibujo 2D, cotas, componentes de detalle, conjunto de impresión; 0.9.1: niveles, rejillas, muros, suelos, techos, habitaciones, conductos, tuberías, circuitos y tablas)", Si, "parcial", "P/W", Compila, "en familias: set, values, move, rotate, mirror, copy, delete; las operaciones de modelado 0.9.1 sólo en proyectos"),
            R("exec consulta", "C# general sobre la API; todo se revierte al final", Si, Si, "R", Unitaria, "compilador Roslyn 4.8; compilación probada contra la API de cada año sin Revit"),
            R("exec ejecutar", "C# general con transacción automática, grupo o sin transacción", Si, Si, "W", Unitaria, "tx=auto|grupo|ninguna; avisos clasificados por FailureDefinitionId"),
            R("fam info / caras", "tipos, parámetros, fórmulas, GUID, conectores, caras planas", Si, Si, "R", RevitLectura2026, "en proyecto: por familia cargada (conectores de una instancia)"),
            R("fam buscar / cargar_archivo", "0.8.0: índice de RFA sólo en carpetas_familias; cargar un RFA indexado y sin cambios (sha-256)", Si, No, "R / W", Unitaria, "nunca explora discos; permiso cargar_archivo"),
            R("fam abrir", "EditFamily desde el proyecto → handle del RFA (reutiliza sólo si lo abrió este proyecto y esta familia)", Si, No, "W", Compila, "0.6.0: procedencia por proyecto + UniqueId de la familia"),
            R("fam conector", "crear o reconciliar conector por clave semántica y verificar", No, Si, "W", Compila, "tubería, ducto, eléctrico, conduit, bandeja; radio o diámetro explícitos"),
            R("fam param", "valor, fórmula, crear y asociar parámetros de familia", No, Si, "W", Compila, ""),
            R("fam cargar", "cargar el RFA en el proyecto con IFamilyLoadOptions y revalidar", No, Si, "W", Compila, "sobrescribir=valores|no"),
            R("doc guardar / guardar_como", "Save / SaveAs con verificación de archivo y hash", Si, Si, "D o W", Compila, ""),
            R("doc sync", "sincronizar con central y verificar", Si, No, "D o W", Compila, "sólo modelos con worksharing"),
            R("doc pdf", "exportar hojas o un conjunto a PDF y verificar archivos", Si, No, "D o W", Compila, "Document.Export(PDFExportOptions), existe desde 2022"),
            R("doc ifc", "exportar IFC (2x3, 4 RV, 4 DTV; modelo o vista) y verificar el archivo", Si, No, "D o W", Compila, "la transacción que Revit exige se revierte siempre: el modelo no cambia"),
            R("doc tabla_csv", "exportar una tabla de planificación a CSV y comparar filas", Si, No, "D o W", Compila, "ViewSchedule.Export con coma y comillas dobles"),
            R("doc cerrar", "cerrar un documento que no es el activo", Si, Si, "D o W", Compila, "Revit no permite cerrar el activo por la API"),
            R("res", "leer resultados largos guardados por handle", Si, Si, "R", Unitaria, "7 días en disco"),
            R("clave= / tarea=", "idempotencia y diario de operaciones durable", Si, Si, "—", Unitaria, "commit, exec ejecutar, fam y doc"),
            R("tarea iniciar / reanudar", "tareas largas por unidades, reanudables, con barreras guardar/sync/pdf (0.6.0)", Si, Si, "W", Fake, "motor probado con fallos inyectados; backend Revit sólo compilado"),
            R("tarea estado / resumen / esperar", "memoria de tarea entre clientes: resumen compacto, unidades, decisiones", Si, Si, "R", Fake, "ambos perfiles (compacto y completo)"),
            R("tarea reconciliar / cancelar", "decisión explícita sobre unidades no verificables; cancelación entre unidades", Si, Si, "W", Fake, ""),
            R("recetas incluidas", "etiquetar_luces, parametro_por_lote, qc_manifiesto, montaje_soportes, exportar_laminas", Si, No, "R/W", Compila, "compiladas contra la API de cada año por una prueba"),
            R("Bim.* (scripts)", "grupos, montaje con soporte real (rayos, vínculos), rango de vista, por qué no se ve, manifiesto", Si, "parcial", "R/W", Compila, "la lógica de QC (manifiesto, transformaciones, visibilidad) sí tiene pruebas sin Revit"),
            R("metricas", "caracteres por llamada y cliente para medir consumo (no tokens)", Si, Si, "—", Unitaria, "local al puente"),
            R("permisos", "0.8.0: 10 clases (leer, modificar proyecto/familia, cargar archivo, guardar, sincronizar, exportar, publicar, código dinámico, interfaz)", Si, Si, "—", Unitaria, "config permisos; vacío = derivados de los niveles; publicar siempre denegado"),
            R("Actualizar en línea (GitHub)", "0.9.1: última Release pública del repositorio, sin cuenta; verifica SHA-256 publicado, tamaño y versión antes de instalar", Si, Si, "—", Unitaria, "sólo desde la cinta y sólo al pulsar; necesita una Release publicada; actualizacion_online=no la deshabilita"),
            R("Actualizar desde ZIP", "0.8.0: manifiesto, firma RSA, hashes, compatibilidad; se aplica al cerrar Revit, con reversión", Si, Si, "—", Unitaria, "sólo desde la cinta; nunca cierra Revit"),
            R("Setup /diagnostico", "versión instalada, cargada en cada Revit, puente, hashes y reinicio pendiente", Si, Si, "—", Unitaria, "el instalador verifica manifest.json antes y después de copiar"),
        };

        private static Row R(string orden, string que, string proyecto, string familia, string nivel, string evidencia, string notas) =>
            new Row { Orden = orden, Que = que, Proyecto = proyecto, Familia = familia, Nivel = nivel, Evidencia = evidencia, Notas = notas };

        /// <summary>Filas para ctx: sólo lo que aplica a ese tipo de documento ("proyecto" o "familia").</summary>
        public static List<object> ForDoc(string kind, int year)
        {
            var fam = string.Equals(kind, "familia", StringComparison.OrdinalIgnoreCase);
            var rows = new List<object>();
            foreach (var r in All)
            {
                if (year > 0 && year < r.Desde) continue;
                var v = fam ? r.Familia : r.Proyecto;
                if (v == No) continue;
                rows.Add(new object[] { r.Orden, v == Si ? "" : v, r.Nivel });
            }
            return rows;
        }

        /// <summary>Lo que NO aplica a ese tipo de documento, con la alternativa.</summary>
        public static List<string> NotFor(string kind)
        {
            var fam = string.Equals(kind, "familia", StringComparison.OrdinalIgnoreCase);
            return All.Where(r => (fam ? r.Familia : r.Proyecto) == No).Select(r => r.Orden).ToList();
        }

        public static string Markdown(string version)
        {
            var sb = new StringBuilder();
            sb.Append("# Matriz de capacidades — BIMBRIDGE - Power Engine ").Append(version).Append("\n\n");
            sb.Append("Generada desde `src/PDI.RevitMcp.Engine/Capabilities.cs` (una prueba comprueba que coincide). ");
            sb.Append("Columna *evidencia*: lo que de verdad se comprobó. Lo nuevo de 0.6.0 todavía no se ha ejecutado dentro de Revit: ");
            sb.Append("está compilado para 2022–2026 y probado sin Revit; la prueba nativa es `tools/aceptacion/PROCEDIMIENTO_0.6.0.md`.\n\n");
            sb.Append("| Orden | Qué hace | Proyecto | Familia | Nivel | Desde | Evidencia | Notas |\n|---|---|---|---|---|---|---|---|\n");
            foreach (var r in All)
                sb.Append("| ").Append(r.Orden).Append(" | ").Append(r.Que).Append(" | ").Append(r.Proyecto).Append(" | ").Append(r.Familia)
                  .Append(" | ").Append(r.Nivel).Append(" | ").Append(r.Desde.ToString(CultureInfo.InvariantCulture)).Append(" | ").Append(r.Evidencia)
                  .Append(" | ").Append(r.Notas).Append(" |\n");
            sb.Append("\n## Por versión\n\n| Revit | Runtime del add-in | Compilador del runner | API de referencia usada al compilar |\n|---|---|---|---|\n");
            sb.Append("| 2022 | .NET Framework 4.8 | Roslyn 4.8 (en carpeta roslyn) | Nice3point 2022.1.80 |\n");
            sb.Append("| 2023 | .NET Framework 4.8 | Roslyn 4.8 (en carpeta roslyn) | Nice3point 2023.1.90 |\n");
            sb.Append("| 2024 | .NET Framework 4.8 | Roslyn 4.8 (en carpeta roslyn) | Nice3point 2024.3.60 |\n");
            sb.Append("| 2025 | .NET 8 | Roslyn 4.8 (contexto de carga propio) | Nice3point 2025.4.60 |\n");
            sb.Append("| 2026 | .NET 8 | Roslyn 4.8 (contexto de carga propio) | Nice3point 2026.4.10 |\n");
            sb.Append("\nEl build mínimo real de cada año no está comprobado: compilar contra la API de referencia no prueba que un método exista en un build anterior. ");
            sb.Append("ctx informa el build exacto y el runner informa el error concreto (MissingMethodException) si un método no existe.\n");
            return sb.ToString();
        }
    }
}
