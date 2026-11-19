using System.Windows;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Views;

public partial class OverwriteModeDialog : Window
{
    public OverwriteMode SelectedMode { get; private set; } = OverwriteMode.All;
    public bool ApplyToBatch => ChkApplyAllBatch.IsChecked == true;

    public OverwriteModeDialog(string modName, bool isBatch)
    {
        InitializeComponent();
        TxtPrompt.Text = $"'{modName}' 업데이트 시 백업 후 새 파일을 설치할 방식을 선택하세요.";
        if (isBatch)
            ChkApplyAllBatch.Visibility = Visibility.Visible;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        SelectedMode = RbSingle.IsChecked == true ? OverwriteMode.Single : OverwriteMode.All;
        DialogResult = true;
    }
}
