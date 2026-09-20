using DesktopWorkflow.App;
using System.Text;

var tests = new (string Name, Action Run)[]
{
    ("原生布局目录完整", NativeLayoutCatalogIsComplete),
    ("比例区域生成正确", LinearZonesAreNormalized),
    ("工作流配置可往返", WorkflowConfigurationRoundTrips),
    ("同一应用可生成多个目标", DuplicateApplicationTargetsAreAllowed),
    ("原生布局窗口数量受档案约束", NativeLayoutUsesProfileZoneCount),
    ("本机快捷键覆盖按预期生效", UserSettingsOverrideIsEffective),
    ("全局快捷键要求主修饰键", GlobalHotkeyRequiresPrimaryModifier),
    ("窗口句柄分配保持唯一", WindowHandleClaimsAreUnique),
    ("比例布局区段完整且无缝", ProportionalSegmentsAreComplete),
    ("非法窗口间隔被拒绝", InvalidLayoutGapsAreRejected)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.Message}");
        Console.Error.WriteLine(failures[^1]);
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count}/{tests.Length} 项测试失败。");
    return 1;
}
Console.WriteLine($"PASS 全部 {tests.Length} 项自动化测试通过。");
return 0;

static void NativeLayoutCatalogIsComplete()
{
    var profiles = NativeLayoutCatalog.CreateDefaults();
    Equal(6, profiles.Count, "固定原生布局数量");
    EqualSequence(Enumerable.Range(4, 6), profiles.Select(profile => profile.MenuNumber), "默认 Win+Z 数字必须为 4–9");
    EqualSequence(new[] { "equal-columns", "wide-left", "three-columns", "left-and-right-stack", "four-grid", "narrow-wide-narrow" },
        profiles.Select(profile => profile.Id), "稳定布局 ID");
    True(profiles.All(profile => profile.Zones.Count >= 2), "每个原生布局至少应有两个区域");
    foreach (var zone in profiles.SelectMany(profile => profile.Zones))
    {
        True(zone.X >= 0 && zone.Y >= 0 && zone.Width > 0 && zone.Height > 0, "区域坐标和尺寸必须有效");
        True(zone.X + zone.Width <= 1.000001 && zone.Y + zone.Height <= 1.000001, "区域必须位于标准化工作区内");
    }
    True(profiles.All(profile => !profile.IsVerified), "默认目录不得跨显示环境预设验证状态");
}

static void LinearZonesAreNormalized()
{
    var horizontal = NativeLayoutCatalog.CreateLinearZones("horizontal", [2, 1, 1]);
    Equal(3, horizontal.Count, "横向比例应生成三个区域");
    Near(0.5, horizontal[0].Width, "首个横向区域宽度");
    Near(1, horizontal.Sum(zone => zone.Width), "横向区域宽度总和");
    var vertical = NativeLayoutCatalog.CreateLinearZones("vertical", [1, 3]);
    Equal(2, vertical.Count, "纵向比例应生成两个区域");
    Near(0.75, vertical[1].Height, "第二个纵向区域高度");
    Near(1, vertical.Sum(zone => zone.Height), "纵向区域高度总和");
}

static void WorkflowConfigurationRoundTrips()
{
    var directory = CreateTemporaryDirectory();
    try
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法读取测试进程路径");
        var path = Path.Combine(directory, "roundtrip.ini");
        var draft = CreateDraft(executable, 2);
        draft.Description = "往返测试";
        draft.Layout.Ratios = [2, 1];
        File.WriteAllText(path, WorkflowIniSerializer.Serialize(draft), new UTF8Encoding(false));
        var workflow = new WorkflowRepository(directory).LoadFile(path);
        Equal("roundtrip", workflow.Id, "工作流 ID");
        Equal("往返测试", workflow.Description, "工作流说明");
        Equal(2, workflow.Windows.Count, "窗口数量");
        EqualSequence(new[] { 2d, 1d }, workflow.Layout.Ratios, "完整比例");
        Equal("wide-left", workflow.Layout.NativeProfileId, "稳定原生布局 ID");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static void DuplicateApplicationTargetsAreAllowed()
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法读取测试进程路径");
    var draft = CreateDraft(executable, 2);
    draft.Windows[0].Name = "同一应用窗口 A";
    draft.Windows[1].Name = "同一应用窗口 B";
    draft.Windows[0].TitleCandidates = ["A"];
    draft.Windows[1].TitleCandidates = ["B"];
    var issues = new WorkflowValidator().Validate(draft, []);
    True(issues.All(issue => issue.Severity != ValidationSeverity.Error), string.Join("；", issues.Select(issue => issue.Message)));
}

static void NativeLayoutUsesProfileZoneCount()
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法读取测试进程路径");
    var draft = CreateDraft(executable, 3);
    draft.Layout.Mode = "native";
    draft.Layout.NativeProfileId = "three-columns";
    draft.Layout.NativeLayout = 6;
    draft.Layout.NativeZones = [1, 2, 3];
    draft.Layout.Ratios = [1, 1, 1];
    var validIssues = new WorkflowValidator().Validate(draft, []);
    True(validIssues.All(issue => issue.Severity != ValidationSeverity.Error), "三列布局应接受三个窗口和三个区域");
    draft.Windows.RemoveAt(2);
    draft.Layout.Ratios.RemoveAt(2);
    var invalidIssues = new WorkflowValidator().Validate(draft, []);
    True(invalidIssues.Any(issue => issue.Severity == ValidationSeverity.Error && issue.Field == "Layout"), "窗口数量不符时必须报错");
}

static void GlobalHotkeyRequiresPrimaryModifier()
{
    Equal(string.Empty, GlobalHotkeyService.Compose(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.None, false),
        "单个字母不得注册为全局快捷键");
    Equal(string.Empty, GlobalHotkeyService.Compose(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Shift, false),
        "仅 Shift 不得注册为全局快捷键");
    Equal("^!1", GlobalHotkeyService.Compose(
        System.Windows.Input.Key.D1,
        System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt,
        false),
        "Ctrl+Alt+1 应可正常录入");

    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法读取测试进程路径");
    var draft = CreateDraft(executable, 1);
    draft.Hotkey = "a";
    var issues = new WorkflowValidator().Validate(draft, []);
    True(issues.Any(issue => issue.Field == "Hotkey" && issue.Severity == ValidationSeverity.Error),
        "导入或手工配置的无修饰快捷键必须被验证器拒绝");
}

static void WindowHandleClaimsAreUnique()
{
    var claimed = new HashSet<nint>();
    var claimLock = new object();
    var first = WorkflowRunner.TryClaimFirst([(nint)101, (nint)102], [], claimed, claimLock);
    var second = WorkflowRunner.TryClaimFirst([(nint)101, (nint)102], [], claimed, claimLock);
    var excluded = WorkflowRunner.TryClaimFirst([(nint)103], [(nint)103], claimed, claimLock);
    Equal((nint)101, first, "首个目标应声明第一个可用窗口");
    Equal((nint)102, second, "第二个目标应跳过已声明窗口");
    Equal((nint)0, excluded, "启动前已存在的窗口不得作为新窗口返回");
}

static void ProportionalSegmentsAreComplete()
{
    var segments = WindowLayoutService.CalculateProportionalSegments(1000, [2, 1, 1], 7);
    Equal(3, segments.Count, "应生成三个比例区段");
    Equal(0, segments[0].Offset, "首段应从工作区起点开始");
    Equal(7, segments[1].Offset - (segments[0].Offset + segments[0].Length), "首段与次段间隔");
    Equal(7, segments[2].Offset - (segments[1].Offset + segments[1].Length), "次段与末段间隔");
    Equal(1000, segments[^1].Offset + segments[^1].Length, "末段应精确覆盖到工作区末端");
    True(segments.All(segment => segment.Length > 0), "每个区段都必须具有正尺寸");
}

static void InvalidLayoutGapsAreRejected()
{
    Throws<InvalidDataException>(() => WindowLayoutService.CalculateProportionalSegments(1000, [1, 1], -1),
        "负间隔必须被拒绝");
    Throws<InvalidDataException>(() => WindowLayoutService.CalculateProportionalSegments(100, [1, 1, 1], 50),
        "占满工作区的总间隔必须被拒绝");

    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法读取测试进程路径");
    var draft = CreateDraft(executable, 2);
    draft.Layout.Gap = -1;
    var issues = new WorkflowValidator().Validate(draft, []);
    True(issues.Any(issue => issue.Field == "Layout.Gap" && issue.Severity == ValidationSeverity.Error),
        "工作流验证器必须拒绝负间隔");
}

static void UserSettingsOverrideIsEffective()
{
    var directory = CreateTemporaryDirectory();
    try
    {
        var settingsPath = Path.Combine(directory, "user-settings.ini");
        var workflow = new WorkflowDefinition
        {
            Id = "settings-test",
            Name = "设置测试",
            Hotkey = "^!1",
            SourcePath = Path.Combine(directory, "settings-test.ini")
        };
        var settings = new UserSettingsStore(settingsPath);
        settings.Load();
        Equal("^!1", settings.Get(workflow).Hotkey, "无本机覆盖时应使用工作流默认快捷键");
        settings.Set(workflow.Id, new WorkflowSetting { Hotkey = "^!2" });
        Equal("^!2", settings.Get(workflow).Hotkey, "本机覆盖应成为实际生效快捷键");
        settings.Remove(workflow.Id);
        Equal("^!1", settings.Get(workflow).Hotkey, "移除覆盖后应恢复工作流默认快捷键");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static WorkflowDraft CreateDraft(string executable, int count)
{
    var draft = new WorkflowDraft
    {
        Id = "roundtrip",
        Name = "自动化测试",
        Layout = new LayoutDraft
        {
            Mode = "proportional",
            Orientation = "horizontal",
            Ratios = Enumerable.Repeat(1d, count).ToList()
        }
    };
    for (var index = 0; index < count; index++)
    {
        draft.Windows.Add(new WindowTargetDraft
        {
            Name = $"测试窗口 {index + 1}",
            Launch = new LaunchSpecification
            {
                Exe = executable,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty
            },
            ProcessName = Path.GetFileName(executable),
            TitleCandidates = [$"测试 {index + 1}"]
        });
    }
    return draft;
}

static string CreateTemporaryDirectory()
{
    var path = Path.Combine(Path.GetTempPath(), "DesktopWorkflow.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}");
}

static void EqualSequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException($"{message}：期望 [{string.Join(',', expected)}]，实际 [{string.Join(',', actual)}]");
}

static void Throws<TException>(Action action, string message) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static void Near(double expected, double actual, string message)
{
    if (Math.Abs(expected - actual) > 0.000001)
        throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}");
}
