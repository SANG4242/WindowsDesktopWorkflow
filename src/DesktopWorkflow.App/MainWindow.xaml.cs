using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DesktopWorkflow.App;

public sealed class WorkflowListItemViewModel
{
    public WorkflowDefinition Definition { get; }
    public string Name => Definition.Name;
    public string SummaryText { get; }

    public WorkflowListItemViewModel(WorkflowDefinition definition, string effectiveHotkey)
    {
        Definition = definition;
        var winCount = $"{definition.Windows.Count} 个窗口";
        var hotkeyText = string.IsNullOrWhiteSpace(effectiveHotkey)
            ? "未设置快捷键"
            : GlobalHotkeyService.FormatForDisplay(effectiveHotkey);
        SummaryText = $"{winCount} · {hotkeyText}";
    }
}

public partial class MainWindow : Window
{
    private readonly App _app;
    private bool _allowClose;
    private bool _loading;
    private IReadOnlyList<WorkflowLoadDiagnostic> _diagnostics = [];

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
    }

    public void SetWorkflows(
        IReadOnlyList<WorkflowDefinition> workflows,
        IReadOnlyList<WorkflowLoadDiagnostic> diagnostics,
        string? selectedId = null)
    {
        _diagnostics = diagnostics;
        _loading = true;
        try
        {
            var items = workflows.Select(workflow =>
                new WorkflowListItemViewModel(workflow, _app.Settings.Get(workflow).Hotkey)).ToList();
            WorkflowListBox.ItemsSource = items;
            WorkflowListBox.SelectedItem = items.FirstOrDefault(item => item.Definition.Id == selectedId) ?? items.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }

        DiagnosticBanner.Visibility = diagnostics.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DiagnosticText.Text = diagnostics.Count == 0 ? string.Empty : $"有 {diagnostics.Count} 个配置文件需要修复，其余工作流仍可使用。";
        AutoStartMenuItem.IsChecked = _app.AutoStart.IsEnabled();

        LoadSelectedWorkflow();
        UpdateWorkflowState(_app.IsWorkflowRunning);
    }

    public void ShowAndActivate()
    {
        AutoStartMenuItem.IsChecked = _app.AutoStart.IsEnabled();
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void SetStartupEnabled(bool enabled) => AutoStartMenuItem.IsChecked = enabled;

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    private WorkflowDefinition? SelectedWorkflow => (WorkflowListBox.SelectedItem as WorkflowListItemViewModel)?.Definition;

    private void WorkflowListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            LoadSelectedWorkflow();
        }
    }

    private void LoadSelectedWorkflow()
    {
        var workflow = SelectedWorkflow;
        if (workflow is null)
        {
            DetailsPanel.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Visible;
            StatusText.Text = _diagnostics.Count > 0 ? "没有可用工作流，请查看配置诊断。" : "创建工作流后即可执行窗口排列。";
            return;
        }

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        DetailsPanel.Visibility = Visibility.Visible;

        SelectedTitleText.Text = workflow.Name;
        StatusBadgeText.Text = "可执行";
        var winNames = workflow.Windows.Select(w => w.Name).ToList();
        var modeDesc = workflow.Layout.Mode == "native" ? "Windows 原生贴靠" : "比例布局";
        SelectedSubtitleText.Text = $"{string.Join(" · ", winNames)} | {modeDesc}";
        var effectiveHotkey = _app.Settings.Get(workflow).Hotkey;
        SelectedHotkeyText.Text = string.IsNullOrWhiteSpace(effectiveHotkey)
            ? "未设置快捷键"
            : $"快捷键 {GlobalHotkeyService.FormatForDisplay(effectiveHotkey)}";
        StatusText.Text = string.Empty;

        // 计算工作区线框预览
        UpdateLayoutPreview(workflow);
    }

    private void UpdateLayoutPreview(WorkflowDefinition workflow)
    {
        var previewItems = new List<LayoutZoneItem>();
        if (workflow.Layout.Mode == "native")
        {
            var profile = NativeLayoutCatalog.FindById(_app.NativeLayoutProfiles, workflow.Layout.NativeProfileId);
            if (profile is null && string.IsNullOrWhiteSpace(workflow.Layout.NativeProfileId))
            {
                var legacy = NativeLayoutCatalog.FindByMenuNumber(NativeLayoutCatalog.CreateDefaults(), workflow.Layout.NativeLayout);
                if (legacy is not null) profile = NativeLayoutCatalog.FindById(_app.NativeLayoutProfiles, legacy.Id);
            }

            if (profile is not null)
            {
                for (var i = 0; i < profile.Zones.Count; i++)
                {
                    var zoneNumber = i + 1;
                    var assignedIndex = workflow.Layout.NativeZones.IndexOf(zoneNumber);
                    var winName = assignedIndex >= 0 && assignedIndex < workflow.Windows.Count
                        ? workflow.Windows[assignedIndex].Name
                        : $"区域 {zoneNumber}";
                    var rect = profile.Zones[i];
                    var ratioDesc = $"{Math.Round(rect.Width * 100)}% 宽度";
                    previewItems.Add(new LayoutZoneItem(rect, winName, $"区域 {zoneNumber} · {ratioDesc}", i == 0));
                }
            }
        }
        else
        {
            var ratios = workflow.Layout.Ratios.Count > 0 ? workflow.Layout.Ratios : [0.5, 0.5];
            var total = ratios.Sum();
            if (total <= 0) total = 1;
            var currentOffset = 0.0;

            for (var i = 0; i < ratios.Count; i++)
            {
                var proportion = ratios[i] / total;
                var rect = workflow.Layout.Orientation == "vertical"
                    ? new NormalizedRectangle(0, currentOffset, 1, proportion)
                    : new NormalizedRectangle(currentOffset, 0, proportion, 1);
                currentOffset += proportion;

                var winName = i < workflow.Windows.Count ? workflow.Windows[i].Name : $"区域 {i + 1}";
                previewItems.Add(new LayoutZoneItem(rect, winName, $"区域 {i + 1} · {ratios[i]}", i == 0));
            }
        }

        LayoutPreview.Items = previewItems;
        RefreshPreviewThumbnailsAsync(workflow);
    }

    private int _previewThumbnailVersion;

    private async void RefreshPreviewThumbnailsAsync(WorkflowDefinition workflow)
    {
        var version = ++_previewThumbnailVersion;
        var currentItems = LayoutPreview.Items.ToList();
        if (currentItems.Count == 0) return;

        var targets = new List<WindowDefinition?>();
        if (workflow.Layout.Mode == "native")
        {
            for (var i = 0; i < currentItems.Count; i++)
            {
                var zoneNumber = i + 1;
                var assignedIndex = workflow.Layout.NativeZones.IndexOf(zoneNumber);
                var target = assignedIndex >= 0 && assignedIndex < workflow.Windows.Count
                    ? workflow.Windows[assignedIndex]
                    : null;
                targets.Add(target);
            }
        }
        else
        {
            for (var i = 0; i < currentItems.Count; i++)
            {
                var target = i < workflow.Windows.Count ? workflow.Windows[i] : null;
                targets.Add(target);
            }
        }

        var thumbnails = await Task.Run(() =>
        {
            var results = new List<System.Windows.Media.ImageSource?>();
            foreach (var target in targets)
            {
                if (target is null)
                {
                    results.Add(null);
                    continue;
                }

                var hwnd = WindowMatcher.Find(target.Match, target.ProcessArgsContains);
                if (hwnd == 0 && !string.IsNullOrWhiteSpace(target.Exe))
                {
                    var exeName = System.IO.Path.GetFileName(target.Exe);
                    hwnd = WindowMatcher.FindByExecutable(exeName, target.ProcessArgsContains).FirstOrDefault();
                }

                results.Add(hwnd != 0 ? WindowThumbnailService.Capture(hwnd) : null);
            }
            return results;
        });

        if (version != _previewThumbnailVersion) return;

        if (thumbnails.Any(t => t is not null) && LayoutPreview.Items.Count == thumbnails.Count)
        {
            var updatedItems = new List<LayoutZoneItem>();
            for (var i = 0; i < LayoutPreview.Items.Count; i++)
            {
                updatedItems.Add(LayoutPreview.Items[i] with { Thumbnail = thumbnails[i] });
            }
            LayoutPreview.Items = updatedItems;
        }
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWorkflow is not { } workflow) return;

        try
        {
            StatusText.Text = "正在排列窗口……";
            await _app.RunWorkflowAsync(workflow.Id);
            StatusText.Text = "排列完成。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "排列已取消，本次操作已回滚。";
        }
        catch (Exception exception)
        {
            _app.ShowErrorDialog(exception.Message, "执行排列失败");
            StatusText.Text = "排列失败，本次操作已回滚；可查看日志了解具体阶段。";
        }
    }

    private void NewWorkflowButton_Click(object sender, RoutedEventArgs e)
    {
        var draft = new WorkflowDraft
        {
            Layout = new LayoutDraft { Ratios = [] }
        };
        OpenEditor(draft);
    }

    private void EditWorkflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWorkflow is { } workflow)
        {
            var draft = WorkflowDraft.FromDefinition(workflow);
            draft.Hotkey = _app.Settings.Get(workflow).Hotkey;
            OpenEditor(draft);
        }
    }

    private void CopyWorkflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWorkflow is not { } workflow) return;
        var draft = WorkflowDraft.FromDefinition(workflow);
        draft.Name += " 副本";
        draft.Id = WorkflowDraft.CreateId(draft.Name);
        draft.Hotkey = string.Empty;
        OpenEditor(draft, isCopy: true);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is not null)
        {
            MoreButton.ContextMenu.PlacementTarget = MoreButton;
            MoreButton.ContextMenu.IsOpen = true;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        AutoStartMenuItem.IsChecked = _app.AutoStart.IsEnabled();
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void AutoStartMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var targetState = AutoStartMenuItem.IsChecked;
        _app.AutoStart.SetEnabled(targetState);
        StatusText.Text = targetState ? "已开启开机启动托盘。" : "已关闭开机启动托盘。";
    }

    private void OpenDataDirMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _app.DataRoot,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _app.ShowErrorDialog(ex.Message, "打开数据目录失败");
        }
    }

    private void ImportWorkflowButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入工作流",
            Filter = "工作流配置 (*.ini)|*.ini",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var previews = new List<string>();
        try
        {
            foreach (var path in dialog.FileNames)
            {
                var candidate = new WorkflowRepository(Path.GetDirectoryName(path)!).LoadFile(path);
                previews.Add($"{Path.GetFileName(path)}\n  {candidate.Name}（{candidate.Id}）\n  布局：{candidate.Layout.Mode}\n  程序：{string.Join("；", candidate.Windows.Select(window => $"{window.Exe} {window.Args}".TrimEnd()))}");
            }
        }
        catch (Exception exception)
        {
            _app.ShowErrorDialog(exception.Message, "读取导入预览失败");
            return;
        }
        if (System.Windows.MessageBox.Show(this,
            "工作流配置可以启动本机程序。请只导入可信来源的文件。\n\n" + string.Join("\n\n", previews),
            "确认导入工作流", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        foreach (var path in dialog.FileNames)
        {
            try
            {
                var candidate = new WorkflowRepository(Path.GetDirectoryName(path)!).LoadFile(path);
                var conflict = _app.Workflows.FirstOrDefault(item => string.Equals(item.Id, candidate.Id, StringComparison.OrdinalIgnoreCase));
                var overwrite = false;
                var createCopy = false;
                if (conflict is not null)
                {
                    var result = System.Windows.MessageBox.Show(this,
                        $"工作流 ID“{candidate.Id}”已存在。\n\n是：覆盖现有工作流\n否：生成新 ID 后导入\n取消：跳过此文件",
                        "处理导入冲突", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Cancel) continue;
                    overwrite = result == MessageBoxResult.Yes;
                    createCopy = result == MessageBoxResult.No;
                }
                var imported = _app.ImportWorkflow(path, overwrite, createCopy);
                StatusText.Text = $"已导入工作流：{imported.Name}";
            }
            catch (Exception exception)
            {
                _app.ShowErrorDialog($"{Path.GetFileName(path)}：{exception.Message}", "导入工作流失败");
            }
        }
    }

    private void ExportWorkflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWorkflow is not { } workflow) return;
        var portabilityWarnings = new List<string>();
        if (workflow.Windows.Any(window => Path.IsPathRooted(window.Exe))) portabilityWarnings.Add("包含本机绝对程序路径");
        if (workflow.Windows.Any(window => window.ProcessArgsContains.Contains("profile", StringComparison.OrdinalIgnoreCase))) portabilityWarnings.Add("包含浏览器 Profile 条件");
        if (portabilityWarnings.Count > 0 && System.Windows.MessageBox.Show(this,
            $"导出的配置{string.Join("、", portabilityWarnings)}，在其他设备上可能需要修改。\n\n是否继续？",
            "导出可移植性提示", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出工作流",
            Filter = "工作流配置 (*.ini)|*.ini",
            FileName = workflow.Name + ".ini",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            App.ExportWorkflow(workflow, dialog.FileName);
            StatusText.Text = $"已导出工作流：{dialog.FileName}";
        }
        catch (Exception exception)
        {
            _app.ShowErrorDialog(exception.Message, "导出工作流失败");
        }
    }

    private void DeleteWorkflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWorkflow is not { } workflow) return;
        if (System.Windows.MessageBox.Show(this,
            $"删除工作流“{workflow.Name}”？配置文件将移入回收站。",
            "删除工作流", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _app.DeleteWorkflow(workflow);
            StatusText.Text = $"已删除工作流：{workflow.Name}";
        }
        catch (Exception exception)
        {
            _app.ShowErrorDialog(exception.Message, "删除工作流失败");
        }
    }

    private void OpenEditor(WorkflowDraft draft, bool isCopy = false)
    {
        var editor = new WorkflowEditorWindow(_app, draft, isCopy)
        {
            Owner = this
        };
        _ = editor.ShowDialog();
    }

    private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        var details = string.Join(Environment.NewLine + Environment.NewLine,
            _diagnostics.Select(item => $"{Path.GetFileName(item.Path)}{Environment.NewLine}{item.Message}"));
        System.Windows.MessageBox.Show(this, details, "配置诊断", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void LogButton_Click(object sender, RoutedEventArgs e) => _app.OpenLog();

    public void UpdateWorkflowState(bool isRunning)
    {
        var hasWorkflow = SelectedWorkflow is not null;
        RunButton.IsEnabled = !isRunning && hasWorkflow;
        WorkflowListBox.IsEnabled = !isRunning;
        EditWorkflowButton.IsEnabled = !isRunning && hasWorkflow;
        MoreButton.IsEnabled = !isRunning && hasWorkflow;
        if (isRunning)
        {
            StatusText.Text = "正在执行窗口排列……";
        }
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _app.Reload(SelectedWorkflow?.Id);
            StatusText.Text = "配置已重新加载。";
        }
        catch (Exception exception)
        {
            _app.ShowErrorDialog(exception.Message, "重新加载失败");
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
