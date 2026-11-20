// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Builds the C# code compiled by the general runner (R003). Accepts three forms:
    ///   expression →  doc.Title                                   (returns its value)
    ///   body       →  var n = 0; foreach (...) n++; return n;     (body of a method with doc, uidoc, app, uiapp, args, ctx)
    ///   class      →  public static class X { public static object Run(ScriptContext ctx) { ... } }
    /// Leading "using X;" lines are hoisted to the header. #line makes errors cite lines of the user's code.
    /// There is no allow-list: it compiles against the full public API of the loaded Revit version.
    /// </summary>
    public static class ScriptSource
    {
        public const string Namespace = "Bimbridge.Scripts";
        public const string EntryMethod = "Run";
        public const string ContextType = "Bimbridge.Addin.Scripting.ScriptContext";

        public static readonly string[] DefaultUsings =
        {
            "System", "System.Collections.Generic", "System.Linq", "System.Text", "System.IO",
            "Autodesk.Revit.DB", "Autodesk.Revit.DB.Architecture", "Autodesk.Revit.DB.Mechanical", "Autodesk.Revit.DB.Plumbing",
            "Autodesk.Revit.DB.Electrical", "Autodesk.Revit.DB.Structure", "Autodesk.Revit.UI", "Bimbridge.Addin.Scripting",
        };

        /// <summary>Names the body already has defined (do not declare them again).</summary>
        public static readonly string[] Predefined = { "ctx", "doc", "uidoc", "app", "uiapp", "args" };

        public sealed class Wrapped
        {
            public string Source;
            public string Kind;
            public string ClassName;
            public List<string> HoistedUsings = new List<string>();
        }

        private static readonly Regex UsingLine = new Regex(@"^\s*using\s+(static\s+)?[A-Za-z_][\w\.]*(\s*=\s*[A-Za-z_][\w\.<>, ]*)?\s*;\s*$", RegexOptions.Compiled);
        /// <summary>Step-by-step recipe (0.6.0): a class that inherits from Receta. Compiled as is inside the scripts namespace.</summary>
        private static readonly Regex RecipeForm = new Regex(@"\bclass\s+\w+\s*:\s*(Bimbridge\.Addin\.Scripting\.)?Receta\b", RegexOptions.Compiled);
        public const string RecipeBaseType = "Bimbridge.Addin.Scripting.Receta";

        public static bool IsRecipe(string code) => code != null && RecipeForm.IsMatch(code);

        private static readonly Regex ClassForm = new Regex(@"\bclass\s+\w+[\s\S]*\bstatic\s+object\s+Run\s*\(", RegexOptions.Compiled);

        public static Wrapped Wrap(string code, string className)
        {
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("empty code");
            code = code.Replace("\r\n", "\n").Trim('\n', ' ', '\t');
            var w = new Wrapped { ClassName = className };
            var lines = code.Split('\n').ToList();
            var userStart = 0;
            // Leading usings (also after // comments) are hoisted out of the class. Comments are not lost: they are only skipped.
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

        /// <summary>A single line with no ';' or braces and no statement keywords = expression.</summary>
        public static bool IsExpression(string body)
        {
            var t = body.Trim();
            if (t.Length == 0 || t.Contains("\n") || t.Contains(";") || t.Contains("{")) return false;
            foreach (var kw in new[] { "return ", "var ", "foreach", "using ", "if ", "if(", "for ", "for(", "while", "throw " })
                if (t.StartsWith(kw, StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>Fingerprint for the compilation cache: code + Revit version + add-in version + references.</summary>
        public static string CacheKey(string source, string revitVersion, string addinVersion, string referencesFingerprint) =>
            Home.Sha256Hex(source + "\u0001" + revitVersion + "\u0001" + addinVersion + "\u0001" + referencesFingerprint, 24);

        /// <summary>Folder of named saved scripts (to rerun them without resending the code).</summary>
        public static string ScriptsFolder => Home.Sub("scripts");

        public static string SafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("empty name");
            var n = name.Trim();
            if (n.Length > 60) throw new ArgumentException("script name of up to 60 characters");
            foreach (var c in n)
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.'))
                    throw new ArgumentException("script name: letters, digits, _ - .");
            if (n.Contains("..")) throw new ArgumentException("invalid script name");
            return n;
        }

        public static string PathOf(string name) => Path.Combine(ScriptsFolder, SafeName(name) + ".cs");
    }
}
