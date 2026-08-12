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

        switch (tag.GenericValue)
        {
            case FPackageIndex packageIndex:
                TryEnqueueExportComponent(packageIndex, owningPackage, visitedExportIndices, queue);
                return;

            case FScriptStruct scriptStruct:
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

        if (tag is OptionalProperty optional && optional.Value is { } innerTag)
        {
            WalkPropertyValue(innerTag, owningPackage, visitedExportIndices, queue);
        }
    }

    private void WalkScriptStructType(IUStruct? structType, IPackage? owningPackage, HashSet<int> visitedExportIndices, Queue<UObject> queue)
    {
        switch (structType)
        {
            case IPropertyHolder structHolder:
                EnqueueExportReferences(structHolder, owningPackage, visitedExportIndices, queue);
                return;

            case FInstancedStruct instancedStruct:
                WalkScriptStructType(instancedStruct.ScriptStruct?.StructType, owningPackage, visitedExportIndices, queue);
                return;

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

            case FInstancedOverridablePropertyBag overridableBag:
                if (overridableBag.Defaults is { } overridableDefaults)
                {
                    EnqueueExportReferences(overridableDefaults, owningPackage, visitedExportIndices, queue);
                }
                return;

            case FStateTreeInstanceData stateTreeInstance:
                if (stateTreeInstance.Data is { } stateTreeData)
                {
                    EnqueueExportReferences(stateTreeData, owningPackage, visitedExportIndices, queue);
                }
                return;

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
            _logError($"[GlbScene] Lossless component load failed (index={packageIndex.Index}, name='{packageIndex.Name}'): {ex.GetType().Name}: {ex.Message}");
            lock (_manifest) { _manifest.RecordDroppedComponent($"index={packageIndex.Index} '{packageIndex.Name}': {ex.Message}"); }
            return;
        }

        if (loaded is null) return;
        queue.Enqueue(loaded);
    }

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
