using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Sims4ModUpdater.Core.Models;
using Sims4ModUpdater.ViewModels;
using Sims4ModUpdater.Views;

namespace Sims4ModUpdater;

public partial class App : Application
{
    internal static Action<string>? SmokeTrace { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        bool isSmokeTest = e.Args.Any(a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase));
        string? smokeLogPath = e.Args
            .FirstOrDefault(a => a.StartsWith("--smoke-log=", StringComparison.OrdinalIgnoreCase))
            ?["--smoke-log=".Length..];

        if (isSmokeTest)
        {
            RunSmokeTest(smokeLogPath);
            return;
        }

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }

    private void RunSmokeTest(string? smokeLogPath)
    {
        var sw = Stopwatch.StartNew();
        int exitCode = 0;

        void Trace(string msg)
        {
            if (string.IsNullOrWhiteSpace(smokeLogPath))
                return;
            try
            {
                File.AppendAllText(smokeLogPath, $"[{sw.ElapsedMilliseconds}ms] {msg}{Environment.NewLine}", new UTF8Encoding(false));
            }
            catch
            {
                // Ignore trace write error
            }
        }

        SmokeTrace = Trace;
        try
        {
            Trace("Starting MainWindow construction...");
            var mainWindow = new MainWindow();
            Trace("MainWindow constructed");

            mainWindow.Show();
            mainWindow.UpdateLayout();
            Trace("MainWindow.Show() + initial UpdateLayout() succeeded");

            if (mainWindow.DataContext is MainViewModel vm)
            {
                Trace($"Scan summary: Total={vm.TotalFileCount}, Pkg={vm.PackageCount}, Scr={vm.ScriptCount}, Units={vm.UpdatableUnitCount}, Creators={vm.CreatorGroupCount}, Warnings={vm.DepthWarnings.Count}");

                vm.ExpandAllCreatorsCommand.Execute(null);
                mainWindow.UpdateLayout();
                vm.CollapseAllCreatorsCommand.Execute(null);
                mainWindow.UpdateLayout();

                foreach (ExplorerViewMode ev in Enum.GetValues<ExplorerViewMode>())
                {
                    vm.ExplorerView = ev;
                    mainWindow.UpdateLayout();
                }

                foreach (MainTab tab in Enum.GetValues<MainTab>())
                {
                    vm.CurrentTab = tab;
                    mainWindow.UpdateLayout();
                    Trace($"Tab layout verified: {tab}");
                }

                // Verify all secondary dialogs instantiate cleanly
                _ = new PinUrlDialog("SmokeTestMod", "https://example.com", true);
                _ = new VariantChoiceDialog("SmokeTestMod", [new VariantChoice { Label = "Opt A", FileName = "a.zip", DownloadUrl = "https://example.com/a.zip" }]);
                _ = new OverwriteModeDialog("SmokeTestMod", isBatch: true);
                _ = new AssignCreatorDialog("SmokeTestMod", "Deaderpool", "Deaderpool", ["Deaderpool", "Turbodriver"], isGroupRename: false, canRevert: true);
                _ = new OrganizeByCreatorDialog((_, _) => new OrganizePlanSummary());
                Trace("All 5 modal dialog XAMLs instantiated cleanly");
            }

            mainWindow.Close();
            Trace("MainWindow closed cleanly");
        }
        catch (Exception ex)
        {
            exitCode = 1;
            Trace($"EXCEPTION: {ex}");
        }
        finally
        {
            SmokeTrace = null;
            Shutdown(exitCode);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"예기치 않은 UI 오류가 발생했습니다:\n{e.Exception.Message}",
            "심즈 4 모드 업데이터 오류",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
