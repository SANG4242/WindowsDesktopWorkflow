using System.Text.Json;

namespace DesktopWorkflow.App;

public sealed class WindowSessionManager(WorkflowLogger logger, string sessionPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private WindowSession? _activeSession;

    public void RestoreInterruptedSession()
    {
        if (!File.Exists(sessionPath))
        {
            return;
        }
        try
        {
            _activeSession = JsonSerializer.Deserialize<WindowSession>(File.ReadAllText(sessionPath), JsonOptions);
            if (_activeSession is null)
            {
                throw new InvalidDataException("临时窗口快照文件为空。");
            }
        }
        catch (Exception exception)
        {
            _activeSession = null;
            logger.Error("临时窗口快照文件已损坏，无法自动回滚。", exception);
            return;
        }

        try
        {
            logger.Info($"检测到工作流“{_activeSession.WorkflowName}”中断时留下的临时快照，开始回滚窗口。");
            Restore();
        }
        catch (Exception exception)
        {
            logger.Error("回滚上次异常中断的窗口失败，请查看窗口映射日志。", exception);
        }
    }

    public void Capture(string operationId, string workflowName, IReadOnlyList<nint> handles)
    {
        if (_activeSession is not null)
        {
            logger.Info($"[{operationId}] 检测到未清理的临时快照，先尽力回滚后再执行本次排列。");
            Restore(operationId);
        }

        var snapshots = new List<WindowSnapshot>(handles.Count);
        foreach (var handle in handles)
        {
            if (!NativeMethods.IsWindow(handle) ||
                !NativeMethods.TryGetWindowPlacement(handle, out var placement) ||
                !NativeMethods.GetWindowRect(handle, out var actualRect))
            {
                throw new InvalidOperationException($"无法保存窗口状态：{NativeMethods.GetWindowTitle(handle)}");
            }
            snapshots.Add(WindowSnapshot.From(
                handle,
                placement,
                actualRect,
                NativeMethods.GetWindowTitle(handle),
                NativeMethods.GetProcessId(handle),
                NativeMethods.GetProcessName(handle)));
        }
        _activeSession = new WindowSession(workflowName, snapshots);
        try
        {
            Persist();
        }
        catch
        {
            _activeSession = null;
            throw;
        }
        logger.Info($"[{operationId}] 已保存工作流“{workflowName}”的 {snapshots.Count} 个本次临时快照：" +
            string.Join("；", snapshots.Select((snapshot, index) =>
                $"窗口{index + 1}=句柄{snapshot.Handle},进程{snapshot.Executable},ShowCommand={snapshot.ShowCommand}," +
                $"边界=({snapshot.ActualLeft},{snapshot.ActualTop})-({snapshot.ActualRight},{snapshot.ActualBottom})")));
    }

    public void Commit(string operationId)
    {
        var session = _activeSession;
        if (session is null)
        {
            return;
        }
        if (File.Exists(sessionPath))
        {
            File.Delete(sessionPath);
        }
        _activeSession = null;
        logger.Info($"[{operationId}] 排列成功，已清除本次临时窗口快照。");
    }

    public void Restore(string? operationId = null)
    {
        var session = _activeSession;
        if (session is null)
        {
            return;
        }

        var logId = operationId ?? "restore";
        var failures = new List<string>();
        foreach (var item in session.Snapshots.Select((snapshot, index) => (snapshot, index)))
        {
            var snapshot = item.snapshot;
            var handle = (nint)snapshot.Handle;
            logger.Info(
                $"[{logId}] 恢复窗口{item.index + 1}映射：快照句柄={snapshot.Handle}，当前句柄={handle.ToInt64()}，" +
                $"进程={snapshot.Executable}/{snapshot.ProcessId}，目标ShowCommand={snapshot.ShowCommand}，" +
                $"目标边界=({snapshot.ActualLeft},{snapshot.ActualTop})-({snapshot.ActualRight},{snapshot.ActualBottom})");
            if (!NativeMethods.IsWindow(handle) ||
                NativeMethods.GetProcessId(handle) != snapshot.ProcessId ||
                !string.Equals(NativeMethods.GetProcessName(handle), snapshot.Executable, StringComparison.OrdinalIgnoreCase))
            {
                logger.Info($"[{logId}] 跳过已关闭或身份已变化的窗口：{snapshot.Title}");
                continue;
            }
            var placement = snapshot.ToPlacement();
            var restored = NativeMethods.SetWindowPlacement(handle, ref placement);
            if (restored && snapshot.ShowCommand == NativeMethods.SwShowNormal)
            {
                restored = NativeMethods.SetWindowPos(
                    handle,
                    0,
                    snapshot.ActualLeft,
                    snapshot.ActualTop,
                    snapshot.ActualRight - snapshot.ActualLeft,
                    snapshot.ActualBottom - snapshot.ActualTop,
                    NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
            }
            var actual = NativeMethods.GetWindowRect(handle, out var actualRect)
                ? $"({actualRect.Left},{actualRect.Top})-({actualRect.Right},{actualRect.Bottom})"
                : "无法读取";
            logger.Info($"[{logId}] 恢复窗口{item.index + 1}结果：成功={restored}，实际边界={actual}");
            if (!restored)
            {
                failures.Add(snapshot.Title);
            }
        }

        _activeSession = null;
        if (File.Exists(sessionPath))
        {
            File.Delete(sessionPath);
        }
        logger.Info(failures.Count == 0
            ? $"[{logId}] 已回滚工作流“{session.WorkflowName}”并清除本次临时快照。"
            : $"[{logId}] 已清除工作流“{session.WorkflowName}”的本次临时快照；以下窗口回滚失败并保留当前状态：{string.Join("、", failures)}");
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        var temporaryPath = sessionPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_activeSession, JsonOptions));
            File.Move(temporaryPath, sessionPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record WindowSession(string WorkflowName, IReadOnlyList<WindowSnapshot> Snapshots);

    private sealed record WindowSnapshot(
        long Handle,
        string Title,
        uint ProcessId,
        string Executable,
        uint Flags,
        uint ShowCommand,
        int MinX,
        int MinY,
        int MaxX,
        int MaxY,
        int Left,
        int Top,
        int Right,
        int Bottom,
        int ActualLeft,
        int ActualTop,
        int ActualRight,
        int ActualBottom)
    {
        public static WindowSnapshot From(
            nint handle,
            NativeMethods.WindowPlacement placement,
            NativeMethods.Rect actualRect,
            string title,
            uint processId,
            string executable) => new(
            handle.ToInt64(),
            title,
            processId,
            executable,
            placement.Flags,
            placement.ShowCommand,
            placement.MinPosition.X,
            placement.MinPosition.Y,
            placement.MaxPosition.X,
            placement.MaxPosition.Y,
            placement.NormalPosition.Left,
            placement.NormalPosition.Top,
            placement.NormalPosition.Right,
            placement.NormalPosition.Bottom,
            actualRect.Left,
            actualRect.Top,
            actualRect.Right,
            actualRect.Bottom);

        public NativeMethods.WindowPlacement ToPlacement() => new()
        {
            Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WindowPlacement>(),
            Flags = Flags,
            ShowCommand = ShowCommand,
            MinPosition = new NativeMethods.Point { X = MinX, Y = MinY },
            MaxPosition = new NativeMethods.Point { X = MaxX, Y = MaxY },
            NormalPosition = new NativeMethods.Rect { Left = Left, Top = Top, Right = Right, Bottom = Bottom }
        };
    }
}
