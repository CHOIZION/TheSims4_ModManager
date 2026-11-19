namespace Sims4ModUpdater.Core.Models;

public enum ModUpdateStatusCode
{
    UpToDate,
    UpdateNeeded,
    ManualCheck,
    Paid,
    EarlyAccess,
    LinkNotFound,
    Error
}

public enum OverwriteMode
{
    Single,
    All
}

/// <summary>
/// 업데이트 조회에 전달할 대상 모드 쿼리 객체
/// </summary>
public sealed class ModQuery
{
    public string Name { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string InstallFolder { get; set; } = string.Empty;
    public string? DirectoryPath { get; set; }
    public string? FilePath { get; set; }
    public string SourceType { get; set; } = "generic_web";
    public string CurrentVersion { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Homepage { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public string Creator { get; set; } = string.Empty;
    public bool IsStandalone { get; set; }
    public List<string> UnitFiles { get; set; } = new();
    public List<string> FolderKeywords { get; set; } = new();
    public List<string> FileKeywords { get; set; } = new();
    public string ModType { get; set; } = string.Empty;
}

public sealed class VariantChoice
{
    public string FileName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public sealed class RemoteFileEntry
{
    public string FileName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
}

public sealed class RejectedSearchCandidate
{
    public int Score { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class FinderDiagnostics
{
    public string MatchStage { get; set; } = string.Empty;
    public string MatchedCatalog { get; set; } = string.Empty;
    public string TokenizedCreator { get; set; } = string.Empty;
    public string TokenizedModTitle { get; set; } = string.Empty;
    public List<string> TokenizedKeywords { get; set; } = new();
    public List<string> QueriesTried { get; set; } = new();
    public int RawCandidateCount { get; set; }
    public List<RejectedSearchCandidate> RejectedSamples { get; set; } = new();
    public string FailureReason { get; set; } = string.Empty;
}

public sealed class RemotePageInspectionResult
{
    public string Status { get; set; } = "browser_required"; // free_direct, early_access, browser_required
    public string Title { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public List<RemoteFileEntry> AllFiles { get; set; } = new();
    public List<VariantChoice> VariantChoices { get; set; } = new();
}

public sealed class ModPageDiscoveryResult
{
    public bool Found { get; set; }
    public bool IsVerifiedSource { get; set; }
    public string PrimaryUrl { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public List<CatalogLink> CandidateLinks { get; set; } = new();
    public ModMetadata ExtractedMeta { get; set; } = new();
    public string Creator { get; set; } = string.Empty;
    public string ModTitle { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool IsPaid { get; set; }
    public bool IsEarlyAccess { get; set; }
    public string ReleaseNotes { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public FinderDiagnostics Diagnostics { get; set; } = new();
}

public sealed class SmartTokenResult
{
    public string Raw { get; set; } = string.Empty;
    public string Creator { get; set; } = string.Empty;
    public List<string> ExtraCreators { get; set; } = new();
    public string ModTitle { get; set; } = string.Empty;
    public List<string> Keywords { get; set; } = new();
    public List<string> QueriesTried { get; set; } = new();
    public int RawCandidateCount { get; set; }
    public List<RejectedSearchCandidate> RejectedSamples { get; set; } = new();
    public string FailureReason { get; set; } = string.Empty;
}

/// <summary>
/// 단일 모드 업데이트 확인 결과
/// </summary>
public sealed class ModCheckResult
{
    public string ModName { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public List<string> UnitFiles { get; set; } = new();
    public string ModType { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string LocalVersionDisplay { get; set; } = "버전 미표기";
    public string LatestVersion { get; set; } = "-";
    public ModUpdateStatusCode StatusCode { get; set; } = ModUpdateStatusCode.ManualCheck;
    public string StatusDisplay { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public bool HasUpdate { get; set; }
    public bool DirectFileDetected { get; set; }
    public bool RequiresBrowser { get; set; }
    public bool IsPaid { get; set; }
    public bool IsEarlyAccess { get; set; }
    public bool LinkNotFound { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public string PatreonUrl { get; set; } = string.Empty;
    public string PostypeUrl { get; set; } = string.Empty;
    public string PostLink { get; set; } = string.Empty;
    public string NaverBlogUrl { get; set; } = string.Empty;
    public string ItchUrl { get; set; } = string.Empty;
    public string AssetName { get; set; } = string.Empty;
    public string DetectedFilename { get; set; } = string.Empty;
    public bool IsMultiFileBundle { get; set; }
    public List<string> BundleFilenames { get; set; } = new();
    public List<VariantChoice> VariantChoices { get; set; } = new();
    public List<RemoteFileEntry> AllRemoteFiles { get; set; } = new();
    public List<CatalogLink> CandidateLinks { get; set; } = new();
    public ModMetadata ExtractedMeta { get; set; } = new();
    public string MatchedCreator { get; set; } = string.Empty;
    public string VerificationNote { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public FinderDiagnostics FinderDiagnostics { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public bool IsPinned { get; set; }
}
