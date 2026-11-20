// SPDX-License-Identifier: GPL-3.0-only
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bimbridge.Scripting
{
    /// <summary>
    /// rc3: declarative metadata of a recipe read from the SOURCE CODE, without instantiating it (previously `tarea iniciar` called the
    /// constructor to read Version/Transaccion, i.e. it ran recipe code without authorization).
    /// Only constant values are accepted: `=> "2"`, `=> PoliticaTx.Lectura`, or `get { return …; }` with the same. Any other form
    /// returns null and the caller assumes the conservative case (that it writes).
    /// </summary>
    public static class RecipeMetadata
    {
        /// <summary>Constant value of the property <paramref name="name"/> in the class that inherits from Receta: the string literal,
        /// or the enum member name (e.g. "Lectura"). null if it is not constant or does not exist.</summary>
        public static string Constant(string source, string name)
        {
            var root = CSharpSyntaxTree.ParseText(source ?? "").GetRoot();
            var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.BaseList != null && c.BaseList.Types.Any(t => t.Type.ToString().Split('.').Last() == "Receta"));
            var prop = cls?.Members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.Text == name);
            if (prop == null) return null;
            ExpressionSyntax e = prop.ExpressionBody?.Expression;
            if (e == null)
            {
                var get = prop.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
                e = get?.ExpressionBody?.Expression;
                if (e == null && get?.Body != null && get.Body.Statements.Count == 1 && get.Body.Statements[0] is ReturnStatementSyntax rs) e = rs.Expression;
            }
            if (e is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)) return lit.Token.ValueText;
            if (e is MemberAccessExpressionSyntax ma && ma.Expression.ToString().Split('.').Last() == "PoliticaTx") return ma.Name.Identifier.Text;
            return null;
        }
    }
}
