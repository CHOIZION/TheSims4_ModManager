using System.Collections.ObjectModel;
using System.Windows;
using Sims4ModUpdater.Core.Models;
using Sims4ModUpdater.Infrastructure;

namespace Sims4ModUpdater.Views;

public partial class DeleteDuplicatesDialog : Window
{
    private readonly Func<bool, DuplicateCleanupPlan> _planBuilder;
    private readonly ObservableCollection<DuplicateDeleteRowViewModel> _rows = new();
    private DuplicateCleanupPlan? _currentPlan;

    public DuplicateCleanupPlan? ConfirmedPlan { get; private set; }
    public IReadOnlyList<DuplicateDeletePlanItem> SelectedItems { get; private set; } = Array.Empty<DuplicateDeletePlanItem>();
    public bool CreateBackup => ChkCreateBackup.IsChecked == true;

    public DeleteDuplicatesDialog(
        Func<bool, DuplicateCleanupPlan> planBuilder,
        DuplicateCleanupPlan? initialPlan = null)
    {
        _planBuilder = planBuilder ?? throw new ArgumentNullException(nameof(planBuilder));
        InitializeComponent();
        GridDuplicates.ItemsSource = _rows;

        if (initialPlan is not null)
            ChkIncludeHashDuplicates.IsChecked = initialPlan.IncludeContentHashDuplicates;

        LoadPlan(initialPlan ?? RebuildPlanFromOptions());
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        LoadPlan(RebuildPlanFromOptions());
    }

    private DuplicateCleanupPlan RebuildPlanFromOptions()
    {
        bool includeHash = ChkIncludeHashDuplicates.IsChecked == true;
        return _planBuilder(includeHash);
    }

    private void LoadPlan(DuplicateCleanupPlan plan)
    {
        _currentPlan = plan;
        _rows.Clear();

        foreach (DuplicateDeletePlanItem item in plan.Items)
        {
            _rows.Add(new DuplicateDeleteRowViewModel(item, UpdateSelectionSummary));
        }

        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        if (_currentPlan is null)
            return;

        int selectedCount = 0;
        long freedBytes = 0;
        foreach (DuplicateDeleteRowViewModel row in _rows)
        {
            if (row.IsSelected)
            {
                selectedCount++;
                freedBytes += row.Model.FileSizeBytes;
            }
        }

        string freedText = FormatSize(freedBytes);
        TxtSummary.Text =
            $"스캔 파일: {_currentPlan.TotalFilesScanned}개  |  중복 그룹: {_currentPlan.DuplicateGroupCount}개  |  삭제 선택: {selectedCount} / {_rows.Count}개  (확보 용량: {freedText})";

        BtnExecute.IsEnabled = selectedCount > 0;
        BtnExecute.Content = selectedCount > 0
            ? $"선택한 중복 파일 {selectedCount}개 일괄 삭제"
            : "삭제할 파일 없음";
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (DuplicateDeleteRowViewModel row in _rows)
            row.SetSelectedSilent(true);
        UpdateSelectionSummary();
    }

    private void OnSelectExactOnlyClick(object sender, RoutedEventArgs e)
    {
        foreach (DuplicateDeleteRowViewModel row in _rows)
            row.SetSelectedSilent(row.Model.IsExactContentMatch);
        UpdateSelectionSummary();
    }

    private void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        foreach (DuplicateDeleteRowViewModel row in _rows)
            row.SetSelectedSilent(false);
        UpdateSelectionSummary();
    }

    private void OnExecuteClick(object sender, RoutedEventArgs e)
    {
        if (_currentPlan is null)
            return;

        List<DuplicateDeletePlanItem> selected = _rows
            .Where(r => r.IsSelected)
            .Select(r => r.Model)
            .ToList();

        if (selected.Count == 0)
            return;

        ConfirmedPlan = _currentPlan;
        SelectedItems = selected;
        DialogResult = true;
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
            return $"{bytes / (1024d * 1024d * 1024d):0.00} GB";
        if (bytes >= 1024L * 1024L)
            return $"{bytes / (1024d * 1024d):0.00} MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} B";
    }

    private sealed class DuplicateDeleteRowViewModel : ObservableObject
    {
        private readonly Action _onSelectionChanged;
        private bool _isSelected;

        public DuplicateDeleteRowViewModel(DuplicateDeletePlanItem model, Action onSelectionChanged)
        {
            Model = model;
            _onSelectionChanged = onSelectionChanged;
            _isSelected = model.IsSelected;
            FileSizeDisplay = FormatSize(model.FileSizeBytes);
        }

        public DuplicateDeletePlanItem Model { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value))
                {
                    Model.IsSelected = value;
                    _onSelectionChanged();
                }
            }
        }

        public string MatchType => Model.MatchType;
        public string KeepRelativePath => Model.KeepRelativePath;
        public string DeleteRelativePath => Model.DeleteRelativePath;
        public string FileSizeDisplay { get; }

        public void SetSelectedSilent(bool value)
        {
            Model.IsSelected = value;
            SetProperty(ref _isSelected, value, nameof(IsSelected));
        }
    }
}
