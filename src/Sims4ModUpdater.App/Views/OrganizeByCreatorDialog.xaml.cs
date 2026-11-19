using System.Windows;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Views;

public partial class OrganizeByCreatorDialog : Window
{
    private readonly Func<CreatorOrganizeLayout, bool, bool, OrganizePlanSummary> _planBuilder;
    private OrganizePlanSummary? _currentPlan;

    public OrganizePlanSummary? ConfirmedPlan { get; private set; }
    public bool CreateBackup => ChkCreateBackup.IsChecked == true;

    public OrganizeByCreatorDialog(Func<CreatorOrganizeLayout, bool, OrganizePlanSummary> planBuilder)
        : this(
            (layout, skipUnknown, _) => (planBuilder ?? throw new ArgumentNullException(nameof(planBuilder)))(layout, skipUnknown),
            isTargetedFolderMode: false,
            targetFolderDisplay: null)
    {
    }

    public OrganizeByCreatorDialog(
        Func<CreatorOrganizeLayout, bool, bool, OrganizePlanSummary> planBuilder,
        bool isTargetedFolderMode = false,
        string? targetFolderDisplay = null)
    {
        _planBuilder = planBuilder ?? throw new ArgumentNullException(nameof(planBuilder));
        InitializeComponent();

        ChkDeleteIdenticalDuplicates.IsChecked = isTargetedFolderMode;

        if (isTargetedFolderMode)
        {
            Title = "지정 폴더 정리 (기존 Mods 제작자 폴더로 통합)";
            TxtHeaderTitle.Text = "지정 폴더 정리 (기존 Mods 폴더로 안전 통합)";
            TxtHeaderSubtitle.Text = string.IsNullOrWhiteSpace(targetFolderDisplay)
                ? "지정한 폴더의 모드들을 기존 Mods 제작자 폴더로 통합하고, 미분류 파일은 Mods\\Unknown으로 이동한 뒤 빈 원본 폴더를 삭제합니다. 한글패치(!WAMTIK/PersonalKO)는 Overrides로, Resource.cfg는 Mods 루트에 보존됩니다."
                : $"대상 폴더: {targetFolderDisplay}  →  기존 제작자 폴더에 통합하고 미분류는 Mods\\Unknown으로 이동한 뒤 빈 원본 폴더를 삭제합니다. 한글패치(!WAMTIK/PersonalKO)는 Overrides로, Resource.cfg는 Mods 루트에 보존됩니다.";
            BtnExecute.Content = "미리보기 대로 지정 폴더 정리 실행";
        }

        RebuildPreview();
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        RebuildPreview();
    }

    private void RebuildPreview()
    {
        CreatorOrganizeLayout layout = RbPrefixedModFolder.IsChecked == true
            ? CreatorOrganizeLayout.CreatorPrefixedModFolder
            : CreatorOrganizeLayout.CreatorSingleFolder;
        bool skipUnknown = ChkMoveUnknownToUnknownFolder.IsChecked != true;
        bool deleteIdenticalDuplicates = ChkDeleteIdenticalDuplicates.IsChecked == true;

        _currentPlan = _planBuilder(layout, skipUnknown, deleteIdenticalDuplicates);
        GridPreview.ItemsSource = _currentPlan.Items;

        TxtPlanSummary.Text =
            $"이동 예정: {_currentPlan.TotalMoves}개  |  중복 자동 삭제: {_currentPlan.DuplicateCleanCount}개  |  이미 정리됨: {_currentPlan.AlreadyOrganizedCount}개  |  미분류 유지: {_currentPlan.SkippedUnknownCount}유닛";

        if (_currentPlan.ConflictCount > 0 || _currentPlan.DepthWarningCount > 0)
        {
            TxtConflictNotice.Text =
                $"⚠️ 충돌/위험 {_currentPlan.ConflictCount + _currentPlan.DepthWarningCount}건 (안전 보호를 위해 덮어쓰지 않고 자동 건너뜀)";
        }
        else
        {
            TxtConflictNotice.Text = "✅ 파일 충돌 및 스크립트 깊이 위반 0건";
        }

        BtnExecute.IsEnabled = _currentPlan.TotalMoves > 0 || _currentPlan.DuplicateCleanCount > 0;
    }

    private void OnExecuteClick(object sender, RoutedEventArgs e)
    {
        if (_currentPlan is null || (_currentPlan.TotalMoves == 0 && _currentPlan.DuplicateCleanCount == 0))
            return;

        ConfirmedPlan = _currentPlan;
        DialogResult = true;
    }
}
