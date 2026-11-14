using System.Windows;

namespace Sims4ModUpdater.Views;

public partial class PinUrlDialog : Window
{
    public string ResultUrl { get; private set; } = string.Empty;
    public bool RemoveRequested { get; private set; }

    public PinUrlDialog(string modName, string currentUrl, bool isCurrentlyPinned)
    {
        InitializeComponent();
        TxtModName.Text = $"대상 모드: {modName}";
        TxtUrl.Text = currentUrl ?? string.Empty;
        if (isCurrentlyPinned)
            BtnRemovePin.Visibility = Visibility.Visible;

        Loaded += (_, _) =>
        {
            TxtUrl.Focus();
            TxtUrl.SelectAll();
        };
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string raw = (TxtUrl.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw) ||
            (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
             !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            TxtValidationError.Text = "http:// 또는 https:// 로 시작하는 올바른 URL을 입력하세요.";
            TxtValidationError.Visibility = Visibility.Visible;
            return;
        }

        if (raw.Contains("google.com/search", StringComparison.OrdinalIgnoreCase))
        {
            TxtValidationError.Text = "구글 검색 결과 URL은 고정할 수 없습니다. 실제 배포 페이지를 입력하세요.";
            TxtValidationError.Visibility = Visibility.Visible;
            return;
        }

        ResultUrl = raw;
        RemoveRequested = false;
        DialogResult = true;
    }

    private void OnRemovePinClick(object sender, RoutedEventArgs e)
    {
        ResultUrl = string.Empty;
        RemoveRequested = true;
        DialogResult = true;
    }
}
