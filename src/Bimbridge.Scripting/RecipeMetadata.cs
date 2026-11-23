// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bimbridge.Scripting
{
    /// <summary>
    /// rc3: declarative metadata of a recipe read from the SOURCE CODE, without instantiating it (previously `tarea iniciar` called the
    /// constructor to read Version/Transaction, i.e. it ran recipe code without authorization).
    /// Only constant values are accepted: `=> "2"`, `=> TxPolicy.ReadOnly`, or `get { return …; }` with the same. Any other form
    /// returns null and the caller assumes the conservative case (that it writes).
    /// </summary>
    public static class RecipeMetadata
    {
        // ---- Legacy aliases (one version only): recipes written before the English rename. Remove with the next version. ----
        private static readonly Dictionary<string, string> LegacyProperty = new Dictionary<string, string> { ["Transaction"] = "Transaccion", ["Description"] = "Descripcion" };
        private static readonly string[] BaseTypes = { "Recipe", "Receta" };
        private static readonly string[] PolicyTypes = { "TxPolicy", "PoliticaTx" };
        private static readonly Dictionary<string, string> LegacyPolicy = new Dictionary<string, string> { ["PorUnidad"] = "PerUnit", ["Ninguna"] = "None", ["Lectura"] = "ReadOnly" };

        /// <summary>Constant value of the property <paramref name="name"/> in the class that inherits from Recipe: the string literal,
        /// or the TxPolicy member name in English (e.g. "ReadOnly"). null if it is not constant or does not exist.</summary>
        public static string Constant(string source, string name)
        {
            var root = CSharpSyntaxTree.ParseText(source ?? "").GetRoot();
            var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.BaseList != null && c.BaseList.Types.Any(t => BaseTypes.Contains(t.Type.ToString().Split('.').Last())));
            LegacyProperty.TryGetValue(name ?? "", out var legacyName);
            var prop = cls?.Members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.Text == name || p.Identifier.Text == legacyName);
            if (prop == null) return null;
            ExpressionSyntax e = prop.ExpressionBody?.Expression;
            if (e == null)
            {
                var get = prop.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
                e = get?.ExpressionBody?.Expression;
                if (e == null && get?.Body != null && get.Body.Statements.Count == 1 && get.Body.Statements[0] is ReturnStatementSyntax rs) e = rs.Expression;
            }
            if (e is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)) return lit.Token.ValueText;
            if (e is MemberAccessExpressionSyntax ma && PolicyTypes.Contains(ma.Expression.ToString().Split('.').Last()))
                return LegacyPolicy.TryGetValue(ma.Name.Identifier.Text, out var en) ? en : ma.Name.Identifier.Text;
            return null;
        }
    }
}
