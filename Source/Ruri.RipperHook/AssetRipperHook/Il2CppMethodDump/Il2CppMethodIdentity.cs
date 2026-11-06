using System;
using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Builder;
using AssetRipper.Import.Logging;
using Cpp2IL.Core.Model.Contexts;
using ManagedMethodDefinition = AsmResolver.DotNet.MethodDefinition;
using ManagedTypeDefinition = AsmResolver.DotNet.TypeDefinition;
using MetadataReader = System.Reflection.Metadata.MetadataReader;

namespace Ruri.RipperHook.AR;

internal static class Il2CppMethodIdentity
{
    internal sealed record ModuleIdentity(AssemblyAnalysisContext Assembly,
        IReadOnlyDictionary<int, MethodAnalysisContext> Methods, IReadOnlyDictionary<int, TypeAnalysisContext> Types);

    private static readonly object _gate = new();
    private static readonly Dictionary<Guid, ModuleIdentity> _modules = new();
    private static ApplicationAnalysisContext _application;

    public static void Capture(ApplicationAnalysisContext application, ModuleDefinition module, ITokenMapping mapping)
    {
        lock (_gate)
        {
            SelectApplication(application);
            if (application == null) return;

            AssemblyAnalysisContext owner = null;
            foreach (AssemblyAnalysisContext assembly in application.Assemblies)
            {
                if (assembly.GetExtraData<AssemblyDefinition>("AsmResolverAssembly")?.Modules.Contains(module) == true)
                {
                    owner = assembly;
                    break;
                }
            }
            if (owner == null) return;

            Dictionary<int, MethodAnalysisContext> methods = new();
            Dictionary<int, TypeAnalysisContext> types = new();
            foreach (TypeAnalysisContext type in owner.Types)
            {
                ManagedTypeDefinition managedType = type.GetExtraData<ManagedTypeDefinition>("AsmResolverType");
                if (managedType != null && managedType.DeclaringModule == module)
                {
                    if (mapping.TryGetNewToken(managedType, out var typeToken) && typeToken.Rid != 0)
                        CaptureToken(types, typeToken.ToInt32(), type, module.Mvid);
                    else
                        Logger.Warning(LogCategory.Export, $"[Il2CppMethodDump] no emitted type token for {type.FullName}.");
                }
                foreach (MethodAnalysisContext method in type.Methods)
                {
                    ManagedMethodDefinition managedMethod = method.GetExtraData<ManagedMethodDefinition>("AsmResolverMethod");
                    if (managedMethod == null || managedMethod.DeclaringModule != module) continue;
                    if (!mapping.TryGetNewToken(managedMethod, out var token) || token.Rid == 0)
                    {
                        Logger.Warning(LogCategory.Export, $"[Il2CppMethodDump] no emitted method token for {method.FullName}.");
                        continue;
                    }

                    CaptureToken(methods, token.ToInt32(), method, module.Mvid);
                }
            }

            Guid moduleIdentity = module.Mvid;
            if (_modules.TryGetValue(moduleIdentity, out ModuleIdentity existing))
            {
                if (existing != null && (!ReferenceEquals(existing.Assembly, owner)
                    || !SameMappings(existing.Methods, methods) || !SameMappings(existing.Types, types)))
                {
                    _modules[moduleIdentity] = null;
                    Logger.Warning(LogCategory.Export, $"[Il2CppMethodDump] conflicting emitted method identities for "
                        + $"MVID={moduleIdentity:D}; listings for this module are omitted.");
                }
                return;
            }
            _modules.Add(moduleIdentity, new ModuleIdentity(owner, methods, types));
        }
    }

    public static bool OwnsModule(ApplicationAnalysisContext application, MetadataReader metadata)
        => ForModule(application, metadata) != null;

    public static ModuleIdentity ForModule(ApplicationAnalysisContext application, MetadataReader metadata)
    {
        lock (_gate)
        {
            SelectApplication(application);
            if (application == null) return null;
            Guid moduleIdentity = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            return _modules.GetValueOrDefault(moduleIdentity);
        }
    }

    private static void SelectApplication(ApplicationAnalysisContext application)
    {
        if (ReferenceEquals(_application, application)) return;
        _modules.Clear();
        _application = application;
    }

    private static void CaptureToken<T>(Dictionary<int, T> mappings, int token, T context, Guid moduleIdentity) where T : class
    {
        if (mappings.TryGetValue(token, out T previous) && !ReferenceEquals(previous, context))
        {
            mappings[token] = null;
            if (previous != null)
                Logger.Warning(LogCategory.Export, $"[Il2CppMethodDump] ambiguous emitted identity: MVID={moduleIdentity:D}, token=0x{token:X8}.");
        }
        else mappings[token] = context;
    }

    private static bool SameMappings<T>(IReadOnlyDictionary<int, T> first, IReadOnlyDictionary<int, T> second) where T : class
    {
        if (first.Count != second.Count) return false;
        foreach (KeyValuePair<int, T> pair in first)
        {
            if (!second.TryGetValue(pair.Key, out T method) || !ReferenceEquals(pair.Value, method))
            {
                return false;
            }
        }
        return true;
    }
}
