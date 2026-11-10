// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Arma el código C# que compila el runner general (R003). Acepta tres formas:
    ///   expresión  →  doc.Title                                   (devuelve su valor)
    ///   cuerpo     →  var n = 0; foreach (...) n++; return n;     (cuerpo de un método con doc, uidoc, app, uiapp, args, ctx)
    ///   clase      →  public static class X { public static object Run(ScriptContext ctx) { ... } }
    /// Las líneas "using X;" del principio se suben a la cabecera. #line hace que los errores citen líneas del código del usuario.
    /// No hay lista blanca: compila contra la API pública completa de la versión de Revit cargada.
    /// </summary>
    public static class ScriptSource
    {
        public const string Namespace = "PDI.RevitMcp.Scripts";
        public const string EntryMethod = "Run";
        public const string ContextType = "PDI.RevitMcp.Addin.Scripting.ScriptContext";

        public static readonly string[] DefaultUsings =
        {
            "System", "System.Collections.Generic", "System.Linq", "System.Text", "System.IO",
            "Autodesk.Revit.DB", "Autodesk.Revit.DB.Architecture", "Autodesk.Revit.DB.Mechanical", "Autodesk.Revit.DB.Plumbing",
            "Autodesk.Revit.DB.Electrical", "Autodesk.Revit.DB.Structure", "Autodesk.Revit.UI", "PDI.RevitMcp.Addin.Scripting",
        };

        /// <summary>Nombres que el cuerpo ya tiene definidos (no declararlos de nuevo).</summary>
        public static readonly string[] Predefined = { "ctx", "doc", "uidoc", "app", "uiapp", "args" };

        public sealed class Wrapped
        {
            public string Source;
            public string Kind;
            public string ClassName;
            public List<string> HoistedUsings = new List<string>();
        }

        private static readonly Regex UsingLine = new Regex(@"^\s*using\s+(static\s+)?[A-Za-z_][\w\.]*(\s*=\s*[A-Za-z_][\w\.<>, ]*)?\s*;\s*$", RegexOptions.Compiled);
        /// <summary>Receta por pasos (0.6.0): una clase que hereda de Receta. Se compila tal cual dentro del espacio de nombres de scripts.</summary>
        private static readonly Regex RecipeForm = new Regex(@"\bclass\s+\w+\s*:\s*(PDI\.RevitMcp\.Addin\.Scripting\.)?Receta\b", RegexOptions.Compiled);
        public const string RecipeBaseType = "PDI.RevitMcp.Addin.Scripting.Receta";

        public static bool IsRecipe(string code) => code != null && RecipeForm.IsMatch(code);

        private static readonly Regex ClassForm = new Regex(@"\bclass\s+\w+[\s\S]*\bstatic\s+object\s+Run\s*\(", RegexOptions.Compiled);

        public static Wrapped Wrap(string code, string className)
        {
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("código vacío");
            code = code.Replace("\r\n", "\n").Trim('\n', ' ', '\t');
            var w = new Wrapped { ClassName = className };
            var lines = code.Split('\n').ToList();
            var userStart = 0;
            // using al principio (también tras comentarios //): se suben fuera de la clase. Los comentarios no se pierden: sólo se saltan.
            while (userStart < lines.Count && (UsingLine.IsMatch(lines[userStart]) || lines[userStart].Trim().Length == 0 || lines[userStart].TrimStart().StartsWith("//", StringComparison.Ordinal)))
            {
                if (UsingLine.IsMatch(lines[userStart])) w.HoistedUsings.Add(lines[userStart].Trim());
                userStart++;
            }
            var body = string.Join("\n", lines.Skip(userStart));
            var sb = new StringBuilder();
            foreach (var u in DefaultUsings) sb.Append("using ").Append(u).Append(";\n");
            foreach (var u in w.HoistedUsings) sb.Append(u).Append('\n');

            if (RecipeForm.IsMatch(body) || ClassForm.IsMatch(body))
            {
                w.Kind = RecipeForm.IsMatch(body) ? "receta" : "clase";
                sb.Append("namespace ").Append(Namespace).Append(" {\n");
                sb.Append("#line ").Append(userStart + 1).Append(" \"script\"\n");
                sb.Append(body).Append("\n#line default\n}\n");
                w.Source = sb.ToString();
                return w;
            }

            var isExpr = IsExpression(body);
            w.Kind = isExpr ? "expresión" : "cuerpo";
            sb.Append("namespace ").Append(Namespace).Append(" {\n");
            sb.Append("public static class ").Append(className).Append(" {\n");
            sb.Append("public static object ").Append(EntryMethod).Append("(").Append(ContextType).Append(" ctx) {\n");
            sb.Append("var doc = ctx.Doc; var uidoc = ctx.UiDoc; var app = ctx.App; var uiapp = ctx.UiApp; var args = ctx.Args;\n");
            sb.Append("#line ").Append(userStart + 1).Append(" \"script\"\n");
            if (isExpr) sb.Append("return (object)(").Append(body).Append(");\n");
            else sb.Append(body).Append('\n');
            sb.Append("#line default\n");
            if (!isExpr) sb.Append("#pragma warning disable CS0162\nreturn null;\n#pragma warning restore CS0162\n");
            sb.Append("}\n}\n}\n");
            w.Source = sb.ToString();
            return w;
        }

        /// <summary>Una sola línea sin ';' ni llaves y sin palabras de sentencia = expresión.</summary>
        public static bool IsExpression(string body)
        {
            var t = body.Trim();
            if (t.Length == 0 || t.Contains("\n") || t.Contains(";") || t.Contains("{")) return false;
            foreach (var kw in new[] { "return ", "var ", "foreach", "using ", "if ", "if(", "for ", "for(", "while", "throw " })
                if (t.StartsWith(kw, StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>Huella para la caché de compilación: código + versión de Revit + versión del add-in + referencias.</summary>
        public static string CacheKey(string source, string revitVersion, string addinVersion, string referencesFingerprint) =>
            Home.Sha256Hex(source + "\u0001" + revitVersion + "\u0001" + addinVersion + "\u0001" + referencesFingerprint, 24);

        /// <summary>Carpeta de scripts guardados con nombre (para repetirlos sin reenviar el código).</summary>
        public static string ScriptsFolder => Home.Sub("scripts");

        public static string SafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("nombre vacío");
            var n = name.Trim();
            if (n.Length > 60) throw new ArgumentException("nombre de script de hasta 60 caracteres");
            foreach (var c in n)
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.'))
                    throw new ArgumentException("nombre de script: letras, números, _ - .");
            if (n.Contains("..")) throw new ArgumentException("nombre de script inválido");
            return n;
        }

        public static string PathOf(string name) => Path.Combine(ScriptsFolder, SafeName(name) + ".cs");
    }
}
