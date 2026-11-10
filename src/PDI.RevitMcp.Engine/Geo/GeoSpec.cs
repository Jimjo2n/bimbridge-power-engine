// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine.Geo
{
    /// <summary>
    /// Petición de la herramienta "geo", validada antes de tocar Revit. Recetas:
    ///   montaje     cara superior del cuerpo contra la cara inferior de lo de arriba (o base contra lo de abajo) en 1/5/9 puntos
    ///   holgura     lo mismo con 1 punto (centro): distancia al primer elemento arriba/abajo
    ///   choques     sólidos que se cruzan con estructura/arquitectura (anfitrión y vínculos)
    ///   cerca       columnas/vigas (u otras categorías) junto o bajo la huella
    ///   muro        distancia al muro más cercano, con lado
    ///   nivel_real  nivel por altura frente al nivel asignado
    ///   duplicados  misma familia/tipo en el mismo sitio
    ///   cuarto      cuarto (vínculo) o espacio (anfitrión) donde está cada elemento
    ///   rango_vista si cada elemento cae dentro del rango de vista de cada plano
    ///   indice      estado del índice de geometría (o reconstruirlo)
    /// </summary>
    public sealed class GeoSpec
    {
        public string Recipe;
        public List<string> Categories = new List<string>();
        public List<string> Levels = new List<string>();
        public List<long> Ids = new List<long>();
        public string FromSet;
        public List<string> Against = new List<string>();
        /// <summary>Filtro de vínculos por nombre (contiene). Vacío = todos los cargados.</summary>
        public List<string> Links = new List<string>();
        public bool IncludeHost = true;
        public bool IncludeLinks = true;
        public bool Up = true;
        public int Points = 9;
        public double Tolerance = Mounting.DefaultTolerance;
        public double MaxRange = 30.0;
        public double Radius = 3.0;
        public double MinVolume = 0.0001;
        public bool SameType = true;
        public string Views;
        public string Source = "ambos";
        public bool Millimeters;
        public int Page = 1;
        public int PageSize = 100;
        public string Only;
        public bool Rebuild;
        public List<string> ExcludeSubcategories = new List<string>();
        /// <summary>Filtros extra de los elementos revisados: worksets por nombre y familia/tipo (contiene).</summary>
        public List<string> Worksets = new List<string>();
        public List<string> Types = new List<string>();

        public static readonly string[] Recipes =
            { "montaje", "holgura", "choques", "cerca", "muro", "nivel_real", "duplicados", "cuarto", "rango_vista", "indice" };

        /// <summary>Categorías por defecto de lo que está "arriba" de una luminaria: estructura y arquitectura, no cielos.</summary>
        public static readonly string[] DefaultAbove = { "Floors", "StructuralFraming", "Roofs", "Ramps", "Stairs" };
        public static readonly string[] DefaultClash = { "Floors", "StructuralFraming", "StructuralColumns", "Walls", "Roofs", "Ramps", "Stairs" };
        public static readonly string[] DefaultNear = { "StructuralColumns", "Columns", "StructuralFraming" };

        /// <summary>Alias en español que la IA o operator pueden usar.</summary>
        public static readonly Dictionary<string, string[]> Aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["losas"] = new[] { "Floors" },
            ["pisos"] = new[] { "Floors" },
            ["vigas"] = new[] { "StructuralFraming" },
            ["columnas"] = new[] { "StructuralColumns", "Columns" },
            ["rampas"] = new[] { "Ramps" },
            ["escaleras"] = new[] { "Stairs" },
            ["techos"] = new[] { "Roofs" },
            ["cubiertas"] = new[] { "Roofs" },
            ["cielos"] = new[] { "Ceilings" },
            ["plafones"] = new[] { "Ceilings" },
            ["muros"] = new[] { "Walls" },
            ["genericos"] = new[] { "GenericModel" },
            ["luminarias"] = new[] { "LightingFixtures" },
            ["estructura"] = new[] { "Floors", "StructuralFraming", "StructuralColumns", "StructuralFoundation" },
        };

        public static GeoSpec Parse(IDictionary<string, object> a)
        {
            var g = new GeoSpec { Recipe = (Arg.GetString(a, "name") ?? Arg.GetString(a, "receta") ?? "").Trim().ToLowerInvariant() };
            if (g.Recipe == "clearance" || g.Recipe == "clearance_up") g.Recipe = "holgura";
            if (g.Recipe == "mount" || g.Recipe == "mount_check") g.Recipe = "montaje";
            if (g.Recipe == "clash") g.Recipe = "choques";
            if (g.Recipe == "near" || g.Recipe == "column_probe") g.Recipe = "cerca";
            if (g.Recipe == "wall" || g.Recipe == "wall_contact") g.Recipe = "muro";
            if (g.Recipe == "level" || g.Recipe == "level_resolve") g.Recipe = "nivel_real";
            if (g.Recipe == "room" || g.Recipe == "room_at") g.Recipe = "cuarto";
            if (g.Recipe == "view_range" || g.Recipe == "view_check") g.Recipe = "rango_vista";
            if (g.Recipe == "duplicates") g.Recipe = "duplicados";
            if (g.Recipe == "index" || g.Recipe == "geo_index") g.Recipe = "indice";
            if (Array.IndexOf(Recipes, g.Recipe) < 0)
                throw new QueryException("geo: name debe ser " + string.Join(", ", Recipes));

            g.Categories = ExpandCategories(Arg.GetStringList(a, "cat"));
            g.Levels = Arg.GetStringList(a, "lvl");
            foreach (var s in Arg.GetStringList(a, "ids"))
            {
                if (s.StartsWith("$")) { g.FromSet = NamedSets.Normalize(s); continue; }
                if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    throw new QueryException("ids: '" + s + "' no es un id ni un conjunto ($nombre)");
                g.Ids.Add(id);
            }
            if (g.Ids.Count == 0 && g.FromSet == null && g.Categories.Count == 0 && g.Recipe != "indice")
                g.Categories.Add("LightingFixtures");

            g.Against = ExpandCategories(Arg.GetStringList(a, "contra"));
            var links = Arg.GetStringList(a, "vinculos");
            if (links.Count == 1 && string.Equals(links[0], "ninguno", StringComparison.OrdinalIgnoreCase)) g.IncludeLinks = false;
            else if (!(links.Count == 1 && string.Equals(links[0], "todos", StringComparison.OrdinalIgnoreCase))) g.Links = links;
            g.IncludeHost = Arg.GetBool(a, "anfitrion") ?? true;

            var side = (Arg.GetString(a, "lado") ?? "arriba").Trim().ToLowerInvariant();
            if (side == "arriba" || side == "up") g.Up = true;
            else if (side == "abajo" || side == "down") g.Up = false;
            else throw new QueryException("lado: arriba (por defecto) o abajo");

            g.Points = (int)(Arg.GetLong(a, "puntos") ?? (g.Recipe == "holgura" ? 1 : 9));
            if (Array.IndexOf(Probes.Allowed, g.Points) < 0) throw new QueryException("puntos: 1, 5 o 9");

            g.Tolerance = Length(a, "tol", g.Tolerance, 0, 5);
            g.MaxRange = Length(a, "max", g.MaxRange, 0.1, 200);
            g.Radius = Length(a, "radio", g.Recipe == "cerca" ? 2.0 : 3.0, 0.01, 100);
            if (g.Recipe == "duplicados" && !a.ContainsKey("tol")) g.Tolerance = 0.01;
            if (a.TryGetValue("min_vol", out var mv) && mv != null)
            {
                if (!(mv is double || mv is long)) throw new QueryException("min_vol: número en pies cúbicos");
                g.MinVolume = Math.Max(0, Convert.ToDouble(mv, CultureInfo.InvariantCulture));
            }
            g.SameType = Arg.GetBool(a, "mismo_tipo") ?? true;
            g.Views = Arg.GetString(a, "views");
            if (g.Recipe == "rango_vista" && string.IsNullOrWhiteSpace(g.Views)) g.Views = "rcp";
            g.Source = (Arg.GetString(a, "fuente") ?? "ambos").Trim().ToLowerInvariant();
            if (g.Source != "ambos" && g.Source != "espacios" && g.Source != "cuartos")
                throw new QueryException("fuente: ambos (por defecto), espacios (MEP del anfitrión) o cuartos (vínculo de arquitectura)");

            var units = (Arg.GetString(a, "units") ?? "ft").Trim().ToLowerInvariant();
            if (units == "mm") g.Millimeters = true;
            else if (units != "ft" && units != "") throw new QueryException("units: ft (por defecto) o mm");
            g.Page = (int)(Arg.GetLong(a, "page") ?? 1);
            g.PageSize = (int)(Arg.GetLong(a, "page_size") ?? 100);
            if (g.Page < 1) throw new QueryException("page empieza en 1");
            if (g.PageSize < 1 || g.PageSize > QuerySpec.MaxPageSize) throw new QueryException("page_size entre 1 y " + QuerySpec.MaxPageSize);
            g.Only = Arg.GetString(a, "solo");
            g.Rebuild = Arg.GetBool(a, "reconstruir") ?? false;
            g.ExcludeSubcategories = Arg.GetStringList(a, "sub_excluir");
            g.Worksets = Arg.GetStringList(a, "ws");
            g.Types = Arg.GetStringList(a, "type");
            // "cerca" busca vigas y columnas alrededor del cuerpo: 10 ft por encima bastan (30 ft llegarían a otros pisos).
            if (g.Recipe == "cerca" && !a.ContainsKey("max")) g.MaxRange = 10.0;

            if (g.Against.Count == 0)
            {
                if (g.Recipe == "montaje" || g.Recipe == "holgura") g.Against.AddRange(g.Up ? DefaultAbove : new[] { "Floors", "Ramps", "Stairs", "StructuralFoundation" });
                else if (g.Recipe == "choques") g.Against.AddRange(DefaultClash);
                else if (g.Recipe == "cerca") g.Against.AddRange(DefaultNear);
                else if (g.Recipe == "muro") g.Against.Add("Walls");
            }
            return g;
        }

        public static List<string> ExpandCategories(IEnumerable<string> names)
        {
            var list = new List<string>();
            foreach (var n in names)
            {
                if (Aliases.TryGetValue(n.Trim(), out var mapped)) list.AddRange(mapped);
                else list.Add(n.Trim());
            }
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static double Length(IDictionary<string, object> a, string key, double fallback, double min, double max)
        {
            if (!a.TryGetValue(key, out var v) || v == null) return fallback;
            if (!Lengths.TryFeet(v, out var ft, out var err)) throw new QueryException(key + ": " + err);
            if (ft < min || ft > max)
                throw new QueryException(key + ": fuera de rango (" + min.ToString(CultureInfo.InvariantCulture) + " a " + max.ToString(CultureInfo.InvariantCulture) + " pies)");
            return ft;
        }

        /// <summary>Clave estable de los argumentos que cambian el resultado (para la caché).</summary>
        public Dictionary<string, object> CacheArgs() => new Dictionary<string, object>
        {
            ["r"] = Recipe, ["cat"] = string.Join(",", Categories), ["lvl"] = string.Join(",", Levels),
            ["ids"] = string.Join(",", Ids), ["set"] = FromSet ?? "", ["contra"] = string.Join(",", Against),
            ["vin"] = IncludeLinks ? string.Join(",", Links) : "-", ["host"] = IncludeHost, ["up"] = Up, ["pts"] = (long)Points,
            ["tol"] = Tolerance, ["max"] = MaxRange, ["radio"] = Radius, ["minv"] = MinVolume, ["mt"] = SameType,
            ["views"] = Views ?? "", ["fuente"] = Source, ["sub"] = string.Join(",", ExcludeSubcategories),
            ["ws"] = string.Join(",", Worksets), ["type"] = string.Join(",", Types),
        };
    }
}
