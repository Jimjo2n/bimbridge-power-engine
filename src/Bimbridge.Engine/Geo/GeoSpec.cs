// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Engine.Geo
{
    /// <summary>
    /// Request for the "geo" tool, validated before touching Revit. Recipes:
    ///   montaje     body top face against the bottom face of what is above (or base against what is below) at 1/5/9 points
    ///   holgura     the same with 1 point (center): distance to the first element above/below
    ///   choques     solids that intersect structure/architecture (host and links)
    ///   cerca       columns/beams (or other categories) next to or under the footprint
    ///   muro        distance to the nearest wall, with side
    ///   nivel_real  level by height versus the assigned level
    ///   duplicados  same family/type in the same place
    ///   cuarto      room (link) or space (host) where each element is
    ///   rango_vista whether each element falls inside the view range of each plan view
    ///   indice      geometry index status (or rebuild it)
    /// </summary>
    public sealed class GeoSpec
    {
        public string Recipe;
        public List<string> Categories = new List<string>();
        public List<string> Levels = new List<string>();
        public List<long> Ids = new List<long>();
        public string FromSet;
        public List<string> Against = new List<string>();
        /// <summary>Link filter by name (contains). Empty = all loaded links.</summary>
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
        public string Source = "both";
        public bool Millimeters;
        public bool SurfaceReference;
        public int Page = 1;
        public int PageSize = 100;
        public string Only;
        public bool Rebuild;
        public List<string> ExcludeSubcategories = new List<string>();
        /// <summary>Extra filters for the checked elements: worksets by name and family/type (contains).</summary>
        public List<string> Worksets = new List<string>();
        public List<string> Types = new List<string>();

        public static readonly string[] Recipes =
            { "mounting", "clearance", "clashes", "near", "wall", "actual_level", "duplicates", "room", "view_range", "index" };

        /// <summary>Default categories for what is "above" a fixture: structure and architecture, not ceilings.</summary>
        public static readonly string[] DefaultAbove = { "Floors", "StructuralFraming", "Roofs", "Ramps", "Stairs" };
        public static readonly string[] DefaultClash = { "Floors", "StructuralFraming", "StructuralColumns", "Walls", "Roofs", "Ramps", "Stairs" };
        public static readonly string[] DefaultNear = { "StructuralColumns", "Columns", "StructuralFraming" };

        /// <summary>Category shorthands accepted as input (the AI or operator may use them); legacy Spanish ones go through LegacyAliases.CategoryAliases first.</summary>
        public static readonly Dictionary<string, string[]> Aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["slabs"] = new[] { "Floors" },
            ["floors"] = new[] { "Floors" },
            ["beams"] = new[] { "StructuralFraming" },
            ["columns"] = new[] { "StructuralColumns", "Columns" },
            ["ramps"] = new[] { "Ramps" },
            ["stairs"] = new[] { "Stairs" },
            ["roofs"] = new[] { "Roofs" },
            ["ceilings"] = new[] { "Ceilings" },
            ["ceiling_panels"] = new[] { "Ceilings" },
            ["walls"] = new[] { "Walls" },
            ["generic"] = new[] { "GenericModel" },
            ["fixtures"] = new[] { "LightingFixtures" },
            ["structure"] = new[] { "Floors", "StructuralFraming", "StructuralColumns", "StructuralFoundation" },
        };

        public static GeoSpec Parse(IDictionary<string, object> a)
        {
            var g = new GeoSpec { Recipe = LegacyAliases.Token((Arg.GetString(a, "name") ?? Arg.GetString(a, "recipe") ?? "").Trim().ToLowerInvariant()) };
            if (g.Recipe == "clearance" || g.Recipe == "clearance_up") g.Recipe = "clearance";
            if (g.Recipe == "mount" || g.Recipe == "mount_check") g.Recipe = "mounting";
            if (g.Recipe == "clash") g.Recipe = "clashes";
            if (g.Recipe == "near" || g.Recipe == "column_probe") g.Recipe = "near";
            if (g.Recipe == "wall" || g.Recipe == "wall_contact") g.Recipe = "wall";
            if (g.Recipe == "level" || g.Recipe == "level_resolve") g.Recipe = "actual_level";
            if (g.Recipe == "room" || g.Recipe == "room_at") g.Recipe = "room";
            if (g.Recipe == "view_range" || g.Recipe == "view_check") g.Recipe = "view_range";
            if (g.Recipe == "index" || g.Recipe == "geo_index") g.Recipe = "index";
            if (Array.IndexOf(Recipes, g.Recipe) < 0)
                throw new QueryException("geo: name must be " + string.Join(", ", Recipes));

            g.Categories = ExpandCategories(Arg.GetStringList(a, "cat"));
            g.Levels = Arg.GetStringList(a, "lvl");
            foreach (var s in Arg.GetStringList(a, "ids"))
            {
                if (s.StartsWith("$")) { g.FromSet = NamedSets.Normalize(s); continue; }
                if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    throw new QueryException("ids: '" + s + "' is not an id or a set ($name)");
                g.Ids.Add(id);
            }
            if (g.Ids.Count == 0 && g.FromSet == null && g.Categories.Count == 0 && g.Recipe != "index")
                g.Categories.Add("LightingFixtures");

            g.Against = ExpandCategories(Arg.GetStringList(a, "against"));
            var links = Arg.GetStringList(a, "links");
            if (links.Count == 1 && string.Equals(links[0], "none", StringComparison.OrdinalIgnoreCase)) g.IncludeLinks = false;
            else if (!(links.Count == 1 && string.Equals(links[0], "all", StringComparison.OrdinalIgnoreCase))) g.Links = links;
            g.IncludeHost = Arg.GetBool(a, "host") ?? true;

            var side = (Arg.GetString(a, "side") ?? "up").Trim().ToLowerInvariant();
            if (side == "up") g.Up = true;
            else if (side == "down") g.Up = false;
            else throw new QueryException("side: up (default) or down");

            g.Points = (int)(Arg.GetLong(a, "points") ?? (g.Recipe == "clearance" ? 1 : 9));
            if (Array.IndexOf(Probes.Allowed, g.Points) < 0) throw new QueryException("points: 1, 5 or 9");

            g.Tolerance = Length(a, "tol", g.Tolerance, 0, 5);
            g.MaxRange = Length(a, "max", g.MaxRange, 0.1, 200);
            g.Radius = Length(a, "radius", g.Recipe == "near" ? 2.0 : 3.0, 0.01, 100);
            if (g.Recipe == "duplicates" && !a.ContainsKey("tol")) g.Tolerance = 0.01;
            if (a.TryGetValue("min_vol", out var mv) && mv != null)
            {
                if (!(mv is double || mv is long)) throw new QueryException("min_vol: a number in cubic feet");
                g.MinVolume = Math.Max(0, Convert.ToDouble(mv, CultureInfo.InvariantCulture));
            }
            g.SameType = Arg.GetBool(a, "same_type") ?? true;
            g.Views = Arg.GetString(a, "views");
            if (g.Recipe == "view_range" && string.IsNullOrWhiteSpace(g.Views)) g.Views = "rcp";
            g.Source = (Arg.GetString(a, "source") ?? "both").Trim().ToLowerInvariant();
            if (g.Source != "both" && g.Source != "spaces" && g.Source != "rooms")
                throw new QueryException("source: both (default), spaces (host MEP spaces) or rooms (architecture link rooms)");

            var units = (Arg.GetString(a, "units") ?? "ft").Trim().ToLowerInvariant();
            if (units == "mm") g.Millimeters = true;
            else if (units != "ft" && units != "") throw new QueryException("units: ft (default) or mm");
            g.Page = (int)(Arg.GetLong(a, "page") ?? 1);
            g.PageSize = (int)(Arg.GetLong(a, "page_size") ?? 100);
            if (g.Page < 1) throw new QueryException("page starts at 1");
            if (g.PageSize < 1 || g.PageSize > QuerySpec.MaxPageSize) throw new QueryException("page_size between 1 and " + QuerySpec.MaxPageSize);
            g.Only = Arg.GetString(a, "only");
            g.Rebuild = Arg.GetBool(a, "rebuild") ?? false;
            g.SurfaceReference = Arg.GetBool(a, "references") ?? false;
            g.ExcludeSubcategories = Arg.GetStringList(a, "sub_exclude");
            g.Worksets = Arg.GetStringList(a, "ws");
            g.Types = Arg.GetStringList(a, "type");
            // "near" looks for beams and columns around the body: 10 ft above is enough (30 ft would reach other floors).
            if (g.Recipe == "near" && !a.ContainsKey("max")) g.MaxRange = 10.0;

            if (g.Against.Count == 0)
            {
                if (g.Recipe == "mounting" || g.Recipe == "clearance") g.Against.AddRange(g.Up ? DefaultAbove : new[] { "Floors", "Ramps", "Stairs", "StructuralFoundation" });
                else if (g.Recipe == "clashes") g.Against.AddRange(DefaultClash);
                else if (g.Recipe == "near") g.Against.AddRange(DefaultNear);
                else if (g.Recipe == "wall") g.Against.Add("Walls");
            }
            return g;
        }

        public static List<string> ExpandCategories(IEnumerable<string> names)
        {
            var list = new List<string>();
            foreach (var n in names)
            {
                var k = LegacyAliases.CategoryAliases.TryGetValue(n.Trim(), out var en) ? en : n.Trim();
                if (Aliases.TryGetValue(k, out var mapped)) list.AddRange(mapped);
                else list.Add(n.Trim());
            }
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static double Length(IDictionary<string, object> a, string key, double fallback, double min, double max)
        {
            if (!a.TryGetValue(key, out var v) || v == null) return fallback;
            if (!Lengths.TryFeet(v, out var ft, out var err)) throw new QueryException(key + ": " + err);
            if (ft < min || ft > max)
                throw new QueryException(key + ": out of range (" + min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture) + " ft)");
            return ft;
        }

        /// <summary>Stable key of the arguments that change the result (for the cache).</summary>
        public Dictionary<string, object> CacheArgs() => new Dictionary<string, object>
        {
            ["r"] = Recipe, ["cat"] = string.Join(",", Categories), ["lvl"] = string.Join(",", Levels),
            ["ids"] = string.Join(",", Ids), ["set"] = FromSet ?? "", ["against"] = string.Join(",", Against),
            ["vin"] = IncludeLinks ? string.Join(",", Links) : "-", ["host"] = IncludeHost, ["up"] = Up, ["pts"] = (long)Points,
            ["tol"] = Tolerance, ["max"] = MaxRange, ["radius"] = Radius, ["minv"] = MinVolume, ["mt"] = SameType,
            ["views"] = Views ?? "", ["source"] = Source, ["sub"] = string.Join(",", ExcludeSubcategories),
            ["ws"] = string.Join(",", Worksets), ["type"] = string.Join(",", Types),
            ["references"] = SurfaceReference,
        };
    }
}
