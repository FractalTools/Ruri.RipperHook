using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Cpp2IL.Core.Model.Contexts;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;
using Cpp2IlApi = Cpp2IL.Core.Cpp2IlApi;

namespace Ruri.RipperHook.AR;

internal sealed class Il2CppAsmCommentTransform : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        ApplicationAnalysisContext app = Cpp2IlApi.CurrentAppContext;
        MetadataReader metadata = context.TypeSystem.MainModule.MetadataFile.Metadata;
        Il2CppMethodIdentity.ModuleIdentity module = Il2CppMethodIdentity.ForModule(app, metadata);
        if (module == null) return;
        Guid moduleIdentity = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        Dictionary<TypeAnalysisContext, int> typeTokens = module.Types.Where(pair => pair.Value != null)
            .ToDictionary(pair => pair.Value, pair => pair.Key);
        EntityDeclaration[] declarations = rootNode.DescendantsAndSelf.OfType<EntityDeclaration>().ToArray();
        Dictionary<TypeAnalysisContext, EntityDeclaration> types = new();
        HashSet<MethodAnalysisContext> presented = new();
        HashSet<ulong> presentedAliases = new();
        foreach (EntityDeclaration declaration in declarations)
        {
            if (declaration.GetResolveResult() is TypeResolveResult resolved
                && resolved.Type.GetDefinition() is { } type
                && BelongsToModule(type.ParentModule, moduleIdentity)
                && type.MetadataToken.Kind == HandleKind.TypeDefinition
                && module.Types.TryGetValue(MetadataTokens.GetToken(type.MetadataToken), out TypeAnalysisContext source)
                && source != null)
            {
                types[source] = declaration;
            }
            if (declaration.GetSymbol() is IMethod method && BelongsToModule(method.ParentModule, moduleIdentity)
                && method.MetadataToken.Kind == HandleKind.MethodDefinition
                && module.Methods.TryGetValue(MetadataTokens.GetToken(method.MetadataToken), out MethodAnalysisContext native)
                && native != null && presented.Add(native))
            {
                AddListing(declaration, app, native, moduleIdentity, MetadataTokens.GetToken(method.MetadataToken), presentedAliases);
            }
        }

        HashSet<TypeAnalysisContext> completed = new();
        foreach ((TypeAnalysisContext type, EntityDeclaration declaration) in types)
            CompleteType(type, declaration);

        if (types.Count == 0 && context.CurrentTypeDefinition == null && context.CurrentMember == null)
        {
            foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
            {
                if (metadata.GetString(metadata.GetTypeDefinition(handle).Name) != "<Module>") continue;
                foreach (MethodDefinitionHandle method in metadata.GetTypeDefinition(handle).GetMethods())
                {
                    int token = MetadataTokens.GetToken(method);
                    if (module.Methods.TryGetValue(token, out MethodAnalysisContext native) && native != null)
                        AddListing(rootNode, app, native, moduleIdentity, token, presentedAliases);
                }
            }
        }

        void CompleteType(TypeAnalysisContext type, EntityDeclaration parent)
        {
            if (!completed.Add(type)) return;
            EntityDeclaration target = types.GetValueOrDefault(type) ?? parent;
            Dictionary<MethodAnalysisContext, int> tokens = new();
            if (typeTokens.TryGetValue(type, out int typeToken))
            {
                TypeDefinitionHandle handle = (TypeDefinitionHandle)MetadataTokens.EntityHandle(typeToken);
                foreach (MethodDefinitionHandle method in metadata.GetTypeDefinition(handle).GetMethods())
                {
                    int token = MetadataTokens.GetToken(method);
                    if (module.Methods.TryGetValue(token, out MethodAnalysisContext native) && native != null)
                        tokens[native] = token;
                }
            }
            foreach (MethodAnalysisContext method in type.Methods)
            {
                if (presented.Add(method)) AddListing(target, app, method, moduleIdentity, tokens.GetValueOrDefault(method), presentedAliases);
            }
            foreach (TypeAnalysisContext nested in type.NestedTypes) CompleteType(nested, target);
        }
    }

    private static bool BelongsToModule(IModule module, Guid moduleIdentity)
    {
        MetadataReader metadata = module?.MetadataFile?.Metadata;
        return metadata != null && metadata.GetGuid(metadata.GetModuleDefinition().Mvid) == moduleIdentity;
    }

    private static void AddListing(AstNode target, ApplicationAnalysisContext app, MethodAnalysisContext method,
        Guid moduleIdentity, int emittedToken, ISet<ulong> presentedAliases)
    {
        string token = emittedToken == 0 ? "unmapped" : "0x" + emittedToken.ToString("X8");
        string header = $"IL2CPP method: {Il2CppAsmAnnotator.MethodSymbol(method)} [emitted-mvid={moduleIdentity:D}, "
            + $"emitted-token={token}, metadata-token=0x{method.Token:X8}]";
        string listing = header + "\n" + Il2CppAsmLookup.GetDisassembly(app, method, presentedAliases);
        foreach (string line in listing.Split('\n'))
        {
            string text = line.TrimEnd('\r', '\t', ' ');
            if (text.Length != 0) target.AddLeadingTrivia(new Comment(" " + text, CommentType.SingleLine));
        }
    }
}
