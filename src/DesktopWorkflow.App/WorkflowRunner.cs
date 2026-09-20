using System.Diagnostics;

namespace DesktopWorkflow.App;

public sealed class WorkflowRunner(
    WindowLayoutService layoutService,
    WindowSessionManager sessionManager,
    WorkflowLogger logger)
{
    public async Task<string> RunAsync(
        WorkflowDefinition workflow,
        WorkflowSetting setting,
        CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid().ToString("N")[..8];
        logger.Info($"[{operationId}] 开始执行工作流“{workflow.Name}”，模式={workflow.Layout.Mode}。");
        var handles = new nint[workflow.Windows.Count];
        var waitTasks = new Dictionary<int, Task<nint>>();
        var startErrors = new List<Exception>();
        var claimedHandles = new HashSet<nint>();
        var claimLock = new object();

        for (var index = 0; index < workflow.Windows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = workflow.Windows[index];
            handles[index] = window.Reuse
                ? TryClaimFirst(WindowMatcher.FindAll(window.Match, window.ProcessArgsContains), [], claimedHandles, claimLock)
                : 0;
            if (handles[index] != 0)
            {
                logger.Info($"[{operationId}] 复用窗口“{window.Name}”：句柄={handles[index].ToInt64()}，标题={NativeMethods.GetWindowTitle(handles[index])}");
                continue;
            }

            var existingExecutableWindows = WindowMatcher.FindByExecutable(
                WindowMatcher.GetExecutable(window.Match),
                window.ProcessArgsContains);
            try
            {
                StartWindow(window);
                logger.Info($"[{operationId}] 已发起启动“{window.Name}”：{window.Exe} {window.Args}".TrimEnd());
                waitTasks[index] = WaitForWindowAsync(
                    window,
                    existingExecutableWindows,
                    claimedHandles,
                    claimLock,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                logger.Error($"[{operationId}] 启动“{window.Name}”失败。", exception);
                startErrors.Add(new InvalidOperationException($"无法启动 {window.Name}：{exception.Message}", exception));
            }
        }

        foreach (var item in waitTasks)
        {
            try
            {
                handles[item.Key] = await item.Value;
                logger.Info($"[{operationId}] 已识别窗口“{workflow.Windows[item.Key].Name}”：句柄={handles[item.Key].ToInt64()}，标题={NativeMethods.GetWindowTitle(handles[item.Key])}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.Error($"[{operationId}] 识别“{workflow.Windows[item.Key].Name}”失败。", exception);
                startErrors.Add(exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (startErrors.Count > 0 || handles.Any(handle => handle == 0))
        {
            var details = startErrors.Count == 0
                ? "存在未识别的目标窗口。"
                : string.Join(Environment.NewLine, startErrors.Select(error => $"- {error.Message}"));
            throw new InvalidOperationException($"工作流窗口未全部就绪：{Environment.NewLine}{details}");
        }

        var layout = workflow.Layout.Clone();

        logger.Info($"[{operationId}] 目标窗口：" + string.Join("；", handles.Select((handle, index) =>
            $"窗口{index + 1}={workflow.Windows[index].Name},句柄={handle.ToInt64()},进程={NativeMethods.GetProcessName(handle)},标题={NativeMethods.GetWindowTitle(handle)}")));
        sessionManager.Capture(operationId, workflow.Name, handles);
        try
        {
            var mode = await layoutService.ApplyAsync(operationId, handles, layout, cancellationToken);
            sessionManager.Commit(operationId);
            logger.Info($"[{operationId}] 工作流“{workflow.Name}”布局完成：{mode}。");
            return mode;
        }
        catch (Exception layoutException)
        {
            try
            {
                sessionManager.Restore(operationId);
            }
            catch (Exception restoreException)
            {
                logger.Error("布局失败后的窗口恢复也失败。", restoreException);
                throw new InvalidOperationException(
                    $"排列失败，临时快照回滚也未完成：{restoreException.Message}。请查看运行日志。",
                    new AggregateException(layoutException, restoreException));
            }
            throw;
        }
    }

    private static void StartWindow(WindowDefinition window)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = window.Exe,
            Arguments = window.Args,
            WorkingDirectory = string.IsNullOrWhiteSpace(window.WorkDir)
                ? Path.GetDirectoryName(window.Exe) ?? string.Empty
                : window.WorkDir,
            UseShellExecute = true
        };
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("系统未返回新进程。");
    }

    private static async Task<nint> WaitForWindowAsync(
        WindowDefinition window,
        IReadOnlyList<nint> existingExecutableWindows,
        ISet<nint> claimedHandles,
        object claimLock,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(window.WaitSeconds);
        var executable = WindowMatcher.GetExecutable(window.Match);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var strictMatch = TryClaimFirst(
                WindowMatcher.FindAll(window.Match, window.ProcessArgsContains),
                existingExecutableWindows,
                claimedHandles,
                claimLock);
            if (strictMatch != 0)
            {
                return strictMatch;
            }

            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != 0 && !existingExecutableWindows.Contains(foreground) &&
                NativeMethods.IsWindowVisible(foreground) &&
                string.Equals(NativeMethods.GetProcessName(foreground), executable, StringComparison.OrdinalIgnoreCase) &&
                WindowMatcher.ProcessArgumentsMatch(foreground, window.ProcessArgsContains) &&
                TryClaim(foreground, claimedHandles, claimLock))
            {
                return foreground;
            }

            var newExecutableWindow = TryClaimFirst(
                WindowMatcher.FindByExecutable(executable, window.ProcessArgsContains),
                existingExecutableWindows,
                claimedHandles,
                claimLock);
            if (newExecutableWindow != 0)
            {
                return newExecutableWindow;
            }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException($"等待新窗口超时：{window.Name}。请检查匹配规则是否与其他目标重叠，并查看 logs/desktop-workflow.log。");
    }

    internal static nint TryClaimFirst(
        IEnumerable<nint> candidates,
        IReadOnlyCollection<nint> excludedHandles,
        ISet<nint> claimedHandles,
        object claimLock)
    {
        foreach (var candidate in candidates)
        {
            if (candidate != 0 && !excludedHandles.Contains(candidate) && TryClaim(candidate, claimedHandles, claimLock))
            {
                return candidate;
            }
        }
        return 0;
    }

    private static bool TryClaim(nint handle, ISet<nint> claimedHandles, object claimLock)
    {
        lock (claimLock)
        {
            return claimedHandles.Add(handle);
        }
    }
}
