// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Bimbridge.Engine;
using Bimbridge.Engine.Qa;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    internal static class LightingCallout
    {
        private static readonly Guid SchemaId = new Guid("983809a3-6fa4-4089-9f86-6b7817611984");
        private const string Field = "Origen";

        internal static Dictionary<string, object> Bind(Document doc, long id, Dictionary<string, object> args)
        {
            if (!(doc.GetElement(Ids.ToId(id)) is FamilyInstance light) ||
                Ids.ToLong(light.Category?.Id ?? ElementId.InvalidElementId) != (long)BuiltInCategory.OST_LightingFixtures)
                throw new QueryException("text_note: " + id + " no es una luminaria del anfitrión");
            var config = new Dictionary<string, object> { ["uid"] = light.UniqueId };
            foreach (var key in new[] { "codigo_param", "altura_param", "descontar_tipo_param", "negrita", "subrayado" })
                if (args.TryGetValue(key, out var value)) config[key] = value;
            return config;
        }

        internal static (string Text, XYZ Point, long Id, string TypeUid) Observe(Document doc, Dictionary<string, object> config)
        {
            if (!(doc.GetElement(Arg.GetString(config, "uid")) is FamilyInstance light) || light.Location is not LocationPoint location)
                throw new QueryException("la luminaria asociada ya no está disponible con un punto de inserción");
            var type = light.Symbol;
            var codeName = Arg.GetString(config, "codigo_param");
            var code = codeName == null ? type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString() : TextParameter(type, codeName);
            if (string.IsNullOrWhiteSpace(code) && codeName == null) code = type.Name;
            if (string.IsNullOrWhiteSpace(code)) throw new QueryException("la luminaria asociada carece de código");
            var height = LengthParameter(light, Arg.GetString(config, "altura_param") ?? "Mounting Height");
            if (Arg.GetString(config, "descontar_tipo_param") is string body) height -= LengthParameter(type, body);
            return (LightingCalloutState.Text(code, height), location.Point, Ids.ToLong(light.Id), type.UniqueId);
        }

        private static string TextParameter(Element element, string name)
        {
            var matches = element.GetParameters(name).Where(p => p.HasValue && p.StorageType == StorageType.String).ToList();
            if (matches.Count != 1) throw new QueryException("el código necesita un parámetro de texto único: " + name);
            return matches[0].AsString();
        }

        private static double LengthParameter(Element element, string name)
        {
            var matches = element.GetParameters(name).Where(p => p.HasValue && p.StorageType == StorageType.Double &&
                p.Definition.GetDataType() == SpecTypeId.Length).ToList();
            if (matches.Count != 1) throw new QueryException("la altura necesita un parámetro de longitud único: " + name);
            return matches[0].AsDouble();
        }

        internal static List<TextNote> InView(Document doc, ElementId view)
        {
            using var collector = new FilteredElementCollector(doc);
            return collector.OfClass(typeof(TextNote)).OwnedByView(view).Cast<TextNote>().ToList();
        }

        internal static Dictionary<string, object> Read(TextNote note)
        {
            var schema = Schema.Lookup(SchemaId);
            if (schema == null) return null;
            var entity = note.GetEntity(schema);
            return entity.IsValid() ? Json.ParseObject(entity.Get<string>(schema.GetField(Field))) : null;
        }

        internal static void Write(TextNote note, Dictionary<string, object> config, XYZ point, string typeUid)
        {
            var schema = Schema.Lookup(SchemaId);
            if (schema == null)
            {
                var builder = new SchemaBuilder(SchemaId);
                builder.SetSchemaName("BimbridgeLightingCallout");
                builder.AddSimpleField(Field, typeof(string));
                schema = builder.Finish();
            }
            var data = WithSource(config, point, typeUid);
            var entity = new Entity(schema);
            entity.Set(schema.GetField(Field), Json.Write(data));
            note.SetEntity(entity);
        }

        internal static bool IsCurrent(TextNote note, Dictionary<string, object> config, string text, XYZ point, string typeUid) =>
            LightingCalloutState.IsCurrent(note.Text, Arg.GetString(config, "tipo_origen"), text, typeUid,
                point.DistanceTo(PreviousPoint(config)));

        internal static XYZ PreviousPoint(Dictionary<string, object> config)
        {
            var p = (List<object>)config["punto_origen"];
            return new XYZ(Convert.ToDouble(p[0], CultureInfo.InvariantCulture), Convert.ToDouble(p[1], CultureInfo.InvariantCulture),
                Convert.ToDouble(p[2], CultureInfo.InvariantCulture));
        }

        internal static string Fingerprint(Dictionary<string, object> config, string text, XYZ point, string typeUid) =>
            Home.Sha256Hex(OpSemantics.Canonical(new object[] { config, text, Vector(point), typeUid }));

        internal static string NoteFingerprint(TextNote note, Dictionary<string, object> config, string source) =>
            NoteFingerprint(note.Text, note.Coord, config, source);

        internal static string NoteFingerprint(string text, XYZ location, Dictionary<string, object> config, string source) =>
            Home.Sha256Hex(OpSemantics.Canonical(new object[] { config, text, Vector(location), source }));

        internal static Dictionary<string, object> WithSource(Dictionary<string, object> config, XYZ point, string typeUid) =>
            new Dictionary<string, object>(config) { ["punto_origen"] = Vector(point), ["tipo_origen"] = typeUid };

        internal static XYZ Reposition(TextNote note, View view, Dictionary<string, object> config, XYZ point)
        {
            var delta = point - PreviousPoint(config);
            return note.Coord + view.RightDirection.Multiply(delta.DotProduct(view.RightDirection)) +
                view.UpDirection.Multiply(delta.DotProduct(view.UpDirection));
        }

        private static List<object> Vector(XYZ point) =>
            new List<object> { Math.Round(point.X, 7), Math.Round(point.Y, 7), Math.Round(point.Z, 7) };

        internal static void Format(TextNote note, Dictionary<string, object> options)
        {
            if (!options.ContainsKey("negrita") && !options.ContainsKey("subrayado")) return;
            using var text = note.GetFormattedText();
            if (Arg.GetBool(options, "negrita") is bool bold) text.SetBoldStatus(bold);
            if (Arg.GetBool(options, "subrayado") is bool underline) text.SetUnderlineStatus(underline);
            note.SetFormattedText(text);
        }
    }
}
