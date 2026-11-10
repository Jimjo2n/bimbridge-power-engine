// SPDX-License-Identifier: GPL-3.0-only
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PDI.RevitMcp.Scripting
{
    /// <summary>
    /// rc3: metadatos declarativos de una receta leídos del CÓDIGO FUENTE, sin instanciarla (antes `tarea iniciar` llamaba al
    /// constructor para leer Version/Transaccion, es decir, ejecutaba código de la receta sin autorización).
    /// Sólo se aceptan valores constantes: `=> "2"`, `=> PoliticaTx.Lectura`, o `get { return …; }` con lo mismo. Cualquier otra forma
    /// devuelve null y el llamador asume lo conservador (que escribe).
    /// </summary>
    public static class RecipeMetadata
    {
        /// <summary>Valor constante de la propiedad <paramref name="name"/> en la clase que hereda de Receta: el literal de texto,
        /// o el nombre del miembro de enumeración (p. ej. "Lectura"). null si no es constante o no existe.</summary>
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
