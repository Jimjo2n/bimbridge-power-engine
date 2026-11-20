// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Bimbridge.Engine;
using Bimbridge.Engine.Qa;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    internal static class SheetSetPdf
    {
        private static readonly Guid SchemaId = new Guid("e778c2a2-fe32-4f45-ba09-821f56374e87");

        internal static Dictionary<string, object> ValidatePreset(object value)
        {
            if (!(value is Dictionary<string, object> preset)) throw new QueryException("preset_pdf exige un objeto JSON");
            foreach (var item in preset)
            {
                var valid = item.Key switch
                {
                    "papel" => item.Value is string name && Enum.TryParse(name, true, out ExportPaperFormat format) &&
                        Enum.IsDefined(typeof(ExportPaperFormat), format) && !int.TryParse(name, out _),
                    "zoom" => item.Value is string zoom && (zoom == "100" || zoom == "ajustar"),
                    "color" => item.Value is string color && (color == "color" || color == "gris"),
                    "combinar" or "raster" or "ocultar_etiquetas_sin_ref" => item.Value is bool,
                    _ => false,
                };
                if (!valid) throw new QueryException("preset_pdf: opción o valor inválido para " + item.Key);
            }
            return preset;
        }

        internal static void Write(ViewSheetSet set, IEnumerable<ElementId> ordered, object preset)
        {
            var schema = Schema.Lookup(SchemaId);
            if (schema == null)
            {
                var builder = new SchemaBuilder(SchemaId);
                builder.SetSchemaName("BimbridgeSheetSetPdf");
                builder.SetReadAccessLevel(AccessLevel.Public);
                builder.SetWriteAccessLevel(AccessLevel.Public);
                builder.AddArrayField("Order", typeof(ElementId));
                builder.AddSimpleField("Preset", typeof(string));
                schema = builder.Finish();
            }
            var entity = new Entity(schema);
            entity.Set<IList<ElementId>>(schema.GetField("Order"), ordered.ToList());
            entity.Set(schema.GetField("Preset"), Json.Write(preset));
            set.SetEntity(entity);
        }

        internal static (IList<ElementId> Order, Dictionary<string, object> Preset) Read(ViewSheetSet set)
        {
            var schema = Schema.Lookup(SchemaId);
            if (schema == null) return (null, null);
            var entity = set.GetEntity(schema);
            if (!entity.IsValid()) return (null, null);
            var preset = Json.Parse(entity.Get<string>(schema.GetField("Preset")));
            return (entity.Get<IList<ElementId>>(schema.GetField("Order")), preset == null ? null : ValidatePreset(preset));
        }

        internal static void CheckViewportBounds(Document doc, IEnumerable<ViewSheet> sheets, List<string> differences)
        {
            foreach (var sheet in sheets)
            {
                var paper = sheet.Outline;
                if (paper == null || paper.Max.U <= paper.Min.U || paper.Max.V <= paper.Min.V)
                {
                    differences.Add(sheet.SheetNumber + ": Revit no proporcionó un contorno de papel evaluable");
                    continue;
                }
                bool Outside(Outline bounds) => !bounds.IsEmpty && PrintSheetSet.OutsidePaper(
                    bounds.MinimumPoint.X, bounds.MinimumPoint.Y, bounds.MaximumPoint.X, bounds.MaximumPoint.Y,
                    paper.Min.U, paper.Min.V, paper.Max.U, paper.Max.V);
                foreach (var id in sheet.GetAllViewports())
                {
                    if (!(doc.GetElement(id) is Viewport viewport)) continue;
                    using var box = viewport.GetBoxOutline();
                    using var label = viewport.GetLabelOutline();
                    if (Outside(box) || Outside(label))
                        differences.Add(sheet.SheetNumber + ": viewport " + Ids.ToLong(id) + " o su título excede el contorno de la hoja");
                }
            }
        }
    }
}
