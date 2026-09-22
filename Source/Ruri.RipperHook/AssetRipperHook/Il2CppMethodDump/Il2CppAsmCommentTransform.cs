using System.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

namespace Ruri.RipperHook.AR;

internal sealed class Il2CppAsmCommentTransform : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        foreach (EntityDeclaration decl in rootNode.DescendantsAndSelf.OfType<EntityDeclaration>().ToList())
        {
            BlockStatement body = decl switch
            {
                MethodDeclaration md => md.Body,
                ConstructorDeclaration cd => cd.Body,
                OperatorDeclaration od => od.Body,
                Accessor ac => ac.Body,
                _ => null
            };
            if (body == null) continue;
            if (decl.GetSymbol() is not IMethod method) continue;

            string asm = Il2CppAsmLookup.GetDisassembly(method);
            if (asm == null) continue;

            if (body.Statements.FirstOrDefault() == null)
            {
                body.Statements.Add(new EmptyStatement());
            }
            Statement first = body.Statements.First();
            foreach (string line in asm.Split('\n'))
            {
                string text = line.TrimEnd('\r', '\t', ' ');
                if (text.Length == 0) continue;                first.AddLeadingTrivia(new Comment(" " + text, CommentType.SingleLine));
            }
        }
    }
}
