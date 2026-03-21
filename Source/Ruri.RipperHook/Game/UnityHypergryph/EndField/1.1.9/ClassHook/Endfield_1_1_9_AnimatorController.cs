using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.Metadata;
using AssetRipper.Checksum;
using AssetRipper.IO.Endian;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_91;
using AssetRipper.SourceGenerated.Subclasses.PPtr_AnimationClip;
using Ruri.SourceGenerated.Subclasses.PPtr_HGAnimationCurveMask;
using System.Reflection;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_1_1_9_Hook
{/*
    [RetargetMethod(typeof(AnimatorController_2018_3), "ReadRelease")]
    public void AnimatorController_2021_ReadRelease(ref EndianSpanReader reader)
    {
        var _this = (object)this as AnimatorController_2018_3;

        // 原版读取结构
        // m_Name = reader.ReadRelease_Utf8StringAlign();
        // m_ControllerSize = reader.ReadUInt32();
        // ((UnityAssetBase)m_Controller).ReadRelease(ref reader);
        // m_TOS.ReadRelease_Map_UInt32_Utf8StringAlign(ref reader);
        // m_AnimationClips.ReadRelease_ArrayAlign_Asset<PPtr_AnimationClip_5>(ref reader);
        // ((UnityAssetBase)m_StateMachineBehaviourVectorDescription).ReadRelease(ref reader);
        // m_StateMachineBehaviours.ReadRelease_ArrayAlign_Asset<PPtr_MonoBehaviour_5>(ref reader);
        // m_MultiThreadedStateMachine = reader.ReadRelease_BooleanAlign();

        // 自定义版读取
        _this.Name = ReadReleaseMethods.ReadRelease_Utf8StringAlign(ref reader);
        _this.ControllerSize = reader.ReadUInt32();
        _this.Controller.ReadRelease(ref reader);
        ReadReleaseMethods.ReadRelease_ArrayAlign_Utf8StringAlign(_this.TOS, ref reader); // 需要修复ConvertTOSData
        AssetList<PPtr_HGAnimationCurveMask> m_AnimationCurveMask = new();
        ReadReleaseMethods.ReadRelease_ArrayAlign_Asset(m_AnimationCurveMask, ref reader);
        ReadReleaseMethods.ReadRelease_ArrayAlign_Asset<AssetList<PPtr_AnimationClip_5>>(_this.AnimationClips, ref reader);
        _this.StateMachineBehaviourVectorDescription.ReadRelease(ref reader);
        ReadReleaseMethods.ReadRelease_ArrayAlign_Asset(_this.StateMachineBehaviours, ref reader);
        _this.MultiThreadedStateMachine = ReadReleaseMethods.ReadRelease_BooleanAlign(ref reader);
        var m_ClothCalculatorType = reader.ReadInt32();
        var m_EnableOptClipBindings = ReadReleaseMethods.ReadRelease_BooleanAlign(ref reader);
        var m_ReserveCount = ReadReleaseMethods.ReadRelease_ByteAlign(ref reader);

        ConvertTOSData(_this, _realthis);
    }*/

    /// <summary>
    /// Endfield 1.1.9 changed AnimatorController's TOS structure:
    ///   Standard Unity: map m_TOS (AssetDictionary&lt;uint, Utf8String&gt;)
    ///   Endfield 1.1.9: vector m_TOSData (AssetList&lt;Utf8String&gt;)
    /// Name-based ClassDeepCopy can't match these, so we use a custom callback
    /// to manually convert m_TOSData[i] → m_TOS.Add(CRC32(name), name).
    /// </summary>
    private void RegisterAnimatorControllerHook()
    {
        Assembly? ruriAssembly;
        try
        {
            ruriAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Ruri.SourceGenerated")
                ?? Assembly.Load("Ruri.SourceGenerated");
        }
        catch
        {
            Console.WriteLine("    [!] Ruri.SourceGenerated not found — AnimatorController hook skipped");
            return;
        }

        int id = (int)ClassIDType.AnimatorController;
        var ruriType = ruriAssembly.GetType($"Ruri.SourceGenerated.Classes.ClassID_{id}.AnimatorController");
        if (ruriType == null)
        {
            Console.WriteLine("    [!] AnimatorController not found in Ruri.SourceGenerated");
            return;
        }

        var ruriCreate = ruriType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(AssetInfo), typeof(UnityVersion) }, null);
        if (ruriCreate == null)
        {
            Console.WriteLine("    [!] AnimatorController.Create not found in Ruri.SourceGenerated");
            return;
        }

        var targetVer = endFieldClassVersion;

        var customCallbacks = new Dictionary<ClassIDType, ReadReleaseDelegate>
        {
            [ClassIDType.AnimatorController] = (object asset, ref EndianSpanReader reader) =>
            {
                var realThis = (IUnityObjectBase)asset;
                var dummyThis = (IUnityObjectBase)ruriCreate.Invoke(null, new object[] { realThis.AssetInfo, targetVer })!;

                dummyThis.ReadRelease(ref reader);
                ReflectionExtensions.ClassDeepCopy(dummyThis, realThis);
                ConvertTOSData(dummyThis, realThis);
            }
        };

        HookClasses(
            new[] { ClassIDType.AnimatorController },
            "2021.3.34f1",
            targetVer,
            "Ruri.SourceGenerated",
            customCallbacks);
    }

    /// <summary>
    /// Converts m_TOSData (AssetList&lt;Utf8String&gt;) from the Ruri dummy object
    /// into m_TOS (AssetDictionary&lt;uint, Utf8String&gt;) on the real object.
    /// Keys are CRC32 hashes of the string values.
    /// Duplicates are skipped because AssetDictionary.TryGetSinglePairForKey
    /// returns false when multiple entries share the same key.
    /// </summary>
    private static void ConvertTOSData(IUnityObjectBase ruriObj, IUnityObjectBase realObj)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var tosField = FindFieldInHierarchy(realObj.GetType(), "TOS", flags, exclude: "TOSData");
        if (tosField == null)
        {
            Console.WriteLine("    [!] ConvertTOSData: m_TOS field not found on target type");
            return;
        }

        var tos = tosField.GetValue(realObj);
        if (tos == null) return;

        var tosCountProp = tos.GetType().GetProperty("Count");
        int tosCount = tosCountProp != null ? (int)tosCountProp.GetValue(tos)! : 0;
        if (tosCount > 0) return;

        var tosDataField = FindFieldInHierarchy(ruriObj.GetType(), "TOSData", flags);
        if (tosDataField == null)
        {
            Console.WriteLine("    [!] ConvertTOSData: m_TOSData field not found on source type");
            return;
        }

        var tosDataObj = tosDataField.GetValue(ruriObj);
        if (tosDataObj == null) return;

        var countProp = tosDataObj.GetType().GetProperty("Count");
        if (countProp == null) return;
        int count = (int)countProp.GetValue(tosDataObj)!;
        if (count == 0) return;

        var indexer = tosDataObj.GetType().GetProperty("Item");
        if (indexer == null) return;

        var addMethod = tos.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 2);
        if (addMethod == null) return;

        var addedKeys = new HashSet<uint>();
        for (int i = 0; i < count; i++)
        {
            var value = indexer.GetValue(tosDataObj, new object[] { i })!;
            uint key = Crc32Algorithm.HashUTF8(value.ToString()!);
            if (addedKeys.Add(key))
            {
                addMethod.Invoke(tos, new object[] { key, value });
            }
        }
        Console.WriteLine($"    [*] ConvertTOSData: {addedKeys.Count} unique entries (from {count} total)");
    }

    private static FieldInfo? FindFieldInHierarchy(Type type, string nameContains, BindingFlags flags, string? exclude = null)
    {
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(flags | BindingFlags.DeclaredOnly))
            {
                if (f.Name.Contains(nameContains) && (exclude == null || !f.Name.Contains(exclude)))
                    return f;
            }
        }
        return null;
    }
}
