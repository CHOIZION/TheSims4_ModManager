using System.Windows;
using System.Windows.Input;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Views;

public partial class VariantChoiceDialog : Window
{
    public VariantChoice? SelectedChoice { get; private set; }

    public VariantChoiceDialog(string modName, IReadOnlyList<VariantChoice> choices)
    {
        InitializeComponent();
        TxtDescription.Text = $"'{modName}' 배포 페이지에 여러 에디션/선택형 애드온 파일이 있습니다. 설치할 항목을 고르세요.";
        LstVariants.ItemsSource = choices;
        if (choices.Count > 0)
            LstVariants.SelectedIndex = 0;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (LstVariants.SelectedItem is VariantChoice chosen)
        {
            SelectedChoice = chosen;
            DialogResult = true;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstVariants.SelectedItem is VariantChoice chosen)
        {
            SelectedChoice = chosen;
            DialogResult = true;
        }
    }
}
