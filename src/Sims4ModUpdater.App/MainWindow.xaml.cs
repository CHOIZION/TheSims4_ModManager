using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Sims4ModUpdater.Core.Models;
using Sims4ModUpdater.ViewModels;
using Sims4ModUpdater.Views;

namespace Sims4ModUpdater;

public partial class MainWindow : Window, IAppDialogService
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        App.SmokeTrace?.Invoke("MainWindow: calling InitializeComponent()...");
        InitializeComponent();
        App.SmokeTrace?.Invoke("MainWindow: InitializeComponent() done, creating MainViewModel...");
        _viewModel = new MainViewModel(this);
        App.SmokeTrace?.Invoke("MainWindow: MainViewModel created, assigning DataContext...");
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.CancelBackgroundWork();
        App.SmokeTrace?.Invoke("MainWindow: constructor complete");
    }

    public void ShowInfo(string title, string message)
        => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message)
        => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string title, string message)
        => MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? BrowseFolder(string title, string initialPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = !string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath)
                ? initialPath
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    public string? SaveTextFile(string title, string defaultFileName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = defaultFileName,
            Filter = filter
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ShowError("URL 열기 실패", $"유효한 HTTP/HTTPS URL이 아닙니다: {url}");
            return;
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void OpenPathInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        else if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
    }

    public void CopyToClipboard(string text)
        => Clipboard.SetText(text ?? string.Empty);

    public (bool Confirmed, bool RemovePin, string Url)? PromptPinUrl(string modName, string currentUrl, bool isCurrentlyPinned)
    {
        var dlg = new PinUrlDialog(modName, currentUrl, isCurrentlyPinned) { Owner = this };
        return dlg.ShowDialog() == true
            ? (true, dlg.RemoveRequested, dlg.ResultUrl)
            : (false, false, string.Empty);
    }

    public VariantChoice? PromptVariantChoice(string modName, IReadOnlyList<VariantChoice> choices)
    {
        var dlg = new VariantChoiceDialog(modName, choices) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.SelectedChoice : null;
    }

    public (bool Confirmed, OverwriteMode Mode, bool ApplyToBatch) PromptOverwriteMode(string modName, bool isBatch)
    {
        var dlg = new OverwriteModeDialog(modName, isBatch) { Owner = this };
        return dlg.ShowDialog() == true
            ? (true, dlg.SelectedMode, dlg.ApplyToBatch)
            : (false, OverwriteMode.All, false);
    }

    public (bool Confirmed, bool RevertToAuto, string CreatorName)? PromptAssignCreator(
        string targetTitle,
        string currentCreator,
        string autoHint,
        IReadOnlyList<string> existingCreators,
        bool isGroupRename,
        bool canRevert)
    {
        var dlg = new AssignCreatorDialog(targetTitle, currentCreator, autoHint, existingCreators, isGroupRename, canRevert)
        {
            Owner = this
        };
        return dlg.ShowDialog() == true
            ? (true, dlg.RevertToAutoRequested, dlg.ResultCreator)
            : (false, false, string.Empty);
    }

    public (bool Confirmed, OrganizePlanSummary? Plan, bool CreateBackup) PromptOrganizeByCreator(
        Func<CreatorOrganizeLayout, bool, OrganizePlanSummary> planBuilder)
    {
        var dlg = new OrganizeByCreatorDialog(planBuilder) { Owner = this };
        return dlg.ShowDialog() == true
            ? (true, dlg.ConfirmedPlan, dlg.CreateBackup)
            : (false, null, true);
    }
}
