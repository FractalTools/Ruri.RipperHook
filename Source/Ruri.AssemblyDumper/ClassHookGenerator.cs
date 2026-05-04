using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using System.Reflection;

// 使用别名解决与 System.Reflection 的冲突
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using ParameterAttributes = AsmResolver.PE.DotNet.Metadata.Tables.ParameterAttributes;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;
using SR = System.Reflection;

namespace AssetRipper.DocExtraction.ConsoleApp;

public static class ClassHookGenerator
{
    private static readonly SR.Assembly DumperAssembly = typeof(AssetRipper.AssemblyDumper.AssemblyBuilder).Assembly;
    private static readonly Type SharedStateType = DumperAssembly.GetType("AssetRipper.AssemblyDumper.SharedState")!;
    private static readonly SR.PropertyInfo SharedStateInstanceProp = SharedStateType.GetProperty("Instance", SR.BindingFlags.Public | SR.BindingFlags.Static)!;
    private static readonly SR.PropertyInfo ModuleProp = SharedStateType.GetProperty("Module", SR.BindingFlags.Public | SR.BindingFlags.Instance)!;
    private static readonly Type Pass000 = DumperAssembly.GetType("AssetRipper.AssemblyDumper.Passes.Pass000_ProcessTpk")!;
    private static readonly Type Pass100 = DumperAssembly.GetType("AssetRipper.AssemblyDumper.Passes.Pass100_FillReadMethods")!;

    public static void Generate()
    {
        Console.WriteLine("Initializing SharedState...");
        var initMethod = Pass000.GetMethod("IntitializeSharedState", SR.BindingFlags.Public | SR.BindingFlags.Static);
        if (initMethod == null) throw new Exception("Could not find Pass000.IntitializeSharedState");

        string tpkPath = "type_tree.tpk";
        if (!File.Exists(tpkPath))
        {
            tpkPath = Path.Combine("..", "type_tree.tpk");
            if (!File.Exists(tpkPath))
            {
                tpkPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "type_tree.tpk");
                if (!File.Exists(tpkPath))
                    throw new FileNotFoundException("type_tree.tpk not found.");
            }
        }

        initMethod.Invoke(null, new object[] { tpkPath });

        // 运行必要的 Pass
        RunPass("Pass001_MergeMovedGroups");
        RunPass("Pass002_RenameSubnodes");
        RunPass("Pass003_FixTextureImporterNodes");
        RunPass("Pass004_FillNameToTypeIdDictionary");
        RunPass("Pass005_SplitAbstractClasses");
        RunPass("Pass007_ExtractSubclasses");
        RunPass("Pass008_DivideAmbiguousPPtr");
        RunPass("Pass009_CreateGroups");
        RunPass("Pass010_InitializeInterfacesAndFactories");
        RunPass("Pass011_ApplyInheritance");
        RunPass("Pass012_ApplyCorrectTypeAttributes");
        RunPass("Pass013_UnifyFieldsOfAbstractTypes");
        RunPass("Pass015_AddFields");
        RunPass("Pass039_InjectEnumValues");
        RunPass("Pass040_AddEnums");
        RunPass("Pass041_NativeEnums");
        RunPass("Pass045_AddMarkerInterfaces");
        RunPass("Pass052_InterfacePropertiesAndMethods");
        RunPass("Pass053_HasMethodsAndNullableAttributes");
        RunPass("Pass054_AssignPropertyHistories");
        RunPass("Pass055_CreateEnumProperties");
        RunPass("Pass058_InjectChineseTextureProperties");
        RunPass("Pass061_AddConstructors");
        RunPass("Pass062_FillConstructors");
        RunPass("Pass063_CreateEmptyMethods");
        RunPass("Pass080_PPtrConversions");
        RunPass("Pass081_CreatePPtrProperties");

        Console.WriteLine("Running Pass100_FillReadMethods to generate IL...");
        var doPass100 = Pass100.GetMethod("DoPass", SR.BindingFlags.Public | SR.BindingFlags.Static);
        doPass100?.Invoke(null, null);

        CreateHookAssembly();
    }

    private static void RunPass(string passName)
    {
        Console.WriteLine($"Running {passName}...");
        var type = DumperAssembly.GetType($"AssetRipper.AssemblyDumper.Passes.{passName}");
        if (type == null) throw new Exception($"Pass {passName} not found.");
        var method = type.GetMethod("DoPass", SR.BindingFlags.Public | SR.BindingFlags.Static);
        method?.Invoke(null, null);
    }

    private static void CreateHookAssembly()
    {
        Console.WriteLine("Creating Hook Assembly...");

        var sharedState = SharedStateInstanceProp.GetValue(null);
        var sourceModule = (ModuleDefinition)ModuleProp.GetValue(sharedState)!;

        // 0. 预加载 SourceGenerated 程序集以获取真实版本
        string sourceAssemblyName = "AssetRipper.SourceGenerated";
        SR.Assembly? originalAssembly = null;
        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        originalAssembly = loadedAssemblies.FirstOrDefault(a => a.GetName().Name == sourceAssemblyName);

        if (originalAssembly == null)
        {
            string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, sourceAssemblyName + ".dll");
            if (File.Exists(dllPath))
            {
                try
                {
                    originalAssembly = SR.Assembly.LoadFrom(dllPath);
                    Console.WriteLine($"Loaded {sourceAssemblyName} from {dllPath}");
                }
                catch { /* Ignore */ }
            }
        }

        // [Fix 1] 强制更新内存中 sourceModule 的版本号
        if (originalAssembly != null)
        {
            var realVersion = originalAssembly.GetName().Version;
            Console.WriteLine($"Forcing SourceModule version to {realVersion} (was {sourceModule.Assembly.Version})");
            sourceModule.Assembly.Version = realVersion;
        }

        // [Fix 2] 同步依赖项版本
        SynchronizeAssemblyReferences(sourceModule);

        // 1. 获取 CorLib
        var corLibDesc = sourceModule.CorLibTypeFactory.CorLibScope.GetAssembly();
        AssemblyReference? corLibRef = null;
        if (corLibDesc != null) corLibRef = new AssemblyReference(corLibDesc);

        if (corLibRef == null)
        {
            var runtimeAsm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "System.Runtime");
            if (runtimeAsm != null)
            {
                var name = runtimeAsm.GetName();
                corLibRef = new AssemblyReference(name.Name, name.Version);
            }
            else
            {
                corLibRef = new AssemblyReference("System.Runtime", new Version(6, 0, 0, 0));
                Console.WriteLine("Warning: Could not detect CorLib from source module, defaulting to System.Runtime 6.0");
            }
        }

        var hookModule = new ModuleDefinition("Ruri.SourceGenerated.dll", corLibRef);
        var hookAssembly = new AssemblyDefinition("Ruri.SourceGenerated", new Version(1, 0, 0, 0));
        hookAssembly.Modules.Add(hookModule);

        // 2. 复制源模块的引用
        foreach (var assemblyRef in sourceModule.AssemblyReferences)
        {
            if (assemblyRef.Name == corLibRef.Name) continue;
            if (!hookModule.AssemblyReferences.Any(r => r.Name == assemblyRef.Name))
                hookModule.AssemblyReferences.Add(new AssemblyReference(assemblyRef));
        }

        // 3. 显式添加 SourceGenerated 引用
        AssemblyReference sourceAssemblyRef;
        if (originalAssembly != null)
        {
            var n = originalAssembly.GetName();
            sourceAssemblyRef = new AssemblyReference(n.Name, n.Version);
        }
        else
        {
            Console.WriteLine($"Warning: {sourceAssemblyName} not loaded. Custom types verification will default to stub generation.");
            sourceAssemblyRef = new AssemblyReference(sourceAssemblyName, new Version(0, 0, 0, 0));
        }

        if (!hookModule.AssemblyReferences.Any(r => r.Name == sourceAssemblyName))
            hookModule.AssemblyReferences.Add(sourceAssemblyRef);

        string assetsAssemblyName = "AssetRipper.Assets";
        if (!hookModule.AssemblyReferences.Any(r => r.Name == assetsAssemblyName))
        {
            var assetsAsm = loadedAssemblies.FirstOrDefault(a => a.GetName().Name == assetsAssemblyName);
            var assetsVer = assetsAsm?.GetName().Version ?? new Version(0, 0, 0, 0);
            hookModule.AssemblyReferences.Add(new AssemblyReference(assetsAssemblyName, assetsVer));
        }

        var typesToHook = sourceModule.TopLevelTypes
            .Where(t => t.Methods.Any(m => m.Name == "ReadRelease" && (m.Attributes & MethodAttributes.Virtual) != 0))
            .ToList();

        Console.WriteLine($"Found {typesToHook.Count} types with ReadRelease to generate.");

        // 初始化 Cloner
        var ilCloner = new ILCloner(hookModule, sourceModule, sourceAssemblyRef, originalAssembly);

        int count = 0;
        foreach (var sourceType in typesToHook)
        {
            var sourceMethod = sourceType.Methods.First(m => m.Name == "ReadRelease");

            // 1. 获取或生成目标类 (Stub)
            var targetType = ilCloner.GetOrGenerateStub(sourceType);

            // 2. 在目标类中创建 ReadRelease 方法
            // 复制签名：public override void ReadRelease(ref EndianSpanReader reader)
            var hookSignature = ilCloner.ImportMethodSignature(sourceMethod.Signature!);

            var hookMethod = new MethodDefinition(
                sourceMethod.Name,
                sourceMethod.Attributes,
                hookSignature);

            hookMethod.ImplAttributes = sourceMethod.ImplAttributes;

            // 复制参数
            foreach (var param in sourceMethod.ParameterDefinitions)
            {
                hookMethod.ParameterDefinitions.Add(new ParameterDefinition(
                    param.Sequence,
                    param.Name,
                    param.Attributes
                ));
            }

            // 3. 复制方法体 (直接使用 Pass100 生成的逻辑)
            if (sourceMethod.CilMethodBody != null)
            {
                var body = new CilMethodBody();
                hookMethod.CilMethodBody = body;

                body.InitializeLocals = sourceMethod.CilMethodBody.InitializeLocals;
                body.MaxStack = sourceMethod.CilMethodBody.MaxStack;

                ilCloner.CloneBody(sourceMethod.CilMethodBody, body);
            }

            // 4. 将方法添加到类中
            targetType.Methods.Add(hookMethod);
            count++;
        }

        // [Fix 3] 暴力去重清理
        DeduplicateReferences(hookModule);

        hookAssembly.Write("Ruri.SourceGenerated.dll");
        Console.WriteLine($"Hook Assembly generated: Ruri.SourceGenerated.dll with {count} methods.");
    }

    private static void SynchronizeAssemblyReferences(ModuleDefinition sourceModule)
    {
        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies().ToDictionary(a => a.GetName().Name, a => a);
        int fixedCount = 0;

        foreach (var asmRef in sourceModule.AssemblyReferences)
        {
            if (string.IsNullOrEmpty(asmRef.Name)) continue;

            if (loadedAssemblies.TryGetValue(asmRef.Name, out var loadedAsm))
            {
                var loadedVersion = loadedAsm.GetName().Version;
                if (asmRef.Version != loadedVersion)
                {
                    asmRef.Version = loadedVersion;
                    fixedCount++;
                }
            }
        }
        if (fixedCount > 0) Console.WriteLine($"Synchronized {fixedCount} assembly references.");
    }

    private static void DeduplicateReferences(ModuleDefinition module)
    {
        var groupedRefs = module.AssemblyReferences
            .GroupBy(r => r.Name)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in groupedRefs)
        {
            var keeper = group.OrderByDescending(r => r.Version).First();
            var toRemove = group.Where(r => r != keeper).ToList();

            Console.WriteLine($"Deduplicating {group.Key}: Keeping {keeper.Version}, Removing {string.Join(", ", toRemove.Select(r => r.Version))}");

            foreach (var typeRef in module.GetImportedTypeReferences())
            {
                if (typeRef.Scope is AssemblyReference asmRef && toRemove.Contains(asmRef))
                {
                    typeRef.Scope = keeper;
                }
            }

            foreach (var badRef in toRemove)
            {
                module.AssemblyReferences.Remove(badRef);
            }
        }
    }
}

internal class ILCloner
{
    private readonly ModuleDefinition _targetModule;
    private readonly ModuleDefinition _sourceModule;
    private readonly AssemblyReference _sourceAssemblyRef;
    private readonly ReferenceImporter _importer;
    private readonly SR.Assembly? _originalAssembly;
    private readonly HashSet<string> _existingTypeNames;
    private readonly Dictionary<string, TypeDefinition> _generatedStubs = new Dictionary<string, TypeDefinition>();

    public ILCloner(ModuleDefinition targetModule, ModuleDefinition sourceModule, AssemblyReference sourceAssemblyRef, SR.Assembly? originalAssembly)
    {
        _targetModule = targetModule;
        _sourceModule = sourceModule;
        _sourceAssemblyRef = sourceAssemblyRef;
        _importer = new ReferenceImporter(targetModule);
        _originalAssembly = originalAssembly;

        _existingTypeNames = new HashSet<string>();
        if (_originalAssembly != null)
        {
            try
            {
                foreach (var t in _originalAssembly.GetExportedTypes())
                {
                    _existingTypeNames.Add(t.FullName?.Replace('+', '.') ?? "");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error caching types from original assembly: {ex.Message}");
            }
        }
    }

    private string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return name.Replace('?', '_').Replace(" ", "_");
    }

    public TypeSignature ImportTypeSignature(TypeSignature signature)
    {
        switch (signature)
        {
            case TypeDefOrRefSignature typeDefOrRef:
                return new TypeDefOrRefSignature(ImportTypeDefOrRef(typeDefOrRef.Type), typeDefOrRef.IsValueType);
            case CorLibTypeSignature:
                return _importer.ImportTypeSignature(signature);
            case SzArrayTypeSignature szArray:
                return new SzArrayTypeSignature(ImportTypeSignature(szArray.BaseType));
            case ByReferenceTypeSignature byRef:
                return new ByReferenceTypeSignature(ImportTypeSignature(byRef.BaseType));
            case PointerTypeSignature pointer:
                return new PointerTypeSignature(ImportTypeSignature(pointer.BaseType));
            case GenericInstanceTypeSignature generic:
                var newGeneric = new GenericInstanceTypeSignature(ImportTypeDefOrRef(generic.GenericType), generic.IsValueType);
                foreach (var arg in generic.TypeArguments)
                    newGeneric.TypeArguments.Add(ImportTypeSignature(arg));
                return newGeneric;
            default:
                return _importer.ImportTypeSignature(signature);
        }
    }

    public MethodSignature ImportMethodSignature(MethodSignature sig)
    {
        var paramsList = sig.ParameterTypes.Select(p => ImportTypeSignature(p));
        var ret = ImportTypeSignature(sig.ReturnType);
        var newSig = new MethodSignature(sig.Attributes, ret, paramsList);
        newSig.GenericParameterCount = sig.GenericParameterCount;
        return newSig;
    }

    public void CloneBody(CilMethodBody sourceBody, CilMethodBody targetBody)
    {
        foreach (var local in sourceBody.LocalVariables)
        {
            targetBody.LocalVariables.Add(new CilLocalVariable(ImportTypeSignature(local.VariableType)));
        }

        var instrMap = new Dictionary<CilInstruction, CilInstruction>();

        foreach (var sourceInstr in sourceBody.Instructions)
        {
            var targetInstr = new CilInstruction(sourceInstr.OpCode);
            targetBody.Instructions.Add(targetInstr);
            instrMap[sourceInstr] = targetInstr;
        }

        for (int i = 0; i < sourceBody.Instructions.Count; i++)
        {
            var s = sourceBody.Instructions[i];
            var t = targetBody.Instructions[i];
            if (s.Operand != null)
                t.Operand = ImportOperand(s.Operand, instrMap);
        }

        foreach (var eh in sourceBody.ExceptionHandlers)
        {
            var newEh = new CilExceptionHandler();
            newEh.HandlerType = eh.HandlerType;
            if (eh.ExceptionType != null)
                newEh.ExceptionType = ImportTypeDefOrRef(eh.ExceptionType);

            newEh.TryStart = MapLabel(eh.TryStart, instrMap);
            newEh.TryEnd = MapLabel(eh.TryEnd, instrMap);
            newEh.HandlerStart = MapLabel(eh.HandlerStart, instrMap);
            newEh.HandlerEnd = MapLabel(eh.HandlerEnd, instrMap);
            if (eh.FilterStart != null)
                newEh.FilterStart = MapLabel(eh.FilterStart, instrMap);

            targetBody.ExceptionHandlers.Add(newEh);
        }
    }

    private ICilLabel? MapLabel(ICilLabel? label, Dictionary<CilInstruction, CilInstruction> map)
    {
        if (label is CilInstructionLabel instrLabel)
        {
            if (map.TryGetValue(instrLabel.Instruction, out var targetInstr))
                return targetInstr.CreateLabel();
        }
        return null;
    }

    private object ImportOperand(object operand, Dictionary<CilInstruction, CilInstruction> map)
    {
        if (operand is ICilLabel label)
            return MapLabel(label, map)!;

        if (operand is IList<ICilLabel> labels)
        {
            var list = new List<ICilLabel>();
            foreach (var l in labels)
            {
                var mapped = MapLabel(l, map);
                if (mapped != null) list.Add(mapped);
            }
            return list;
        }

        if (operand is IMetadataMember member)
            return ImportMember(member);

        return operand;
    }

    private object ImportMember(IMetadataMember member)
    {
        if (member is TypeDefinition typeDef) return ImportTypeDefOrRef(typeDef);
        if (member is TypeReference typeRef) return ImportTypeDefOrRef(typeRef);
        if (member is TypeSpecification typeSpec) return new TypeSpecification(ImportTypeSignature(typeSpec.Signature!));

        if (member is FieldDefinition fieldDef)
        {
            var declType = ImportTypeDefOrRef(fieldDef.DeclaringType!);

            if (declType is TypeDefinition stubType && stubType.DeclaringModule == _targetModule)
            {
                var sanitizedName = Sanitize(fieldDef.Name);
                var stubField = stubType.Fields.FirstOrDefault(f => f.Name == sanitizedName);
                if (stubField != null)
                    return stubField;
            }

            // Fallback for when we don't have the field definition in our stub yet or it's external
            var fieldMemberRef = new MemberReference(declType, Sanitize(fieldDef.Name), ImportFieldSignature(fieldDef.Signature!));
            return _importer.ImportField(fieldMemberRef);
        }

        if (member is MethodDefinition methodDef)
        {
            var declType = ImportTypeDefOrRef(methodDef.DeclaringType!);

            if (declType is TypeDefinition stubType && stubType.DeclaringModule == _targetModule)
            {
                var stubMethod = stubType.Methods.FirstOrDefault(m => m.Name == methodDef.Name && m.Parameters.Count == methodDef.Parameters.Count);
                if (stubMethod != null)
                    return stubMethod;
            }

            var methodMemberRef = new MemberReference(declType, methodDef.Name, ImportMethodSignature(methodDef.Signature!));
            return _importer.ImportMethod(methodMemberRef);
        }

        if (member is MemberReference memberRef)
        {
            if (memberRef.Parent is ITypeDefOrRef parentType)
            {
                var importedParent = ImportTypeDefOrRef(parentType);

                if (memberRef.IsMethod)
                {
                    var sig = ImportMethodSignature((MethodSignature)memberRef.Signature!);
                    if (importedParent is TypeDefinition stubType && stubType.DeclaringModule == _targetModule)
                    {
                        var stubMethod = stubType.Methods.FirstOrDefault(m => m.Name == memberRef.Name && m.Parameters.Count == sig.ParameterTypes.Count);
                        if (stubMethod != null) return stubMethod;
                    }

                    var newRef = new MemberReference(importedParent, memberRef.Name, sig);
                    return _importer.ImportMethod(newRef);
                }
                if (memberRef.IsField)
                {
                    var sig = ImportFieldSignature((FieldSignature)memberRef.Signature!);
                    if (importedParent is TypeDefinition stubType && stubType.DeclaringModule == _targetModule)
                    {
                        var sanitizedName = Sanitize(memberRef.Name);
                        var stubField = stubType.Fields.FirstOrDefault(f => f.Name == sanitizedName);
                        if (stubField != null) return stubField;
                    }

                    var newRef = new MemberReference(importedParent, Sanitize(memberRef.Name), sig);
                    return _importer.ImportField(newRef);
                }
            }
            if (memberRef.IsMethod) return _importer.ImportMethod(memberRef);
            if (memberRef.IsField) return _importer.ImportField(memberRef);
        }

        if (member is MethodSpecification methodSpec)
        {
            var m = (IMethodDefOrRef)ImportMember(methodSpec.Method!);
            var sig = ImportGenericInstanceMethodSignature(methodSpec.Signature!);
            return new MethodSpecification(m, sig);
        }

        return member;
    }

    public ITypeDefOrRef ImportTypeDefOrRef(ITypeDefOrRef type)
    {
        bool isSourceType = false;
        string typeFullName = type.FullName;
        string typeNamespace = type.Namespace ?? "";
        string typeName = type.Name ?? "";

        if (type is TypeDefinition td && td.DeclaringModule == _sourceModule)
        {
            isSourceType = true;
        }
        else if (type is TypeReference tr && tr.Scope == _sourceModule)
        {
            isSourceType = true;
        }

        if (isSourceType)
        {
            string normalizedName = typeFullName.Replace('/', '.').Replace('+', '.');
            bool existsInDll = _existingTypeNames.Contains(normalizedName);

            if (existsInDll)
            {
                IResolutionScope scope;
                if (type.DeclaringType != null)
                {
                    scope = (IResolutionScope)ImportTypeDefOrRef(type.DeclaringType);
                }
                else
                {
                    scope = _sourceAssemblyRef;
                }
                return new TypeReference(scope, typeNamespace, typeName);
            }
            else
            {
                if (type is TypeDefinition sourceDef)
                {
                    return GetOrGenerateStub(sourceDef);
                }
                else if (type is TypeReference sourceRef)
                {
                    var resolved = sourceRef.Resolve();
                    if (resolved != null)
                    {
                        return GetOrGenerateStub(resolved);
                    }
                    else
                    {
                        Console.WriteLine($"Warning: Cannot resolve {typeFullName} to generate stub, using reference.");
                        return _importer.ImportType(type);
                    }
                }
            }
        }

        return _importer.ImportType(type);
    }

    public TypeDefinition GetOrGenerateStub(TypeDefinition sourceDef)
    {
        if (_generatedStubs.TryGetValue(sourceDef.FullName, out var stub))
            return stub;

        TypeDefinition? declaringStub = null;
        if (sourceDef.DeclaringType != null)
        {
            var parentDef = sourceDef.DeclaringType;
            var importedParent = ImportTypeDefOrRef(parentDef);
            if (importedParent is TypeDefinition parentStub && parentStub.DeclaringModule == _targetModule)
            {
                declaringStub = parentStub;
            }
        }

        if (declaringStub != null)
        {
            stub = new TypeDefinition(sourceDef.Namespace, sourceDef.Name, sourceDef.Attributes);
            declaringStub.NestedTypes.Add(stub);
        }
        else
        {
            string name = sourceDef.Name;
            if (sourceDef.DeclaringType != null)
            {
                name = sourceDef.DeclaringType.Name + "_" + sourceDef.Name;
            }
            stub = new TypeDefinition(sourceDef.Namespace, name, sourceDef.Attributes);
            _targetModule.TopLevelTypes.Add(stub);
        }

        _generatedStubs[sourceDef.FullName] = stub;

        if (sourceDef.BaseType != null)
            stub.BaseType = ImportTypeDefOrRef(sourceDef.BaseType);

        foreach (var sourceField in sourceDef.Fields)
        {
            var safeName = Sanitize(sourceField.Name);
            var newField = new FieldDefinition(safeName, sourceField.Attributes, ImportFieldSignature(sourceField.Signature!));
            stub.Fields.Add(newField);
        }

        // [Fix 2] Important: Ensure helper classes like ReadReleaseMethods have their methods copied, not just constructors.
        // ReadReleaseMethods (Pass100) contains static methods used by ReadRelease.
        bool copyAllMethods = sourceDef.Name == "ReadReleaseMethods" ||
                              sourceDef.Name == "ReadEditorMethods" ||
                              sourceDef.Name == "TypelessDataHelper"; // Fix: Add TypelessDataHelper to allow-list

        foreach (var sourceMethod in sourceDef.Methods)
        {
            if ((sourceMethod.IsConstructor && !sourceMethod.IsStatic) || copyAllMethods)
            {
                var newMethod = new MethodDefinition(sourceMethod.Name, sourceMethod.Attributes, ImportMethodSignature(sourceMethod.Signature!));
                newMethod.ImplAttributes = sourceMethod.ImplAttributes;

                stub.Methods.Add(newMethod);

                foreach (var genericParam in sourceMethod.GenericParameters)
                {
                    var newGenericParam = new GenericParameter(genericParam.Name, genericParam.Attributes);
                    newMethod.GenericParameters.Add(newGenericParam);
                }

                for (int i = 0; i < sourceMethod.GenericParameters.Count; i++)
                {
                    var srcParam = sourceMethod.GenericParameters[i];
                    var dstParam = newMethod.GenericParameters[i];
                    foreach (var constraint in srcParam.Constraints)
                    {
                        dstParam.Constraints.Add(new GenericParameterConstraint(ImportTypeDefOrRef(constraint.Constraint)));
                    }
                }

                foreach (var param in sourceMethod.ParameterDefinitions)
                {
                    newMethod.ParameterDefinitions.Add(new ParameterDefinition(
                        (ushort)param.Sequence,
                        param.Name,
                        param.Attributes
                    ));
                }

                if (sourceMethod.CilMethodBody != null)
                {
                    var body = new CilMethodBody();
                    newMethod.CilMethodBody = body;

                    body.InitializeLocals = sourceMethod.CilMethodBody.InitializeLocals;
                    body.MaxStack = sourceMethod.CilMethodBody.MaxStack;

                    CloneBody(sourceMethod.CilMethodBody, body);
                }
            }
        }

        return stub;
    }

    private FieldSignature ImportFieldSignature(FieldSignature sig)
    {
        return new FieldSignature(ImportTypeSignature(sig.FieldType));
    }

    private GenericInstanceMethodSignature ImportGenericInstanceMethodSignature(GenericInstanceMethodSignature sig)
    {
        var newSig = new GenericInstanceMethodSignature(sig.Attributes);
        foreach (var arg in sig.TypeArguments)
            newSig.TypeArguments.Add(ImportTypeSignature(arg));
        return newSig;
    }
}