using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace maccy.Services;

public sealed class UpdatePackageManifest
{
    [JsonPropertyName("appId")]
    public string AppId { get; set; } = "maccy";

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("runtime")]
    public string Runtime { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<UpdatePackageFile> Files { get; set; } = [];
}

public sealed class UpdatePackageFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
