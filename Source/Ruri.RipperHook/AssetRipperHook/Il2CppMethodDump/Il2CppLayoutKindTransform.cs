using System.Linq;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;

namespace Ruri.RipperHook.AR;

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

    private static bool TypeIsLayoutKind(AstType type) => type switch
    {
        SimpleType simple => simple.Identifier == "LayoutKind",
        MemberType member => member.MemberName == "LayoutKind",
        _ => false,
    };

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
