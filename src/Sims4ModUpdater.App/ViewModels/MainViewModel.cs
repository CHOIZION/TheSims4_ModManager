using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sims4ModUpdater.Core.Models;
using Sims4ModUpdater.Core.Services;
using Sims4ModUpdater.Infrastructure;

namespace Sims4ModUpdater.ViewModels;

public enum MainTab
{
    Explorer,
    Categories,
    Updates,
    Backups,
    Diagnostics,
    Settings
}

public enum CategoryFilterMode
{
    All,
    ScriptCoreMod,
    XmlGameplayMod,
    ObjectModAndCc,
    AnimationAndPose
}

public enum ExplorerViewMode
{
    ByCreator,
    Units,
    Folders
}

public enum CreatorDisplayMode
{
    ThumbnailGrid,
    CompactList
}

public enum ExplorerFilterMode
{
    All,
    UpdatableOnly,
    CcAndSimpleOnly
}

public enum UpdateFilterMode
{
    All,
    UpdateNeeded,
    ManualCheck,
    PaidOrEarly,
    LinkNotFound,
    UpToDate
}

public enum UpdateScopeMode
{
    ScriptCoreOnly,
    ScriptAndXml,
    AllMods
}

public interface IAppDialogService
{
    void ShowInfo(string title, string message);
    void ShowError(string title, string message);
    bool Confirm(string title, string message);
    string? BrowseFolder(string title, string initialPath);
    string? SaveTextFile(string title, string defaultFileName, string filter);
    void OpenUrl(string url);
    void OpenPathInExplorer(string path);
    void CopyToClipboard(string text);
    (bool Confirmed, bool RemovePin, string Url)? PromptPinUrl(string modName, string currentUrl, bool isCurrentlyPinned);
    VariantChoice? PromptVariantChoice(string modName, IReadOnlyList<VariantChoice> choices);
    (bool Confirmed, OverwriteMode Mode, bool ApplyToBatch) PromptOverwriteMode(string modName, bool isBatch);
    (bool Confirmed, bool RevertToAuto, string CreatorName)? PromptAssignCreator(
        string targetTitle,
        string currentCreator,
        string autoHint,
        IReadOnlyList<string> existingCreators,
        bool isGroupRename,
        bool canRevert);
    (bool Confirmed, OrganizePlanSummary? Plan, bool CreateBackup) PromptOrganizeByCreator(
        Func<CreatorOrganizeLayout, bool, bool, OrganizePlanSummary> planBuilder,
        bool isTargetedFolderMode = false,
        string? targetFolderDisplay = null);
    (bool Confirmed, DuplicateCleanupPlan? Plan, IReadOnlyList<DuplicateDeletePlanItem> SelectedItems, bool CreateBackup) PromptDeleteDuplicates(
        Func<bool, DuplicateCleanupPlan> planBuilder,
        DuplicateCleanupPlan? initialPlan = null);
}

public sealed class MainViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly CatalogService _catalogService;
    private readonly ModMetadataExtractor _metadataExtractor;
    private readonly DuplicateCleanupService _duplicateCleanupService = new();
    private readonly IAppDialogService _dialogs;

    private AppConfig _config = new();
    private ModScannerService? _scanner;
    private BackupService? _backupService;
    private ModUpdaterService? _updaterService;
    private CreatorOrganizerService? _creatorService;
    private CancellationTokenSource? _activeCts;
    private CancellationTokenSource? _metadataEnrichCts;

    private MainTab _currentTab = MainTab.Explorer;
    private bool _isBusy;
    private bool _isIndeterminate;
    private int _progressValue;
    private int _progressMaximum = 100;
    private string _statusMessage = "준비 완료";

    // Explorer state
    private ExplorerViewMode _explorerView = ExplorerViewMode.ByCreator;
    private CreatorDisplayMode _creatorDisplay = CreatorDisplayMode.ThumbnailGrid;
    private bool _showUnknownCreatorOnly;
    private bool _showDuplicateModsOnly;
    private int _duplicateModCount;
    private bool _isResolvingCreators;
    private string _explorerSearchText = string.Empty;
    private BitmapSource? _selectedUnitThumbnail;
    private readonly Dictionary<string, BitmapSource?> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _thumbnailLoadCts;
    private bool _suppressGroupExpandThumbnailLoad;
    private ExplorerFilterMode _explorerFilter = ExplorerFilterMode.All;
    private ModItemViewModel? _selectedExplorerUnit;
    private ExplorerFolderItemViewModel? _selectedExplorerFolder;
    private ModMetadata? _selectedUnitMetadata;
    private ModInstalledInfo? _selectedDuplicateInfo;
    private readonly ObservableCollection<string> _selectedInspectorFiles = new();
    private readonly HashSet<string> _expandedCreators = new(StringComparer.OrdinalIgnoreCase);

    // Summary counters
    private int _totalFileCount;
    private int _packageCount;
    private int _scriptCount;
    private int _updatableUnitCount;
    private int _folderCount;
    private int _creatorGroupCount;
    private int _updateNeededCount;
    private int _checkedCount;

    // Categories state
    private CategoryFilterMode _categoryFilter = CategoryFilterMode.All;
    private string _categorySearchText = string.Empty;
    private ModItemViewModel? _selectedCategoryMod;
    private BitmapSource? _selectedCategoryThumbnail;
    private ModMetadata? _selectedCategoryMetadata;
    private ModInstalledInfo? _selectedCategoryDuplicateInfo;
    private readonly ObservableCollection<string> _selectedCategoryInspectorFiles = new();
    private int _categoryScriptCount;
    private int _categoryXmlCount;
    private int _categoryObjectCcCount;
    private int _categoryAnimationCount;
    private int _categoryPendingCount;
    private int _excludedCcPackageCount;

    // Updates state
    private string _updateSearchText = string.Empty;
    private UpdateScopeMode _updateScope = UpdateScopeMode.ScriptCoreOnly;
    private UpdateFilterMode _updateFilter = UpdateFilterMode.All;
    private ModItemViewModel? _selectedUpdateMod;

    // Backups state
    private BackupSessionInfo? _selectedBackup;

    // Diagnostics state
    private ScanDiagnostics? _scanDiagnostics;
    private string _diagnosticReportText = string.Empty;
    private string _lastReportPath = string.Empty;

    // Catalog & Settings state
    private string _catalogSearchText = string.Empty;
    private string _modsFolderDraft = string.Empty;
    private string _backupFolderDraft = string.Empty;
    private bool _autoBackupDraft = true;
    private int _maxConcurrentChecksDraft = 4;

    public MainViewModel(IAppDialogService dialogs, string? baseDirectory = null)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _configService = new ConfigService(baseDirectory);
        _catalogService = new CatalogService();
        _metadataExtractor = new ModMetadataExtractor(_catalogService);

        AllModUnits = new ObservableCollection<ModItemViewModel>();
        FilteredCreatorGroups = new ObservableCollection<CreatorGroupViewModel>();
        FilteredExplorerUnits = new ObservableCollection<ModItemViewModel>();
        AllFolderSummaries = new ObservableCollection<ExplorerFolderItemViewModel>();
        FilteredExplorerFolders = new ObservableCollection<ExplorerFolderItemViewModel>();
        FilteredCategoryMods = new ObservableCollection<ModItemViewModel>();
        FilteredUpdateMods = new ObservableCollection<ModItemViewModel>();
        DepthWarnings = new ObservableCollection<string>();
        BackupSessions = new ObservableCollection<BackupSessionInfo>();
        FilteredCatalogItems = new ObservableCollection<CatalogItem>();
        PinnedEntries = new ObservableCollection<PinnedManifestViewModel>();

        RefreshScanCommand = new RelayCommand(ReloadModsData, () => !IsBusy);
        ExpandAllCreatorsCommand = new RelayCommand(() => SetAllCreatorGroupsExpanded(true));
        CollapseAllCreatorsCommand = new RelayCommand(() => SetAllCreatorGroupsExpanded(false));
        SelectExplorerModCommand = new RelayCommand(p => { if (p is ModItemViewModel m) SelectedExplorerUnit = m; });
        AssignCreatorCommand = new RelayCommand(p => AssignCreatorForMod(p as ModItemViewModel ?? (CurrentTab == MainTab.Categories ? SelectedCategoryMod : SelectedExplorerUnit)));
        RenameCreatorGroupCommand = new RelayCommand(p => RenameCreatorGroup(p as CreatorGroupViewModel));
        OpenOrganizeByCreatorCommand = new AsyncRelayCommand(OpenOrganizeByCreatorAsync, () => !IsBusy && AllModUnits.Count > 0, HandleCommandError);
        OrganizeTargetFolderCommand = new AsyncRelayCommand(OrganizeTargetFolderAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(_config.ModsFolder) && Directory.Exists(_config.ModsFolder), HandleCommandError);
        DeleteDuplicatesCommand = new AsyncRelayCommand(DeleteDuplicatesAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(_config.ModsFolder) && Directory.Exists(_config.ModsFolder), HandleCommandError);
        SetCategoryFilterCommand = new RelayCommand(SetCategoryFilterFromCommand);
        ToggleCategoryKpiCommand = new RelayCommand(ToggleCategoryKpiFromCommand);
        CheckFilteredCategoryCommand = new AsyncRelayCommand(CheckFilteredCategoryAsync, () => !IsBusy && FilteredCategoryMods.Count > 0, HandleCommandError);
        OpenModInExplorerTabCommand = new RelayCommand(p => NavigateToModInExplorer(p as ModItemViewModel ?? SelectedCategoryMod));
        CheckAllUpdatesCommand = new AsyncRelayCommand(CheckAllUpdatesAsync, () => !IsBusy && ScopedUpdateCount > 0, HandleCommandError);
        CheckSelectedUpdatesCommand = new AsyncRelayCommand(CheckSelectedUpdatesAsync, () => !IsBusy && GetScopedUpdateMods().Any(m => m.IsChecked), HandleCommandError);
        CheckSingleModCommand = new AsyncRelayCommand(p => CheckSingleModAsync(p as ModItemViewModel ?? (CurrentTab == MainTab.Categories ? SelectedCategoryMod : SelectedExplorerUnit)), _ => !IsBusy, HandleCommandError);
        UpdateSingleModCommand = new AsyncRelayCommand(p => UpdateSingleModAsync(p as ModItemViewModel ?? SelectedUpdateMod), _ => !IsBusy, HandleCommandError);
        UpdateSelectedModsCommand = new AsyncRelayCommand(UpdateSelectedModsAsync, () => !IsBusy && GetScopedUpdateMods().Any(m => m.IsChecked && m.CanDownloadUpdate), HandleCommandError);
        CancelOperationCommand = new RelayCommand(CancelActiveOperation, () => IsBusy && _activeCts is not null);

        SelectAllUpdatesCommand = new RelayCommand(() => SetAllFilteredSelection(true));
        ClearAllUpdatesCommand = new RelayCommand(() => SetAllFilteredSelection(false));
        SelectUpdatableOnlyCommand = new RelayCommand(SelectUpdatableItems);

        PinUrlCommand = new RelayCommand(p => PinUrlForMod(p as ModItemViewModel ?? (CurrentTab == MainTab.Categories ? SelectedCategoryMod : SelectedUpdateMod ?? SelectedExplorerUnit)));
        OpenPageUrlCommand = new RelayCommand(p => OpenModPageUrl(p as ModItemViewModel ?? (CurrentTab == MainTab.Categories ? SelectedCategoryMod : SelectedUpdateMod ?? SelectedExplorerUnit)));
        OpenUrlDirectCommand = new RelayCommand(p => { if (p is string u && !string.IsNullOrWhiteSpace(u)) _dialogs.OpenUrl(u); });
        OpenInExplorerCommand = new RelayCommand(OpenSelectedModInExplorer);

        CreateManualBackupCommand = new AsyncRelayCommand(CreateManualBackupAsync, () => !IsBusy, HandleCommandError);
        RestoreBackupCommand = new AsyncRelayCommand(RestoreSelectedBackupAsync, () => !IsBusy && SelectedBackup is not null, HandleCommandError);
        DeleteBackupCommand = new RelayCommand(DeleteSelectedBackup, () => !IsBusy && SelectedBackup is not null);
        RefreshBackupsCommand = new RelayCommand(RefreshBackupsList);
        OpenBackupsFolderCommand = new RelayCommand(OpenBackupsFolder);

        GenerateDiagnosticsCommand = new RelayCommand(BuildDiagnosticReport);
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnosticReport, () => !string.IsNullOrWhiteSpace(DiagnosticReportText));
        SaveDiagnosticsCommand = new RelayCommand(SaveDiagnosticReport, () => !string.IsNullOrWhiteSpace(DiagnosticReportText));

        DetectModsFolderCommand = new RelayCommand(AutoDetectModsFolder);
        BrowseModsFolderCommand = new RelayCommand(BrowseModsFolder);
        BrowseBackupFolderCommand = new RelayCommand(BrowseBackupFolder);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        RemoveManifestPinCommand = new RelayCommand(p => RemovePinnedManifestEntry(p as PinnedManifestViewModel));

        Initialize();
    }

    public ObservableCollection<ModItemViewModel> AllModUnits { get; }
    public ObservableCollection<CreatorGroupViewModel> FilteredCreatorGroups { get; }
    public ObservableCollection<ModItemViewModel> FilteredExplorerUnits { get; }
    public ObservableCollection<ExplorerFolderItemViewModel> AllFolderSummaries { get; }
    public ObservableCollection<ExplorerFolderItemViewModel> FilteredExplorerFolders { get; }
    public ObservableCollection<ModItemViewModel> FilteredCategoryMods { get; }
    public ObservableCollection<string> SelectedCategoryInspectorFiles => _selectedCategoryInspectorFiles;
    public ObservableCollection<ModItemViewModel> FilteredUpdateMods { get; }
    public ObservableCollection<string> DepthWarnings { get; }
    public ObservableCollection<string> SelectedInspectorFiles => _selectedInspectorFiles;
    public ObservableCollection<BackupSessionInfo> BackupSessions { get; }
    public ObservableCollection<CatalogItem> FilteredCatalogItems { get; }
    public ObservableCollection<PinnedManifestViewModel> PinnedEntries { get; }

    public RelayCommand RefreshScanCommand { get; }
    public RelayCommand ExpandAllCreatorsCommand { get; }
    public RelayCommand CollapseAllCreatorsCommand { get; }
    public RelayCommand SelectExplorerModCommand { get; }
    public RelayCommand AssignCreatorCommand { get; }
    public RelayCommand RenameCreatorGroupCommand { get; }
    public AsyncRelayCommand OpenOrganizeByCreatorCommand { get; }
    public AsyncRelayCommand OrganizeTargetFolderCommand { get; }
    public AsyncRelayCommand DeleteDuplicatesCommand { get; }
    public RelayCommand SetCategoryFilterCommand { get; }
    public RelayCommand ToggleCategoryKpiCommand { get; }
    public AsyncRelayCommand CheckFilteredCategoryCommand { get; }
    public RelayCommand OpenModInExplorerTabCommand { get; }
    public AsyncRelayCommand CheckAllUpdatesCommand { get; }
    public AsyncRelayCommand CheckSelectedUpdatesCommand { get; }
    public AsyncRelayCommand CheckSingleModCommand { get; }
    public AsyncRelayCommand UpdateSingleModCommand { get; }
    public AsyncRelayCommand UpdateSelectedModsCommand { get; }
    public RelayCommand CancelOperationCommand { get; }
    public RelayCommand SelectAllUpdatesCommand { get; }
    public RelayCommand ClearAllUpdatesCommand { get; }
    public RelayCommand SelectUpdatableOnlyCommand { get; }
    public RelayCommand PinUrlCommand { get; }
    public RelayCommand OpenPageUrlCommand { get; }
    public RelayCommand OpenUrlDirectCommand { get; }
    public RelayCommand OpenInExplorerCommand { get; }
    public AsyncRelayCommand CreateManualBackupCommand { get; }
    public AsyncRelayCommand RestoreBackupCommand { get; }
    public RelayCommand DeleteBackupCommand { get; }
    public RelayCommand RefreshBackupsCommand { get; }
    public RelayCommand OpenBackupsFolderCommand { get; }
    public RelayCommand GenerateDiagnosticsCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }
    public RelayCommand SaveDiagnosticsCommand { get; }
    public RelayCommand DetectModsFolderCommand { get; }
    public RelayCommand BrowseModsFolderCommand { get; }
    public RelayCommand BrowseBackupFolderCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand RemoveManifestPinCommand { get; }

    public MainTab CurrentTab
    {
        get => _currentTab;
        set => SetProperty(ref _currentTab, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }

    public int ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public int ProgressMaximum
    {
        get => _progressMaximum;
        private set => SetProperty(ref _progressMaximum, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ExplorerViewMode ExplorerView
    {
        get => _explorerView;
        set
        {
            if (SetProperty(ref _explorerView, value))
                ApplyExplorerFilters();
        }
    }

    public bool ShowUnknownCreatorOnly
    {
        get => _showUnknownCreatorOnly;
        set
        {
            if (SetProperty(ref _showUnknownCreatorOnly, value))
                ApplyExplorerFilters();
        }
    }

    public CreatorDisplayMode CreatorDisplay
    {
        get => _creatorDisplay;
        set
        {
            if (SetProperty(ref _creatorDisplay, value))
            {
                OnPropertyChanged(nameof(IsThumbnailCreatorView));
                OnPropertyChanged(nameof(IsCompactCreatorView));
                if (IsThumbnailCreatorView)
                    LoadVisibleExpandedThumbnails();
            }
        }
    }

    public bool IsThumbnailCreatorView => _creatorDisplay == CreatorDisplayMode.ThumbnailGrid;

    public bool IsCompactCreatorView => _creatorDisplay == CreatorDisplayMode.CompactList;

    public bool ShowDuplicateModsOnly
    {
        get => _showDuplicateModsOnly;
        set
        {
            if (SetProperty(ref _showDuplicateModsOnly, value))
                ApplyExplorerFilters();
        }
    }

    public int DuplicateModCount
    {
        get => _duplicateModCount;
        private set
        {
            if (SetProperty(ref _duplicateModCount, value))
                OnPropertyChanged(nameof(DuplicateFilterLabel));
        }
    }

    public string DuplicateFilterLabel =>
        DuplicateModCount > 0 ? $"중복만 보기 ({DuplicateModCount})" : "중복만 보기";

    public BitmapSource? SelectedUnitThumbnail
    {
        get => _selectedUnitThumbnail;
        private set => SetProperty(ref _selectedUnitThumbnail, value);
    }

    public bool IsResolvingCreators
    {
        get => _isResolvingCreators;
        private set => SetProperty(ref _isResolvingCreators, value);
    }

    public int CreatorGroupCount
    {
        get => _creatorGroupCount;
        private set => SetProperty(ref _creatorGroupCount, value);
    }

    public string ExplorerSearchText
    {
        get => _explorerSearchText;
        set
        {
            if (SetProperty(ref _explorerSearchText, value))
                ApplyExplorerFilters();
        }
    }

    public ExplorerFilterMode ExplorerFilter
    {
        get => _explorerFilter;
        set
        {
            if (SetProperty(ref _explorerFilter, value))
                ApplyExplorerFilters();
        }
    }

    public ModItemViewModel? SelectedExplorerUnit
    {
        get => _selectedExplorerUnit;
        set
        {
            if (_selectedExplorerUnit is not null)
                _selectedExplorerUnit.IsSelectedInExplorer = false;

            if (SetProperty(ref _selectedExplorerUnit, value))
            {
                if (value is not null)
                {
                    value.IsSelectedInExplorer = true;
                    InspectUnitDetails(value);
                }
                else
                {
                    SelectedUnitThumbnail = null;
                }
            }
        }
    }

    public ExplorerFolderItemViewModel? SelectedExplorerFolder
    {
        get => _selectedExplorerFolder;
        set
        {
            if (SetProperty(ref _selectedExplorerFolder, value) && value is not null)
                InspectFolderDetails(value);
        }
    }

    public ModMetadata? SelectedUnitMetadata
    {
        get => _selectedUnitMetadata;
        private set
        {
            if (SetProperty(ref _selectedUnitMetadata, value))
            {
                OnPropertyChanged(nameof(SelectedMetadataCreatorsText));
                OnPropertyChanged(nameof(SelectedMetadataUrlsText));
                OnPropertyChanged(nameof(SelectedMetadataDisplayNamesText));
                OnPropertyChanged(nameof(SelectedMetadataModulesText));
            }
        }
    }

    public ModInstalledInfo? SelectedDuplicateInfo
    {
        get => _selectedDuplicateInfo;
        private set => SetProperty(ref _selectedDuplicateInfo, value);
    }

    public string SelectedMetadataCreatorsText => FormatMetadataCreators(SelectedUnitMetadata);
    public string SelectedMetadataUrlsText => FormatMetadataUrls(SelectedUnitMetadata);
    public string SelectedMetadataDisplayNamesText => FormatMetadataDisplayNames(SelectedUnitMetadata);
    public string SelectedMetadataModulesText => FormatMetadataModules(SelectedUnitMetadata);

    public string SelectedCategoryMetadataCreatorsText => FormatMetadataCreators(SelectedCategoryMetadata);
    public string SelectedCategoryMetadataUrlsText => FormatMetadataUrls(SelectedCategoryMetadata);
    public string SelectedCategoryMetadataDisplayNamesText => FormatMetadataDisplayNames(SelectedCategoryMetadata);
    public string SelectedCategoryMetadataModulesText => FormatMetadataModules(SelectedCategoryMetadata);

    private static string FormatMetadataCreators(ModMetadata? meta) =>
        meta is null
            ? "-"
            : (meta.Creators.Count > 0
                ? string.Join(", ", meta.Creators)
                : (!string.IsNullOrWhiteSpace(meta.InferredCreator) ? meta.InferredCreator : "감지 안 됨"));

    private static string FormatMetadataUrls(ModMetadata? meta) =>
        meta is { Urls.Count: > 0 } ? string.Join(Environment.NewLine, meta.Urls) : "내장 URL 없음";

    private static string FormatMetadataDisplayNames(ModMetadata? meta) =>
        meta is { DisplayNames.Count: > 0 } ? string.Join(", ", meta.DisplayNames) : "-";

    private static string FormatMetadataModules(ModMetadata? meta) =>
        meta is null
            ? "-"
            : (meta.Modules.Count > 0
                ? string.Join(", ", meta.Modules)
                : (meta.RootTunings.Count > 0 ? string.Join(", ", meta.RootTunings) : "-"));

    public int TotalFileCount { get => _totalFileCount; private set => SetProperty(ref _totalFileCount, value); }
    public int PackageCount { get => _packageCount; private set => SetProperty(ref _packageCount, value); }
    public int ScriptCount { get => _scriptCount; private set => SetProperty(ref _scriptCount, value); }
    public int UpdatableUnitCount { get => _updatableUnitCount; private set => SetProperty(ref _updatableUnitCount, value); }
    public int FolderCount { get => _folderCount; private set => SetProperty(ref _folderCount, value); }
    public int UpdateNeededCount { get => _updateNeededCount; private set => SetProperty(ref _updateNeededCount, value); }
    public int CheckedCount { get => _checkedCount; private set => SetProperty(ref _checkedCount, value); }

    public string ActiveModsFolderDisplay =>
        string.IsNullOrWhiteSpace(_config.ModsFolder) ? "(Mods 경로 미설정)" : _config.ModsFolder;

    public CategoryFilterMode CategoryFilter
    {
        get => _categoryFilter;
        set
        {
            if (SetProperty(ref _categoryFilter, value))
                ApplyCategoryFilters(keepSelection: true);
        }
    }

    public string CategorySearchText
    {
        get => _categorySearchText;
        set
        {
            if (SetProperty(ref _categorySearchText, value))
                ApplyCategoryFilters(keepSelection: true);
        }
    }

    public ModItemViewModel? SelectedCategoryMod
    {
        get => _selectedCategoryMod;
        set
        {
            if (SetProperty(ref _selectedCategoryMod, value))
            {
                if (value is not null)
                    InspectCategoryModDetails(value);
                else
                {
                    SelectedCategoryThumbnail = null;
                    SelectedCategoryMetadata = null;
                    SelectedCategoryDuplicateInfo = null;
                    _selectedCategoryInspectorFiles.Clear();
                }
            }
        }
    }

    public BitmapSource? SelectedCategoryThumbnail
    {
        get => _selectedCategoryThumbnail;
        private set => SetProperty(ref _selectedCategoryThumbnail, value);
    }

    public ModMetadata? SelectedCategoryMetadata
    {
        get => _selectedCategoryMetadata;
        private set
        {
            if (SetProperty(ref _selectedCategoryMetadata, value))
            {
                OnPropertyChanged(nameof(SelectedCategoryMetadataCreatorsText));
                OnPropertyChanged(nameof(SelectedCategoryMetadataUrlsText));
                OnPropertyChanged(nameof(SelectedCategoryMetadataDisplayNamesText));
                OnPropertyChanged(nameof(SelectedCategoryMetadataModulesText));
            }
        }
    }

    public ModInstalledInfo? SelectedCategoryDuplicateInfo
    {
        get => _selectedCategoryDuplicateInfo;
        private set => SetProperty(ref _selectedCategoryDuplicateInfo, value);
    }

    public int CategoryScriptCount { get => _categoryScriptCount; private set => SetProperty(ref _categoryScriptCount, value); }
    public int CategoryXmlCount { get => _categoryXmlCount; private set => SetProperty(ref _categoryXmlCount, value); }
    public int CategoryObjectCcCount { get => _categoryObjectCcCount; private set => SetProperty(ref _categoryObjectCcCount, value); }
    public int CategoryAnimationCount { get => _categoryAnimationCount; private set => SetProperty(ref _categoryAnimationCount, value); }
    public int CategoryPendingCount { get => _categoryPendingCount; private set => SetProperty(ref _categoryPendingCount, value); }
    public int ExcludedCcPackageCount { get => _excludedCcPackageCount; private set => SetProperty(ref _excludedCcPackageCount, value); }

    public string CategoryAnalysisSummaryText
    {
        get
        {
            string excludedNote = ExcludedCcPackageCount > 0 ? $" · CC 전용 폴더 {ExcludedCcPackageCount}개 별도" : string.Empty;
            if (CategoryPendingCount > 0)
                return $"DBPF 구조 분석 중... (남은 항목 {CategoryPendingCount}개 / 스크립트 {CategoryScriptCount} · XML {CategoryXmlCount} · 오브젝트+CC {CategoryObjectCcCount} · 애니/포즈 {CategoryAnimationCount}){excludedNote}";

            return $"DBPF/스크립트 자동 분류 완료 · 1) 스크립트 {CategoryScriptCount} · 2) XML {CategoryXmlCount} · 3) 오브젝트+CC {CategoryObjectCcCount} · 4) 애니/포즈 {CategoryAnimationCount}{excludedNote}";
        }
    }

    public string CategoryFilteredCountLabel => $"표시 중 {FilteredCategoryMods.Count}개 / 전체 {AllModUnits.Count}개";

    public UpdateScopeMode UpdateScope
    {
        get => _updateScope;
        set
        {
            if (SetProperty(ref _updateScope, value))
            {
                ApplyUpdateFilters();
                NotifyUpdateScopeProperties();
            }
        }
    }

    public int UpdateScriptScopeCount =>
        AllModUnits.Count(m => m.FunctionalCategory == ModFunctionalCategory.ScriptCoreMod);

    public int UpdateScriptAndXmlScopeCount =>
        AllModUnits.Count(m => m.FunctionalCategory is ModFunctionalCategory.ScriptCoreMod or ModFunctionalCategory.XmlGameplayMod);

    public int ScopedUpdateCount => GetScopedUpdateMods().Count();

    public string UpdateScriptScopeChipLabel => $"1. ts4script 핵심만 ({UpdateScriptScopeCount})";
    public string UpdateScriptAndXmlScopeChipLabel => $"핵심 + XML 튜닝 ({UpdateScriptAndXmlScopeCount})";
    public string UpdateAllScopeChipLabel => $"전체 모드 ({AllModUnits.Count})";

    public string CheckAllUpdatesButtonLabel => UpdateScope switch
    {
        UpdateScopeMode.ScriptCoreOnly => $"핵심 모드 일괄 조회 ({ScopedUpdateCount}개)",
        UpdateScopeMode.ScriptAndXml => $"핵심+XML 일괄 조회 ({ScopedUpdateCount}개)",
        _ => $"전체 일괄 조회 ({ScopedUpdateCount}개)"
    };

    public string UpdatesScopeSummaryText => UpdateScope switch
    {
        UpdateScopeMode.ScriptCoreOnly => AllModUnits.Count > UpdateScriptScopeCount
            ? $"현재 대상: 1. ts4script 기반 핵심 모드 {UpdateScriptScopeCount}개만 조회 (CC·애니메이션 등 {AllModUnits.Count - UpdateScriptScopeCount}개 제외)"
            : $"현재 대상: 1. ts4script 기반 핵심 모드 {UpdateScriptScopeCount}개만 조회",
        UpdateScopeMode.ScriptAndXml => $"현재 대상: 1. ts4script + 2. XML 튜닝·번역 모드 {UpdateScriptAndXmlScopeCount}개 조회",
        _ => $"현재 대상: 전체 스캔 유닛 {AllModUnits.Count}개 조회 (CC·애니메이션 포함)"
    };

    public string UpdateFilteredCountLabel => $"목록 {FilteredUpdateMods.Count}개";

    private void NotifyUpdateScopeProperties()
    {
        OnPropertyChanged(nameof(UpdateScriptScopeCount));
        OnPropertyChanged(nameof(UpdateScriptAndXmlScopeCount));
        OnPropertyChanged(nameof(ScopedUpdateCount));
        OnPropertyChanged(nameof(UpdateScriptScopeChipLabel));
        OnPropertyChanged(nameof(UpdateScriptAndXmlScopeChipLabel));
        OnPropertyChanged(nameof(UpdateAllScopeChipLabel));
        OnPropertyChanged(nameof(CheckAllUpdatesButtonLabel));
        OnPropertyChanged(nameof(UpdatesScopeSummaryText));
        OnPropertyChanged(nameof(UpdateFilteredCountLabel));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    public string UpdateSearchText
    {
        get => _updateSearchText;
        set
        {
            if (SetProperty(ref _updateSearchText, value))
                ApplyUpdateFilters();
        }
    }

    public UpdateFilterMode UpdateFilter
    {
        get => _updateFilter;
        set
        {
            if (SetProperty(ref _updateFilter, value))
                ApplyUpdateFilters();
        }
    }

    public ModItemViewModel? SelectedUpdateMod
    {
        get => _selectedUpdateMod;
        set => SetProperty(ref _selectedUpdateMod, value);
    }

    public BackupSessionInfo? SelectedBackup
    {
        get => _selectedBackup;
        set => SetProperty(ref _selectedBackup, value);
    }

    public ScanDiagnostics? ScanDiagnostics
    {
        get => _scanDiagnostics;
        private set => SetProperty(ref _scanDiagnostics, value);
    }

    public string DiagnosticReportText
    {
        get => _diagnosticReportText;
        private set => SetProperty(ref _diagnosticReportText, value);
    }

    public string LastReportPath
    {
        get => _lastReportPath;
        private set => SetProperty(ref _lastReportPath, value);
    }

    public string CatalogSearchText
    {
        get => _catalogSearchText;
        set
        {
            if (SetProperty(ref _catalogSearchText, value))
                ApplyCatalogFilter();
        }
    }

    public string ModsFolderDraft
    {
        get => _modsFolderDraft;
        set => SetProperty(ref _modsFolderDraft, value);
    }

    public string BackupFolderDraft
    {
        get => _backupFolderDraft;
        set => SetProperty(ref _backupFolderDraft, value);
    }

    public bool AutoBackupDraft
    {
        get => _autoBackupDraft;
        set => SetProperty(ref _autoBackupDraft, value);
    }

    public int MaxConcurrentChecksDraft
    {
        get => _maxConcurrentChecksDraft;
        set => SetProperty(ref _maxConcurrentChecksDraft, Math.Clamp(value, 1, 8));
    }

    private void Initialize()
    {
        _config = _configService.LoadConfig();
        SyncSettingsDraftFromConfig();
        RebuildServices();
        ApplyCatalogFilter();
        ReloadModsData();
    }

    private void SyncSettingsDraftFromConfig()
    {
        ModsFolderDraft = _config.ModsFolder;
        BackupFolderDraft = _config.BackupFolder;
        AutoBackupDraft = _config.AutoBackup;
        MaxConcurrentChecksDraft = Math.Clamp(_config.MaxConcurrentChecks, 1, 8);
        OnPropertyChanged(nameof(ActiveModsFolderDisplay));
    }

    private void RebuildServices()
    {
        string modsDir = _config.ModsFolder ?? string.Empty;
        string backupDir = _configService.ResolveBackupFolder(_config);
        string manifestPath = _configService.ResolveManifestPath(_config);
        string overridesPath = _configService.ResolveCreatorOverridesPath(_config);

        _scanner = new ModScannerService(modsDir, _catalogService);
        _backupService = new BackupService(modsDir, backupDir);
        _updaterService = new ModUpdaterService(modsDir, manifestPath, _backupService, _catalogService);
        _creatorService = new CreatorOrganizerService(overridesPath, _catalogService);
    }

    public void CancelBackgroundWork()
    {
        _metadataEnrichCts?.Cancel();
        _metadataEnrichCts?.Dispose();
        _metadataEnrichCts = null;

        CancelThumbnailLoads();

        _activeCts?.Cancel();
    }

    private string GetPinnedUrlForUnit(ModUnit unit)
    {
        string pinned = _updaterService?.GetPinnedUrl(unit.PrimaryFileName, unit.FolderName) ?? string.Empty;
        if (string.IsNullOrEmpty(pinned))
            pinned = _updaterService?.GetPinnedUrl(unit.Name, unit.FolderName) ?? string.Empty;
        return pinned;
    }

    public void ReloadModsData()
    {
        _metadataEnrichCts?.Cancel();
        _metadataEnrichCts?.Dispose();
        _metadataEnrichCts = null;
        IsResolvingCreators = false;

        CancelThumbnailLoads();
        _thumbnailCache.Clear();
        SelectedUnitThumbnail = null;
        SelectedCategoryThumbnail = null;

        RebuildServices();
        AllModUnits.Clear();
        AllFolderSummaries.Clear();
        DepthWarnings.Clear();

        if (_scanner is null || string.IsNullOrWhiteSpace(_scanner.ModsFolder) || !Directory.Exists(_scanner.ModsFolder))
        {
            TotalFileCount = 0;
            PackageCount = 0;
            ScriptCount = 0;
            UpdatableUnitCount = 0;
            FolderCount = 0;
            CreatorGroupCount = 0;
            DuplicateModCount = 0;
            ExcludedCcPackageCount = 0;
            UpdateNeededCount = 0;
            CheckedCount = 0;
            ScanDiagnostics = null;
            SelectedExplorerUnit = null;
            SelectedExplorerFolder = null;
            SelectedCategoryMod = null;
            ApplyExplorerFilters();
            RefreshCategoryView(keepSelection: false);
            ApplyUpdateFilters();
            NotifyUpdateScopeProperties();
            RefreshBackupsList();
            RefreshPinnedEntriesList();
            StatusMessage = "유효한 심즈 4 Mods 폴더가 설정되지 않았습니다. 설정 탭에서 경로를 지정하세요.";
            return;
        }

        ModsScanResult scan = _scanner.Scan();
        TotalFileCount = scan.TotalCount;
        PackageCount = scan.PackageCount;
        ScriptCount = scan.ScriptCount;
        FolderCount = scan.FolderMods.Count;

        foreach (string w in scan.Warnings)
            DepthWarnings.Add(w);

        foreach (ModFolderSummary fm in scan.FolderMods)
            AllFolderSummaries.Add(new ExplorerFolderItemViewModel(fm));

        ExcludedCcPackageCount = AllFolderSummaries.Where(f => !f.IsUpdatable).Sum(f => f.PackageCount);

        Dictionary<string, string> overrides = _creatorService?.LoadOverrideLookup() ?? new(StringComparer.OrdinalIgnoreCase);
        List<ModUnit> units = _scanner.ScanUpdatableModUnits();
        HashSet<string> duplicatePaths = _scanner.CollectAllDuplicateFilePaths();
        HashSet<string>? batchCreators = _creatorService is not null
            ? CreatorOrganizerService.BuildKnownBatchCreators(units)
            : null;
        int duplicateModCount = 0;
        foreach (ModUnit unit in units)
        {
            string pinned = GetPinnedUrlForUnit(unit);
            var vm = new ModItemViewModel(unit, pinned);
            int dupFileMatches = unit.UnitFiles.Count(duplicatePaths.Contains);
            bool hasDup = dupFileMatches > 0 ||
                          (!string.IsNullOrWhiteSpace(unit.PrimaryFilePath) && duplicatePaths.Contains(unit.PrimaryFilePath));
            if (hasDup)
            {
                vm.HasDuplicateWarning = true;
                vm.DuplicateFileCount = Math.Max(1, dupFileMatches);
                duplicateModCount++;
            }
            if (_creatorService is not null)
                vm.ApplyCreatorResolution(_creatorService.ResolveCreator(unit, null, pinned, overrides, batchCreators));

            bool hasScript = unit.UnitFiles.Any(f => f.EndsWith(".ts4script", StringComparison.OrdinalIgnoreCase)) ||
                             (!string.IsNullOrWhiteSpace(unit.PrimaryFilePath) &&
                              unit.PrimaryFilePath.EndsWith(".ts4script", StringComparison.OrdinalIgnoreCase));
            if (hasScript)
                vm.ApplyFunctionalClassification(_metadataExtractor.ClassifyFunctionalCategory(unit));

            AllModUnits.Add(vm);
        }

        UpdatableUnitCount = AllModUnits.Count;
        DuplicateModCount = duplicateModCount;
        UpdateNeededCount = 0;
        CheckedCount = 0;

        ScanDiagnostics = _scanner.CollectScanDiagnostics();
        ApplyExplorerFilters();
        RefreshCategoryView(keepSelection: false);
        ApplyUpdateFilters();
        NotifyUpdateScopeProperties();
        RefreshBackupsList();
        RefreshPinnedEntriesList();

        SelectedExplorerUnit = AllModUnits.Count > 0 ? AllModUnits[0] : null;
        SelectedExplorerFolder = AllFolderSummaries.Count > 0 ? AllFolderSummaries[0] : null;

        StatusMessage = $"스캔 완료: 모드 파일 총 {TotalFileCount}개 (.package {PackageCount} / .ts4script {ScriptCount}), 업데이트 유닛 {UpdatableUnitCount}개 · 제작자 그룹 {CreatorGroupCount}개";

        if (AllModUnits.Count > 0)
        {
            var cts = new CancellationTokenSource();
            _metadataEnrichCts = cts;
            _ = EnrichCreatorsFromMetadataAsync(AllModUnits.ToList(), cts, cts.Token);
        }
    }

    private void ApplyExplorerFilters()
    {
        string q = (ExplorerSearchText ?? string.Empty).Trim();

        RebuildCreatorGroups(q);

        FilteredExplorerUnits.Clear();
        foreach (ModItemViewModel m in AllModUnits)
        {
            if (!MatchesExplorerQuery(m, q))
                continue;
            if (ShowUnknownCreatorOnly && m.IsCreatorKnown)
                continue;
            if (ShowDuplicateModsOnly && !m.HasDuplicateWarning)
                continue;
            FilteredExplorerUnits.Add(m);
        }

        FilteredExplorerFolders.Clear();
        foreach (ExplorerFolderItemViewModel f in AllFolderSummaries)
        {
            if (ExplorerFilter == ExplorerFilterMode.UpdatableOnly && !f.IsUpdatable)
                continue;
            if (ExplorerFilter == ExplorerFilterMode.CcAndSimpleOnly && f.IsUpdatable)
                continue;

            if (!string.IsNullOrEmpty(q) &&
                !f.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !f.ModType.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            FilteredExplorerFolders.Add(f);
        }
    }

    private bool MatchesUpdateScope(ModItemViewModel m) => UpdateScope switch
    {
        UpdateScopeMode.ScriptCoreOnly => m.FunctionalCategory == ModFunctionalCategory.ScriptCoreMod,
        UpdateScopeMode.ScriptAndXml => m.FunctionalCategory is ModFunctionalCategory.ScriptCoreMod or ModFunctionalCategory.XmlGameplayMod,
        _ => true
    };

    private IEnumerable<ModItemViewModel> GetScopedUpdateMods() => AllModUnits.Where(MatchesUpdateScope);

    private void ApplyUpdateFilters()
    {
        ModItemViewModel? previous = SelectedUpdateMod;
        string q = (UpdateSearchText ?? string.Empty).Trim();
        FilteredUpdateMods.Clear();

        foreach (ModItemViewModel m in GetScopedUpdateMods())
        {
            bool matchFilter = UpdateFilter switch
            {
                UpdateFilterMode.All => true,
                UpdateFilterMode.UpdateNeeded => m.HasChecked && m.StatusCode == ModUpdateStatusCode.UpdateNeeded,
                UpdateFilterMode.ManualCheck => m.HasChecked && m.StatusCode is ModUpdateStatusCode.ManualCheck or ModUpdateStatusCode.Error,
                UpdateFilterMode.PaidOrEarly => m.HasChecked && m.StatusCode is ModUpdateStatusCode.Paid or ModUpdateStatusCode.EarlyAccess,
                UpdateFilterMode.LinkNotFound => m.HasChecked && m.StatusCode == ModUpdateStatusCode.LinkNotFound,
                UpdateFilterMode.UpToDate => m.HasChecked && m.StatusCode == ModUpdateStatusCode.UpToDate,
                _ => true
            };
            if (!matchFilter)
                continue;

            if (!string.IsNullOrEmpty(q) &&
                !m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !m.FolderName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !m.CreatorName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !m.MatchedCreator.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FilteredUpdateMods.Add(m);
        }

        OnPropertyChanged(nameof(UpdateFilteredCountLabel));

        if (previous is not null && FilteredUpdateMods.Contains(previous))
            SelectedUpdateMod = previous;
        else
            SelectedUpdateMod = FilteredUpdateMods.Count > 0 ? FilteredUpdateMods[0] : null;
    }

    private void InspectUnitDetails(ModItemViewModel vm)
    {
        SelectedUnitMetadata = PopulateModInspectorCommon(vm, _selectedInspectorFiles);
        UpdateInspectorThumbnail(vm);
        SelectedDuplicateInfo = _scanner?.DetectModInstalled(vm.Query);
    }

    private void InspectCategoryModDetails(ModItemViewModel vm)
    {
        SelectedCategoryMetadata = PopulateModInspectorCommon(vm, _selectedCategoryInspectorFiles);
        UpdateCategoryInspectorThumbnail(vm);
        SelectedCategoryDuplicateInfo = _scanner?.DetectModInstalled(vm.Query);
    }

    private ModMetadata PopulateModInspectorCommon(ModItemViewModel vm, ObservableCollection<string> targetFiles)
    {
        ModMetadata meta = vm.CachedMetadata ?? _metadataExtractor.Extract(vm.PrimaryFilePath);
        vm.CachedMetadata = meta;

        if (vm.FunctionalCategory == ModFunctionalCategory.Pending)
        {
            vm.ApplyFunctionalClassification(_metadataExtractor.ClassifyFunctionalCategory(vm.Unit));
            RefreshCategoryCounts();
        }

        if (_creatorService is not null)
        {
            Dictionary<string, string> overrides = _creatorService.LoadOverrideLookup();
            HashSet<string> batchCreators = CreatorOrganizerService.BuildKnownBatchCreators(AllModUnits.Select(m => m.Unit));
            string url = !string.IsNullOrWhiteSpace(vm.PageUrl) ? vm.PageUrl : GetPinnedUrlForUnit(vm.Unit);
            string prevCreator = vm.CreatorName;
            vm.ApplyCreatorResolution(_creatorService.ResolveCreator(vm.Unit, meta, url, overrides, batchCreators));
            if (!string.Equals(prevCreator, vm.CreatorName, StringComparison.OrdinalIgnoreCase))
                RebuildCreatorGroups((ExplorerSearchText ?? string.Empty).Trim());
        }

        targetFiles.Clear();
        foreach (string f in vm.UnitFiles)
            targetFiles.Add(Path.GetFileName(f));

        return meta;
    }

    private void RefreshCategoryCounts()
    {
        int script = 0, xml = 0, objCc = 0, anim = 0, pending = 0;
        foreach (ModItemViewModel m in AllModUnits)
        {
            switch (m.FunctionalCategory)
            {
                case ModFunctionalCategory.ScriptCoreMod: script++; break;
                case ModFunctionalCategory.XmlGameplayMod: xml++; break;
                case ModFunctionalCategory.ObjectModAndCc: objCc++; break;
                case ModFunctionalCategory.AnimationAndPose: anim++; break;
                default: pending++; break;
            }
        }

        CategoryScriptCount = script;
        CategoryXmlCount = xml;
        CategoryObjectCcCount = objCc;
        CategoryAnimationCount = anim;
        CategoryPendingCount = pending;
        OnPropertyChanged(nameof(CategoryAnalysisSummaryText));
    }

    private void RefreshCategoryView(bool keepSelection)
    {
        RefreshCategoryCounts();
        ApplyCategoryFilters(keepSelection);
    }

    private void ApplyCategoryFilters(bool keepSelection = true)
    {
        ModItemViewModel? previous = keepSelection ? SelectedCategoryMod : null;
        string q = (CategorySearchText ?? string.Empty).Trim();
        FilteredCategoryMods.Clear();

        foreach (ModItemViewModel m in AllModUnits)
        {
            bool matchCategory = CategoryFilter switch
            {
                CategoryFilterMode.All => true,
                CategoryFilterMode.ScriptCoreMod => m.FunctionalCategory == ModFunctionalCategory.ScriptCoreMod,
                CategoryFilterMode.XmlGameplayMod => m.FunctionalCategory == ModFunctionalCategory.XmlGameplayMod,
                CategoryFilterMode.ObjectModAndCc => m.FunctionalCategory == ModFunctionalCategory.ObjectModAndCc,
                CategoryFilterMode.AnimationAndPose => m.FunctionalCategory == ModFunctionalCategory.AnimationAndPose,
                _ => true
            };
            if (!matchCategory)
                continue;

            if (!string.IsNullOrEmpty(q) &&
                !m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !m.CreatorName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !m.FolderName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !m.FunctionalCategoryShortLabel.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !m.FunctionalSubCategory.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !m.FunctionalReason.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FilteredCategoryMods.Add(m);
        }

        OnPropertyChanged(nameof(CategoryFilteredCountLabel));

        if (previous is not null && FilteredCategoryMods.Contains(previous))
            SelectedCategoryMod = previous;
        else
            SelectedCategoryMod = FilteredCategoryMods.Count > 0 ? FilteredCategoryMods[0] : null;
    }

    private void SetCategoryFilterFromCommand(object? parameter)
    {
        if (TryParseCategoryFilter(parameter, out CategoryFilterMode mode))
            CategoryFilter = mode;
    }

    private void ToggleCategoryKpiFromCommand(object? parameter)
    {
        if (!TryParseCategoryFilter(parameter, out CategoryFilterMode mode))
            return;

        CategoryFilter = CategoryFilter == mode ? CategoryFilterMode.All : mode;
    }

    private static bool TryParseCategoryFilter(object? parameter, out CategoryFilterMode mode)
    {
        if (parameter is CategoryFilterMode parsedMode)
        {
            mode = parsedMode;
            return true;
        }

        if (parameter is string text && Enum.TryParse(text, ignoreCase: true, out CategoryFilterMode fromText))
        {
            mode = fromText;
            return true;
        }

        mode = CategoryFilterMode.All;
        return false;
    }

    private void EnsureUpdateTargetsVisible(IReadOnlyCollection<ModItemViewModel> targets)
    {
        if (targets.Count == 0)
            return;

        if (targets.Any(m => !MatchesUpdateScope(m)))
        {
            bool allScriptOrXml = targets.All(m =>
                m.FunctionalCategory is ModFunctionalCategory.ScriptCoreMod or ModFunctionalCategory.XmlGameplayMod);
            UpdateScope = allScriptOrXml ? UpdateScopeMode.ScriptAndXml : UpdateScopeMode.AllMods;
        }

        if (UpdateFilter != UpdateFilterMode.All)
            UpdateFilter = UpdateFilterMode.All;
        if (!string.IsNullOrEmpty(UpdateSearchText))
            UpdateSearchText = string.Empty;
    }

    private async Task CheckFilteredCategoryAsync()
    {
        List<ModItemViewModel> targets = FilteredCategoryMods.ToList();
        if (targets.Count == 0)
            return;

        EnsureUpdateTargetsVisible(targets);
        CurrentTab = MainTab.Updates;
        SelectedUpdateMod = targets[0];
        await CheckTargetsAsync(targets);
    }

    private void NavigateToModInExplorer(ModItemViewModel? vm)
    {
        if (vm is null)
            return;

        ExplorerView = ExplorerViewMode.ByCreator;
        ExplorerFilter = ExplorerFilterMode.All;
        ShowUnknownCreatorOnly = false;
        ShowDuplicateModsOnly = false;
        ExplorerSearchText = string.Empty;

        if (!string.IsNullOrWhiteSpace(vm.CreatorName))
            _expandedCreators.Add(vm.CreatorName);

        RebuildCreatorGroups(string.Empty);
        CreatorGroupViewModel? group = FilteredCreatorGroups.FirstOrDefault(g =>
            string.Equals(g.CreatorName, vm.CreatorName, StringComparison.OrdinalIgnoreCase));
        if (group is not null)
            group.IsExpanded = true;

        SelectedExplorerUnit = vm;
        CurrentTab = MainTab.Explorer;
    }

    private void InspectFolderDetails(ExplorerFolderItemViewModel folderVm)
    {
        SelectedUnitMetadata = null;
        SelectedUnitThumbnail = null;
        _selectedInspectorFiles.Clear();

        if (_scanner is not null && Directory.Exists(folderVm.FolderPath))
        {
            var (_, _, allFiles, _) = _scanner.ScanAllModFiles(folderVm.FolderPath);
            foreach (ModFileItem item in allFiles.Take(250))
                _selectedInspectorFiles.Add(item.RelativePath);

            string? rep = allFiles.FirstOrDefault(f => f.Extension == ".ts4script")?.FullPath
                          ?? allFiles.FirstOrDefault()?.FullPath;
            if (!string.IsNullOrEmpty(rep))
                SelectedUnitMetadata = _metadataExtractor.Extract(rep);
        }

        SelectedDuplicateInfo = null;
    }

    private async Task CheckAllUpdatesAsync()
    {
        await CheckTargetsAsync(GetScopedUpdateMods().ToList());
    }

    private async Task CheckSelectedUpdatesAsync()
    {
        var targets = GetScopedUpdateMods().Where(m => m.IsChecked).ToList();
        await CheckTargetsAsync(targets);
    }

    private async Task CheckSingleModAsync(ModItemViewModel? vm)
    {
        if (vm is null)
            return;

        EnsureUpdateTargetsVisible([vm]);
        CurrentTab = MainTab.Updates;
        SelectedUpdateMod = vm;
        await CheckTargetsAsync([vm]);
    }

    private async Task CheckTargetsAsync(List<ModItemViewModel> targets)
    {
        if (_updaterService is null || targets.Count == 0)
            return;

        BeginOperation(targets.Count, false, $"업데이트 확인 중 (총 {targets.Count}개)...");
        try
        {
            CancellationToken ct = _activeCts!.Token;
            var vmByKey = new Dictionary<string, ModItemViewModel>(StringComparer.OrdinalIgnoreCase);
            var queries = new List<ModQuery>(targets.Count);

            foreach (ModItemViewModel vm in targets)
            {
                vm.RefreshQueryFromDisk();
                queries.Add(vm.Query);
                vmByKey[vm.PrimaryFilePath] = vm;
                vmByKey[$"{vm.FolderName}::{vm.Name}"] = vm;
            }

            var progress = new Progress<(int Completed, int Total, ModCheckResult Result)>(report =>
            {
                ProgressValue = report.Completed;
                StatusMessage = $"업데이트 확인 중... ({report.Completed}/{report.Total}) {report.Result.ModName}";

                if (vmByKey.TryGetValue(report.Result.FilePath, out ModItemViewModel? targetVm) ||
                    vmByKey.TryGetValue($"{report.Result.FolderName}::{report.Result.ModName}", out targetVm))
                {
                    bool pinned = !string.IsNullOrEmpty(_updaterService.GetPinnedUrl(targetVm.PrimaryFileName, targetVm.FolderName)) ||
                                  !string.IsNullOrEmpty(_updaterService.GetPinnedUrl(targetVm.Name, targetVm.FolderName));
                    targetVm.ApplyCheckResult(report.Result, pinned);
                }

                UpdateCountersAfterCheck();
            });

            await _updaterService.CheckMultipleModsAsync(
                queries,
                maxConcurrency: Math.Clamp(_config.MaxConcurrentChecks, 1, 8),
                progress: progress,
                ct: ct);

            UpdateCountersAfterCheck();
            ApplyUpdateFilters();
            RefreshPinnedEntriesList();
            StatusMessage = $"업데이트 조회 완료: 총 {targets.Count}개 점검, 업데이트 가능 {UpdateNeededCount}개";
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task UpdateSingleModAsync(ModItemViewModel? vm)
    {
        if (vm is null || _updaterService is null)
            return;

        if (!vm.HasChecked || string.IsNullOrWhiteSpace(vm.DownloadUrl))
        {
            _dialogs.ShowInfo("안내", $"'{vm.Name}' 모드는 먼저 업데이트 확인 후 직접 다운로드 URL이 감지되어야 원클릭 업데이트가 가능합니다.\n\n수동 확인이 필요한 경우 '배포처 열기'를 이용하세요.");
            return;
        }

        await ExecuteUpdateBatchAsync([vm]);
    }

    private async Task UpdateSelectedModsAsync()
    {
        var targets = GetScopedUpdateMods().Where(m => m.IsChecked && m.CanDownloadUpdate).ToList();
        if (targets.Count == 0)
            return;

        await ExecuteUpdateBatchAsync(targets);
    }

    private async Task ExecuteUpdateBatchAsync(List<ModItemViewModel> targets)
    {
        if (_updaterService is null || targets.Count == 0)
            return;

        bool isBatch = targets.Count > 1;
        OverwriteMode? batchMode = null;

        BeginOperation(targets.Count, false, $"모드 업데이트 적용 중 (0/{targets.Count})...");
        int succeeded = 0;

        try
        {
            CancellationToken ct = _activeCts!.Token;

            for (int i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                ModItemViewModel vm = targets[i];
                ModCheckResult? check = vm.CheckResult;
                if (check is null || string.IsNullOrWhiteSpace(check.DownloadUrl))
                    continue;

                // Variant selection if applicable
                if (check.VariantChoices.Count > 1)
                {
                    VariantChoice? picked = _dialogs.PromptVariantChoice(vm.Name, check.VariantChoices);
                    if (picked is null)
                        continue;

                    check.DownloadUrl = picked.DownloadUrl;
                    check.AssetName = picked.FileName;
                    check.DetectedFilename = picked.FileName;
                }

                // Overwrite mode prompt
                OverwriteMode chosenMode;
                if (batchMode.HasValue)
                {
                    chosenMode = batchMode.Value;
                }
                else
                {
                    var (confirmed, mode, applyToBatch) = _dialogs.PromptOverwriteMode(vm.Name, isBatch);
                    if (!confirmed)
                        continue;

                    chosenMode = mode;
                    if (applyToBatch)
                        batchMode = mode;
                }

                vm.IsUpdating = true;
                StatusMessage = $"다운로드 & 설치 중 ({i + 1}/{targets.Count}): {vm.Name}";

                var dlProgress = new Progress<(long DownloadedBytes, long TotalBytes)>(p =>
                {
                    if (p.TotalBytes > 0)
                    {
                        double pct = p.DownloadedBytes * 100.0 / p.TotalBytes;
                        StatusMessage = $"다운로드 중: {vm.Name} ({pct:F0}% | {p.DownloadedBytes / 1024.0:F0} KB)";
                    }
                });

                List<string> installed = await _updaterService.UpdateModAsync(
                    vm.Query,
                    check,
                    overwriteMode: chosenMode,
                    skipBackup: !_config.AutoBackup,
                    downloadProgress: dlProgress,
                    ct: ct);

                vm.MarkUpdated(check.LatestVersion);
                vm.IsUpdating = false;
                succeeded++;
                ProgressValue = i + 1;
            }

            UpdateCountersAfterCheck();
            ApplyUpdateFilters();
            RefreshBackupsList();
            StatusMessage = $"업데이트 작업 완료: 총 {succeeded}/{targets.Count}개 모드 업데이트 적용";
            _dialogs.ShowInfo("업데이트 완료", $"{succeeded}개 모드의 다운로드 및 설치(백업 포함)가 완료되었습니다.");
        }
        finally
        {
            foreach (ModItemViewModel vm in targets)
                vm.IsUpdating = false;
            EndOperation();
        }
    }

    private void PinUrlForMod(ModItemViewModel? vm)
    {
        if (vm is null || _updaterService is null)
            return;

        string currentUrl = !string.IsNullOrWhiteSpace(vm.PageUrl)
            ? vm.PageUrl
            : _updaterService.GetPinnedUrl(vm.PrimaryFileName, vm.FolderName);

        var dialogRes = _dialogs.PromptPinUrl(vm.Name, currentUrl, vm.IsPinned);
        if (dialogRes is null || !dialogRes.Value.Confirmed)
            return;

        if (dialogRes.Value.RemovePin)
        {
            _updaterService.RemovePinnedUrl(vm.PrimaryFileName);
            _updaterService.RemovePinnedUrl(vm.Name);
            vm.ApplyPinnedUrl(null);
            RefreshPinnedEntriesList();
            StatusMessage = $"'{vm.Name}'의 URL 고정이 해제되었습니다.";
            return;
        }

        _updaterService.PinVerifiedUrl(vm.Query, dialogRes.Value.Url, vm.LatestVersion, vm.PrimaryFileName);
        vm.ApplyPinnedUrl(dialogRes.Value.Url);
        RefreshPinnedEntriesList();
        StatusMessage = $"'{vm.Name}'의 배포처 URL이 고정되었습니다.";
    }

    private void OpenModPageUrl(ModItemViewModel? vm)
    {
        if (vm is null)
            return;

        string url = !string.IsNullOrWhiteSpace(vm.PageUrl) ? vm.PageUrl : vm.DownloadUrl;
        if (!string.IsNullOrWhiteSpace(url))
            _dialogs.OpenUrl(url);
    }

    private void OpenSelectedModInExplorer()
    {
        string path = (CurrentTab == MainTab.Categories ? SelectedCategoryMod?.PrimaryFilePath : null)
                      ?? (CurrentTab == MainTab.Updates ? SelectedUpdateMod?.PrimaryFilePath : null)
                      ?? SelectedExplorerUnit?.PrimaryFilePath
                      ?? SelectedExplorerFolder?.FolderPath
                      ?? SelectedCategoryMod?.PrimaryFilePath
                      ?? SelectedUpdateMod?.PrimaryFilePath
                      ?? _config.ModsFolder;

        if (!string.IsNullOrWhiteSpace(path))
            _dialogs.OpenPathInExplorer(path);
    }

    private void SetAllFilteredSelection(bool check)
    {
        foreach (ModItemViewModel m in FilteredUpdateMods)
            m.IsChecked = check;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void SelectUpdatableItems()
    {
        foreach (ModItemViewModel m in FilteredUpdateMods)
            m.IsChecked = m.StatusCode == ModUpdateStatusCode.UpdateNeeded && m.CanDownloadUpdate;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateCountersAfterCheck()
    {
        CheckedCount = AllModUnits.Count(m => m.HasChecked);
        UpdateNeededCount = AllModUnits.Count(m => m.HasChecked && m.StatusCode == ModUpdateStatusCode.UpdateNeeded);
    }

    private async Task CreateManualBackupAsync()
    {
        if (_backupService is null || string.IsNullOrWhiteSpace(_config.ModsFolder) || !Directory.Exists(_config.ModsFolder))
        {
            _dialogs.ShowError("백업 오류", "유효한 Mods 폴더가 없어 백업을 생성할 수 없습니다.");
            return;
        }

        BeginOperation(100, true, "Mods 전체 수동 백업 생성 중...");
        try
        {
            string modsFolder = _config.ModsFolder;
            BackupService bk = _backupService;

            string sessionDir = await Task.Run(() =>
            {
                string dir = bk.CreateBackupSession("manual_full");
                bk.BackupItem(modsFolder, dir);
                return dir;
            }, _activeCts!.Token);

            RefreshBackupsList();
            StatusMessage = $"수동 전체 백업 생성 완료: {Path.GetFileName(sessionDir)}";
            _dialogs.ShowInfo("백업 생성 완료", $"Mods 전체 백업이 안전하게 생성되었습니다.\n({Path.GetFileName(sessionDir)})");
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task RestoreSelectedBackupAsync()
    {
        if (_backupService is null || SelectedBackup is null)
            return;

        BackupSessionInfo target = SelectedBackup;
        if (!_dialogs.Confirm("백업 복원 확인",
                $"선택한 백업 '{target.Name}' (총 {target.FileCount}개 파일)의 내용을 현재 Mods 폴더로 복원·덮어쓰시겠습니까?"))
        {
            return;
        }

        BeginOperation(100, true, $"백업 복원 중: {target.Name}...");
        try
        {
            BackupService bk = _backupService;
            int restored = await Task.Run(() => bk.RestoreBackup(target.FullPath), _activeCts!.Token);
            ReloadModsData();
            StatusMessage = $"백업 복원 완료: {restored}개 파일 복구됨";
            _dialogs.ShowInfo("백업 복원 완료", $"'{target.Name}'에서 총 {restored}개 파일을 복구했습니다.");
        }
        finally
        {
            EndOperation();
        }
    }

    private void DeleteSelectedBackup()
    {
        if (_backupService is null || SelectedBackup is null)
            return;

        BackupSessionInfo target = SelectedBackup;
        if (!_dialogs.Confirm("백업 삭제 확인", $"백업 세션 '{target.Name}' 을(를) 영구 삭제하시겠습니까?"))
            return;

        try
        {
            _backupService.DeleteBackup(target.FullPath);
            RefreshBackupsList();
            StatusMessage = $"백업 삭제 완료: {target.Name}";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("백업 삭제 실패", ex.Message);
        }
    }

    private void RefreshBackupsList()
    {
        BackupSessions.Clear();
        if (_backupService is null)
            return;

        foreach (BackupSessionInfo item in _backupService.ListBackups())
            BackupSessions.Add(item);

        if (BackupSessions.Count > 0 && SelectedBackup is null)
            SelectedBackup = BackupSessions[0];
    }

    private void OpenBackupsFolder()
    {
        string backupDir = _configService.ResolveBackupFolder(_config);
        Directory.CreateDirectory(backupDir);
        _dialogs.OpenPathInExplorer(backupDir);
    }

    private void BuildDiagnosticReport()
    {
        if (_scanner is null || !Directory.Exists(_scanner.ModsFolder))
        {
            _dialogs.ShowError("진단 리포트 생성 불가", "먼저 유효한 Mods 폴더를 설정하세요.");
            return;
        }

        var checkedResults = AllModUnits
            .Where(m => m.HasChecked && m.CheckResult is not null)
            .Select(m => m.CheckResult!)
            .ToList();

        var reporter = new DiagnosticReportService(_configService.BaseDirectory, _metadataExtractor);
        DiagnosticReportService.DiagnosticReportOutput output = reporter.GenerateReport(_scanner, checkedResults);

        ScanDiagnostics = _scanner.CollectScanDiagnostics();
        DiagnosticReportText = output.Content;
        LastReportPath = output.ReportPath;
        StatusMessage = $"정밀 진단 리포트 생성 완료 (미분류 사각지대: {output.UnaccountedCount}건 | 개인정보 마스킹 적용됨)";
    }

    private void CopyDiagnosticReport()
    {
        if (string.IsNullOrWhiteSpace(DiagnosticReportText))
            return;

        _dialogs.CopyToClipboard(DiagnosticReportText);
        StatusMessage = "진단 리포트 전문이 클립보드에 복사되었습니다.";
    }

    private void SaveDiagnosticReport()
    {
        if (string.IsNullOrWhiteSpace(DiagnosticReportText))
            return;

        string? savePath = _dialogs.SaveTextFile("진단 리포트 저장", "scan_report.txt", "텍스트 파일 (*.txt)|*.txt");
        if (string.IsNullOrWhiteSpace(savePath))
            return;

        File.WriteAllText(savePath, DiagnosticReportText, new System.Text.UTF8Encoding(false));
        LastReportPath = savePath;
        StatusMessage = $"리포트 저장 완료: {Path.GetFileName(savePath)}";
    }

    private void ApplyCatalogFilter()
    {
        string q = (CatalogSearchText ?? string.Empty).Trim();
        FilteredCatalogItems.Clear();

        foreach (CatalogItem item in _catalogService.Items)
        {
            if (!string.IsNullOrEmpty(q) &&
                !item.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !item.Creator.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !item.ModTitle.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !item.Keywords.Any(k => k.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            FilteredCatalogItems.Add(item);
        }
    }

    private void RefreshPinnedEntriesList()
    {
        PinnedEntries.Clear();
        if (_updaterService is null)
            return;

        foreach (ManifestEntry entry in _updaterService.LoadManifest())
        {
            if (string.IsNullOrWhiteSpace(entry.PinnedPageUrl) && string.IsNullOrWhiteSpace(entry.Homepage))
                continue;

            PinnedEntries.Add(new PinnedManifestViewModel
            {
                Name = entry.Name,
                LocalFile = entry.LocalFile ?? string.Empty,
                InstallFolder = entry.InstallFolder,
                PinnedPageUrl = !string.IsNullOrWhiteSpace(entry.PinnedPageUrl) ? entry.PinnedPageUrl! : entry.Homepage!,
                CurrentVersion = entry.CurrentVersion
            });
        }
    }

    private void RemovePinnedManifestEntry(PinnedManifestViewModel? item)
    {
        if (item is null || _updaterService is null)
            return;

        _updaterService.RemovePinnedUrl(!string.IsNullOrWhiteSpace(item.LocalFile) ? item.LocalFile : item.Name);
        _updaterService.RemovePinnedUrl(item.Name);

        foreach (ModItemViewModel m in AllModUnits)
        {
            if (m.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase) ||
                m.PrimaryFileName.Equals(item.LocalFile, StringComparison.OrdinalIgnoreCase))
            {
                m.ApplyPinnedUrl(null);
            }
        }

        RefreshPinnedEntriesList();
        StatusMessage = $"'{item.Name}' 고정 링크 해제됨";
    }

    private void AutoDetectModsFolder()
    {
        string? detected = ConfigService.DetectSims4ModsFolder();
        if (string.IsNullOrWhiteSpace(detected))
        {
            _dialogs.ShowError("자동 탐지 실패", "PC 내 심즈 4 Mods 폴더를 자동으로 찾지 못했습니다. 찾아보기 버튼으로 직접 지정하세요.");
            return;
        }

        ModsFolderDraft = detected;
        StatusMessage = $"Mods 폴더 자동 탐지됨: {detected}";
    }

    private void BrowseModsFolder()
    {
        string? chosen = _dialogs.BrowseFolder("심즈 4 Mods 폴더 선택", ModsFolderDraft);
        if (!string.IsNullOrWhiteSpace(chosen))
            ModsFolderDraft = chosen;
    }

    private void BrowseBackupFolder()
    {
        string? chosen = _dialogs.BrowseFolder("백업 저장 루트 폴더 선택", _configService.ResolveBackupFolder(_config));
        if (!string.IsNullOrWhiteSpace(chosen))
            BackupFolderDraft = chosen;
    }

    private void SaveSettings()
    {
        if (string.IsNullOrWhiteSpace(ModsFolderDraft) || !Directory.Exists(ModsFolderDraft))
        {
            _dialogs.ShowError("잘못된 Mods 경로", "존재하는 심즈 4 Mods 폴더 경로를 지정하세요.");
            return;
        }

        _config.ModsFolder = ModsFolderDraft.Trim();
        _config.BackupFolder = string.IsNullOrWhiteSpace(BackupFolderDraft) ? "backups" : BackupFolderDraft.Trim();
        _config.AutoBackup = AutoBackupDraft;
        _config.MaxConcurrentChecks = Math.Clamp(MaxConcurrentChecksDraft, 1, 8);

        _configService.SaveConfig(_config);
        OnPropertyChanged(nameof(ActiveModsFolderDisplay));
        ReloadModsData();
        StatusMessage = "설정이 저장되고 Mods 폴더를 다시 스캔했습니다.";
        _dialogs.ShowInfo("설정 저장 완료", "환경설정이 config.json에 저장되었으며 새 경로로 다시 스캔했습니다.");
    }

    private void BeginOperation(int maxProgress, bool indeterminate, string initialStatus)
    {
        _activeCts?.Dispose();
        _activeCts = new CancellationTokenSource();
        IsBusy = true;
        IsIndeterminate = indeterminate;
        ProgressMaximum = Math.Max(1, maxProgress);
        ProgressValue = 0;
        StatusMessage = initialStatus;
    }

    private void EndOperation()
    {
        IsBusy = false;
        IsIndeterminate = false;
        _activeCts?.Dispose();
        _activeCts = null;
    }

    private void CancelActiveOperation()
    {
        if (_activeCts is not null && !_activeCts.IsCancellationRequested)
        {
            _activeCts.Cancel();
            StatusMessage = "작업 취소 요청 중...";
        }
    }

    private void HandleCommandError(Exception ex)
    {
        StatusMessage = $"오류 발생: {ex.Message}";
        _dialogs.ShowError("작업 중 오류 발생", ex.Message);
    }

    private static bool MatchesExplorerQuery(ModItemViewModel m, string q)
    {
        if (string.IsNullOrEmpty(q))
            return true;

        return m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               m.FolderName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               m.PrimaryFileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               m.CreatorName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildCreatorGroups(string q)
    {
        bool autoExpand = !string.IsNullOrEmpty(q) || ShowDuplicateModsOnly;
        List<IGrouping<string, ModItemViewModel>> rawGroups = AllModUnits
            .Where(m => MatchesExplorerQuery(m, q) && (!ShowDuplicateModsOnly || m.HasDuplicateWarning))
            .GroupBy(m => string.IsNullOrWhiteSpace(m.CreatorName) ? CreatorOrganizerService.UnknownCreatorLabel : m.CreatorName,
                     StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => string.Equals(g.Key, CreatorOrganizerService.UnknownCreatorLabel, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        FilteredCreatorGroups.Clear();
        foreach (IGrouping<string, ModItemViewModel> g in rawGroups)
        {
            bool isUnknown = string.Equals(g.Key, CreatorOrganizerService.UnknownCreatorLabel, StringComparison.OrdinalIgnoreCase);
            if (ShowUnknownCreatorOnly && !isUnknown)
                continue;

            List<ModItemViewModel> sortedMods = g
                .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            foreach (ModItemViewModel mod in sortedMods)
            {
                if (!mod.IsThumbnailChecked && _thumbnailCache.TryGetValue(ThumbnailCacheKey(mod), out BitmapSource? cached))
                    mod.SetThumbnail(cached);
            }

            bool isExpanded = autoExpand || _expandedCreators.Contains(g.Key);
            var groupVm = new CreatorGroupViewModel(g.Key, isUnknown, sortedMods, isExpanded);

            groupVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(CreatorGroupViewModel.IsExpanded))
                    return;

                if (!autoExpand)
                {
                    if (groupVm.IsExpanded)
                        _expandedCreators.Add(groupVm.CreatorName);
                    else
                        _expandedCreators.Remove(groupVm.CreatorName);
                }

                if (!_suppressGroupExpandThumbnailLoad && groupVm.IsExpanded && IsThumbnailCreatorView)
                    LoadVisibleExpandedThumbnails();
            };

            FilteredCreatorGroups.Add(groupVm);
        }

        CreatorGroupCount = FilteredCreatorGroups.Count;

        if (IsThumbnailCreatorView)
            LoadVisibleExpandedThumbnails();
    }

    private void SetAllCreatorGroupsExpanded(bool expanded)
    {
        _suppressGroupExpandThumbnailLoad = true;
        try
        {
            if (!expanded)
                _expandedCreators.Clear();

            foreach (CreatorGroupViewModel group in FilteredCreatorGroups)
            {
                group.IsExpanded = expanded;
                if (expanded)
                    _expandedCreators.Add(group.CreatorName);
            }
        }
        finally
        {
            _suppressGroupExpandThumbnailLoad = false;
        }

        if (expanded && IsThumbnailCreatorView)
            LoadVisibleExpandedThumbnails();
        else if (!expanded)
            CancelThumbnailLoads();
    }

    private static string ThumbnailCacheKey(ModItemViewModel mod) =>
        !string.IsNullOrWhiteSpace(mod.PrimaryFilePath) ? mod.PrimaryFilePath : mod.Name;

    private BitmapSource? DecodeThumbnailBitmap(ModUnit unit)
    {
        byte[]? bytes = _metadataExtractor.ExtractThumbnailBytesForUnit(unit);
        if (bytes is null || bytes.Length == 0)
            return null;

        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 200;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateInspectorThumbnail(ModItemViewModel vm) =>
        LoadSingleThumbnailAsync(
            vm,
            bmp => SelectedUnitThumbnail = bmp,
            () => ReferenceEquals(SelectedExplorerUnit, vm));

    private void UpdateCategoryInspectorThumbnail(ModItemViewModel vm) =>
        LoadSingleThumbnailAsync(
            vm,
            bmp => SelectedCategoryThumbnail = bmp,
            () => ReferenceEquals(SelectedCategoryMod, vm));

    private void LoadSingleThumbnailAsync(ModItemViewModel vm, Action<BitmapSource?> applyThumbnail, Func<bool> isStillCurrent)
    {
        if (vm.IsThumbnailChecked)
        {
            applyThumbnail(vm.ThumbnailImage);
            return;
        }

        string key = ThumbnailCacheKey(vm);
        if (_thumbnailCache.TryGetValue(key, out BitmapSource? cached))
        {
            vm.SetThumbnail(cached);
            applyThumbnail(cached);
            return;
        }

        applyThumbnail(null);
        Dispatcher? uiDispatcher = Application.Current?.Dispatcher;
        SynchronizationContext? uiSync = SynchronizationContext.Current;
        ModUnit unit = vm.Unit;

        _ = Task.Run(() =>
        {
            BitmapSource? bmp = DecodeThumbnailBitmap(unit);
            void Apply()
            {
                _thumbnailCache[key] = bmp;
                vm.SetThumbnail(bmp);
                if (isStillCurrent())
                    applyThumbnail(bmp);
            }

            if (uiDispatcher is not null)
                uiDispatcher.BeginInvoke(DispatcherPriority.Normal, Apply);
            else if (uiSync is not null)
                uiSync.Post(_ => Apply(), null);
            else
                Apply();
        });
    }

    private void CancelThumbnailLoads()
    {
        _thumbnailLoadCts?.Cancel();
        _thumbnailLoadCts?.Dispose();
        _thumbnailLoadCts = null;
    }

    private void LoadVisibleExpandedThumbnails()
    {
        CancelThumbnailLoads();

        var pending = new List<ModItemViewModel>();
        foreach (CreatorGroupViewModel group in FilteredCreatorGroups)
        {
            if (!group.IsExpanded)
                continue;

            foreach (ModItemViewModel mod in group.Mods)
            {
                if (mod.IsThumbnailChecked)
                    continue;

                string key = ThumbnailCacheKey(mod);
                if (_thumbnailCache.TryGetValue(key, out BitmapSource? cached))
                {
                    mod.SetThumbnail(cached);
                    continue;
                }

                pending.Add(mod);
            }
        }

        if (pending.Count == 0)
            return;

        var cts = new CancellationTokenSource();
        _thumbnailLoadCts = cts;
        CancellationToken ct = cts.Token;
        Dispatcher? uiDispatcher = Application.Current?.Dispatcher;
        SynchronizationContext? uiSync = SynchronizationContext.Current;

        _ = Task.Run(() =>
        {
            const int BatchSize = 6;
            var batch = new List<(ModItemViewModel Mod, string Key, BitmapSource? Bitmap)>(BatchSize);

            void FlushBatch(List<(ModItemViewModel Mod, string Key, BitmapSource? Bitmap)> toApply)
            {
                void Apply()
                {
                    if (ct.IsCancellationRequested)
                        return;

                    foreach ((ModItemViewModel mod, string key, BitmapSource? bmp) in toApply)
                    {
                        _thumbnailCache[key] = bmp;
                        mod.SetThumbnail(bmp);
                    }
                }

                if (uiDispatcher is not null)
                    uiDispatcher.BeginInvoke(DispatcherPriority.Background, Apply);
                else if (uiSync is not null)
                    uiSync.Post(_ => Apply(), null);
                else
                    Apply();
            }

            foreach (ModItemViewModel mod in pending)
            {
                if (ct.IsCancellationRequested)
                    break;

                string key = ThumbnailCacheKey(mod);
                BitmapSource? bmp = DecodeThumbnailBitmap(mod.Unit);
                batch.Add((mod, key, bmp));

                if (batch.Count >= BatchSize)
                {
                    FlushBatch(batch.ToList());
                    batch.Clear();
                }
            }

            if (batch.Count > 0 && !ct.IsCancellationRequested)
                FlushBatch(batch);
        }, ct);
    }

    private List<string> GetExistingCreatorsSorted()
    {
        return AllModUnits
            .Where(m => m.IsCreatorKnown && !string.IsNullOrWhiteSpace(m.CreatorName))
            .Select(m => m.CreatorName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void ReResolveAllCreators()
    {
        if (_creatorService is null)
            return;

        Dictionary<string, string> overrides = _creatorService.LoadOverrideLookup();
        HashSet<string> batchCreators = CreatorOrganizerService.BuildKnownBatchCreators(AllModUnits.Select(m => m.Unit));
        foreach (ModItemViewModel m in AllModUnits)
        {
            string url = !string.IsNullOrWhiteSpace(m.PageUrl) ? m.PageUrl : GetPinnedUrlForUnit(m.Unit);
            m.ApplyCreatorResolution(_creatorService.ResolveCreator(m.Unit, m.CachedMetadata, url, overrides, batchCreators));
        }

        RebuildCreatorGroups((ExplorerSearchText ?? string.Empty).Trim());
    }

    private void AssignCreatorForMod(ModItemViewModel? vm)
    {
        if (vm is null || _creatorService is null)
            return;

        (bool confirmed, bool revertToAuto, string creatorName)? result = _dialogs.PromptAssignCreator(
            vm.Name,
            vm.IsCreatorKnown ? vm.CreatorName : string.Empty,
            vm.AutoDetectedCreator,
            GetExistingCreatorsSorted(),
            isGroupRename: false,
            canRevert: vm.IsCreatorOverride);

        if (result is null || !result.Value.confirmed)
            return;

        if (result.Value.revertToAuto)
        {
            _creatorService.SetCreatorOverride(vm.Unit, null);
            StatusMessage = $"'{vm.Name}'의 제작자 지정을 해제하고 자동 인식 결과로 되돌렸습니다.";
        }
        else
        {
            string trimmed = result.Value.creatorName.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return;

            _creatorService.SetCreatorOverride(vm.Unit, trimmed);
            _expandedCreators.Add(trimmed);
            StatusMessage = $"'{vm.Name}'의 제작자를 '{trimmed}'(으)로 지정했습니다.";
        }

        ReResolveAllCreators();
        SelectedExplorerUnit = vm;
        if (!string.IsNullOrWhiteSpace(CategorySearchText))
            ApplyCategoryFilters(keepSelection: true);
    }

    private void RenameCreatorGroup(CreatorGroupViewModel? group)
    {
        if (group is null || _creatorService is null || group.Mods.Count == 0)
            return;

        (bool confirmed, bool _, string creatorName)? result = _dialogs.PromptAssignCreator(
            group.CreatorName,
            group.IsUnknownGroup ? string.Empty : group.CreatorName,
            string.Empty,
            GetExistingCreatorsSorted(),
            isGroupRename: true,
            canRevert: false);

        if (result is null || !result.Value.confirmed || string.IsNullOrWhiteSpace(result.Value.creatorName))
            return;

        string targetCreator = result.Value.creatorName.Trim();
        _creatorService.RenameOrMergeCreator(group.Mods.Select(m => m.Unit), targetCreator);
        _expandedCreators.Add(targetCreator);

        ReResolveAllCreators();
        StatusMessage = $"'{group.CreatorName}' 그룹의 모드 {group.ModCount}개를 '{targetCreator}'(으)로 묶었습니다.";
    }

    private async Task OpenOrganizeByCreatorAsync()
    {
        if (_creatorService is null || string.IsNullOrWhiteSpace(_config.ModsFolder) || !Directory.Exists(_config.ModsFolder))
        {
            _dialogs.ShowError("Mods 폴더 없음", "유효한 심즈 4 Mods 폴더가 설정되어 있지 않습니다.");
            return;
        }

        (Dictionary<string, ModMetadata> metadataCache, Dictionary<string, string> urlCache, List<ModUnit> unitSnapshot) =
            BuildOrganizerLookupCaches();

        (bool confirmed, OrganizePlanSummary? plan, bool createBackup) = _dialogs.PromptOrganizeByCreator(
            (layout, skipUnknown, deleteIdenticalDuplicates) => _creatorService.BuildOrganizePlan(
                _config.ModsFolder,
                unitSnapshot,
                layout,
                skipUnknown,
                metadataCache,
                urlCache,
                deleteIdenticalDuplicates),
            isTargetedFolderMode: false);

        if (!confirmed || plan is null)
            return;

        if (plan.TotalMoves == 0 && plan.DuplicateCleanCount == 0)
        {
            _dialogs.ShowInfo("폴더 정리", "이동하거나 정리할 중복 파일이 없으며 이미 제작자 규칙대로 정리되어 있습니다.");
            return;
        }

        await ExecuteOrganizePlanWithUiAsync(plan, createBackup, isTargetedFolderMode: false, selectedSourceFolder: null);
    }

    private async Task OrganizeTargetFolderAsync()
    {
        if (_creatorService is null || string.IsNullOrWhiteSpace(_config.ModsFolder) || !Directory.Exists(_config.ModsFolder))
        {
            _dialogs.ShowError("Mods 폴더 없음", "유효한 심즈 4 Mods 폴더가 설정되어 있지 않습니다.");
            return;
        }

        string? selectedFolder = _dialogs.BrowseFolder(
            "정리할 모드 폴더 선택 (예: Mods\\test, test2, 새로 받은 모드 폴더)",
            _config.ModsFolder);

        if (string.IsNullOrWhiteSpace(selectedFolder) || !Directory.Exists(selectedFolder))
            return;

        string modsFull = Path.GetFullPath(_config.ModsFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string selectedFull = Path.GetFullPath(selectedFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(selectedFull, modsFull, StringComparison.OrdinalIgnoreCase))
        {
            await OpenOrganizeByCreatorAsync();
            return;
        }

        if (modsFull.StartsWith(selectedFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.ShowError(
                "선택 불가 폴더",
                "Mods 폴더의 상위 폴더는 지정할 수 없습니다.\nMods 내부의 추가 폴더(예: Mods\\test)나 별도의 모드 폴더를 선택해 주세요.");
            return;
        }

        if (string.Equals(selectedFull, Path.Combine(modsFull, CreatorOrganizerService.OverridesFolderName), StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.ShowError(
                "선택 불가 폴더",
                "Overrides 폴더는 한글패치(!WAMTIK/PersonalKO) 등 최우선 적용 모드 전용 폴더입니다. 다른 폴더를 선택해 주세요.");
            return;
        }

        (_, Dictionary<string, string> urlCache, _) = BuildOrganizerLookupCaches();
        CreatorOrganizerService creatorService = _creatorService;
        string modsFolder = _config.ModsFolder;

        (bool confirmed, OrganizePlanSummary? plan, bool createBackup) = _dialogs.PromptOrganizeByCreator(
            (layout, skipUnknown, deleteIdenticalDuplicates) => creatorService.BuildFolderOrganizePlan(
                modsFolder,
                selectedFull,
                layout,
                skipUnknown,
                deleteIdenticalDuplicates,
                deleteEmptyTargetFolder: true,
                _metadataExtractor,
                _scanner,
                urlCache),
            isTargetedFolderMode: true,
            targetFolderDisplay: selectedFull);

        if (!confirmed || plan is null)
            return;

        if (plan.TotalMoves == 0 && plan.DuplicateCleanCount == 0)
        {
            _dialogs.ShowInfo("지정 폴더 정리", "지정한 폴더에 이동하거나 정리할 모드 파일이 없습니다.");
            return;
        }

        await ExecuteOrganizePlanWithUiAsync(plan, createBackup, isTargetedFolderMode: true, selectedSourceFolder: selectedFull);
    }

    private (Dictionary<string, ModMetadata> MetadataCache, Dictionary<string, string> UrlCache, List<ModUnit> Units) BuildOrganizerLookupCaches()
    {
        var metadataCache = new Dictionary<string, ModMetadata>(StringComparer.OrdinalIgnoreCase);
        var urlCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<ModUnit> unitSnapshot = new(AllModUnits.Count);

        foreach (ModItemViewModel m in AllModUnits)
        {
            unitSnapshot.Add(m.Unit);
            if (m.CachedMetadata is not null)
                metadataCache[m.PrimaryFilePath] = m.CachedMetadata;

            string url = !string.IsNullOrWhiteSpace(m.PageUrl) ? m.PageUrl : GetPinnedUrlForUnit(m.Unit);
            if (!string.IsNullOrWhiteSpace(url))
                urlCache[m.PrimaryFilePath] = url;
        }

        return (metadataCache, urlCache, unitSnapshot);
    }

    private async Task ExecuteOrganizePlanWithUiAsync(
        OrganizePlanSummary plan,
        bool createBackup,
        bool isTargetedFolderMode,
        string? selectedSourceFolder)
    {
        if (_creatorService is null)
            return;

        CreatorOrganizerService service = _creatorService;
        BackupService? backupService = _backupService;
        string busyText = isTargetedFolderMode
            ? "지정 폴더의 모드를 기존 제작자 폴더로 통합 정리 중..."
            : "제작자별 폴더 구조로 정리 중...";

        BeginOperation(1, indeterminate: true, initialStatus: busyText);
        OrganizeExecutionResult execResult;
        try
        {
            execResult = await Task.Run(() => service.ExecuteOrganizePlan(plan, backupService, createBackup));
        }
        finally
        {
            EndOperation();
        }

        ReloadModsData();

        var summaryLines = new List<string>
        {
            $"• 이동 완료 파일: {execResult.MovedFileCount}개"
        };
        if (execResult.DeletedDuplicateCount > 0)
            summaryLines.Add($"• 동일 중복 원본 삭제: {execResult.DeletedDuplicateCount}개");
        summaryLines.Add($"• 충돌·보호 건너뜀: {execResult.SkippedConflictCount}개");
        summaryLines.Add($"• 빈 폴더 정리: {execResult.CleanedEmptyFolderCount}개");

        if (isTargetedFolderMode && !string.IsNullOrWhiteSpace(selectedSourceFolder) && !Directory.Exists(selectedSourceFolder))
            summaryLines.Add($"• 비워진 지정 폴더 삭제 완료: {Path.GetFileName(selectedSourceFolder)}");

        if (!string.IsNullOrWhiteSpace(execResult.BackupSessionDir))
            summaryLines.Add($"• 사전 백업 위치: {execResult.BackupSessionDir}");

        if (execResult.Warnings.Count > 0)
        {
            summaryLines.Add(string.Empty);
            summaryLines.Add("주의/건너뜀 상세 (최대 5건):");
            summaryLines.AddRange(execResult.Warnings.Take(5));
        }

        string title = isTargetedFolderMode ? "지정 폴더 정리 완료" : "제작자별 폴더 맞추기 완료";
        StatusMessage = $"{title}: 파일 {execResult.MovedFileCount}개 이동, 중복 삭제 {execResult.DeletedDuplicateCount}개, 빈 폴더 {execResult.CleanedEmptyFolderCount}개 정리";
        _dialogs.ShowInfo(title, string.Join(Environment.NewLine, summaryLines));
    }

    private async Task DeleteDuplicatesAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.ModsFolder) || !Directory.Exists(_config.ModsFolder))
        {
            _dialogs.ShowError("Mods 폴더 없음", "유효한 심즈 4 Mods 폴더가 설정되어 있지 않습니다.");
            return;
        }

        string modsFolder = _config.ModsFolder;
        DuplicateCleanupPlan initialPlan;
        BeginOperation(1, indeterminate: true, initialStatus: "중복 파일 및 내용(SHA-256) 일치 검사 중...");
        try
        {
            initialPlan = await Task.Run(() => _duplicateCleanupService.BuildCleanupPlan(modsFolder, includeContentHashDuplicates: true));
        }
        finally
        {
            EndOperation();
        }

        if (initialPlan.Items.Count == 0)
        {
            _dialogs.ShowInfo("중복 파일 일괄 삭제", $"총 {initialPlan.TotalFilesScanned}개의 모드 파일을 검사했으며, 삭제할 중복 파일이 없습니다.");
            return;
        }

        (bool confirmed, DuplicateCleanupPlan? confirmedPlan, IReadOnlyList<DuplicateDeletePlanItem> selectedItems, bool createBackup) =
            _dialogs.PromptDeleteDuplicates(
                includeHash => _duplicateCleanupService.BuildCleanupPlan(modsFolder, includeHash),
                initialPlan);

        if (!confirmed || confirmedPlan is null || selectedItems.Count == 0)
            return;

        BackupService? backupService = _backupService;
        BeginOperation(1, indeterminate: true, initialStatus: $"중복 파일 {selectedItems.Count}개 백업 및 삭제 중...");
        DuplicateCleanupResult result;
        try
        {
            result = await Task.Run(() => _duplicateCleanupService.ExecuteCleanup(confirmedPlan, selectedItems, backupService, createBackup));
        }
        finally
        {
            EndOperation();
        }

        ReloadModsData();

        string freedText = Views.DeleteDuplicatesDialog.FormatSize(result.FreedBytes);
        var summaryLines = new List<string>
        {
            $"• 삭제 완료 중복 파일: {result.DeletedCount}개 ({freedText} 확보)",
            $"• 건너뜀/보호: {result.SkippedCount}개",
            $"• 빈 폴더 정리: {result.CleanedEmptyFolderCount}개"
        };
        if (!string.IsNullOrWhiteSpace(result.BackupSessionDir))
            summaryLines.Add($"• 사전 백업 위치: {result.BackupSessionDir}");
        if (result.Warnings.Count > 0)
        {
            summaryLines.Add(string.Empty);
            summaryLines.Add("경고/건너뜀 상세 (최대 5건):");
            summaryLines.AddRange(result.Warnings.Take(5));
        }

        StatusMessage = $"중복 파일 {result.DeletedCount}개 일괄 삭제 완료 ({freedText} 확보)";
        _dialogs.ShowInfo("중복 파일 일괄 삭제 완료", string.Join(Environment.NewLine, summaryLines));
    }

    private async Task EnrichCreatorsFromMetadataAsync(
        List<ModItemViewModel> items,
        CancellationTokenSource localCts,
        CancellationToken ct)
    {
        CreatorOrganizerService? creatorService = _creatorService;
        SynchronizationContext? uiContext = SynchronizationContext.Current;
        IsResolvingCreators = creatorService is not null;

        try
        {
            var inputs = items
                .Select(vm => (Vm: vm, vm.Unit, Url: !string.IsNullOrWhiteSpace(vm.PageUrl) ? vm.PageUrl : GetPinnedUrlForUnit(vm.Unit)))
                .ToList();

            Dictionary<string, string> overrides = creatorService?.LoadOverrideLookup()
                                                   ?? new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> batchCreators = creatorService is not null
                ? CreatorOrganizerService.BuildKnownBatchCreators(items.Select(i => i.Unit))
                : new(StringComparer.OrdinalIgnoreCase);

            await Task.Run(() =>
            {
                const int batchSize = 24;
                var batch = new List<(ModItemViewModel Vm, ModMetadata Meta, CreatorResolutionResult? Res, ModCategoryClassification? CatRes)>(batchSize);

                foreach (var entry in inputs)
                {
                    ct.ThrowIfCancellationRequested();

                    ModMetadata meta = entry.Vm.CachedMetadata ?? _metadataExtractor.Extract(entry.Unit.PrimaryFilePath);
                    CreatorResolutionResult? res = creatorService?.ResolveCreator(entry.Unit, meta, entry.Url, overrides, batchCreators);
                    ModCategoryClassification? catRes = entry.Vm.FunctionalCategory == ModFunctionalCategory.Pending
                        ? _metadataExtractor.ClassifyFunctionalCategory(entry.Unit)
                        : null;
                    batch.Add((entry.Vm, meta, res, catRes));

                    if (batch.Count >= batchSize)
                    {
                        var flush = batch.ToList();
                        batch.Clear();
                        PostEnrichBatch(flush, ct);
                    }
                }

                if (batch.Count > 0)
                    PostEnrichBatch(batch, ct);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation on reload or close
        }
        finally
        {
            if (ReferenceEquals(_metadataEnrichCts, localCts))
                IsResolvingCreators = false;
        }

        void PostEnrichBatch(
            List<(ModItemViewModel Vm, ModMetadata Meta, CreatorResolutionResult? Res, ModCategoryClassification? CatRes)> batch,
            CancellationToken token)
        {
            void ApplyOnUi()
            {
                if (token.IsCancellationRequested)
                    return;

                bool groupChanged = false;
                bool categoryChanged = false;
                foreach (var item in batch)
                {
                    item.Vm.CachedMetadata = item.Meta;
                    if (item.CatRes is not null)
                    {
                        if (item.Vm.FunctionalCategory != item.CatRes.Category)
                            categoryChanged = true;
                        item.Vm.ApplyFunctionalClassification(item.CatRes);
                    }

                    if (item.Res is null || item.Vm.IsCreatorOverride)
                        continue;

                    string before = item.Vm.CreatorName;
                    item.Vm.ApplyCreatorResolution(item.Res);
                    if (!string.Equals(before, item.Vm.CreatorName, StringComparison.OrdinalIgnoreCase))
                        groupChanged = true;
                }

                if (groupChanged)
                    RebuildCreatorGroups((ExplorerSearchText ?? string.Empty).Trim());

                RefreshCategoryCounts();
                NotifyUpdateScopeProperties();
                bool hasCategoryQuery = CategoryFilter != CategoryFilterMode.All || !string.IsNullOrWhiteSpace(CategorySearchText);
                if (hasCategoryQuery && (categoryChanged || groupChanged))
                    ApplyCategoryFilters(keepSelection: true);

                bool updateNeedsRefresh = (categoryChanged && UpdateScope == UpdateScopeMode.ScriptAndXml) ||
                                          (groupChanged && !string.IsNullOrWhiteSpace(UpdateSearchText));
                if (updateNeedsRefresh)
                    ApplyUpdateFilters();
            }

            if (uiContext is not null)
                uiContext.Post(_ => ApplyOnUi(), null);
            else if (Application.Current?.Dispatcher is { } disp)
                disp.BeginInvoke(ApplyOnUi);
            else
                ApplyOnUi();
        }
    }
}
