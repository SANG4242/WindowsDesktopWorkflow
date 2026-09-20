using System.Windows.Forms;

namespace DesktopWorkflow.App;

public sealed class WindowLayoutService(WorkflowLogger logger, NativeLayoutProfileStore profileStore)
{
    private const byte VkMenu = 0x12;
    private const byte VkLwin = 0x5B;
    private const byte VkZ = 0x5A;
    private const byte VkEscape = 0x1B;

    public async Task<string> ApplyAsync(
        string operationId,
        IReadOnlyList<nint> handles,
        LayoutDefinition layout,
        CancellationToken cancellationToken)
    {
        if (layout.Mode == "native")
        {
            await ApplyNativeAsync(operationId, handles, layout, cancellationToken);
            return "native";
        }
        ApplyProportional(handles, layout);
        return "proportional";
    }

    private async Task ApplyNativeAsync(
        string operationId,
        IReadOnlyList<nint> handles,
        LayoutDefinition layout,
        CancellationToken cancellationToken)
    {
        var profiles = profileStore.Load();
        var profile = NativeLayoutCatalog.FindById(profiles, layout.NativeProfileId);
        if (profile is null && string.IsNullOrWhiteSpace(layout.NativeProfileId))
        {
            var legacy = NativeLayoutCatalog.FindByMenuNumber(NativeLayoutCatalog.CreateDefaults(), layout.NativeLayout);
            if (legacy is not null) profile = NativeLayoutCatalog.FindById(profiles, legacy.Id);
        }
        if (profile is null)
        {
            throw new InvalidDataException("当前环境没有该原生布局的稳定档案。");
        }
        var menuNumber = profile.MenuNumber;
        if (!profile.IsVerified)
        {
            logger.Info($"[{operationId}] “{profile.Name}”尚未在当前显示环境验证，将使用 Win+Z 数字 {menuNumber} 测试，本次成功后记录验证状态。");
        }
        if (handles.Count != profile.Zones.Count || layout.NativeZones.Count != handles.Count ||
            layout.NativeZones.Distinct().Count() != layout.NativeZones.Count ||
            layout.NativeZones.Any(zone => zone < 1 || zone > profile.Zones.Count))
        {
            throw new InvalidDataException($"“{profile.Name}”需要 {profile.Zones.Count} 个窗口和互不重复的有效区域。");
        }

        var screen = ResolveScreen(layout.Monitor);
        try
        {
            for (var index = 0; index < handles.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapped = false;
                for (var attempt = 1; attempt <= 2 && !snapped; attempt++)
                {
                    if (!NativeMethods.IsWindow(handles[index]))
                    {
                        throw new InvalidOperationException($"第 {index + 1} 个目标窗口已经关闭。");
                    }
                    await ActivateAsync(handles[index], cancellationToken);
                    logger.Info(
                        $"[{operationId}] 窗口{index + 1}直接贴靠：尝试={attempt}/2，句柄={handles[index].ToInt64()}，" +
                        $"布局={profile.Name}，Win+Z数字={menuNumber}，区域={layout.NativeZones[index]}，{DescribeWindow(handles[index])}");
                    NativeMethods.SendChord(VkLwin, VkZ);
                    await Task.Delay(layout.NativeDelay, cancellationToken);
                    NativeMethods.SendKey((byte)(0x30 + menuNumber));
                    await Task.Delay(layout.NativeDelay, cancellationToken);
                    NativeMethods.SendKey((byte)(0x30 + layout.NativeZones[index]));
                    await Task.Delay(layout.NativeDelay * 2, cancellationToken);
                    NativeMethods.SendKey(VkEscape);
                    await Task.Delay(layout.NativeDelay, cancellationToken);
                    snapped = LooksLikeRequestedZone(handles[index], index, layout, profile, screen);
                    logger.Info($"[{operationId}] 窗口{index + 1}贴靠结果：成功={snapped}，{DescribeWindow(handles[index])}");
                }

                if (!snapped)
                {
                    profileStore.MarkUnverified(profile.Id);
                    throw new InvalidOperationException(
                        $"第 {index + 1} 个窗口未进入“{profile.Name}”的指定区域。当前 Win+Z 数字 {menuNumber} 可能已失效，请在编辑器中重新校准。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            TryCloseSnapUi();
            throw;
        }
        catch (Exception exception)
        {
            TryCloseSnapUi();
            logger.Info($"[{operationId}] 原生排列中止：{DescribeLayout(handles, screen)}");
            throw new InvalidOperationException("Windows 贴靠交互失败，将回滚到本次排列前状态。", exception);
        }

        if (!LooksLikeRequestedLayout(handles, layout, profile, screen))
        {
            profileStore.MarkUnverified(profile.Id);
            logger.Info($"[{operationId}] {DescribeLayout(handles, screen)}");
            throw new InvalidOperationException(
                $"窗口未进入“{profile.Name}”的指定区域。当前 Win+Z 数字 {menuNumber} 可能已失效，请重新校准；本次操作将回滚。");
        }
        if (!profile.IsVerified)
        {
            profileStore.MarkVerified(profile.Id);
            logger.Info($"[{operationId}] “{profile.Name}”使用 Win+Z 数字 {menuNumber} 通过当前显示环境的真实贴靠与几何验证。");
        }
    }

    private static async Task ActivateAsync(nint window, CancellationToken cancellationToken)
    {
        var currentThread = NativeMethods.GetCurrentThreadId();
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == 0 ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var targetThread = NativeMethods.GetWindowThreadProcessId(window, out _);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread &&
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread &&
            NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        try
        {
            _ = NativeMethods.BringWindowToTop(window);
            _ = NativeMethods.SetForegroundWindow(window);
            _ = NativeMethods.SetActiveWindow(window);
            _ = NativeMethods.SetFocus(window);
        }
        finally
        {
            if (attachedTarget)
            {
                _ = NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            }
            if (attachedForeground)
            {
                _ = NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        if (await WaitForForegroundAsync(window, TimeSpan.FromMilliseconds(700), cancellationToken))
        {
            return;
        }

        NativeMethods.SendKey(VkMenu);
        _ = NativeMethods.SetWindowPos(window, NativeMethods.HwndTopmost, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpShowWindow);
        NativeMethods.SwitchToThisWindow(window, true);
        _ = NativeMethods.SetForegroundWindow(window);
        _ = NativeMethods.SetActiveWindow(window);
        _ = NativeMethods.BringWindowToTop(window);
        _ = NativeMethods.SetFocus(window);
        _ = NativeMethods.SetWindowPos(window, NativeMethods.HwndNoTopmost, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpShowWindow);
        if (await WaitForForegroundAsync(window, TimeSpan.FromSeconds(2), cancellationToken))
        {
            return;
        }
        var actualForeground = NativeMethods.GetForegroundWindow();
        throw new InvalidOperationException(
            $"目标窗口未成为前台窗口。目标：{NativeMethods.GetWindowTitle(window)}；当前前台：{NativeMethods.GetWindowTitle(actualForeground)}");
    }

    private static async Task<bool> WaitForForegroundAsync(
        nint window,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.GetForegroundWindow() == window)
            {
                return true;
            }
            await Task.Delay(50, cancellationToken);
        }
        return false;
    }

    private static bool LooksLikeRequestedLayout(
        IReadOnlyList<nint> handles,
        LayoutDefinition layout,
        NativeLayoutProfile profile,
        Screen screen) => handles
            .Select((handle, index) => LooksLikeRequestedZone(handle, index, layout, profile, screen))
            .All(result => result);

    private static bool LooksLikeRequestedZone(
        nint handle,
        int index,
        LayoutDefinition layout,
        NativeLayoutProfile profile,
        Screen screen)
    {
        if (!NativeMethods.GetWindowRect(handle, out var actual) || index >= layout.NativeZones.Count)
        {
            return false;
        }
        var zoneNumber = layout.NativeZones[index];
        if (zoneNumber < 1 || zoneNumber > profile.Zones.Count)
        {
            return false;
        }
        var zone = profile.Zones[zoneNumber - 1];
        var area = screen.WorkingArea;
        var expectedLeft = area.Left + area.Width * zone.X;
        var expectedTop = area.Top + area.Height * zone.Y;
        var expectedRight = expectedLeft + area.Width * zone.Width;
        var expectedBottom = expectedTop + area.Height * zone.Height;
        var horizontalTolerance = Math.Max(24, area.Width * 0.04);
        var verticalTolerance = Math.Max(24, area.Height * 0.04);
        return Math.Abs(actual.Left - expectedLeft) <= horizontalTolerance
            && Math.Abs(actual.Top - expectedTop) <= verticalTolerance
            && Math.Abs(actual.Right - expectedRight) <= horizontalTolerance
            && Math.Abs(actual.Bottom - expectedBottom) <= verticalTolerance;
    }

    private static string DescribeWindow(nint handle)
    {
        var placement = NativeMethods.TryGetWindowPlacement(handle, out var currentPlacement)
            ? currentPlacement.ShowCommand.ToString()
            : "未知";
        var bounds = NativeMethods.GetWindowRect(handle, out var rect)
            ? $"边界=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})"
            : "边界=无法读取";
        return $"句柄={handle.ToInt64()}，进程={NativeMethods.GetProcessName(handle)}，ShowCommand={placement}，" +
            $"前台={NativeMethods.GetForegroundWindow().ToInt64()}，{bounds}";
    }

    private static string DescribeLayout(IReadOnlyList<nint> handles, Screen screen)
    {
        var area = screen.WorkingArea;
        var descriptions = handles.Select((handle, index) => $"窗口{index + 1}：{DescribeWindow(handle)}");
        return $"贴靠边界校验失败：工作区=({area.Left},{area.Top})-({area.Right},{area.Bottom})；{string.Join("；", descriptions)}";
    }

    private static void ApplyProportional(IReadOnlyList<nint> handles, LayoutDefinition layout)
    {
        if (layout.Ratios.Count != handles.Count)
        {
            throw new InvalidDataException("窗口数量与布局比例数量不一致。");
        }
        var area = ResolveScreen(layout.Monitor).WorkingArea;
        var axisLength = layout.Orientation == "vertical" ? area.Height : area.Width;
        var segments = CalculateProportionalSegments(axisLength, layout.Ratios, layout.Gap);
        if (handles.Any(handle => !NativeMethods.IsWindow(handle)))
        {
            throw new InvalidOperationException("布局过程中目标窗口已经关闭。");
        }

        for (var index = 0; index < handles.Count; index++)
        {
            _ = NativeMethods.ShowWindow(handles[index], NativeMethods.SwRestore);
            var segment = segments[index];
            var moved = layout.Orientation == "vertical"
                ? NativeMethods.SetWindowPos(handles[index], 0, area.Left, area.Top + segment.Offset, area.Width, segment.Length,
                    NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate)
                : NativeMethods.SetWindowPos(handles[index], 0, area.Left + segment.Offset, area.Top, segment.Length, area.Height,
                    NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
            if (!moved)
            {
                throw new InvalidOperationException($"无法移动第 {index + 1} 个窗口。");
            }
        }
    }

    internal static IReadOnlyList<(int Offset, int Length)> CalculateProportionalSegments(
        int axisLength,
        IReadOnlyList<double> ratios,
        int gap)
    {
        if (axisLength <= 0 || ratios.Count == 0 || ratios.Any(value => !double.IsFinite(value) || value <= 0))
        {
            throw new InvalidDataException("比例布局尺寸或比例无效。");
        }
        if (gap < 0)
        {
            throw new InvalidDataException("窗口间隔不能为负数。");
        }

        var availableLength = axisLength - (long)gap * (ratios.Count - 1);
        if (availableLength < ratios.Count)
        {
            throw new InvalidDataException("窗口间隔过大，剩余工作区不足以容纳全部窗口。");
        }

        var total = ratios.Sum();
        var segments = new List<(int Offset, int Length)>(ratios.Count);
        var consumed = 0;
        for (var index = 0; index < ratios.Count; index++)
        {
            var next = index == ratios.Count - 1
                ? (int)availableLength
                : (int)Math.Round(availableLength * ratios.Take(index + 1).Sum() / total);
            var length = next - consumed;
            if (length <= 0)
            {
                throw new InvalidDataException("比例布局产生了无效的窗口尺寸。");
            }
            segments.Add((consumed + gap * index, length));
            consumed = next;
        }
        return segments;
    }

    private static void TryCloseSnapUi()
    {
        try
        {
            NativeMethods.SendKey(VkEscape);
        }
        catch
        {
        }
    }

    private static Screen ResolveScreen(string monitor)
    {
        if (monitor == "mouse")
        {
            return Screen.FromPoint(Cursor.Position);
        }
        if (int.TryParse(monitor, out var index) && index >= 1 && index <= Screen.AllScreens.Length)
        {
            return Screen.AllScreens[index - 1];
        }
        return Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }
}
