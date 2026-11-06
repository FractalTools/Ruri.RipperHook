using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AssetRipper.Import.Logging;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;

namespace Ruri.RipperHook.AR;

internal static class Il2CppAsmLookup
{
    internal sealed record Implementation(MethodAnalysisContext Source, MethodAnalysisContext Method,
        Cpp2IlMethodRef Reference, int Ordinal, string Failure)
    {
        public ulong Address => Reference?.GenericVariantPtr ?? Method?.UnderlyingPointer ?? 0;
        public object Identity => (object)Reference ?? Method ?? Source;
        public string Symbol => Method == null
            ? $"unresolved generic instantiation {Ordinal} of {Il2CppAsmAnnotator.MethodSymbol(Source)}"
            : Il2CppAsmAnnotator.MethodSymbol(Method);
    }

    internal sealed record Listing(Implementation Implementation, string Text, string Status, long ByteCount);

    private static readonly object _gate = new();

    public static IEnumerable<Implementation> Implementations(ApplicationAnalysisContext app, MethodAnalysisContext method)
    {
        if (method is ConcreteGenericMethodAnalysisContext concrete)
        {
            yield return new(concrete.BaseMethodContext, concrete, concrete.MethodRef, 0, null);
            yield break;
        }
        if (method.Definition != null
            && app.Binary.ConcreteGenericMethods.TryGetValue(method.Definition, out List<Cpp2IlMethodRef> instances)
            && instances.Count > 0)
        {
            for (int index = 0; index < instances.Count; index++)
            {
                Cpp2IlMethodRef reference = instances[index];
                ConcreteGenericMethodAnalysisContext instance = null;
                string failure = null;
                try
                {
                    instance = app.ResolveContextForMethod(reference);
                }
                catch (Exception exception)
                {
                    failure = Il2CppX86Listing.Describe(exception);
                }
                yield return new(method, instance, reference, index + 1, failure);
            }
            yield break;
        }
        yield return new(method, method, null, 0, null);
    }

    public static IEnumerable<Implementation> AtAddress(ApplicationAnalysisContext app, ulong address)
    {
        if (!app.MethodsByAddress.TryGetValue(address, out List<MethodAnalysisContext> methods)) yield break;
        HashSet<object> seen = new();
        foreach (MethodAnalysisContext method in methods)
        {
            foreach (Implementation implementation in Implementations(app, method))
            {
                if (implementation.Address == address && seen.Add(implementation.Identity)) yield return implementation;
            }
        }
    }

    public static Listing Display(ApplicationAnalysisContext app, Implementation implementation, ISet<ulong> presentedAliases = null)
    {
        lock (_gate)
        {
            StringBuilder text = new();
            text.Append("Method: ").Append(implementation.Symbol)
                .Append("  metadata-token=0x").Append(implementation.Source.Token.ToString("X8")).Append('\n');
            MethodAnalysisContext projection = implementation.Method ?? implementation.Source;
            if (projection.Name != projection.DefaultName)
                text.Append("Managed projection name: ").Append(projection.Name).Append('\n');
            if (implementation.Reference != null)
                text.Append("Generic instantiation: ").Append(implementation.Symbol).Append('\n');
            if (implementation.Failure != null)
            {
                text.Append("Native implementation unresolved at VA=0x").Append(implementation.Address.ToString("X"))
                    .Append(": ").Append(implementation.Failure).Append('\n');
                return new(implementation, text.ToString(), "unresolved", 0);
            }
            if (implementation.Address == 0)
            {
                text.Append("No native implementation recorded for ").Append(implementation.Symbol).Append(".\n");
                return new(implementation, text.ToString(), "no-native", 0);
            }

            MethodAnalysisContext method = implementation.Method;
            try
            {
                text.Append("Native prototype: ").Append(Prototype(method, implementation.Source)).Append('\n');
                if (implementation.Reference == null && (method.GenericParameters.Count > 0
                    || method.DeclaringType?.GenericParameters.Count > 0))
                    text.Append("Generic definition; native specialization is not recorded.\n");
                text.Append("VA=0x").Append(implementation.Address.ToString("X"))
                    .Append("  RVA=0x").Append(method.Rva.ToString("X")).Append('\n');
                if (presentedAliases?.Contains(implementation.Address) == true)
                {
                    text.Append("Shared native address; metadata aliases at VA=0x").Append(implementation.Address.ToString("X"))
                        .Append(" (listed once in this file).\n");
                }
                else
                {
                    Implementation[] aliases = AtAddress(app, implementation.Address).ToArray();
                    if (aliases.Length > 1)
                    {
                        text.Append("Shared native address; metadata aliases at VA=0x").Append(implementation.Address.ToString("X"))
                            .Append(":\n");
                        foreach (Implementation alias in aliases)
                            text.Append("  [").Append(alias.Source.DeclaringType?.DeclaringAssembly.CleanAssemblyName)
                                .Append("] ").Append(alias.Symbol).Append("  metadata-token=0x")
                                .Append(alias.Source.Token.ToString("X8")).Append('\n');
                        presentedAliases?.Add(implementation.Address);
                    }
                }
                if (app.InstructionSet is X86InstructionSet)
                {
                    Il2CppNativeBody body = Il2CppX86Listing.Decode(app, method, withChunks: true);
                    text.Append("Native status: ").Append(body.Status).Append("  bytes=0x")
                        .Append(body.ByteCount.ToString("X")).Append('\n');
                    text.Append(Il2CppX86Listing.Render(app, method, body, withAddresses: true));
                    return new(implementation, text.ToString(), body.Status, body.ByteCount);
                }
                text.Append(Il2CppAsmAnnotator.Annotate(app, app.InstructionSet.PrintAssembly(method)));
                text.Append("\nNative byte count is unverified for this instruction set.\n");
                return new(implementation, text.ToString(), "unverified", 0);
            }
            catch (Exception exception)
            {
                string failure = Il2CppX86Listing.Describe(exception);
                Logger.Warning(LogCategory.Export, $"[Il2CppMethodDump] no listing for {implementation.Symbol} "
                    + $"at 0x{implementation.Address:X}: {failure}");
                text.Append("Native listing failed: ").Append(failure).Append('\n');
                return new(implementation, text.ToString(), "error", 0);
            }
        }
    }

    public static string GetDisassembly(ApplicationAnalysisContext app, MethodAnalysisContext method, ISet<ulong> presentedAliases = null)
        => string.Join("\n", Implementations(app, method).Select(implementation => Display(app, implementation, presentedAliases).Text));

    private static string Prototype(MethodAnalysisContext method, MethodAnalysisContext source)
    {
        string modifiers = (method.IsStatic ? "static " : string.Empty)
            + (method.IsAbstract ? "abstract " : method.IsVirtual ? $"virtual[{source.Definition?.slot}] " : string.Empty);
        List<string> parameters = [];
        if (!method.IsStatic) parameters.Add(method.DeclaringType.FullName + " this");
        parameters.AddRange(method.Parameters.Select(parameter => parameter.ParameterType.FullName + " " + parameter.ParameterName));
        parameters.Add("MethodInfo method");
        return $"{modifiers}{method.ReturnType.FullName} {method.DeclaringType.FullName}::{method.DefaultName}({string.Join(", ", parameters)})";
    }
}
