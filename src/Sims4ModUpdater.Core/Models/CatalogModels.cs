using System.Text.Json.Serialization;

namespace Sims4ModUpdater.Core.Models;

/// <summary>
/// 내장 공식 모드 카탈로그 항목 (known_catalog.json)
/// </summary>
public sealed class CatalogItem
{
    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = new();

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("creator")]
    public string Creator { get; set; } = string.Empty;

    [JsonPropertyName("mod_title")]
    public string ModTitle { get; set; } = string.Empty;

    [JsonPropertyName("override_mod_title")]
    public bool OverrideModTitle { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("is_paid")]
    public bool IsPaid { get; set; }

    [JsonPropertyName("is_early_access")]
    public bool IsEarlyAccess { get; set; }

    [JsonPropertyName("release_notes")]
    public string ReleaseNotes { get; set; } = string.Empty;

    [JsonPropertyName("links")]
    public List<CatalogLink> Links { get; set; } = new();
}

/// <summary>
/// 카탈로그 또는 검색 후보 링크
/// </summary>
public sealed class CatalogLink
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// 카탈로그 매칭 결과
/// </summary>
public sealed class CatalogMatchResult
{
    public bool Found { get; init; }
    public string PrimaryUrl { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
    public List<CatalogLink> CandidateLinks { get; init; } = new();
    public string MatchedCatalog { get; init; } = string.Empty;
    public string Creator { get; init; } = string.Empty;
    public string ModTitle { get; init; } = string.Empty;
    public bool OverrideModTitle { get; init; }
    public string Version { get; init; } = string.Empty;
    public bool IsPaid { get; init; }
    public bool IsEarlyAccess { get; init; }
    public string ReleaseNotes { get; init; } = string.Empty;
    public string Query { get; init; } = string.Empty;
}
