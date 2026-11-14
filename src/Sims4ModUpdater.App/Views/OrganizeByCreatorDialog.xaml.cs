using System.Windows;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Views;

public partial class OrganizeByCreatorDialog : Window
{
    private readonly Func<CreatorOrganizeLayout, bool, OrganizePlanSummary> _planBuilder;
    private OrganizePlanSummary? _currentPlan;

    public OrganizePlanSummary? ConfirmedPlan { get; private set; }
    public bool CreateBackup => ChkCreateBackup.IsChecked == true;

    public OrganizeByCreatorDialog(Func<CreatorOrganizeLayout, bool, OrganizePlanSummary> planBuilder)
    {
        _planBuilder = planBuilder ?? throw new ArgumentNullException(nameof(planBuilder));
        InitializeComponent();
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
        bool skipUnknown = ChkSkipUnknown.IsChecked == true;

        _currentPlan = _planBuilder(layout, skipUnknown);
        GridPreview.ItemsSource = _currentPlan.Items;

        TxtPlanSummary.Text =
            $"이동 예정 파일: {_currentPlan.TotalMoves}개  |  이미 정리됨: {_currentPlan.AlreadyOrganizedCount}개  |  미분류 제외: {_currentPlan.SkippedUnknownCount}유닛";

        if (_currentPlan.ConflictCount > 0 || _currentPlan.DepthWarningCount > 0)
        {
            TxtConflictNotice.Text =
                $"⚠️ 충돌/위험 {_currentPlan.ConflictCount + _currentPlan.DepthWarningCount}건 (안전 보호를 위해 덮어쓰지 않고 자동 건너뜀)";
        }
        else
        {
            TxtConflictNotice.Text = "✅ 파일 충돌 및 스크립트 깊이 위반 0건";
        }

        BtnExecute.IsEnabled = _currentPlan.TotalMoves > 0;
    }

    private void OnExecuteClick(object sender, RoutedEventArgs e)
    {
        if (_currentPlan is null || _currentPlan.TotalMoves == 0)
            return;

        ConfirmedPlan = _currentPlan;
        DialogResult = true;
    }
}
