namespace Ruri.RipperHook.Attributes;

/// <summary>Discovery metadata on an optional feature; reading it never creates a provider.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RemoteSourceAttribute(string id, string label, string feature) : Attribute
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string Feature { get; } = feature;
    public string AuthDataset { get; set; } = string.Empty;
    public string SessionDataset { get; set; } = string.Empty;
    public string SearchDataset { get; set; } = string.Empty;
    public string DetailDataset { get; set; } = string.Empty;
    public string VersionsDataset { get; set; } = string.Empty;
    public string DownloadDataset { get; set; } = string.Empty;
    public string DownloadStartDataset { get; set; } = string.Empty;
    public string PlatformsDataset { get; set; } = string.Empty;
    public string AuthMode { get; set; } = "credentials";
    public string ThumbnailDataset { get; set; } = string.Empty;
    public string DownloadPlanDataset { get; set; } = string.Empty;
    public string PackagePrepareDataset { get; set; } = string.Empty;
}
