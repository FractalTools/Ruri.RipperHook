using System.Linq;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;

namespace Ruri.RipperHook.AR;

/// <summary>
/// ILSpy AST transform that names the <c>LayoutKind</c> argument of a synthesized <c>[StructLayout]</c> attribute.
/// il2cpp strips the <c>LayoutKind</c> enum's members from the reconstructed mscorlib (unlike MethodImplOptions, AR does
/// not polyfill it), so ILSpy cannot resolve the value to a name and emits a raw cast — <c>[StructLayout((LayoutKind)3)]</c>
/// instead of <c>[StructLayout(LayoutKind.Auto)]</c>. The three values are a fixed, closed metadata enum
/// (Sequential=0, Explicit=2, Auto=3), so rewriting the cast expression to the named member is exact; any other value is
/// left as the honest cast rather than guessed. Registered alongside <see cref="Il2CppAsmCommentTransform"/> via the
/// <c>WholeProjectDecompiler.CreateDecompiler</c> hook.
/// </summary>
internal sealed class Il2CppLayoutKindTransform : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        foreach (CastExpression cast in rootNode.DescendantsAndSelf.OfType<CastExpression>().ToList())
        {
            if (!TypeIsLayoutKind(cast.Type) || cast.Expression is not PrimitiveExpression primitive)
                continue;
            string name = ToMemberName(primitive.Value);
            if (name == null)
                continue;
            cast.ReplaceWith(new MemberReferenceExpression(new TypeReferenceExpression(new SimpleType("LayoutKind")), name));
        }
    }

    /// <summary>Whether an AST type is <c>LayoutKind</c> (simple or namespace-qualified).</summary>
    private static bool TypeIsLayoutKind(AstType type) => type switch
    {
        SimpleType simple => simple.Identifier == "LayoutKind",
        MemberType member => member.MemberName == "LayoutKind",
        _ => false,
    };

    /// <summary>The named member for a LayoutKind value, or null for an unrecognized value (kept as an honest cast).</summary>
    private static string ToMemberName(object value)
    {
        if (value is not int intValue)
            return null;
        return intValue switch
        {
            0 => "Sequential",
            2 => "Explicit",
            3 => "Auto",
            _ => null,
        };
    }
}
