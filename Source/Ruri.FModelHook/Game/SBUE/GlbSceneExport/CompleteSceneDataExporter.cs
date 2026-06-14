using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.UE4;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.StateTree;
using CUE4Parse.UE4.Objects.StructUtils;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.WorldCondition;
using Newtonsoft.Json;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// Writes the LOSSLESS LAYER under `<output>/<map>/Actors/`.
//
// Every actor collected by WorldActorCollector — regardless of whether any
// IComponentExporter claims it on the render side — gets one JSON file. The
// payload is byte-equivalent to FModel's Save-Properties dump (the exact same
// `JsonConvert.SerializeObject(obj, Formatting.Indented)` call FModel uses for
// its tabbed property view; see CUE4Parse `UObjectConverter.WriteJson` at
// CUE4Parse/JsonConverters.cs:822-841 and UObject's protected internal
// `WriteJson(JsonWriter,JsonSerializer)` at UObject.cs:385-470 which dumps
// Type/Name/Flags/Class/Outer/Super/Template + the full `Properties` tree).
//
// Per actor we emit ONE JSON file of the shape:
//
//   {
//     "Actor": { ...UObjectConverter dump of the actor... },
//     "Components": [
//       { "PathName": "/Game/.../Actor.MeshComponent",
//         "Object":   { ...UObjectConverter dump of MeshComponent... } },
//       { "PathName": "/Game/.../Actor.PointLightComponent",
//         "Object":   { ...UObjectConverter dump... } },
//       ...
//     ]
//   }
//
// `Components[]` carries every UObject reachable from the actor through any
// FPackageIndex with `IsExport == true` whose owning package matches the
// actor's package. This is the cooked-graph closure of the actor inside its
// own package (BlueprintCreatedComponents, InstanceComponents, named
// singleton components, AttachParent chains, SCS-spawned children,
// Niagara/Light/Camera/Spline children — anything the cooker laid down as a
// sibling export of the actor). FPackageIndex.IsExport is the exact gate
// `FPackageIndex` uses (ObjectResource.cs:64).
//
// References that resolve to a DIFFERENT package — UStaticMesh, UMaterial,
// UNiagaraSystem, UWorld, USoundCue, UDataAsset — are recorded as their
// `ObjectName`/`ObjectPath` reference inside the actor JSON (the JSON path
// `FPackageIndexConverter` writes at JsonConverters.cs:944-955) and the
// CLOSURE LAYER (DependencyClosureExporter) is responsible for dumping their
// content. The two layers are orthogonal: an asset PathName seen here will
// surface in the closure layer regardless of whether the actor renders.
//
// FModel's render preview throws away every actor whose ExportType is not on
// its small renderable allow-list (Renderer.cs:443-456). Niagara emitters,
// PostProcessVolume, ExponentialHeightFog, SkyAtmosphere, SphereReflectionCapture,
// CineCameraActor, LevelSequenceActor, RuntimeVirtualTextureVolume, WorldDataLayers,
// CullDistanceVolume, PlayerStart, SkyLight, RectLight and every "I am just a
// marker AActor" type are silently dropped. Here they all land losslessly:
// the UObjectConverter walks `Properties` reflectively, so whatever the
// cooker stored — Niagara `Asset`/parameter overrides, fog Settings/Inscattering
// fields, sky atmosphere RayleighScattering/SkyColor, level-sequence
// `LevelSequenceAsset`, virtual-texture `VirtualTexture` reference — appears
// as fields on the dump. The CUE4Parse property tree IS the uasset's truth
// for any UPROPERTY-tagged field (and for UE 5.7 every gameplay-relevant
// field on these types is UPROPERTY; cross-checked against UE source at
// E:/Games/UnrealEngine-5.7.4-release/Engine/Plugins/FX/Niagara/Source/Niagara/Public/NiagaraComponent.h
// + Engine/Source/Runtime/Engine/Classes/Engine/{ExponentialHeightFog,PostProcessVolume,SkyLight}.h
// + Engine/Source/Runtime/Engine/Classes/Components/SkyAtmosphereComponent.h
// + Engine/Source/Runtime/LevelSequence/Public/LevelSequenceActor.h).
//
// Parallelism: per-actor work is independent (disjoint output files, no
// shared mutable state in the JSON write path). Actors run on
// `Parallel.ForEach`. The only cross-thread surface is `SceneManifest`,
// which has plain `Dictionary<>` aggregators in its public contract — every
// `RecordActor`/`RecordComponent`/`RecordDroppedActor`/`RecordDroppedComponent`
// call is serialised through a single `lock (_manifest)` so the manifest
// counters stay consistent without changing its public surface.
//
// File naming: `<index:D6>_<ExportType>_<SafeName>.json`. The 6-digit zero
// padded index is the position of the actor in the collected list (so the
// 2537 files of Oni_Valley land in deterministic order and never collide on
// duplicate names). Filesystem-illegal characters are mapped to `_`.
public sealed class CompleteSceneDataExporter
{
    private readonly Action<string> _log;
    private readonly Action<string> _logError;
    private readonly SceneManifest _manifest;

    public CompleteSceneDataExporter(Action<string> log, Action<string> logError, SceneManifest manifest)
    {
        _log = log;
        _logError = logError;
        _manifest = manifest;
    }

    public void ExportAll(IReadOnlyList<IPropertyHolder> actors, string actorsOutputDirectory)
    {
        Directory.CreateDirectory(actorsOutputDirectory);

        // Per-thread counters reduce contention on the shared manifest lock.
        // Each task increments locals, then folds them into the manifest at
        // task end. Same observable behaviour as one-call-per-component, but
        // far fewer Monitor enters on a 2537-actor world.
        int writtenActorTotal = 0;
        int writtenComponentTotal = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
        };

        Parallel.For(0, actors.Count, parallelOptions, () => new ThreadLocalCounters(), (actorIndex, _, counters) =>
        {
            IPropertyHolder actor = actors[actorIndex];
            try
            {
                ExportSingleActor(actorIndex, actor, actorsOutputDirectory, counters);
            }
            catch (Exception ex)
            {
                // Best-effort cleanup: a JSON write that threw mid-stream
                // leaves a truncated file behind. Drop it so the directory
                // count remains a faithful "successfully exported actor"
                // tally — even if the cleanup itself fails (file locked,
                // permission), the manifest still records the drop reason
                // so the audit trail is complete.
                string actorDescription = actor is UObject uo ? uo.GetPathName() : actor.GetType().Name;
                if (actor is UObject failedActor)
                {
                    string partialPath = Path.Combine(
                        actorsOutputDirectory,
                        $"{actorIndex:D6}_{failedActor.ExportType}_{MakeFilesystemSafe(failedActor.Name)}.json");
                    try { if (File.Exists(partialPath)) File.Delete(partialPath); }
                    catch (Exception cleanupException)
                    {
                        _logError($"[GlbScene] Lossless partial-file cleanup failed for '{partialPath}': {cleanupException.Message}");
                    }
                }
                _logError($"[GlbScene] Lossless actor export failed for '{actorDescription}': {ex.GetType().Name}: {ex.Message}");
                lock (_manifest)
                {
                    _manifest.RecordDroppedActor($"{actorDescription}: {ex.Message}");
                }
            }
            return counters;
        }, counters =>
        {
            Interlocked.Add(ref writtenActorTotal, counters.WrittenActors);
            Interlocked.Add(ref writtenComponentTotal, counters.WrittenComponents);
        });

        _log($"[GlbScene] Lossless actors written: {writtenActorTotal} (components inlined: {writtenComponentTotal}) -> {actorsOutputDirectory}");
    }

    private void ExportSingleActor(int actorIndex, IPropertyHolder actor, string actorsOutputDirectory, ThreadLocalCounters counters)
    {
        if (actor is not UObject actorObject)
        {
            lock (_manifest) { _manifest.RecordDroppedActor("not a UObject (no ExportType)"); }
            return;
        }

        string exportType = actorObject.ExportType;
        string safeName = MakeFilesystemSafe(actorObject.Name);
        string actorFileName = $"{actorIndex:D6}_{exportType}_{safeName}.json";
        string actorJsonPath = Path.Combine(actorsOutputDirectory, actorFileName);

        // BFS over export-indexed FPackageIndex references reachable from the
        // actor to collect every component that lives in the actor's own
        // package. The walker recurses through StructProperty/ArrayProperty/
        // MapProperty/SetProperty/OptionalProperty/Delegate values so an
        // attach chain like Actor -> SceneRoot -> StaticMesh -> attached
        // PointLight that is buried under a StructProperty still lands here.
        IPackage? owningPackage = actorObject.Owner;
        var visitedExportIndices = new HashSet<int>();
        var queue = new Queue<UObject>();
        var orderedComponents = new List<UObject>();
        EnqueueExportReferences(actorObject, owningPackage, visitedExportIndices, queue);
        while (queue.Count > 0)
        {
            UObject component = queue.Dequeue();
            orderedComponents.Add(component);
            EnqueueExportReferences(component, owningPackage, visitedExportIndices, queue);
        }

        WriteActorAndComponentsJson(actorJsonPath, actorObject, orderedComponents);

        counters.WrittenActors++;
        counters.WrittenComponents += orderedComponents.Count;

        lock (_manifest)
        {
            _manifest.RecordActor(exportType);
            foreach (UObject component in orderedComponents)
            {
                _manifest.RecordComponent(component.ExportType);
            }
        }
    }

    // Stream-write the per-actor JSON instead of materialising a JObject tree
    // so big actor + component dumps stay flat in memory and never go through
    // a double-encode pass. The serializer delegates `actor` and each
    // component to `UObjectConverter` automatically because UObject has
    // `[JsonConverter(typeof(UObjectConverter))]` on the class itself
    // (UObject.cs:99) — the writer here only frames the top-level shape.
    private static void WriteActorAndComponentsJson(string filePath, UObject actor, IReadOnlyList<UObject> components)
    {
        var serializer = JsonSerializer.CreateDefault();
        serializer.Formatting = Formatting.Indented;
        using var fileStream = new FileStream(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var textWriter = new StreamWriter(fileStream, new UTF8Encoding(false));
        using var jsonWriter = new JsonTextWriter(textWriter)
        {
            Formatting = Formatting.Indented,
            CloseOutput = false,
        };

        jsonWriter.WriteStartObject();

        jsonWriter.WritePropertyName("Actor");
        serializer.Serialize(jsonWriter, actor);

        jsonWriter.WritePropertyName("Components");
        jsonWriter.WriteStartArray();
        foreach (UObject component in components)
        {
            jsonWriter.WriteStartObject();
            jsonWriter.WritePropertyName("PathName");
            jsonWriter.WriteValue(component.GetPathName());
            jsonWriter.WritePropertyName("Object");
            serializer.Serialize(jsonWriter, component);
            jsonWriter.WriteEndObject();
        }
        jsonWriter.WriteEndArray();

        jsonWriter.WriteEndObject();
    }

    // Add every export-indexed FPackageIndex reachable from `holder.Properties`
    // (recursively into struct/array/map/set/optional values, plus delegate
    // bindings) into the BFS queue if it has not been visited and its target
    // load resolves to a UObject. Loads inside the same package are cheap (the
    // package's `ExportsLazy[]` is already materialised by the time we hit
    // here because WorldActorCollector loaded the actor and its outer first).
    private void EnqueueExportReferences(IPropertyHolder holder, IPackage? owningPackage, HashSet<int> visitedExportIndices, Queue<UObject> queue)
    {
        if (holder.Properties is not { Count: > 0 } properties) return;
        foreach (FPropertyTag propertyTag in properties)
        {
            WalkPropertyValue(propertyTag.Tag, owningPackage, visitedExportIndices, queue);
        }
    }

    private void WalkPropertyValue(FPropertyTagType? tag, IPackage? owningPackage, HashSet<int> visitedExportIndices, Queue<UObject> queue)
    {
        if (tag is null) return;

        // ObjectProperty / ClassProperty / WeakObjectProperty — every concrete
        // subtype of `ObjectProperty` is `FPropertyTagType<FPackageIndex>`
        // (ObjectProperty.cs:8). InterfaceProperty wraps FScriptInterface
        // whose `.Object` is also FPackageIndex.
        switch (tag.GenericValue)
        {
            case FPackageIndex packageIndex:
                TryEnqueueExportComponent(packageIndex, owningPackage, visitedExportIndices, queue);
                return;

            case FScriptStruct scriptStruct:
                // Struct content recurses through `Properties` IF the struct
                // type is the generic `FStructFallback` (specialised struct
                // types like FBox/FVector/FColor/FGuid carry only value data,
                // no UObject references). Treating only AbstractPropertyHolder
                // structs as recurse-targets matches CUE4Parse's own typing —
                // FStructFallback IS the catch-all for unknown struct shapes
                // (FStructFallback.cs:15-19) and any cooked actor's nested
                // property graph that COULD harbour a component pointer goes
                // through it.
                WalkScriptStructType(scriptStruct.StructType, owningPackage, visitedExportIndices, queue);
                return;

            case UScriptArray scriptArray:
                foreach (FPropertyTagType element in scriptArray.Properties)
                {
                    WalkPropertyValue(element, owningPackage, visitedExportIndices, queue);
                }
                return;

            case UScriptSet scriptSet:
                foreach (FPropertyTagType element in scriptSet.Properties)
                {
                    WalkPropertyValue(element, owningPackage, visitedExportIndices, queue);
                }
                return;

            case UScriptMap scriptMap:
                foreach (var entry in scriptMap.Properties)
                {
                    WalkPropertyValue(entry.Key, owningPackage, visitedExportIndices, queue);
                    WalkPropertyValue(entry.Value, owningPackage, visitedExportIndices, queue);
                }
                return;

            case FScriptInterface scriptInterface when scriptInterface.Object is { } interfacePackageIndex:
                TryEnqueueExportComponent(interfacePackageIndex, owningPackage, visitedExportIndices, queue);
                return;

            case FScriptDelegate scriptDelegate when scriptDelegate.Object is { } delegatePackageIndex:
                TryEnqueueExportComponent(delegatePackageIndex, owningPackage, visitedExportIndices, queue);
                return;

            case FMulticastScriptDelegate multicastDelegate when multicastDelegate.InvocationList is { Length: > 0 } invocations:
                foreach (FScriptDelegate inner in invocations)
                {
                    if (inner.Object is { } innerPackageIndex)
                    {
                        TryEnqueueExportComponent(innerPackageIndex, owningPackage, visitedExportIndices, queue);
                    }
                }
                return;
        }

        // OptionalProperty wraps another FPropertyTagType (OptionalProperty.cs:8).
        if (tag is OptionalProperty optional && optional.Value is { } innerTag)
        {
            WalkPropertyValue(innerTag, owningPackage, visitedExportIndices, queue);
        }
    }

    // Recurse into the payload of an `FScriptStruct.StructType` (`IUStruct`)
    // looking for component references. Three layers of coverage:
    //
    //   1. `IPropertyHolder` (i.e., `FStructFallback`): the catch-all struct
    //      body the cooker uses for any unknown struct shape. Its `Properties`
    //      tree is walked recursively (the property graph that COULD harbour a
    //      component pointer always goes through here).
    //   2. `IUStruct` wrappers whose own typed payload is itself one or more
    //      `FStructFallback` bodies. CUE4Parse hides those payloads behind
    //      typed wrappers (`FInstancedStruct.NonConstIUSturct`,
    //      `FInstancedStructContainer.Structs`, `FInstancedOverridablePropertyBag.Defaults`,
    //      `FStateTreeInstanceData.Data`, `FWorldConditionQueryDefinition.{StaticStruct,SharedDefinition}`,
    //      `FUniversalObjectLocatorFragment.FragmentStruct`). Without this
    //      hop a cooked AActor that bakes a component reference into a
    //      StateTree / WorldCondition / OverridableBag would NOT surface that
    //      component on the lossless layer. Faithful to the BFS contract:
    //      every export-indexed `FPackageIndex` reachable from the actor's
    //      property graph must land in `Components[]`.
    //   3. Other specialised struct types (FBox/FVector/FColor/FGuid/etc.)
    //      carry only value data and intentionally do not recurse.
    //
    // `FInstancedPropertyBag` (without the Overridable subclass) and the
    // closed-form game-specific structs (FBlueprintFunctionLibraryConditional
    // and friends) do not expose any reachable `FStructFallback`/`FPackageIndex`
    // payload from CUE4Parse's parsed tree — what the cooker wrote is preserved
    // verbatim through the JSON dump but cannot harbour an actor-package
    // component reference the BFS would otherwise miss.
    private void WalkScriptStructType(IUStruct? structType, IPackage? owningPackage, HashSet<int> visitedExportIndices, Queue<UObject> queue)
    {
        switch (structType)
        {
            // FStructFallback (and the two game-specific FAoCFile / FAion2DataFile
            // holders which also implement IPropertyHolder).
            case IPropertyHolder structHolder:
                EnqueueExportReferences(structHolder, owningPackage, visitedExportIndices, queue);
                return;

            // FInstancedStruct wraps an `FStructFallback` under
            // `NonConstIUSturct` (FInstancedStruct.cs:14-16). The payload is
            // itself an IUStruct so recurse through the same dispatch — this
            // keeps InstancedStruct/TedInstancedStruct/VinInstancedStruct
            // (FScriptStruct.cs:193,280,293) — common on data layers and
            // gameplay tag containers — surrendering their internal
            // component references.
            case FInstancedStruct instancedStruct:
                WalkScriptStructType(instancedStruct.NonConstIUSturct, owningPackage, visitedExportIndices, queue);
                return;

            // FInstancedStructContainer.Structs[] is an array of nested
            // FStructFallback (FInstancedStructContainer.cs:9). Each entry
            // can itself harbour a property graph with component pointers,
            // so descend into every non-null one.
            case FInstancedStructContainer instancedContainer:
                if (instancedContainer.Structs is { Length: > 0 } containerStructs)
                {
                    foreach (FStructFallback? containerEntry in containerStructs)
                    {
                        if (containerEntry is not null)
                        {
                            EnqueueExportReferences(containerEntry, owningPackage, visitedExportIndices, queue);
                        }
                    }
                }
                return;

            // FInstancedOverridablePropertyBag.Defaults : FStructFallback?
            // (FInstancedOverridablePropertyBag.cs:11,20). The base class
            // `FInstancedPropertyBag` skips its payload at the CUE4Parse layer
            // (FInstancedPropertyBag.cs:69-74 TODO) — but the Overridable
            // subclass DOES materialise its defaults as an FStructFallback,
            // and a designer-authored Overridable bag CAN bake direct
            // ObjectProperty values pointing back into the actor package.
            case FInstancedOverridablePropertyBag overridableBag:
                if (overridableBag.Defaults is { } overridableDefaults)
                {
                    EnqueueExportReferences(overridableDefaults, owningPackage, visitedExportIndices, queue);
                }
                return;

            // FStateTreeInstanceData.Data : FStructFallback?
            // (FStateTreeInstanceData.cs:9). StateTree instance data on an
            // actor can pin specific component instances (e.g., AI-driven
            // perception or animation rigs); the FStructFallback walk surfaces
            // those component pointers.
            case FStateTreeInstanceData stateTreeInstance:
                if (stateTreeInstance.Data is { } stateTreeData)
                {
                    EnqueueExportReferences(stateTreeData, owningPackage, visitedExportIndices, queue);
                }
                return;

            // FWorldConditionQueryDefinition.{StaticStruct,SharedDefinition}
            // (FWorldConditionQueryDefinition.cs:10-11). World-condition queries
            // can reference scene objects through their static and shared
            // definition payloads.
            case FWorldConditionQueryDefinition worldConditionQuery:
                if (worldConditionQuery.StaticStruct is { } worldConditionStatic)
                {
                    EnqueueExportReferences(worldConditionStatic, owningPackage, visitedExportIndices, queue);
                }
                if (worldConditionQuery.SharedDefinition is { } worldConditionShared)
                {
                    EnqueueExportReferences(worldConditionShared, owningPackage, visitedExportIndices, queue);
                }
                return;

            // FUniversalObjectLocatorFragment.FragmentStruct : FStructFallback?
            // (FUniversalObjectLocatorFragment.cs:10). Universal Object
            // Locators frequently point at SubObjects of the host actor; the
            // FStructFallback dispatch surrenders those FPackageIndex values.
            case FUniversalObjectLocatorFragment universalLocator:
                if (universalLocator.FragmentStruct is { } universalLocatorStruct)
                {
                    EnqueueExportReferences(universalLocatorStruct, owningPackage, visitedExportIndices, queue);
                }
                return;
        }
    }

    private void TryEnqueueExportComponent(FPackageIndex packageIndex, IPackage? owningPackage, HashSet<int> visitedExportIndices, Queue<UObject> queue)
    {
        if (packageIndex is null) return;
        if (!packageIndex.IsExport) return;
        // Cross-package guard: a cooked sub-asset can have its own embedded
        // exports that resolve through a different Owner. We only descend
        // into exports of the actor's own package — exports of other
        // packages are the closure layer's job. When the actor has no Owner
        // we cannot establish a "same package" rule, so we refuse to descend
        // at all (this is the same conservative behaviour FModel applies
        // when ResolvePackageIndex would otherwise misroute).
        if (owningPackage is null) return;
        if (packageIndex.Owner is not null && !ReferenceEquals(packageIndex.Owner, owningPackage)) return;
        if (!visitedExportIndices.Add(packageIndex.Index)) return;

        UObject? loaded;
        try
        {
            loaded = packageIndex.Load() as UObject;
        }
        catch (Exception ex)
        {
            // The same lazy-load semantics ComponentResolver uses
            // (ComponentResolver.cs:174-186): a bad component must NOT tear
            // down the actor's whole walk. Record the drop and move on.
            _logError($"[GlbScene] Lossless component load failed (index={packageIndex.Index}, name='{packageIndex.Name}'): {ex.GetType().Name}: {ex.Message}");
            lock (_manifest) { _manifest.RecordDroppedComponent($"index={packageIndex.Index} '{packageIndex.Name}': {ex.Message}"); }
            return;
        }

        if (loaded is null) return;
        queue.Enqueue(loaded);
    }

    // Strip path-illegal characters; UE export names are usually clean but
    // ":"/"."/"<>"/"/" can appear in synthesised names. Stays on the stack
    // for short names; falls back to heap for the very rare long ones.
    private static string MakeFilesystemSafe(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Unnamed";
        if (name.Length <= 256)
        {
            Span<char> buffer = stackalloc char[name.Length];
            ScrubInto(name, buffer);
            return new string(buffer);
        }
        char[] heap = new char[name.Length];
        ScrubInto(name, heap.AsSpan());
        return new string(heap);
    }

    private static void ScrubInto(string source, Span<char> destination)
    {
        for (int characterIndex = 0; characterIndex < source.Length; characterIndex++)
        {
            char character = source[characterIndex];
            destination[characterIndex] = character switch
            {
                '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' => '_',
                _ => character,
            };
        }
    }

    private sealed class ThreadLocalCounters
    {
        public int WrittenActors;
        public int WrittenComponents;
    }
}
