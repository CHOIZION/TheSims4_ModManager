using System.Text.Json.Serialization;

namespace Sims4ModUpdater.Core.Models;

/// <summary>
/// mods_manifest.json 루트 컨테이너
/// </summary>
public sealed class ManifestDocument
{
    [JsonPropertyName("mods")]
    public List<ManifestEntry> Mods { get; set; } = new();
}

/// <summary>
/// mods_manifest.json 내 개별 모드 및 핀(Pinning) 정보 항목
/// </summary>
public sealed class ManifestEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("local_file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LocalFile { get; set; }

    [JsonPropertyName("source_type")]
    public string SourceType { get; set; } = "generic_web";

    [JsonPropertyName("install_folder")]
    public string InstallFolder { get; set; } = string.Empty;

    [JsonPropertyName("folder_keywords")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FolderKeywords { get; set; }

    [JsonPropertyName("file_keywords")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FileKeywords { get; set; }

    [JsonPropertyName("channel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Channel { get; set; }

    [JsonPropertyName("require_script")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RequireScript { get; set; }

    [JsonPropertyName("current_version")]
    public string CurrentVersion { get; set; } = string.Empty;

    [JsonPropertyName("homepage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Homepage { get; set; }

    [JsonPropertyName("pinned_page_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PinnedPageUrl { get; set; }

    [JsonPropertyName("postype_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PostypeUrl { get; set; }

    [JsonPropertyName("naver_blog_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NaverBlogUrl { get; set; }

    [JsonPropertyName("repo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Repo { get; set; }
}
