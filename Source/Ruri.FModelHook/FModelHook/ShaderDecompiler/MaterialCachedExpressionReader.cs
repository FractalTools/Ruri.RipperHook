using System;
using System.Collections.Generic;
using System.Reflection;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using Ruri.Hook.Core;

namespace Ruri.FModelHook.ShaderDecompiler;

internal static class MaterialCachedExpressionReader
{
    public static CachedParameterNames? Read(UMaterialInterface material)
    {
        var result = new CachedParameterNames();

        try
        {
            FStructFallback? cached = material.CachedExpressionData;
            if (cached != null)
            {
                ReadCachedExpressionData(cached, result);
            }

            ReadInstanceOverrides(material, result);

            ReadMaterialExpressions(material, result);

            if (cached != null)
            {
                RecursiveSweep(cached, result, depth: 0, propertyTrail: string.Empty);
            }

            SweepUObjectProperties(material, result);
        }
        catch (Exception ex)
        {
            HookLogger.LogWarning($"[MaterialCachedExpressionReader] {material?.GetPathName() ?? "<null>"}: {ex.GetType().Name}: {ex.Message}");
        }

        DedupeAll(result);
        return Empty(result) ? null : result;
    }

    public static CachedParameterNames? ReadGeneric(UObject asset)
    {
        if (asset == null) return null;
        var result = new CachedParameterNames();

        try
        {
            SweepUObjectProperties(asset, result);
        }
        catch (Exception ex)
        {
            HookLogger.LogWarning($"[MaterialCachedExpressionReader.Generic] {asset.GetPathName()}: {ex.GetType().Name}: {ex.Message}");
        }

        DedupeAll(result);
        return Empty(result) ? null : result;
    }

    private static void DedupeAll(CachedParameterNames p)
    {
        Dedupe(p.ScalarNames);
        Dedupe(p.VectorNames);
        Dedupe(p.StaticSwitchNames);
        Dedupe(p.TextureNames);
        Dedupe(p.RuntimeVirtualTextureNames);
        Dedupe(p.SparseVolumeTextureNames);
        Dedupe(p.FontNames);
        Dedupe(p.UnknownKindNames);
    }

    private static void SweepUObjectProperties(UObject asset, CachedParameterNames result)
    {
        if (asset?.Properties == null) return;
        foreach (FPropertyTag tag in asset.Properties)
        {
            string propName = tag.Name.Text;
            object? raw = tag.Tag?.GenericValue;

            if (raw is FStructFallback nested)
            {
                RecursiveSweep(nested, result, depth: 0, propertyTrail: propName);
            }
            else if (raw is System.Array arr)
            {
                int idx = 0;
                foreach (object? element in arr)
                {
                    if (element is FStructFallback childArr)
                    {
                        RecursiveSweep(childArr, result, depth: 0, propertyTrail: propName);
                    }
                    else if (element is FName fname && !fname.IsNone)
                    {
                        BucketByTrail(propName, fname.Text, result);
                    }
                    idx++;
                }
            }
            else if (raw is FName topFname && !topFname.IsNone && IsNameish(propName))
            {
                BucketByTrail(propName, topFname.Text, result);
            }
        }
    }

    private static void ReadCachedExpressionData(FStructFallback cached, CachedParameterNames dest)
    {
        AppendParameterInfos(cached, "ScalarParameterValues", dest.ScalarNames);
        AppendParameterInfos(cached, "VectorParameterValues", dest.VectorNames);
        AppendParameterInfos(cached, "DoubleVectorParameterValues", dest.VectorNames);
        AppendParameterInfos(cached, "StaticSwitchParameterValues", dest.StaticSwitchNames);
        AppendParameterInfos(cached, "TextureParameterValues", dest.TextureNames);
        AppendParameterInfos(cached, "RuntimeVirtualTextureParameterValues", dest.RuntimeVirtualTextureNames);
        AppendParameterInfos(cached, "SparseVolumeTextureParameterValues", dest.SparseVolumeTextureNames);
        AppendParameterInfos(cached, "FontParameterValues", dest.FontNames);

        if (cached.TryGetValue(out FStructFallback parameters, "Parameters") && parameters != null)
        {
            AppendParameterInfos(parameters, "ScalarValues", dest.ScalarNames);
            AppendParameterInfos(parameters, "VectorValues", dest.VectorNames);

            if (parameters.TryGetAllValues(out FStructFallback[] runtimeEntries, "RuntimeEntries") && runtimeEntries != null)
            {
                ReadParameterEntryArray(runtimeEntries, dest);
            }
            if (parameters.TryGetAllValues(out FStructFallback[] editorEntries, "EditorOnlyEntries") && editorEntries != null)
            {
                ReadParameterEntryArray(editorEntries, dest);
            }
        }

        if (cached.TryGetAllValues(out FStructFallback[] overrides, "ParameterOverrides") && overrides != null)
        {
            foreach (FStructFallback o in overrides)
            {
                ClassifyByOwnProperty(o, dest);
            }
        }
    }

    private static void ReadParameterEntryArray(FStructFallback[] entries, CachedParameterNames dest)
    {
        foreach (FStructFallback entry in entries)
        {
            if (entry == null) continue;
            ClassifyByOwnProperty(entry, dest);
        }
    }

    private static void ClassifyByOwnProperty(FStructFallback entry, CachedParameterNames dest)
    {
        List<string> names = ExtractParameterNames(entry);
        if (names.Count == 0) return;

        bool hasScalars = HasAnyPropertyNamed(entry, "ScalarValues", "ScalarValue", "ScalarOverrides");
        bool hasVectors = HasAnyPropertyNamed(entry, "VectorValues", "VectorValue", "VectorOverrides", "DoubleVectorValues");
        bool hasSwitches = HasAnyPropertyNamed(entry, "StaticSwitchValues", "StaticSwitchValue", "SwitchOverrides", "Values");        bool hasTextures = HasAnyPropertyNamed(entry, "TextureValues", "TextureValue", "Textures", "Texture", "TextureOverrides");
        bool hasRvt = HasAnyPropertyNamed(entry, "RuntimeVirtualTextureValues", "RuntimeVirtualTextures", "RuntimeVirtualTexture");
        bool hasSvt = HasAnyPropertyNamed(entry, "SparseVolumeTextureValues", "SparseVolumeTextures", "SparseVolumeTexture");
        bool hasFonts = HasAnyPropertyNamed(entry, "FontValues", "FontPageValues", "Fonts", "Font");

        if (hasScalars) Append(names, dest.ScalarNames);
        else if (hasVectors) Append(names, dest.VectorNames);
        else if (hasSwitches) Append(names, dest.StaticSwitchNames);
        else if (hasTextures) Append(names, dest.TextureNames);
        else if (hasRvt) Append(names, dest.RuntimeVirtualTextureNames);
        else if (hasSvt) Append(names, dest.SparseVolumeTextureNames);
        else if (hasFonts) Append(names, dest.FontNames);
        else Append(names, dest.UnknownKindNames);
    }

    private static void AppendParameterInfos(FStructFallback owner, string propertyName, List<string> dest)
    {
        if (owner == null) return;
        if (!owner.TryGetAllValues(out FStructFallback[] entries, propertyName) || entries == null) return;
        foreach (FStructFallback entry in entries)
        {
            if (entry == null) continue;
            foreach (string name in ExtractParameterNames(entry))
            {
                dest.Add(name);
            }
        }
    }

    private static List<string> ExtractParameterNames(FStructFallback entry)
    {
        var names = new List<string>();
        if (entry == null) return names;

        TryAddFromInfo(entry, names);

        TryAddInfoSet(entry, "ParameterInfoSet", names);
        TryAddInfoSet(entry, "ParameterInfos", names);
        TryAddInfoSet(entry, "ParameterInfo", names);
        TryAddInfoSet(entry, "Parameters", names);

        return names;
    }

    private static void TryAddFromInfo(FStructFallback entry, List<string> dest)
    {
        if (entry.TryGetValue(out FStructFallback wrapper, "ParameterInfo") && wrapper != null)
        {
            string? n = ReadFNameLike(wrapper, "Name", "ParameterName");
            if (!string.IsNullOrWhiteSpace(n) && !IsNoneName(n!)) dest.Add(n!);
        }
        string? direct = ReadFNameLike(entry, "ParameterName", "Name");
        if (!string.IsNullOrWhiteSpace(direct) && !IsNoneName(direct!)) dest.Add(direct!);
    }

    private static void TryAddInfoSet(FStructFallback entry, string property, List<string> dest)
    {
        if (!entry.TryGetAllValues(out FStructFallback[] arr, property) || arr == null) return;
        foreach (FStructFallback inner in arr)
        {
            if (inner == null) continue;
            string? n = ReadFNameLike(inner, "Name", "ParameterName");
            if (!string.IsNullOrWhiteSpace(n) && !IsNoneName(n!)) dest.Add(n!);
        }
    }

    private static string? ReadFNameLike(FStructFallback owner, params string[] candidates)
    {
        foreach (string c in candidates)
        {
            if (owner.TryGetValue(out FName n, c) && !n.IsNone) return n.Text;
            if (owner.TryGetValue(out string s, c) && !string.IsNullOrEmpty(s)) return s;
        }
        return null;
    }

    private static bool HasAnyPropertyNamed(FStructFallback owner, params string[] names)
    {
        if (owner?.Properties == null) return false;
        foreach (FPropertyTag tag in owner.Properties)
        {
            string n = tag.Name.Text;
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(n, names[i], StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    private static void ReadInstanceOverrides(UMaterialInterface material, CachedParameterNames dest)
    {
        AppendInstanceOverride(material, "ScalarParameterValues", dest.ScalarNames);
        AppendInstanceOverride(material, "VectorParameterValues", dest.VectorNames);
        AppendInstanceOverride(material, "DoubleVectorParameterValues", dest.VectorNames);
        AppendInstanceOverride(material, "TextureParameterValues", dest.TextureNames);
        AppendInstanceOverride(material, "RuntimeVirtualTextureParameterValues", dest.RuntimeVirtualTextureNames);
        AppendInstanceOverride(material, "SparseVolumeTextureParameterValues", dest.SparseVolumeTextureNames);
        AppendInstanceOverride(material, "FontParameterValues", dest.FontNames);
        AppendInstanceOverride(material, "StaticSwitchParameters", dest.StaticSwitchNames);
        AppendInstanceOverride(material, "StaticSwitchParameterValues", dest.StaticSwitchNames);
    }

    private static void AppendInstanceOverride(UMaterialInterface material, string propertyName, List<string> dest)
    {
        if (!material.TryGetValue(out FStructFallback[] arr, propertyName) || arr == null) return;
        foreach (FStructFallback entry in arr)
        {
            if (entry == null) continue;
            foreach (string n in ExtractParameterNames(entry))
            {
                dest.Add(n);
            }
        }
    }

    private static void ReadMaterialExpressions(UMaterialInterface material, CachedParameterNames dest)
    {
        if (material is not UMaterial umat) return;
        foreach (FPackageIndex idx in umat.Expressions)
        {
            if (!idx.TryLoad(out UMaterialExpression expression)) continue;
            string typeName = expression.GetType().Name;
            string? nm = TryReadExpressionName(expression);
            if (string.IsNullOrWhiteSpace(nm)) continue;

            if (typeName.Contains("ScalarParameter", StringComparison.Ordinal)) dest.ScalarNames.Add(nm!);
            else if (typeName.Contains("VectorParameter", StringComparison.Ordinal)) dest.VectorNames.Add(nm!);
            else if (typeName.Contains("StaticBoolParameter", StringComparison.Ordinal)
                  || typeName.Contains("StaticSwitchParameter", StringComparison.Ordinal)) dest.StaticSwitchNames.Add(nm!);
            else if (typeName.Contains("TextureSampleParameter", StringComparison.Ordinal)
                  || typeName.Contains("TextureObjectParameter", StringComparison.Ordinal)) dest.TextureNames.Add(nm!);
            else if (typeName.Contains("RuntimeVirtualTexture", StringComparison.Ordinal)) dest.RuntimeVirtualTextureNames.Add(nm!);
            else if (typeName.Contains("SparseVolumeTexture", StringComparison.Ordinal)) dest.SparseVolumeTextureNames.Add(nm!);
            else if (typeName.Contains("FontSampleParameter", StringComparison.Ordinal)) dest.FontNames.Add(nm!);
            else dest.UnknownKindNames.Add(nm!);
        }
    }

    private static string? TryReadExpressionName(UMaterialExpression expression)
    {
        Type t = expression.GetType();
        FieldInfo? f = t.GetField("ParameterName", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        if (f?.GetValue(expression) is FName fname && !fname.IsNone) return fname.Text;

        if (expression.TryGetValue(out FName n, "ParameterName") && !n.IsNone) return n.Text;
        if (expression.TryGetValue(out string s, "ParameterName") && !string.IsNullOrEmpty(s)) return s;
        return null;
    }

    private static void RecursiveSweep(FStructFallback root, CachedParameterNames dest, int depth, string propertyTrail)
    {
        if (root?.Properties == null) return;
        if (depth > 6) return;
        foreach (FPropertyTag tag in root.Properties)
        {
            string propName = tag.Name.Text;
            string trail = string.IsNullOrEmpty(propertyTrail) ? propName : propertyTrail + "." + propName;

            object? raw = tag.Tag?.GenericValue;
            if (raw is FName fname && !fname.IsNone && IsNameish(propName))
            {
                BucketByTrail(trail, fname.Text, dest);
                continue;
            }

            if (raw is FStructFallback child)
            {
                RecursiveSweep(child, dest, depth + 1, trail);
            }
            else if (raw is System.Array arr)
            {
                foreach (object? element in arr)
                {
                    if (element is FStructFallback childArr) RecursiveSweep(childArr, dest, depth + 1, trail);
                }
            }
        }
    }

    private static bool IsNameish(string propertyName)
        => string.Equals(propertyName, "Name", StringComparison.Ordinal)
        || string.Equals(propertyName, "ParameterName", StringComparison.Ordinal);

    private static void BucketByTrail(string trail, string name, CachedParameterNames dest)
    {
        if (string.IsNullOrWhiteSpace(name) || IsNoneName(name)) return;

        string lower = trail.ToLowerInvariant();
        if (lower.Contains("scalar")) dest.ScalarNames.Add(name);
        else if (lower.Contains("doublevector") || lower.Contains("vector")) dest.VectorNames.Add(name);
        else if (lower.Contains("staticswitch") || lower.Contains("staticbool")) dest.StaticSwitchNames.Add(name);
        else if (lower.Contains("runtimevirtualtexture")) dest.RuntimeVirtualTextureNames.Add(name);
        else if (lower.Contains("sparsevolumetexture")) dest.SparseVolumeTextureNames.Add(name);
        else if (lower.Contains("fontparameter") || lower.Contains("fontpage")) dest.FontNames.Add(name);
        else if (lower.Contains("texture")) dest.TextureNames.Add(name);
        else dest.UnknownKindNames.Add(name);
    }

    private static bool IsNoneName(string name) => string.Equals(name, "None", StringComparison.OrdinalIgnoreCase);

    private static bool Empty(CachedParameterNames p)
        => p.ScalarNames.Count == 0
        && p.VectorNames.Count == 0
        && p.StaticSwitchNames.Count == 0
        && p.TextureNames.Count == 0
        && p.RuntimeVirtualTextureNames.Count == 0
        && p.SparseVolumeTextureNames.Count == 0
        && p.FontNames.Count == 0
        && p.UnknownKindNames.Count == 0;

    private static void Dedupe(List<string> list)
    {
        if (list.Count <= 1) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!seen.Add(list[i])) list.RemoveAt(i);
        }
        list.Reverse();
    }

    private static void Append(List<string> source, List<string> dest)
    {
        for (int i = 0; i < source.Count; i++) dest.Add(source[i]);
    }
}
