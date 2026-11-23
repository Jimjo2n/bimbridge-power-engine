// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bimbridge.Engine.Qc
{
    /// <summary>
    /// Why is an element not visible in a view? (§7.3). The add-in gathers the facts through the API (hiding, category, filters, workset,
    /// phase, design option, crop, view range or plan region range) and this class decides, without Revit, which causes apply and which
    /// cannot be checked through the API. It does not "fix" anything: it never suggests raising the view range without saying what else would be affected.
    /// </summary>
    public static class VisibilityQc
    {
        public sealed class Facts
        {
            public bool ElementHidden;
            public bool CategoryHidden;
            public List<string> HidingFilters = new List<string>();
            public bool WorksetHidden;
            /// <summary>true = the element's phase falls outside the view's phase filter (created later or demolished earlier).</summary>
            public bool? PhaseHidden;
            public bool? DesignOptionHidden;
            public bool CropActive;
            /// <summary>Element box inside the view crop (null = could not be computed).</summary>
            public bool? InsideCrop;
            /// <summary>Template that controls the view (name) or null.</summary>
            public string Template;
            /// <summary>"floorplan" | "rcp" | other (no range).</summary>
            public string PlanKind;
            /// <summary>Absolute elevations (mm) of the range that applies at that point (the plan region's if the element falls in one).</summary>
            public double? Cut, Bottom, Top, Depth;
            public string RangeSource;
            /// <summary>Visible body of the element (mm), not the family origin.</summary>
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
            var via = f.Template != null ? " (controlled by template " + f.Template + ")" : "";
            if (f.ElementHidden) r.Add(new Reason { Code = "hidden_in_view", Detail = "hidden element by element in this view", Certain = true });
            if (f.CategoryHidden) r.Add(new Reason { Code = "hidden_category", Detail = "category turned off in Visibility/Graphics" + via, Certain = true });
            foreach (var name in f.HidingFilters ?? new List<string>()) r.Add(new Reason { Code = "filter", Detail = name + " hides it" + via, Certain = true });
            if (f.WorksetHidden) r.Add(new Reason { Code = "hidden_workset", Detail = "its workset is not visible in this view" + via, Certain = true });
            if (f.PhaseHidden == true) r.Add(new Reason { Code = "phase", Detail = "its phase falls outside the view's phase filter", Certain = true });
            if (f.DesignOptionHidden == true) r.Add(new Reason { Code = "design_option", Detail = "belongs to a design option the view does not show", Certain = true });
            if (f.CropActive && f.InsideCrop == false) r.Add(new Reason { Code = "outside_crop", Detail = "falls outside the crop region", Certain = true });

            if ((f.PlanKind == "floorplan" || f.PlanKind == "rcp") && f.ZMin.HasValue && f.ZMax.HasValue && f.Cut.HasValue)
            {
                var src = f.RangeSource ?? "view range";
                var cut = f.Cut.Value;
                if (f.PlanKind == "rcp")
                {
                    // RCP: looks up from the cut plane to "top".
                    var top = f.Top ?? double.MaxValue;
                    if (f.ZMax < cut) r.Add(new Reason { Code = "view_range_of", Detail = "the body (" + F(f.ZMin) + "–" + F(f.ZMax) + " mm) is below the cut " + F(cut) + " of the " + src, Certain = true });
                    else if (f.ZMin > top) r.Add(new Reason { Code = "view_range_of", Detail = "the body starts at " + F(f.ZMin) + " mm, above top " + F(top) + " of the " + src, Certain = true });
                }
                else
                {
                    var depth = f.Depth ?? f.Bottom ?? double.MinValue;
                    if (f.ZMax < depth) r.Add(new Reason { Code = "view_range_of", Detail = "the body ends at " + F(f.ZMax) + " mm, below the depth " + F(depth) + " of the " + src, Certain = true });
                    else if (f.ZMin > cut)
                        r.Add(new Reason { Code = "view_range_of", Detail = "the body is entirely above the cut " + F(cut) + " of the " + src + "; in floor plan, fixtures above the cut are not drawn (unless their category rules say otherwise)", Certain = false });
                }
            }
            notCheckable = new List<string>
            {
                "the family's own visibility (visibility parameters, detail level, subcategories) — check in the view",
                "overlap with opaque filled regions or other elements on top — final visual inspection",
            };
            if (f.PhaseHidden == null) notCheckable.Add("phase: could not be read");
            if (f.CropActive && f.InsideCrop == null) notCheckable.Add("crop: could not compute the element's box in the view");
            return r;
        }

        private static string F(double? v) => v.HasValue ? v.Value.ToString("0", CultureInfo.InvariantCulture) : "?";
    }
}
