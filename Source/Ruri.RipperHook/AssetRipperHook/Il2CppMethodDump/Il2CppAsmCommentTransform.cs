using System.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

namespace Ruri.RipperHook.AR;

/// <summary>
/// ILSpy AST 变换：对每个有方法体的声明（方法 / 构造 / 运算符 / 属性·索引器·事件访问器），
/// 取其 <see cref="IMethod"/>，查回 Cpp2IL 的原生反汇编，并以单行注释逐行注入到方法体最前面。
/// 注册方式见 <c>AR_Il2CppMethodDump_Hook</c> 对 <c>WholeProjectDecompiler.CreateDecompiler</c> 的 hook。
/// </summary>
internal sealed class Il2CppAsmCommentTransform : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        // 物化一份再改树，避免边遍历边改。
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
            if (body == null || body.IsNull) continue;
            if (decl.GetSymbol() is not IMethod method) continue;

            string asm = Il2CppAsmLookup.GetDisassembly(method);
            if (asm == null) continue;

            // ILSpy only emits comments INSIDE a block when they anchor before a statement; an empty
            // block would render them after the closing brace. Give empty bodies an EmptyStatement
            // (renders as a lone ';') so the asm lands inside the body.
            if (body.Statements.FirstOrDefault() == null)
            {
                body.Statements.Add(new EmptyStatement());
            }
            Statement first = body.Statements.First();
            foreach (string line in asm.Split('\n'))
            {
                string text = line.TrimEnd('\r', '\t', ' ');
                if (text.Length == 0) continue; // 跳过反汇编尾部的空行
                body.InsertChildBefore(first, new Comment(" " + text, CommentType.SingleLine), Roles.Comment);
            }
        }
    }
}
