using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sims4ModUpdater.Core.Services;

namespace Sims4ModUpdater.Views;

public partial class AssignCreatorDialog : Window
{
    private readonly List<string> _allExistingCreators;
    private bool _suppressTextFilter;

    public string ResultCreator { get; private set; } = string.Empty;
    public bool RevertToAutoRequested { get; private set; }

    public AssignCreatorDialog(
        string targetTitle,
        string currentCreator,
        string autoHint,
        IReadOnlyList<string> existingCreators,
        bool isGroupRename,
        bool canRevert)
    {
        InitializeComponent();

        _allExistingCreators = existingCreators
            .Where(c => !string.IsNullOrWhiteSpace(c) && !c.Equals(CreatorOrganizerService.UnknownCreatorLabel, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        TxtDialogTitle.Text = isGroupRename ? "제작자 그룹 이름 변경 / 통합" : "모드 제작자 지정 · 변경";
        TxtTargetInfo.Text = isGroupRename
            ? $"대상 그룹: {targetTitle} (다른 기존 제작자 이름을 선택/입력하면 하나로 합쳐집니다)"
            : $"대상 모드: {targetTitle}";

        _suppressTextFilter = true;
        TxtCreatorInput.Text = currentCreator.Equals(CreatorOrganizerService.UnknownCreatorLabel, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : currentCreator;
        _suppressTextFilter = false;

        TxtAutoHint.Text = !string.IsNullOrWhiteSpace(autoHint)
            ? $"자동 감지 힌트: {autoHint}"
            : "자동 감지된 제작자 없음 — 원하는 제작자 이름을 직접 입력하거나 아래 목록에서 고르세요.";

        if (!isGroupRename && canRevert)
            BtnRevert.Visibility = Visibility.Visible;

        RefreshCreatorList(string.Empty);

        Loaded += (_, _) =>
        {
            TxtCreatorInput.Focus();
            TxtCreatorInput.SelectAll();
        };
    }

    private void RefreshCreatorList(string filter)
    {
        string q = (filter ?? string.Empty).Trim();
        var items = string.IsNullOrEmpty(q)
            ? _allExistingCreators
            : _allExistingCreators.Where(c => c.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        // Keep full list fallback if user typed a brand-new name not matching anything
        if (items.Count == 0 && !string.IsNullOrEmpty(q))
            items = _allExistingCreators;

        LstExistingCreators.ItemsSource = items;
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextFilter)
            return;
        RefreshCreatorList(TxtCreatorInput.Text);
    }

    private void OnCreatorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstExistingCreators.SelectedItem is string picked)
        {
            _suppressTextFilter = true;
            TxtCreatorInput.Text = picked;
            TxtCreatorInput.CaretIndex = picked.Length;
            _suppressTextFilter = false;
        }
    }

    private void OnCreatorDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstExistingCreators.SelectedItem is string picked)
        {
            ResultCreator = picked.Trim();
            RevertToAutoRequested = false;
            DialogResult = true;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string raw = (TxtCreatorInput.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            MessageBox.Show(this, "제작자 이름을 입력하거나 목록에서 선택하세요.", "입력 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResultCreator = raw;
        RevertToAutoRequested = false;
        DialogResult = true;
    }

    private void OnRevertClick(object sender, RoutedEventArgs e)
    {
        ResultCreator = string.Empty;
        RevertToAutoRequested = true;
        DialogResult = true;
    }
}
