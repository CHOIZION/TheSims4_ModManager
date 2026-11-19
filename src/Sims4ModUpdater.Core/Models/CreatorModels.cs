namespace Sims4ModUpdater.Core.Models;

public enum CreatorOrganizeLayout
{
    /// <summary>
    /// 제작자별 단일 폴더(Mods\제작자\파일) - 1단계 깊이 자동 준수, 폴더 수 최소화
    /// </summary>
    CreatorSingleFolder,

    /// <summary>
    /// 제작자 접두사 폴더(Mods\[제작자] 모드명\파일) - 모드별 파일 분리 + 가나다순 제작자 정렬
    /// </summary>
    CreatorPrefixedModFolder
}

public sealed class CreatorOverrideEntry
{
    public string ModKey { get; set; } = string.Empty;
    public string PrimaryFileName { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
}

public sealed class CreatorOverridesDocument
{
    public List<CreatorOverrideEntry> Overrides { get; set; } = new();
}

public sealed class CreatorResolutionResult
{
    public string Creator { get; init; } = string.Empty;
    public bool IsKnown { get; init; }
    public bool IsOverride { get; init; }
    public string Source { get; init; } = "unknown";
    public string AutoDetectedCreator { get; init; } = string.Empty;

    public string SourceDisplayLabel => Source switch
    {
        "manual_override" => "직접 지정",
        "metadata" => "내부 메타데이터",
        "catalog" => "공식 카탈로그",
        "url_domain" => "내장/고정 URL",
        "filename" => "파일·폴더명",
        _ => "미분류"
    };
}

public sealed class OrganizeMovePlanItem
{
    public string ModName { get; init; } = string.Empty;
    public string Creator { get; init; } = string.Empty;
    public string SourceFullPath { get; init; } = string.Empty;
    public string TargetFullPath { get; init; } = string.Empty;
    public string SourceRelativePath { get; init; } = string.Empty;
    public string TargetRelativePath { get; init; } = string.Empty;
    public bool HasConflict { get; init; }
    public bool HasScriptDepthRisk { get; init; }
    public bool IsIdenticalDuplicate { get; init; }
    public bool DeleteDuplicateSource { get; init; }
    public string StatusNote { get; init; } = string.Empty;
}

public sealed class OrganizePlanSummary
{
    public CreatorOrganizeLayout Layout { get; init; }
    public string ModsFolder { get; init; } = string.Empty;
    public string SourceRootFolder { get; init; } = string.Empty;
    public bool DeleteEmptySourceRoot { get; init; }
    public bool DeleteIdenticalDuplicates { get; init; }
    public int TotalMoves { get; init; }
    public int DuplicateCleanCount { get; init; }
    public int AlreadyOrganizedCount { get; init; }
    public int SkippedUnknownCount { get; init; }
    public int ConflictCount { get; init; }
    public int DepthWarningCount { get; init; }
    public List<OrganizeMovePlanItem> Items { get; init; } = new();
}

public sealed class OrganizeExecutionResult
{
    public int MovedFileCount { get; init; }
    public int DeletedDuplicateCount { get; init; }
    public int SkippedConflictCount { get; init; }
    public int CleanedEmptyFolderCount { get; init; }
    public string? BackupSessionDir { get; init; }
    public List<string> Warnings { get; init; } = new();
}

public sealed class DuplicateDeletePlanItem
{
    public bool IsSelected { get; set; } = true;
    public string GroupName { get; init; } = string.Empty;
    public string MatchType { get; init; } = string.Empty;
    public bool IsExactContentMatch { get; init; }
    public string KeepPath { get; init; } = string.Empty;
    public string DeletePath { get; init; } = string.Empty;
    public string KeepRelativePath { get; init; } = string.Empty;
    public string DeleteRelativePath { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public DateTime KeepModifiedUtc { get; init; }
    public DateTime DeleteModifiedUtc { get; init; }
}

public sealed class DuplicateCleanupPlan
{
    public string ModsFolder { get; init; } = string.Empty;
    public bool IncludeContentHashDuplicates { get; init; }
    public int TotalFilesScanned { get; init; }
    public int DuplicateGroupCount { get; init; }
    public List<DuplicateDeletePlanItem> Items { get; init; } = new();
}

public sealed class DuplicateCleanupResult
{
    public int DeletedCount { get; init; }
    public int SkippedCount { get; init; }
    public long FreedBytes { get; init; }
    public int CleanedEmptyFolderCount { get; init; }
    public string? BackupSessionDir { get; init; }
    public List<string> Warnings { get; init; } = new();
}
