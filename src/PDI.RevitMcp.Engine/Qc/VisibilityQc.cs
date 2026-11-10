// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PDI.RevitMcp.Engine.Qc
{
    /// <summary>
    /// ¿Por qué no se ve un elemento en una vista? (§7.3). El add-in junta los hechos con la API (ocultación, categoría, filtros, workset,
    /// fase, opción de diseño, recorte, rango de vista o de la región de plano) y aquí se decide, sin Revit, qué causas aplican y cuáles no
    /// se pueden comprobar por API. No "arregla" nada: nunca sugiere subir el rango de vista sin decir qué otras cosas verían afectadas.
    /// </summary>
    public static class VisibilityQc
    {
        public sealed class Facts
        {
            public bool ElementHidden;
            public bool CategoryHidden;
            public List<string> HidingFilters = new List<string>();
            public bool WorksetHidden;
            /// <summary>true = la fase del elemento queda fuera del filtro de fase de la vista (creado después o demolido antes).</summary>
            public bool? PhaseHidden;
            public bool? DesignOptionHidden;
            public bool CropActive;
            /// <summary>Caja del elemento dentro del recorte de la vista (null = no se pudo calcular).</summary>
            public bool? InsideCrop;
            /// <summary>Plantilla que controla la vista (nombre) o null.</summary>
            public string Template;
            /// <summary>"planta" | "rcp" | otro (sin rango).</summary>
            public string PlanKind;
            /// <summary>Cotas absolutas (mm) del rango que aplica en ese punto (el de la región de plano si el elemento cae en una).</summary>
            public double? Cut, Bottom, Top, Depth;
            public string RangeSource;
            /// <summary>Cuerpo visible del elemento (mm), no el origen de la familia.</summary>
            public double? ZMin, ZMax;
        }

        public sealed class Reason
        {
            public string Code;
            public string Detail;
            public bool Certain;
            public override string ToString() => Code + (Detail != null ? ": " + Detail : "") + (Certain ? "" : " (probable)");
        }

        public static List<Reason> Explain(Facts f, out List<string> notCheckable)
        {
            var r = new List<Reason>();
            var via = f.Template != null ? " (lo controla la plantilla " + f.Template + ")" : "";
            if (f.ElementHidden) r.Add(new Reason { Code = "oculto_en_vista", Detail = "ocultado elemento a elemento en esta vista", Certain = true });
            if (f.CategoryHidden) r.Add(new Reason { Code = "categoria_oculta", Detail = "categoría apagada en Visibilidad/Gráficos" + via, Certain = true });
            foreach (var name in f.HidingFilters ?? new List<string>()) r.Add(new Reason { Code = "filtro", Detail = name + " lo oculta" + via, Certain = true });
            if (f.WorksetHidden) r.Add(new Reason { Code = "workset_oculto", Detail = "su workset no es visible en esta vista" + via, Certain = true });
            if (f.PhaseHidden == true) r.Add(new Reason { Code = "fase", Detail = "su fase queda fuera del filtro de fase de la vista", Certain = true });
            if (f.DesignOptionHidden == true) r.Add(new Reason { Code = "opcion_de_diseno", Detail = "pertenece a una opción de diseño que la vista no muestra", Certain = true });
            if (f.CropActive && f.InsideCrop == false) r.Add(new Reason { Code = "fuera_de_recorte", Detail = "queda fuera de la región de recorte", Certain = true });

            if ((f.PlanKind == "planta" || f.PlanKind == "rcp") && f.ZMin.HasValue && f.ZMax.HasValue && f.Cut.HasValue)
            {
                var src = f.RangeSource ?? "rango de la vista";
                var cut = f.Cut.Value;
                if (f.PlanKind == "rcp")
                {
                    // RCP: se mira hacia arriba desde el plano de corte hasta "superior".
                    var top = f.Top ?? double.MaxValue;
                    if (f.ZMax < cut) r.Add(new Reason { Code = "rango_de_vista", Detail = "el cuerpo (" + F(f.ZMin) + "–" + F(f.ZMax) + " mm) está por debajo del corte " + F(cut) + " del " + src, Certain = true });
                    else if (f.ZMin > top) r.Add(new Reason { Code = "rango_de_vista", Detail = "el cuerpo empieza en " + F(f.ZMin) + " mm, por encima de superior " + F(top) + " del " + src, Certain = true });
                }
                else
                {
                    var depth = f.Depth ?? f.Bottom ?? double.MinValue;
                    if (f.ZMax < depth) r.Add(new Reason { Code = "rango_de_vista", Detail = "el cuerpo termina en " + F(f.ZMax) + " mm, por debajo de la profundidad " + F(depth) + " del " + src, Certain = true });
                    else if (f.ZMin > cut)
                        r.Add(new Reason { Code = "rango_de_vista", Detail = "el cuerpo está entero por encima del corte " + F(cut) + " del " + src + "; en planta las luminarias sobre el corte no se dibujan (salvo reglas de su categoría)", Certain = false });
                }
            }
            notCheckable = new List<string>
            {
                "visibilidad propia de la familia (parámetros de visibilidad, nivel de detalle, subcategorías) — revisar en la vista",
                "superposición con regiones rellenas opacas u otros elementos encima — inspección visual final",
            };
            if (f.PhaseHidden == null) notCheckable.Add("fase: no se pudo leer");
            if (f.CropActive && f.InsideCrop == null) notCheckable.Add("recorte: no se pudo calcular la caja del elemento en la vista");
            return r;
        }

        private static string F(double? v) => v.HasValue ? v.Value.ToString("0", CultureInfo.InvariantCulture) : "?";
    }
}
