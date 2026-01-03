using System.Text.Json.Serialization;

namespace maccy.Services;

public sealed class UpdateManifest
{
    [JsonPropertyName("appId")]
    public string AppId { get; set; } = "maccy";

    [JsonPropertyName("latest")]
    public UpdateRelease Latest { get; set; } = new();
}

public sealed class UpdateRelease
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; set; }

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;

    [JsonPropertyName("publishedAt")]
    public string PublishedAt { get; set; } = string.Empty;

    [JsonPropertyName("installer")]
    public UpdateInstaller Installer { get; set; } = new();
}

public sealed class UpdateInstaller
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
