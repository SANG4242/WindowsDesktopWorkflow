using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace DesktopWorkflow.App;

public partial class App : System.Windows.Application, IDisposable
{
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _runStateLock = new();
    private Mutex? _publishMutex;
    private bool _ownsPublishMutex;
    private AppPaths? _paths;
    private WorkflowRepository? _repository;
    private WorkflowFileStore? _fileStore;
    private SingleInstanceCoordinator? _singleInstance;
    private GlobalHotkeyService? _hotkeys;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Icon? _applicationIcon;
    private MainWindow? _mainWindow;
    private WorkflowRunner? _runner;
    private WindowSessionManager? _sessionManager;
    private NativeLayoutProfileStore? _nativeLayoutProfiles;
    private WorkflowLogger? _logger;
    private CancellationTokenSource? _runCancellation;
    private IReadOnlyList<WorkflowDefinition> _workflows = [];
    private bool _hotkeysAttached;
    private bool _isRunning;
    private bool _errorDialogOpen;
    private bool _exiting;
    private bool _disposed;

    public UserSettingsStore Settings { get; private set; } = null!;
    public StartupService AutoStart { get; private set; } = null!;
    public bool IsWorkflowRunning => _isRunning;
    public string LogPath => _logger?.LogPath ?? string.Empty;
    public string DataRoot => _paths?.DataRoot ?? string.Empty;
    public IReadOnlyList<WorkflowDefinition> Workflows => _workflows;
    public IReadOnlyList<NativeLayoutProfile> NativeLayoutProfiles => _nativeLayoutProfiles?.Load() ?? NativeLayoutCatalog.CreateDefaults();

    public void SaveNativeLayoutMenuNumbers(IReadOnlyDictionary<string, int> menuNumbers)
    {
        _nativeLayoutProfiles?.SaveMenuNumbers(menuNumbers);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var validateWorkflows = e.Args.Contains("--validate-workflows", StringComparer.OrdinalIgnoreCase);
        var probeWindowMatches = e.Args.Contains("--probe-window-matches", StringComparer.OrdinalIgnoreCase);
        var headlessMode = validateWorkflows || probeWindowMatches;
        try
        {
            _paths = AppPaths.Resolve(e.Args);
            if (headlessMode)
            {
                var result = new WorkflowRepository(_paths.WorkflowDirectory).Load();
                if (result.Diagnostics.Count > 0)
                {
                    throw new InvalidDataException(string.Join(Environment.NewLine,
                        result.Diagnostics.Select(item => $"{Path.GetFileName(item.Path)}：{item.Message}")));
                }
                if (probeWindowMatches)
                {
                    foreach (var workflow in result.Workflows)
                    {
                        foreach (var window in workflow.Windows)
                        {
                            var match = WindowMatcher.Find(window.Match, window.ProcessArgsContains);
                            if (match == 0)
                            {
                                throw new InvalidOperationException($"未找到严格匹配窗口：{workflow.Name}/{window.Name}");
                            }
                        }
                    }
                }
                Shutdown();
                return;
            }
            _logger = new WorkflowLogger(_paths.LogPath);
            _logger.Info("桌面工作流启动。");
            _singleInstance = new SingleInstanceCoordinator();
            if (!_singleInstance.IsPrimary)
            {
                await _singleInstance.SendShowAsync();
                Shutdown();
                return;
            }
            _publishMutex = new Mutex(false, "Local\\DesktopWorkflow-Publish");
            try
            {
                _ownsPublishMutex = _publishMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                _ownsPublishMutex = true;
            }
            if (!_ownsPublishMutex)
            {
                throw new InvalidOperationException("桌面工作流正在发布，请稍后再启动。");
            }

            _repository = new WorkflowRepository(_paths.WorkflowDirectory);
            Settings = new UserSettingsStore(_paths.SettingsPath);
            AutoStart = new StartupService();
            _fileStore = new WorkflowFileStore(
                _paths.WorkflowDirectory,
                _repository,
                Settings,
                new WorkflowValidator());
            _sessionManager = new WindowSessionManager(_logger, _paths.SessionPath);
            _nativeLayoutProfiles = new NativeLayoutProfileStore(_paths.NativeLayoutProfilesPath);
            _sessionManager.RestoreInterruptedSession();
            _runner = new WorkflowRunner(new WindowLayoutService(_logger, _nativeLayoutProfiles), _sessionManager, _logger);
            _mainWindow = new MainWindow(this);
            _hotkeys = new GlobalHotkeyService();
            _ = new System.Windows.Interop.WindowInteropHelper(_mainWindow).EnsureHandle();
            AttachHotkeys();
            CreateTray();
            Reload();

            _singleInstance.StartServer(() => Dispatcher.InvokeAsync(ShowMainWindow).Task);
            if (!e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
            {
                _ = Dispatcher.InvokeAsync(ShowMainWindow, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }
        catch (Exception exception)
        {
            _logger?.Error("应用启动失败。", exception);
            if (!headlessMode)
            {
                System.Windows.MessageBox.Show(exception.Message, "桌面工作流启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Dispose();
            Shutdown(1);
        }
    }

    public void Reload(string? selectedId = null, bool strict = false)
    {
        if (IsWorkflowRunning)
        {
            throw new InvalidOperationException("工作流执行期间不能重新加载配置。");
        }
        Settings.Load();
        var result = _repository!.Load();
        _workflows = result.Workflows;
        if (strict && selectedId is not null && !_workflows.Any(item =>
            string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"更新后的工作流未能进入有效配置集合：{selectedId}");
        }
        _mainWindow!.SetWorkflows(_workflows, result.Diagnostics, selectedId);
        RebuildTray();
        if (_hotkeysAttached)
        {
            RegisterHotkeys(strict);
        }
        _mainWindow.UpdateWorkflowState(IsWorkflowRunning);
    }

    public WorkflowDefinition SaveWorkflow(WorkflowDraft draft)
    {
        if (IsWorkflowRunning)
        {
            throw new InvalidOperationException("工作流执行期间不能修改配置。");
        }
        var conflictingWorkflow = _workflows.FirstOrDefault(item =>
            !string.Equals(item.SourcePath, draft.SourcePath, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(draft.Hotkey) &&
            string.Equals(Settings.Get(item).Hotkey, draft.Hotkey, StringComparison.OrdinalIgnoreCase));
        if (conflictingWorkflow is not null)
        {
            throw new InvalidDataException($"快捷键已由工作流“{conflictingWorkflow.Name}”使用：{draft.Hotkey}");
        }
        return _fileStore!.Save(draft, _workflows, selectedId => Reload(selectedId, strict: true));
    }

    public void DeleteWorkflow(WorkflowDefinition workflow)
    {
        if (IsWorkflowRunning)
        {
            throw new InvalidOperationException("工作流执行期间不能删除配置。");
        }
        _fileStore!.Delete(workflow, selectedId => Reload(selectedId, strict: true));
    }

    public WorkflowDefinition ImportWorkflow(string sourcePath, bool overwrite, bool createCopy)
    {
        if (IsWorkflowRunning) throw new InvalidOperationException("工作流执行期间不能导入配置。");
        var imported = _repository!.LoadFile(sourcePath);
        var draft = WorkflowDraft.FromDefinition(imported);
        var conflict = _workflows.FirstOrDefault(item => string.Equals(item.Id, draft.Id, StringComparison.OrdinalIgnoreCase));
        if (conflict is not null && overwrite)
        {
            draft.SourcePath = conflict.SourcePath;
        }
        else
        {
            draft.SourcePath = null;
        }
        if (createCopy || conflict is not null && !overwrite)
        {
            var baseName = draft.Name + "（导入）";
            draft.Name = baseName;
            var baseId = WorkflowDraft.CreateId(baseName);
            var suffix = 1;
            draft.Id = baseId;
            while (_workflows.Any(item => string.Equals(item.Id, draft.Id, StringComparison.OrdinalIgnoreCase)))
            {
                suffix++;
                draft.Id = $"{baseId}-{suffix}";
            }
            draft.Hotkey = string.Empty;
        }
        return SaveWorkflow(draft);
    }

    public static void ExportWorkflow(WorkflowDefinition workflow, string destinationPath)
    {
        var draft = WorkflowDraft.FromDefinition(workflow);
        File.WriteAllText(destinationPath, WorkflowIniSerializer.Serialize(draft), new System.Text.UTF8Encoding(false));
    }

    public async Task SaveSettingAsync(WorkflowDefinition workflow, WorkflowSetting setting, bool startupEnabled)
    {
        var previous = Settings.Get(workflow);
        try
        {
            Settings.Set(workflow.Id, setting);
            RegisterHotkeys(strict: true);
            AutoStart.SetEnabled(startupEnabled);
            RebuildTray();
        }
        catch
        {
            Settings.Set(workflow.Id, previous);
            RegisterHotkeys(strict: false);
            throw;
        }
        await Task.CompletedTask;
    }

    public async Task<string> RunWorkflowAsync(string id, WorkflowSetting? overrideSetting = null)
    {
        if (_errorDialogOpen)
        {
            ShowMainWindow();
            throw new OperationCanceledException("错误提示尚未关闭，已忽略重复排列请求。");
        }
        if (!await _runGate.WaitAsync(0))
        {
            throw new OperationCanceledException("排列操作正在进行，已忽略重复请求。");
        }
        try
        {
            var workflow = _workflows.FirstOrDefault(item => item.Id == id)
                ?? throw new InvalidOperationException($"工作流不存在：{id}");
            lock (_runStateLock)
            {
                _runCancellation = new CancellationTokenSource();
                _isRunning = true;
            }
            NotifyWorkflowStateChanged();
            return await _runner!.RunAsync(workflow, overrideSetting ?? Settings.Get(workflow), _runCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _logger?.Info("工作流操作已取消。");
            throw;
        }
        catch (Exception exception)
        {
            _logger?.Error("工作流执行失败。", exception);
            throw;
        }
        finally
        {
            lock (_runStateLock)
            {
                _runCancellation?.Dispose();
                _runCancellation = null;
                _isRunning = false;
            }
            _runGate.Release();
            NotifyWorkflowStateChanged();
        }
    }

    private async Task CancelRunningWorkflowAsync()
    {
        lock (_runStateLock)
        {
            _runCancellation?.Cancel();
        }
        await _runGate.WaitAsync();
        _runGate.Release();
        NotifyWorkflowStateChanged();
    }

    private void NotifyWorkflowStateChanged()
    {
        if (_mainWindow is null)
        {
            return;
        }
        _ = Dispatcher.InvokeAsync(() =>
        {
            _mainWindow.UpdateWorkflowState(IsWorkflowRunning);
            RebuildTray();
        });
    }

    private void AttachHotkeys()
    {
        _hotkeys!.Attach(_mainWindow!);
        _hotkeysAttached = true;
        RegisterHotkeys(strict: false);
    }

    private void RegisterHotkeys(bool strict)
    {
        try
        {
            _hotkeys!.Register(_workflows.Select(workflow =>
                (Settings.Get(workflow).Hotkey, (Action)(() => Dispatcher.Invoke(async () => await RunWorkflowFromTrayAsync(workflow.Id))))));
        }
        catch (Exception exception)
        {
            _logger?.Error("注册全局快捷键失败。", exception);
            if (strict)
            {
                throw;
            }
        }
    }

    private async Task RunWorkflowFromTrayAsync(string id)
    {
        try
        {
            var mode = await RunWorkflowAsync(id);
            _trayIcon?.ShowBalloonTip(1800, "桌面工作流",
                mode == "native" ? "工作流已创建原生贴靠组合。" : "工作流已按自定义比例排列。",
                Forms.ToolTipIcon.Info);
        }
        catch (OperationCanceledException)
        {
            _logger?.Info("从托盘或快捷键启动的工作流已取消。");
        }
        catch (Exception exception)
        {
            _logger?.Info($"从托盘或快捷键收到排列失败结果：{exception.Message}");
            ShowErrorDialog(exception.Message, "执行排列失败");
        }
    }

    public void ShowErrorDialog(
        string message,
        string title,
        MessageBoxImage image = MessageBoxImage.Error)
    {
        if (_errorDialogOpen || _mainWindow is null)
        {
            _logger?.Info("错误提示尚未关闭，已忽略重复提示。");
            return;
        }

        _errorDialogOpen = true;
        try
        {
            ShowMainWindow();
            System.Windows.MessageBox.Show(_mainWindow, message, title, MessageBoxButton.OK, image);
        }
        finally
        {
            _errorDialogOpen = false;
        }
    }

    private void CreateTray()
    {
        using var iconStream = GetResourceStream(new Uri("Assets/desktop-workflow.ico", UriKind.Relative))?.Stream
            ?? throw new InvalidOperationException("桌面工作流图标资源缺失。");
        _applicationIcon = (Icon)new Icon(iconStream).Clone();
        _trayMenu = new Forms.ContextMenuStrip
        {
            DropShadowEnabled = false,
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new Font("Segoe UI", 9.25f)
        };
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "桌面工作流",
            Icon = _applicationIcon,
            Visible = true
        };
        _trayIcon.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Right && _trayMenu is not null)
            {
                ShowTrayMenu();
            }
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }

    private void ShowTrayMenu()
    {
        if (_trayMenu is null)
        {
            return;
        }

        RebuildTray();
        var cursor = Forms.Cursor.Position;
        var workingArea = Forms.Screen.FromPoint(cursor).WorkingArea;
        var anchor = new System.Drawing.Point(
            Math.Clamp(cursor.X, workingArea.Left, workingArea.Right - 1),
            Math.Clamp(cursor.Y, workingArea.Top, workingArea.Bottom - 1));
        var menuSize = _trayMenu.GetPreferredSize(System.Drawing.Size.Empty);
        var opensDown = workingArea.Bottom - anchor.Y >= menuSize.Height
            || anchor.Y - workingArea.Top < menuSize.Height;
        var opensLeft = anchor.X - workingArea.Left >= menuSize.Width
            && workingArea.Right - anchor.X < menuSize.Width;
        var direction = (opensDown, opensLeft) switch
        {
            (true, true) => Forms.ToolStripDropDownDirection.BelowLeft,
            (true, false) => Forms.ToolStripDropDownDirection.BelowRight,
            (false, true) => Forms.ToolStripDropDownDirection.AboveLeft,
            _ => Forms.ToolStripDropDownDirection.AboveRight
        };
        _trayMenu.Show(anchor, direction);
    }

    private void RebuildTray()
    {
        if (_trayMenu is null)
        {
            return;
        }
        _trayMenu.Items.Clear();
        _trayMenu.Items.Add("打开控制窗口", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        foreach (var workflow in _workflows)
        {
            var item = _trayMenu.Items.Add($"执行排列：{workflow.Name}", null,
                async (_, _) => await RunWorkflowFromTrayAsync(workflow.Id));
            item.Enabled = !_isRunning;
        }
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("重新加载配置", null, (_, _) => Dispatcher.Invoke(() => Reload()));
        _trayMenu.Items.Add("打开运行日志", null, (_, _) => OpenLog());
        _trayMenu.Items.Add("打开数据目录", null, (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_paths!.DataRoot}\"") { UseShellExecute = true }));
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(new Forms.ToolStripMenuItem("登录后启动托盘", null, (sender, _) =>
            ToggleAutoStartFromTray((Forms.ToolStripMenuItem)sender!))
        {
            Checked = AutoStart.IsEnabled()
        });
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("退出", null, async (_, _) => await ExitApplicationAsync());
    }

    private void ToggleAutoStartFromTray(Forms.ToolStripMenuItem item)
    {
        try
        {
            var enabled = !AutoStart.IsEnabled();
            AutoStart.SetEnabled(enabled);
            item.Checked = enabled;
            _mainWindow?.SetStartupEnabled(enabled);
            _trayIcon?.ShowBalloonTip(
                1800,
                "桌面工作流",
                enabled ? "已启用登录后启动托盘。" : "已关闭登录后启动托盘。",
                Forms.ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            _logger?.Error("切换开机启动失败。", exception);
            _trayIcon?.ShowBalloonTip(2500, "开机启动设置失败", exception.Message, Forms.ToolTipIcon.Error);
        }
    }

    public void OpenLog()
    {
        if (_logger is null)
        {
            return;
        }
        _logger.Info("打开运行日志。");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_logger.LogPath) { UseShellExecute = true });
    }

    private void ShowMainWindow() => _mainWindow!.ShowAndActivate();

    private async Task ExitApplicationAsync()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        try
        {
            await CancelRunningWorkflowAsync();
        }
        catch (Exception exception)
        {
            _logger?.Error("退出时取消当前排列失败。", exception);
            System.Windows.MessageBox.Show(exception.Message, "退出前取消排列失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _mainWindow?.ClosePermanently();
        Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting)
        {
            lock (_runStateLock)
            {
                _runCancellation?.Cancel();
            }
            if (!_isRunning)
            {
                try
                {
                    _sessionManager?.Restore();
                }
                catch (Exception exception)
                {
                    _logger?.Error("异常退出时回滚临时窗口快照失败。", exception);
                }
            }
            else
            {
                _logger?.Info("应用在排列期间退出，保留本次临时快照供下次启动回滚。");
            }
            Dispose();
        }
        _logger?.Info("桌面工作流退出。");
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _hotkeys?.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayMenu?.Dispose();
        _applicationIcon?.Dispose();
        _singleInstance?.Dispose();
        if (_ownsPublishMutex)
        {
            _publishMutex?.ReleaseMutex();
            _ownsPublishMutex = false;
        }
        _publishMutex?.Dispose();
    }
}
