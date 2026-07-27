using System.Diagnostics;

namespace Ruri.AssemblyDumper.Pipeline;

/// <summary>
/// Mirror of AssetRipper.AssemblyDumper.Program.RunGeneration() — runs every AR pass in upstream
/// order via reflection (most are <c>internal static</c>). 998 (Save) and 999 (Documentation) are
/// the only ones the orchestrator runs separately so it can splice work in between.
/// </summary>
internal static class PassRunner
{
    public static void RunAllExceptSave(string tpkPath)
    {
        Call("Pass 000: Initialization",
            "AssetRipper.AssemblyDumper.Passes.Pass000_ProcessTpk", "IntitializeSharedState", tpkPath);
        Pass("Pass 001: Merge Moved Groups", "Pass001_MergeMovedGroups");
        Pass("Pass 002: Rename Subnodes", "Pass002_RenameSubnodes");
        Pass("Pass 003: Fix TextureImporter Nodes", "Pass003_FixTextureImporterNodes");
        Pass("Pass 004: Fill Name to Type Id Dictionary", "Pass004_FillNameToTypeIdDictionary");
        Pass("Pass 005: Split Abstract Classes", "Pass005_SplitAbstractClasses");
        Pass("Pass 007: Extract Subclasses", "Pass007_ExtractSubclasses");
        Pass("Pass 008: Divide Ambiguous PPtr", "Pass008_DivideAmbiguousPPtr");
        Pass("Pass 009: Create Groups", "Pass009_CreateGroups");
        Pass("Pass 010: Initialize Interfaces", "Pass010_InitializeInterfacesAndFactories");
        Pass("Pass 011: Apply Inheritance", "Pass011_ApplyInheritance");
        Pass("Pass 012: Apply Correct Type Attributes", "Pass012_ApplyCorrectTypeAttributes");
        Pass("Pass 013: Unify Fields of Abstract Types", "Pass013_UnifyFieldsOfAbstractTypes");
        Pass("Pass 015: Add Fields", "Pass015_AddFields");
        Pass("Pass 039: Inject Enum Values", "Pass039_InjectEnumValues");
        Pass("Pass 040: Add Enum Types", "Pass040_AddEnums");
        Pass("Pass 041: Add Native Enum Types", "Pass041_NativeEnums");
        Pass("Pass 045: Marker Interfaces", "Pass045_AddMarkerInterfaces");
        Pass("Pass 052: Interface Properties and Methods", "Pass052_InterfacePropertiesAndMethods");
        Pass("Pass 053: Has Methods and Nullable Attributes", "Pass053_HasMethodsAndNullableAttributes");
        Pass("Pass 054: Assign Property Histories", "Pass054_AssignPropertyHistories");
        Pass("Pass 055: Create Enum Properties", "Pass055_CreateEnumProperties");
        Pass("Pass 058: Inject Chinese Texture Properties", "Pass058_InjectChineseTextureProperties");
        Pass("Pass 061: Add Constructors", "Pass061_AddConstructors");
        Pass("Pass 062: Fill Constructors", "Pass062_FillConstructors");
        Pass("Pass 063: Create Empty Methods", "Pass063_CreateEmptyMethods");
        Pass("Pass 080: PPtr Conversions", "Pass080_PPtrConversions");
        Pass("Pass 081: PPtr Properties", "Pass081_CreatePPtrProperties");
        Pass("Pass 100: Filling Read Methods", "Pass100_FillReadMethods");
        Pass("Pass 101: Filling Write Methods", "Pass101_FillWriteMethods");
        Pass("Pass 102: Ignore Field In Meta Files Methods", "Pass102_IgnoreFieldInMetaFilesMethods");
        Pass("Pass 103: Filling Dependency Methods", "Pass103_FillDependencyMethods");
        Pass("Pass 104: Reset Methods", "Pass104_ResetMethods");
        Pass("Pass 105: CopyValues Methods", "Pass105_CopyValuesMethods");
        Pass("Pass 108: Walk Methods", "Pass108_WalkMethods");
        Pass("Pass 110: Class Name and ID Overrides", "Pass110_ClassNameAndIdOverrides");
        Pass("Pass 201: GUID Explicit Conversion", "Pass201_GuidConversionOperators");
        Pass("Pass 202: Vector Explicit Conversions", "Pass202_VectorExplicitConversions");
        Pass("Pass 203: OffsetPtr Implicit Conversions", "Pass203_OffsetPtrImplicitConversions");
        Pass("Pass 204: Hash128 Explicit Conversion", "Pass204_Hash128ExplicitConversion");
        Pass("Pass 205: Color Explicit Conversions", "Pass205_ColorExplicitConversions");
        Pass("Pass 206: BoneWeights4 Explicit Conversions", "Pass206_BoneWeights4ExplicitConversions");
        Pass("Pass 300: Named Interface", "Pass300_NamedInterface");
        Pass("Pass 301: SourcePrefab Property", "Pass301_SourcePrefabProperty");
        Pass("Pass 400: Equality Comparison", "Pass400_EqualityComparison");
        Pass("Pass 410: SetValues Methods", "Pass410_SetValuesMethods");
        Pass("Pass 500: Fixing PPtr Yaml", "Pass500_PPtrFixes");
        Pass("Pass 501: Fixing MonoBehaviour", "Pass501_MonoBehaviourImplementation");
        Pass("Pass 502: Fixing Guid and Hash Yaml Export", "Pass502_FixGuidAndHashYaml");
        Pass("Pass 504: Fixing Shader Name", "Pass504_FixShaderName");
        Pass("Pass 505: Fixing Old AudioClips", "Pass505_FixOldAudioClip");
        Pass("Pass 506: Fixing UnityConnectSettings", "Pass506_FixUnityConnectSettings");
        Pass("Pass 507: Inject Properties", "Pass507_InjectedProperties");
        Pass("Pass 508: Lazy SceneObjectIdentifier", "Pass508_LazySceneObjectIdentifier");
        Pass("Pass 510: Fix Component Pair Walking", "Pass510_FixComponentPairWalking");
        Pass("Pass 555: Create Common String", "Pass555_CreateCommonString");
        Pass("Pass 556: Create ClassIDType Enum", "Pass556_CreateClassIDTypeEnum");
        Pass("Pass 557: Create SourceTpk Class", "Pass557_CreateSourceTpkClass");
        Pass("Pass 558: Create Type to ClassIDType Dictionary", "Pass558_TypeCache");
        Pass("Pass 920: Interface Inheritance", "Pass920_InterfaceInheritance");
        Pass("Pass 940: Make Asset Factory", "Pass940_MakeAssetFactory");
        Pass("Pass 941: Make Field Hashes", "Pass941_MakeFieldHashes");
    }

    public static void RunSave() => Pass("Pass 998: Write Assembly", "Pass998_SaveAssembly");
    public static void RunDocumentation() => Pass("Pass 999: Generate Documentation", "Pass999_Documentation");

    private static void Pass(string label, string passClassShortName) =>
        Call(label, $"AssetRipper.AssemblyDumper.Passes.{passClassShortName}", "DoPass");

    private static void Call(string label, string typeFullName, string methodName, params object[] args)
    {
        Console.WriteLine(label);
        var sw = Stopwatch.StartNew();
        try
        {
            ArReflect.InvokeStaticVoid(typeFullName, methodName, args);
        }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
        sw.Stop();
        Console.WriteLine($"\tFinished in {sw.ElapsedMilliseconds} ms");
    }
}
