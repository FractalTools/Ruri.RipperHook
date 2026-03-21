using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ruri.RipperHook.AR;

/// <summary>
/// Module initializer that patches AssetRipper.Primitives.dll at startup to re-add
/// IsGreaterEqual/IsLess methods that USCSandbox requires but were removed in v3.2.0.
/// Runs before any type from this assembly is used, ensuring the patched DLL is loaded.
/// </summary>
internal static class PrimitivesPatcher
{
    [ModuleInitializer]
    internal static void PatchPrimitivesIfNeeded()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dllPath = Path.Combine(baseDir, "AssetRipper.Primitives.dll");
            if (!File.Exists(dllPath)) return;

            using var asm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters
            {
                ReadWrite = true,
                InMemory = true
            });

            var module = asm.MainModule;
            var unityVersionType = module.Types.FirstOrDefault(t => t.FullName == "AssetRipper.Primitives.UnityVersion");
            if (unityVersionType == null) return;

            // Check if already patched
            if (unityVersionType.Methods.Any(m => m.Name == "IsGreaterEqual")) return;

            var boolRef = module.TypeSystem.Boolean;
            var ushortRef = module.TypeSystem.UInt16;
            var voidRef = module.TypeSystem.Void;

            // Find the UnityVersion(ushort, ushort) constructor
            var ctor2 = unityVersionType.Methods.FirstOrDefault(m =>
                m.IsConstructor && m.Parameters.Count == 2 &&
                m.Parameters[0].ParameterType.FullName == "System.UInt16" &&
                m.Parameters[1].ParameterType.FullName == "System.UInt16");

            // Find the UnityVersion(ushort) constructor
            var ctor1 = unityVersionType.Methods.FirstOrDefault(m =>
                m.IsConstructor && m.Parameters.Count == 1 &&
                m.Parameters[0].ParameterType.FullName == "System.UInt16");

            // Find operator >= and <
            var opGte = unityVersionType.Methods.FirstOrDefault(m =>
                m.Name == "op_GreaterThanOrEqual" && m.Parameters.Count == 2);
            var opLt = unityVersionType.Methods.FirstOrDefault(m =>
                m.Name == "op_LessThan" && m.Parameters.Count == 2);

            if (opGte == null || opLt == null)
            {
                Console.Error.WriteLine("[PrimitivesPatcher] UnityVersion comparison operators not found");
                return;
            }

            // Add IsGreaterEqual(ushort, ushort)
            if (ctor2 != null)
            {
                AddComparisonMethod(unityVersionType, "IsGreaterEqual", boolRef, ushortRef,
                    new[] { ("major", ushortRef), ("minor", ushortRef) },
                    ctor2, opGte, module);

                AddComparisonMethod(unityVersionType, "IsLess", boolRef, ushortRef,
                    new[] { ("major", ushortRef), ("minor", ushortRef) },
                    ctor2, opLt, module);
            }

            // Add IsGreaterEqual(ushort) and IsLess(ushort) for single-param calls
            if (ctor1 != null)
            {
                AddComparisonMethod(unityVersionType, "IsGreaterEqual", boolRef, ushortRef,
                    new[] { ("major", ushortRef) },
                    ctor1, opGte, module);

                AddComparisonMethod(unityVersionType, "IsLess", boolRef, ushortRef,
                    new[] { ("major", ushortRef) },
                    ctor1, opLt, module);
            }

            // Save back to disk
            asm.Write(dllPath);
            Console.WriteLine("[PrimitivesPatcher] Patched AssetRipper.Primitives.dll with IsGreaterEqual/IsLess methods");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PrimitivesPatcher] Failed: {ex.Message}");
        }
    }

    private static void AddComparisonMethod(
        TypeDefinition type, string name,
        TypeReference returnType, TypeReference paramBaseType,
        (string name, TypeReference type)[] parameters,
        MethodReference ctor, MethodReference comparisonOp,
        ModuleDefinition module)
    {
        // Check for existing method with same parameter count to avoid duplicates
        if (type.Methods.Any(m => m.Name == name && m.Parameters.Count == parameters.Length))
            return;

        var method = new MethodDefinition(name,
            MethodAttributes.Public | MethodAttributes.HideBySig,
            returnType);

        foreach (var (pName, pType) in parameters)
            method.Parameters.Add(new ParameterDefinition(pName, ParameterAttributes.None, pType));

        var il = method.Body.GetILProcessor();

        // Load 'this' (UnityVersion is a struct, so ldarg.0 is a pointer)
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldobj, type));

        // Load constructor params and create new UnityVersion
        for (int i = 0; i < parameters.Length; i++)
            il.Append(il.Create(OpCodes.Ldarg, i + 1));
        il.Append(il.Create(OpCodes.Newobj, ctor));

        // Call comparison operator
        il.Append(il.Create(OpCodes.Call, comparisonOp));
        il.Append(il.Create(OpCodes.Ret));

        type.Methods.Add(method);
    }
}
