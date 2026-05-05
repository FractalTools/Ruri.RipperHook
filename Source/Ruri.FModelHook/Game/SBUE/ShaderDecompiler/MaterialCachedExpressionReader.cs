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

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Defensive reader for material parameter names that survive **shipping
// IoStore cooks where `LoadedMaterialResources` is empty**.
//
// Every modern UE5 cook with `r.ShaderCodeLibrary.Enabled=1` (the default)
// externalises material shader-maps to `.ushaderbytecode` archives. The
// material UAsset keeps the *names* of its parameters (drives editor UI +
// runtime parameter overrides) but loses the inline shader-map blob that
// pairs names with byte offsets in the `Material` cbuffer. In CUE4Parse
// terms: `LoadedMaterialResources` is empty even with `ReadShaderMaps=true`.
//
// This class is deliberately:
//   1. **Schema-agnostic** — every read goes through `IPropertyHolder`
//      (`TryGetValue` / `TryGetAllValues`) which is name-keyed, NOT layout-
//      offset-keyed. Custom UE forks renaming `Parameters` to `Params` (or
//      adding a new sub-struct) doesn't break us — we just miss that bucket
//      and move to the next.
//   2. **Multi-source** — it tries (in order):
//        a. `material.CachedExpressionData` (the persistent-cooked
//           FMaterialCachedExpressionData).
//        b. The material's property bag at top level (`ScalarParameterValues`,
//           `VectorParameterValues`, ... — present on UMaterialInstance even
//           in shipping cook because they're override values, not editor
//           data).
//        c. `material.Expressions` for UMaterial (UMaterialExpressionScalar/
//           VectorParameter etc.) — typed walk for the rare case the asset
//           was cooked WITH editor data (e.g. uncooked dev builds).
//      Every step is best-effort, never throws on missing fields.
//   3. **Recursive-walk fallback** — when the named-key path comes up
//      empty for a given bucket, we recursively walk the entire property
//      bag and collect any `FName` / `FMaterialParameterInfo` value we
//      see, classifying it heuristically by the property name that owns
//      it. This catches custom-engine renames at the price of being
//      slightly noisier — we report unknown-bucket finds in
//      `CachedParameterNames.UnknownKindNames`.
internal static class MaterialCachedExpressionReader
{
    // Material-specific entry point. Layered on top of the generic
    // UObject reader (`ReadGeneric`) — adds the material-only sources
    // (CachedExpressionData property bag, instance-override array
    // properties, UMaterialExpression typed walk) and lets the generic
    // path handle everything else (top-level property bag walk +
    // recursive sweep). Calling code should use whichever entry point
    // matches what they have in hand:
    //   * UMaterialInterface in scope -> Read(material) for the full
    //     material-aware fallback chain.
    //   * Any other UObject (Niagara script/system/emitter, particle
    //     system, etc.) -> ReadGeneric(asset) so the same defensive
    //     property-bag walk drives parameter-name extraction without
    //     baking in the material-specific layouts.
    public static CachedParameterNames? Read(UMaterialInterface material)
    {
        var result = new CachedParameterNames();

        try
        {
            // Source A — CachedExpressionData (FStructFallback property bag).
            // This is THE survival path for shipping IoStore cooks of
            // Material/MaterialInstance assets.
            FStructFallback? cached = material.CachedExpressionData;
            if (cached != null)
            {
                ReadCachedExpressionData(cached, result);
            }

            // Source B — top-level property bag on the material itself.
            // UMaterialInstance keeps explicit override values here even
            // without CachedExpressionData (the editor wrote them as real
            // UProperties so they cook unconditionally).
            ReadInstanceOverrides(material, result);

            // Source C — UMaterial.Expressions (rare; only present when an
            // asset was cooked with editor data, e.g. -nostripeditor builds).
            ReadMaterialExpressions(material, result);

            // Source D — recursive sweep of the CachedExpressionData
            // property bag for anything the named-bucket path missed
            // (custom-engine renames, future UE versions). Best-effort
            // only; finds go into UnknownKindNames if they don't pattern-
            // match a typed bucket.
            if (cached != null)
            {
                RecursiveSweep(cached, result, depth: 0, propertyTrail: string.Empty);
            }

            // Source E — same generic UObject sweep used by the Niagara
            // / other-asset path. Adds nothing for a typical material
            // (CachedExpressionData is exhaustive there) but covers
            // instance-override exotic shapes and custom-engine forks
            // that store parameter names directly on the UObject.
            SweepUObjectProperties(material, result);
        }
        catch (Exception ex)
        {
            // The reader is purely additive; any failure here just means we
            // didn't enrich the material. Logged at info level so a future
            // schema break is visible without spamming.
            HookLogger.LogWarning($"[MaterialCachedExpressionReader] {material?.GetPathName() ?? "<null>"}: {ex.GetType().Name}: {ex.Message}");
        }

        DedupeAll(result);
        return Empty(result) ? null : result;
    }

    // Generic entry point — works on any UObject. Used for Niagara
    // packages (UNiagaraScript / UNiagaraSystem / UNiagaraEmitter) where
    // there's no `LoadedMaterialResources` / `CachedExpressionData`
    // analogue, but the cooked UAsset still carries parameter names in
    // the typed property bag (`ExposedParameters`, `EmitterSpawnAttributes`,
    // `SystemCompiledData.DataSetCompiledData[i].Variables[]`, etc.).
    //
    // Mirrors the material reader's defensive philosophy — every read
    // is name-keyed via IPropertyHolder, no engine-specific class
    // layout is mirrored, and the recursive sweep catches anything
    // the named-bucket pass missed.
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

    // Walk every UProperty on the asset. For each top-level property:
    //   * If the value is FName -> classify by property name (BucketByTrail
    //     handles the trail-based heuristic; works for both material and
    //     niagara naming conventions because the bucket selection is
    //     content-driven, not class-driven).
    //   * If the value is FStructFallback -> recurse via the same sweep.
    //   * If the value is an array of FStructFallback -> recurse into
    //     each element.
    //   * If the value is a string array of names directly (Niagara
    //     `Variables.Name` style) -> treat as parameter names.
    //
    // Property names that are well-known parameter buckets on Niagara
    // assets get explicit bias so they land in the right bucket
    // (e.g. `ExposedParameters` -> Vector/Scalar mix; `Variables` ->
    // unknown-kind; `EmitterSpawnAttributes` -> scalar). These probes
    // don't replace the recursive sweep — they augment it so the
    // typed buckets get populated even on Niagara, which means the
    // downstream patcher can pick up author-facing names instead of
    // anonymous Material_Tn placeholders.
    private static void SweepUObjectProperties(UObject asset, CachedParameterNames result)
    {
        if (asset?.Properties == null) return;
        foreach (FPropertyTag tag in asset.Properties)
        {
            string propName = tag.Name.Text;
            object? raw = tag.Tag?.GenericValue;

            // Direct named-bucket probes — recognise common Niagara
            // / Material parameter property names so they land in the
            // typed buckets rather than the unknown bucket.
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
                        // Bare FName arrays — `ExposedParameters` etc.
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

    // --- Source A: FMaterialCachedExpressionData property bag ----------
    //
    // The shipping-cook layout we observe most often (UE 5.0 - 5.4) is:
    //   FMaterialCachedExpressionData
    //     Parameters : FMaterialCachedParameters
    //       RuntimeEntries : FMaterialCachedParameterEntry[NumRuntimeTypes]
    //         ParameterInfoSet : FMaterialParameterInfo[]   <-- names live here
    //         ParameterInfos   : FMaterialParameterInfo[]   <-- alias on older versions
    //       ScalarValues / VectorValues / StaticSwitchValues / ...
    //     ScalarParameters / VectorParameters / ... (legacy 4.x layout)
    //
    // We probe each well-known sub-key but never assume a particular index
    // ordering — that's why each `RuntimeEntries[i]` gets its own
    // type-bucket guess based on its OWN property names rather than the
    // engine's enum order, which has been silently re-ordered between
    // 4.27 / 5.0 / 5.3 / 5.4.
    private static void ReadCachedExpressionData(FStructFallback cached, CachedParameterNames dest)
    {
        // Top-level direct buckets — present on some 4.x layouts where
        // FMaterialCachedExpressionData fields are flattened.
        AppendParameterInfos(cached, "ScalarParameterValues", dest.ScalarNames);
        AppendParameterInfos(cached, "VectorParameterValues", dest.VectorNames);
        AppendParameterInfos(cached, "DoubleVectorParameterValues", dest.VectorNames);
        AppendParameterInfos(cached, "StaticSwitchParameterValues", dest.StaticSwitchNames);
        AppendParameterInfos(cached, "TextureParameterValues", dest.TextureNames);
        AppendParameterInfos(cached, "RuntimeVirtualTextureParameterValues", dest.RuntimeVirtualTextureNames);
        AppendParameterInfos(cached, "SparseVolumeTextureParameterValues", dest.SparseVolumeTextureNames);
        AppendParameterInfos(cached, "FontParameterValues", dest.FontNames);

        // Modern shape (5.0+): { Parameters: { RuntimeEntries: [...] } }
        if (cached.TryGetValue(out FStructFallback parameters, "Parameters") && parameters != null)
        {
            // Some versions also flatten these:
            AppendParameterInfos(parameters, "ScalarValues", dest.ScalarNames);
            AppendParameterInfos(parameters, "VectorValues", dest.VectorNames);

            if (parameters.TryGetAllValues(out FStructFallback[] runtimeEntries, "RuntimeEntries") && runtimeEntries != null)
            {
                ReadParameterEntryArray(runtimeEntries, dest);
            }
            // EditorOnlyEntries cooked into the asset on -nostripeditor
            // builds carry the same shape; reuse the reader.
            if (parameters.TryGetAllValues(out FStructFallback[] editorEntries, "EditorOnlyEntries") && editorEntries != null)
            {
                ReadParameterEntryArray(editorEntries, dest);
            }
        }

        // Some FMaterialInstanceCachedData shapes (5.x) carry a flat
        // ParameterOverrides array.
        if (cached.TryGetAllValues(out FStructFallback[] overrides, "ParameterOverrides") && overrides != null)
        {
            foreach (FStructFallback o in overrides)
            {
                ClassifyByOwnProperty(o, dest);
            }
        }
    }

    // Walks an array of FMaterialCachedParameterEntry-shaped structs.
    // Each entry typically has a `ParameterInfoSet : TSet<FMaterialParameterInfo>`
    // (or `ParameterInfos` on 4.x) plus value arrays. We use
    // ClassifyByOwnProperty to guess which bucket the entry belongs to.
    private static void ReadParameterEntryArray(FStructFallback[] entries, CachedParameterNames dest)
    {
        foreach (FStructFallback entry in entries)
        {
            if (entry == null) continue;
            ClassifyByOwnProperty(entry, dest);
        }
    }

    // Pulls out a `ParameterInfoSet` / `ParameterInfos` array of
    // FMaterialParameterInfo and routes the names into a bucket guessed
    // from the OTHER properties on the same entry struct.
    //
    // The classifier is deliberately content-driven: if the entry has a
    // `ScalarValues` sibling property, the names are scalars; a `VectorValues`
    // sibling implies vectors; a `Texture` / `Textures` value array implies
    // textures; etc. This works regardless of the parameter-type-enum
    // ordering UE uses internally, which has changed across versions.
    private static void ClassifyByOwnProperty(FStructFallback entry, CachedParameterNames dest)
    {
        List<string> names = ExtractParameterNames(entry);
        if (names.Count == 0) return;

        // Probe sibling property names to pick a bucket.
        bool hasScalars = HasAnyPropertyNamed(entry, "ScalarValues", "ScalarValue", "ScalarOverrides");
        bool hasVectors = HasAnyPropertyNamed(entry, "VectorValues", "VectorValue", "VectorOverrides", "DoubleVectorValues");
        bool hasSwitches = HasAnyPropertyNamed(entry, "StaticSwitchValues", "StaticSwitchValue", "SwitchOverrides", "Values"); // legacy: bool Values
        bool hasTextures = HasAnyPropertyNamed(entry, "TextureValues", "TextureValue", "Textures", "Texture", "TextureOverrides");
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

    // Pulls usable names from a single struct. Tries (in order):
    //   1. Nested `ParameterInfo.Name` (FMaterialParameterInfo wrapper).
    //   2. Direct `Name` / `ParameterName` (flat struct).
    //   3. `ParameterInfoSet` array (TSet<FMaterialParameterInfo>).
    //   4. `ParameterInfos` array (alias used by some forks).
    private static List<string> ExtractParameterNames(FStructFallback entry)
    {
        var names = new List<string>();
        if (entry == null) return names;

        // Single-info shapes.
        TryAddFromInfo(entry, names);

        // Set-shaped containers (modern path).
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
        // FStructFallback exposes Properties via the base. We treat the
        // mere presence of the named property as the signal — value type
        // doesn't matter because we just need to bias the bucket guess.
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

    // --- Source B: typed instance overrides on the property bag -----
    //
    // UMaterialInstance keeps explicit ScalarParameterValues /
    // VectorParameterValues / TextureParameterValues / etc. arrays as
    // first-class UProperties (not under CachedExpressionData). They
    // cook unconditionally because the runtime applies them as
    // overrides on top of the parent material's evaluation result.
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

    // --- Source C: UMaterialExpression typed walk ----------------------
    //
    // Only relevant for assets cooked with editor data. The expression
    // graph is walked in-place by CUE4Parse's UMaterial.GetParams; we
    // mirror the relevant cases here so we capture parameter names even
    // when GetParams hasn't been called.
    private static void ReadMaterialExpressions(UMaterialInterface material, CachedParameterNames dest)
    {
        if (material is not UMaterial umat) return;
        foreach (FPackageIndex idx in umat.Expressions)
        {
            if (!idx.TryLoad(out UMaterialExpression expression)) continue;
            // Pattern-match by class name to avoid taking a hard
            // dependency on every expression-parameter subclass living
            // in the same assembly version. Custom engines often add
            // their own expression types we'd otherwise miss.
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
        // Prefer the strongly-typed ParameterName field if present
        // (UMaterialExpressionParameter and subclasses).
        Type t = expression.GetType();
        FieldInfo? f = t.GetField("ParameterName", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        if (f?.GetValue(expression) is FName fname && !fname.IsNone) return fname.Text;

        // Fallback: read off the shared property bag.
        if (expression.TryGetValue(out FName n, "ParameterName") && !n.IsNone) return n.Text;
        if (expression.TryGetValue(out string s, "ParameterName") && !string.IsNullOrEmpty(s)) return s;
        return null;
    }

    // --- Source D: recursive sweep -------------------------------------
    //
    // Last-resort: walk every nested FStructFallback in the cache and
    // collect parameter-name-shaped values. The walk is depth-bounded
    // because a custom asset could be arbitrarily deep, and we trail
    // the property path so finds get bucketed by context.
    //
    // We do NOT recurse into already-extracted typed buckets — the
    // sweep is purely a safety net for fork-renamed sub-structs.
    private static void RecursiveSweep(FStructFallback root, CachedParameterNames dest, int depth, string propertyTrail)
    {
        if (root?.Properties == null) return;
        if (depth > 6) return;     // budget — UE cached struct depth maxes around 5

        foreach (FPropertyTag tag in root.Properties)
        {
            string propName = tag.Name.Text;
            string trail = string.IsNullOrEmpty(propertyTrail) ? propName : propertyTrail + "." + propName;

            // Heuristic: if the property name is "Name"/"ParameterName" and
            // the value is a string-like, classify by trail.
            object? raw = tag.Tag?.GenericValue;
            if (raw is FName fname && !fname.IsNone && IsNameish(propName))
            {
                BucketByTrail(trail, fname.Text, dest);
                continue;
            }

            // Recurse into nested FStructFallback / arrays.
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
