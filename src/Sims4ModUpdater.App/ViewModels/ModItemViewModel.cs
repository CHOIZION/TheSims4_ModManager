using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using Sims4ModUpdater.Core.Models;
using Sims4ModUpdater.Core.Services;
using Sims4ModUpdater.Infrastructure;

namespace Sims4ModUpdater.ViewModels;

public sealed class ModItemViewModel : ObservableObject
{
    private bool _isChecked;
    private bool _hasChecked;
    private bool _isUpdating;
    private bool _isPinned;
    private bool _isSelectedInExplorer;
    private ModCheckResult? _checkResult;
    private ModMetadata? _cachedMetadata;
    private string _localVersionDisplay;
    private string _latestVersion = "-";
    private ModUpdateStatusCode _statusCode = ModUpdateStatusCode.ManualCheck;
    private string _statusLabel = "조회 전";
    private string _pageUrl = string.Empty;
    private string _downloadUrl = string.Empty;
    private string _matchedCreator = string.Empty;
    private string _verificationNote = string.Empty;
    private string _releaseNotes = string.Empty;
    private string _creatorName = CreatorOrganizerService.UnknownCreatorLabel;
    private bool _isCreatorKnown;
    private bool _isCreatorOverride;
    private string _creatorSourceLabel = "미분류";
    private string _autoDetectedCreator = string.Empty;
    private bool _hasDuplicateWarning;
    private int _duplicateFileCount;
    private BitmapSource? _thumbnailImage;
    private bool _isThumbnailChecked;
    private ModFunctionalCategory _functionalCategory = ModFunctionalCategory.Pending;
    private string _functionalSubCategory = "분석 중";
    private string _functionalReason = string.Empty;

    public ModItemViewModel(ModUnit unit, string? pinnedUrl)
    {
        Unit = unit;
        Query = ModUpdaterService.CreateQueryFromUnit(unit);

        string extracted = VersionUtils.ExtractVersionFromFilename(unit.PrimaryFileName);
        _localVersionDisplay = string.IsNullOrEmpty(extracted) ? "버전 미표기" : extracted;

        if (!string.IsNullOrWhiteSpace(pinnedUrl))
        {
            _isPinned = true;
            _pageUrl = pinnedUrl;
        }
    }

    public ModUnit Unit { get; }
    public ModQuery Query { get; private set; }

    public string Name => Unit.DisplayName;
    public string PrimaryFileName => Unit.PrimaryFileName;
    public string PrimaryFilePath => Unit.PrimaryFilePath;
    public string FolderName => Unit.FolderName;
    public string DirectoryPath => Unit.DirectoryPath;
    public string ModType => string.IsNullOrWhiteSpace(Unit.ModType) ? "-" : Unit.ModType;
    public bool IsStandalone => Unit.IsStandalone;
    public IReadOnlyList<string> UnitFiles => Unit.UnitFiles;
    public string FileCountBadge => Unit.UnitFiles.Count > 1 ? $"{Unit.UnitFiles.Count}개 묶음" : "단일";

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    public bool HasChecked
    {
        get => _hasChecked;
        private set
        {
            if (SetProperty(ref _hasChecked, value))
                OnPropertyChanged(nameof(CanDownloadUpdate));
        }
    }

    public bool IsUpdating
    {
        get => _isUpdating;
        set
        {
            if (SetProperty(ref _isUpdating, value))
                OnPropertyChanged(nameof(CanDownloadUpdate));
        }
    }

    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    public ModCheckResult? CheckResult
    {
        get => _checkResult;
        private set => SetProperty(ref _checkResult, value);
    }

    public string LocalVersionDisplay
    {
        get => _localVersionDisplay;
        private set => SetProperty(ref _localVersionDisplay, value);
    }

    public string LatestVersion
    {
        get => _latestVersion;
        private set => SetProperty(ref _latestVersion, value);
    }

    public ModUpdateStatusCode StatusCode
    {
        get => _statusCode;
        private set
        {
            if (SetProperty(ref _statusCode, value))
                OnPropertyChanged(nameof(CanDownloadUpdate));
        }
    }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => SetProperty(ref _statusLabel, value);
    }

    public string PageUrl
    {
        get => _pageUrl;
        private set
        {
            if (SetProperty(ref _pageUrl, value))
                OnPropertyChanged(nameof(HasPageUrl));
        }
    }

    public string DownloadUrl
    {
        get => _downloadUrl;
        private set
        {
            if (SetProperty(ref _downloadUrl, value))
                OnPropertyChanged(nameof(CanDownloadUpdate));
        }
    }

    public string MatchedCreator
    {
        get => _matchedCreator;
        private set => SetProperty(ref _matchedCreator, value);
    }

    public string VerificationNote
    {
        get => _verificationNote;
        private set => SetProperty(ref _verificationNote, value);
    }

    public string ReleaseNotes
    {
        get => _releaseNotes;
        private set => SetProperty(ref _releaseNotes, value);
    }

    public bool HasPageUrl => !string.IsNullOrWhiteSpace(PageUrl);

    public bool CanDownloadUpdate =>
        !IsUpdating &&
        !string.IsNullOrWhiteSpace(DownloadUrl) &&
        HasChecked &&
        StatusCode is not (ModUpdateStatusCode.Paid or ModUpdateStatusCode.EarlyAccess);

    public IReadOnlyList<CatalogLink> CandidateLinks => CheckResult?.CandidateLinks ?? (IReadOnlyList<CatalogLink>)Array.Empty<CatalogLink>();
    public IReadOnlyList<VariantChoice> VariantChoices => CheckResult?.VariantChoices ?? (IReadOnlyList<VariantChoice>)Array.Empty<VariantChoice>();
    public ModMetadata? ExtractedMeta => CheckResult?.ExtractedMeta;

    public string MatchStageDisplay =>
        CheckResult?.FinderDiagnostics is { MatchStage.Length: > 0 } fd
            ? fd.MatchStage
            : (HasChecked ? "web_or_catalog" : "-");

    public string FailureReasonDisplay =>
        CheckResult?.FinderDiagnostics is { FailureReason.Length: > 0 } fd
            ? fd.FailureReason
            : (CheckResult?.ErrorMessage ?? string.Empty);

    public void ApplyCheckResult(ModCheckResult result, bool isPinnedNow)
    {
        CheckResult = result;
        HasChecked = true;
        IsPinned = isPinnedNow || result.IsPinned;
        LocalVersionDisplay = string.IsNullOrWhiteSpace(result.LocalVersionDisplay) ? "버전 미표기" : result.LocalVersionDisplay;
        LatestVersion = string.IsNullOrWhiteSpace(result.LatestVersion) ? "-" : result.LatestVersion;
        StatusCode = result.StatusCode;
        StatusLabel = !string.IsNullOrWhiteSpace(result.StatusLabel) ? result.StatusLabel : result.StatusDisplay;
        PageUrl = result.PageUrl ?? string.Empty;
        DownloadUrl = result.DownloadUrl ?? string.Empty;
        MatchedCreator = result.MatchedCreator ?? string.Empty;
        VerificationNote = result.VerificationNote ?? string.Empty;
        ReleaseNotes = result.ReleaseNotes ?? string.Empty;

        OnPropertyChanged(nameof(CandidateLinks));
        OnPropertyChanged(nameof(VariantChoices));
        OnPropertyChanged(nameof(ExtractedMeta));
        OnPropertyChanged(nameof(MatchStageDisplay));
        OnPropertyChanged(nameof(FailureReasonDisplay));
    }

    public void ApplyPinnedUrl(string? pinnedUrl)
    {
        IsPinned = !string.IsNullOrWhiteSpace(pinnedUrl);
        if (!string.IsNullOrWhiteSpace(pinnedUrl))
        {
            PageUrl = pinnedUrl;
            Query.Homepage = pinnedUrl;
        }
        else
        {
            Query.Homepage = string.Empty;
        }
    }

    public void MarkUpdated(string? installedLatestVersion)
    {
        if (!string.IsNullOrWhiteSpace(installedLatestVersion) && installedLatestVersion != "-")
        {
            LocalVersionDisplay = installedLatestVersion;
            LatestVersion = installedLatestVersion;
            Query.CurrentVersion = installedLatestVersion;
        }
        StatusCode = ModUpdateStatusCode.UpToDate;
        StatusLabel = "최신 상태 (업데이트 완료)";
        VerificationNote = $"방금 업데이트 적용 완료 ({DateTime.Now:HH:mm:ss})";
        IsChecked = false;
    }

    public void RefreshQueryFromDisk()
    {
        Query = ModUpdaterService.CreateQueryFromUnit(Unit);
    }

    public List<string> GetUnitFileDisplayNames()
        => Unit.UnitFiles.Select(p => Path.GetFileName(p)).Where(n => !string.IsNullOrEmpty(n)).ToList();

    public bool IsSelectedInExplorer
    {
        get => _isSelectedInExplorer;
        set => SetProperty(ref _isSelectedInExplorer, value);
    }

    public ModMetadata? CachedMetadata
    {
        get => _cachedMetadata;
        set => SetProperty(ref _cachedMetadata, value);
    }

    public string CreatorName
    {
        get => _creatorName;
        private set => SetProperty(ref _creatorName, value);
    }

    public bool IsCreatorKnown
    {
        get => _isCreatorKnown;
        private set => SetProperty(ref _isCreatorKnown, value);
    }

    public bool IsCreatorOverride
    {
        get => _isCreatorOverride;
        private set => SetProperty(ref _isCreatorOverride, value);
    }

    public string CreatorSourceLabel
    {
        get => _creatorSourceLabel;
        private set => SetProperty(ref _creatorSourceLabel, value);
    }

    public string AutoDetectedCreator
    {
        get => _autoDetectedCreator;
        private set => SetProperty(ref _autoDetectedCreator, value);
    }

    public void ApplyCreatorResolution(CreatorResolutionResult resolution)
    {
        CreatorName = resolution.Creator;
        IsCreatorKnown = resolution.IsKnown;
        IsCreatorOverride = resolution.IsOverride;
        CreatorSourceLabel = resolution.SourceDisplayLabel;
        AutoDetectedCreator = resolution.AutoDetectedCreator;
    }

    public bool HasDuplicateWarning
    {
        get => _hasDuplicateWarning;
        set
        {
            if (SetProperty(ref _hasDuplicateWarning, value))
                OnPropertyChanged(nameof(DuplicateBadge));
        }
    }

    public int DuplicateFileCount
    {
        get => _duplicateFileCount;
        set
        {
            if (SetProperty(ref _duplicateFileCount, value))
                OnPropertyChanged(nameof(DuplicateBadge));
        }
    }

    public string DuplicateBadge => HasDuplicateWarning ? "중복" : string.Empty;

    public bool IsScriptUnit =>
        Unit.UnitFiles.Count > 0
            ? Unit.UnitFiles.All(f => f.EndsWith(".ts4script", StringComparison.OrdinalIgnoreCase))
            : PrimaryFilePath.EndsWith(".ts4script", StringComparison.OrdinalIgnoreCase);

    public BitmapSource? ThumbnailImage
    {
        get => _thumbnailImage;
        private set
        {
            if (SetProperty(ref _thumbnailImage, value))
                OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnailImage is not null;

    public bool IsThumbnailChecked
    {
        get => _isThumbnailChecked;
        private set => SetProperty(ref _isThumbnailChecked, value);
    }

    public void SetThumbnail(BitmapSource? bitmap)
    {
        IsThumbnailChecked = true;
        ThumbnailImage = bitmap;
    }

    public ModFunctionalCategory FunctionalCategory
    {
        get => _functionalCategory;
        private set
        {
            if (SetProperty(ref _functionalCategory, value))
            {
                OnPropertyChanged(nameof(FunctionalCategoryShortLabel));
                OnPropertyChanged(nameof(FunctionalCategoryFullLabel));
            }
        }
    }

    public string FunctionalSubCategory
    {
        get => _functionalSubCategory;
        private set
        {
            if (SetProperty(ref _functionalSubCategory, value))
                OnPropertyChanged(nameof(FunctionalCategoryFullLabel));
        }
    }

    public string FunctionalReason
    {
        get => _functionalReason;
        private set => SetProperty(ref _functionalReason, value);
    }

    public string FunctionalCategoryShortLabel => _functionalCategory switch
    {
        ModFunctionalCategory.ScriptCoreMod => "1. ts4script",
        ModFunctionalCategory.XmlGameplayMod => "2. XML 튜닝",
        ModFunctionalCategory.ObjectModAndCc => "3. 오브젝트+CC",
        ModFunctionalCategory.AnimationAndPose => "4. 애니/포즈",
        _ => "분석 중"
    };

    public string FunctionalCategoryFullLabel => _functionalCategory switch
    {
        ModFunctionalCategory.ScriptCoreMod => "1번. ts4script 기반 핵심 모드",
        ModFunctionalCategory.XmlGameplayMod => "2번. XML 튜닝 기반 게임 플레이 모드",
        ModFunctionalCategory.ObjectModAndCc => "3번. 작동 기능이 있는 오브젝트 모드 + CC",
        ModFunctionalCategory.AnimationAndPose => "4번. 애니메이션 / 포즈",
        _ => "구조 분석 대기 중"
    };

    public void ApplyFunctionalClassification(ModCategoryClassification classification)
    {
        ArgumentNullException.ThrowIfNull(classification);
        FunctionalCategory = classification.Category;
        FunctionalSubCategory = string.IsNullOrWhiteSpace(classification.SubCategory)
            ? FunctionalCategoryShortLabel
            : classification.SubCategory;
        FunctionalReason = classification.Reason ?? string.Empty;
    }
}

public sealed class CreatorGroupViewModel : ObservableObject
{
    private bool _isExpanded;

    public CreatorGroupViewModel(string creatorName, bool isUnknownGroup, IEnumerable<ModItemViewModel> mods, bool isExpanded = false)
    {
        CreatorName = creatorName;
        IsUnknownGroup = isUnknownGroup;
        Mods = new ObservableCollection<ModItemViewModel>(mods);
        _isExpanded = isExpanded;
    }

    public string CreatorName { get; }
    public bool IsUnknownGroup { get; }
    public ObservableCollection<ModItemViewModel> Mods { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public int ModCount => Mods.Count;
    public int TotalFileCount => Mods.Sum(m => m.Unit.UnitFiles.Count);
    public int OverrideCount => Mods.Count(m => m.IsCreatorOverride);
    public bool HasOverrides => OverrideCount > 0;
    public int DuplicateCount => Mods.Count(m => m.HasDuplicateWarning);
    public bool HasDuplicates => DuplicateCount > 0;
    public string DuplicateSummaryBadge => DuplicateCount > 0 ? $"중복 {DuplicateCount}" : string.Empty;
    public string SummaryBadge => $"유닛 {ModCount}개 · 파일 {TotalFileCount}개";
    public string OverrideBadge => OverrideCount > 0 ? $"직접지정 {OverrideCount}" : string.Empty;
}

public sealed class ExplorerFolderItemViewModel
{
    public ExplorerFolderItemViewModel(ModFolderSummary summary)
    {
        Summary = summary;
    }

    public ModFolderSummary Summary { get; }
    public string Name => Summary.Name;
    public string FolderPath => Summary.Path;
    public int PackageCount => Summary.PackageCount;
    public int ScriptCount => Summary.ScriptCount;
    public int SubdirCount => Summary.SubdirCount;
    public string ModType => string.IsNullOrWhiteSpace(Summary.ModType) ? "-" : Summary.ModType;
    public bool IsUpdatable => Summary.IsUpdatable;
    public string CategoryBadge => Summary.IsUpdatable ? $"업데이트 대상 ({Summary.UnitCount} 유닛)" : "CC / 단순 패키지";
}

public sealed class PinnedManifestViewModel
{
    public string Name { get; init; } = string.Empty;
    public string LocalFile { get; init; } = string.Empty;
    public string InstallFolder { get; init; } = string.Empty;
    public string PinnedPageUrl { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = string.Empty;
}
