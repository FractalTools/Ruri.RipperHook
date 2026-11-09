using Ruri.RipperHook.Attributes;
using System.Reflection;
using System.Text.Json;
using Ruri.Hook.Core;
using Ruri.RipperHook.BlenderBridge.Data;
using Ruri.RipperHook.BlenderBridge.Tables;

namespace Ruri.RipperHook.BlenderBridge.Data;

internal static class SourceDatasets
{
    internal const string SourcesId = "core.sources";
    internal const string ControlId = "core.sources.control";

    internal static void Register(Func<string, bool, bool> setOptionalFeature)
    {
        Datasets.Publish(SourcesId, DataRole.Session, [],
            "Optional remote source capabilities discovered without opening an install.", Table, cache: false, readerIndependent: true);
        Datasets.PublishBlob(ControlId, DataRole.Session, [],
            "Enable or disable a discovered source without changing the current install.",
            request => Control(request, setOptionalFeature), readerIndependent: true);
        DownloadJobs.Register();
    }

    internal static void Unregister()
    {
        Datasets.Remove(SourcesId);
        Datasets.Remove(ControlId);
        DownloadJobs.Unregister();
    }

    private static RemoteSourceAttribute[] Discover()
    {
        RemoteSourceAttribute[] sources = HookCatalog.Features
            .Select(feature => (Feature: feature, Source: feature.Type.GetCustomAttribute<RemoteSourceAttribute>()))
            .Where(pair => pair.Source is not null)
            .Select(pair =>
            {
                RemoteSourceAttribute source = pair.Source!;
                if (string.IsNullOrWhiteSpace(source.Id) || string.IsNullOrWhiteSpace(source.Label)
                    || !string.Equals(pair.Feature.Name, source.Feature, StringComparison.OrdinalIgnoreCase))
                    throw new SourceRequestException("invalid_capability", "A source declares invalid feature metadata.");
                return source;
            }).OrderBy(source => source.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        if (sources.Select(source => source.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new SourceRequestException("invalid_capability", "Source identifiers must be unique.");
        return sources;
    }

    private static ColumnTable Table(DataRequest request)
    {
        request.Cancellation.ThrowIfCancellationRequested();
        HashSet<string> active = new(Ruri.Hook.RuriHook.AppliedHookIds, StringComparer.OrdinalIgnoreCase);
        TableBuilder table = new(SourcesId, "id", "label", "feature", "enabled#", "auth", "session", "search",
            "detail", "versions", "download", "download_start", "platforms", "auth_mode", "thumbnail", "download_plan", "package_prepare");
        table.Role(ColumnRole.Key, "id").Role(ColumnRole.Label, "label");
        foreach (RemoteSourceAttribute source in Discover())
            table.Row(source.Id, source.Label, source.Feature, active.Contains(source.Feature), source.AuthDataset,
                source.SessionDataset, source.SearchDataset, source.DetailDataset, source.VersionsDataset,
                source.DownloadDataset, source.DownloadStartDataset, source.PlatformsDataset, source.AuthMode,
                source.ThumbnailDataset, source.DownloadPlanDataset, source.PackagePrepareDataset);
        return table.Build();
    }

    private static byte[] Control(DataRequest request, Func<string, bool, bool> setOptionalFeature)
    {
        request.Cancellation.ThrowIfCancellationRequested();
        (string action, string id) = ControlPayload.Read(request.Payload, "source");
        if (action is not ("enable" or "disable"))
            throw new SourceRequestException("invalid_action", "The source action must be enable or disable.");
        RemoteSourceAttribute source = Discover().FirstOrDefault(source =>
            string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new SourceRequestException("source_not_found", "The requested source is not present in this build.");
        bool enabled = setOptionalFeature(source.Feature, action == "enable");
        return JsonSerializer.SerializeToUtf8Bytes(new { source = source.Id, enabled });
    }
}
