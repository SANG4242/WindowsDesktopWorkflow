using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopWorkflow.App;

public enum ValidationSeverity
{
    Warning,
    Error
}

public sealed record ValidationIssue(string Field, string Message, ValidationSeverity Severity = ValidationSeverity.Error);

public sealed record WorkflowLoadDiagnostic(string Path, string Message);

public sealed class WorkflowLoadResult
{
    public IReadOnlyList<WorkflowDefinition> Workflows { get; init; } = [];
    public IReadOnlyList<WorkflowLoadDiagnostic> Diagnostics { get; init; } = [];
}

public sealed class LaunchSpecification
{
    public string Exe { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
}

public sealed class WindowTargetDraft
{
    public string Name { get; set; } = string.Empty;
    public string SourceSummary { get; set; } = string.Empty;
    public LaunchSpecification Launch { get; set; } = new();
    public List<string> TitleCandidates { get; set; } = [];
    public string TitleCandidatesText
    {
        get => string.Join('|', TitleCandidates);
        set => TitleCandidates = [.. value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
    public string ProcessName { get; set; } = string.Empty;
    public string ProcessArgumentsContains { get; set; } = string.Empty;
    public bool Reuse { get; set; } = true;
    public int WaitSeconds { get; set; } = 25;

    public string Match
    {
        get
        {
            var titles = string.Join('|', TitleCandidates.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));
            return string.IsNullOrWhiteSpace(titles)
                ? $"ahk_exe {ProcessName}".Trim()
                : $"{titles} ahk_exe {ProcessName}".Trim();
        }
    }
}

public sealed class LayoutDraft
{
    public string Mode { get; set; } = "proportional";
    public string Orientation { get; set; } = "horizontal";
    public List<double> Ratios { get; set; } = [0.5, 0.5];
    public int Gap { get; set; }
    public string Monitor { get; set; } = "primary";
    public string NativeProfileId { get; set; } = "wide-left";
    public int NativeLayout { get; set; } = 5;
    public List<int> NativeZones { get; set; } = [1, 2];
    public int NativeDelay { get; set; } = 700;
}

public sealed class WorkflowDraft
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Hotkey { get; set; } = string.Empty;
    public string? SourcePath { get; set; }
    public List<WindowTargetDraft> Windows { get; set; } = [];
    public LayoutDraft Layout { get; set; } = new();

    public static WorkflowDraft FromDefinition(WorkflowDefinition workflow) => new()
    {
        Id = workflow.Id,
        Name = workflow.Name,
        Description = workflow.Description,
        Hotkey = workflow.Hotkey,
        SourcePath = workflow.SourcePath,
        Windows = workflow.Windows.Select(window =>
        {
            var (titles, processName) = ParseMatch(window.Match);
            return new WindowTargetDraft
            {
                Name = window.Name,
                Launch = new LaunchSpecification
                {
                    Exe = window.Exe,
                    Arguments = window.Args,
                    WorkingDirectory = window.WorkDir
                },
                TitleCandidates = titles,
                ProcessName = processName,
                ProcessArgumentsContains = window.ProcessArgsContains,
                Reuse = window.Reuse,
                WaitSeconds = window.WaitSeconds
            };
        }).ToList(),
        Layout = new LayoutDraft
        {
            Mode = workflow.Layout.Mode,
            Orientation = workflow.Layout.Orientation,
            Ratios = [.. workflow.Layout.Ratios],
            Gap = workflow.Layout.Gap,
            Monitor = workflow.Layout.Monitor,
            NativeProfileId = workflow.Layout.NativeProfileId,
            NativeLayout = workflow.Layout.NativeLayout,
            NativeZones = [.. workflow.Layout.NativeZones],
            NativeDelay = workflow.Layout.NativeDelay
        }
    };

    public WorkflowDefinition ToDefinition(string sourcePath) => new()
    {
        Id = Id.Trim(),
        Name = Name.Trim(),
        Description = Description.Trim(),
        Hotkey = Hotkey.Trim(),
        SourcePath = sourcePath,
        Windows = Windows.Select(window => new WindowDefinition
        {
            Name = window.Name.Trim(),
            Exe = window.Launch.Exe.Trim(),
            Args = window.Launch.Arguments.Trim(),
            WorkDir = window.Launch.WorkingDirectory.Trim(),
            Match = window.Match,
            ProcessArgsContains = window.ProcessArgumentsContains.Trim(),
            Reuse = window.Reuse,
            WaitSeconds = window.WaitSeconds
        }).ToList(),
        Layout = new LayoutDefinition
        {
            Mode = Layout.Mode,
            Orientation = Layout.Orientation,
            Ratios = [.. Layout.Ratios],
            Gap = Layout.Gap,
            Monitor = Layout.Monitor,
            NativeProfileId = Layout.NativeProfileId,
            NativeLayout = Layout.NativeLayout,
            NativeZones = [.. Layout.NativeZones],
            NativeDelay = Layout.NativeDelay
        }
    };

    public static string CreateId(string name)
    {
        var value = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}]+", "-").Trim('-');
        return value.Length == 0 ? "workflow" : value;
    }

    private static (List<string> Titles, string ProcessName) ParseMatch(string match)
    {
        const string marker = "ahk_exe";
        var index = match.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return ([.. match.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)], string.Empty);
        }
        var titlePart = match[..index].Trim();
        var processName = match[(index + marker.Length)..].Trim();
        return ([.. titlePart.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)], processName);
    }
}

public sealed class WorkflowValidator
{
    public IReadOnlyList<ValidationIssue> Validate(
        WorkflowDraft draft,
        IReadOnlyList<WorkflowDefinition> existing,
        string? originalSourcePath = null,
        IReadOnlyList<NativeLayoutProfile>? nativeProfiles = null)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(draft.Name)) issues.Add(new("Name", "工作流名称不能为空。"));
        if (string.IsNullOrWhiteSpace(draft.Id)) issues.Add(new("Id", "工作流 ID 不能为空。"));
        if (!Regex.IsMatch(draft.Id, @"^[\p{L}\p{N}][\p{L}\p{N}._-]*$"))
            issues.Add(new("Id", "工作流 ID 只能包含字母、数字、点、下划线和连字符。"));
        if (existing.Any(item => string.Equals(item.Id, draft.Id, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(item.SourcePath, originalSourcePath, StringComparison.OrdinalIgnoreCase)))
            issues.Add(new("Id", $"工作流 ID 已存在：{draft.Id}"));
        if (!string.IsNullOrWhiteSpace(draft.Hotkey) && !GlobalHotkeyService.HasPrimaryModifier(draft.Hotkey))
            issues.Add(new("Hotkey", "全局快捷键必须包含 Ctrl、Alt 或 Win。"));
        if (!string.IsNullOrWhiteSpace(draft.Hotkey) && existing.Any(item =>
            string.Equals(item.Hotkey, draft.Hotkey, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(item.SourcePath, originalSourcePath, StringComparison.OrdinalIgnoreCase)))
            issues.Add(new("Hotkey", $"工作流快捷键已存在：{draft.Hotkey}"));
        if (draft.Windows.Count == 0) issues.Add(new("Windows", "至少添加一个应用。"));

        for (var index = 0; index < draft.Windows.Count; index++)
        {
            var window = draft.Windows[index];
            var prefix = $"Windows[{index}]";
            if (string.IsNullOrWhiteSpace(window.Name)) issues.Add(new(prefix, $"应用 {index + 1} 的名称不能为空。"));
            if (string.IsNullOrWhiteSpace(window.Launch.Exe) || !File.Exists(window.Launch.Exe))
                issues.Add(new(prefix, $"应用 {index + 1} 的 EXE 不存在：{window.Launch.Exe}"));
            if (!string.IsNullOrWhiteSpace(window.Launch.WorkingDirectory) && !Directory.Exists(window.Launch.WorkingDirectory))
                issues.Add(new(prefix, $"应用 {index + 1} 的工作目录不存在：{window.Launch.WorkingDirectory}"));
            if (string.IsNullOrWhiteSpace(window.ProcessName)) issues.Add(new(prefix, $"应用 {index + 1} 的进程文件名不能为空。"));
            if (window.WaitSeconds <= 0) issues.Add(new(prefix, $"应用 {index + 1} 的等待时间必须大于 0。"));
        }

        if (draft.Layout.Mode is not "native" and not "proportional") issues.Add(new("Layout.Mode", "布局模式无效。"));
        if (draft.Layout.Orientation is not "horizontal" and not "vertical") issues.Add(new("Layout.Orientation", "布局方向无效。"));
        if (draft.Layout.Ratios.Count != draft.Windows.Count)
            issues.Add(new("Layout.Ratios", "应用数量与布局比例数量必须一致。"));
        if (draft.Layout.Ratios.Any(value => !double.IsFinite(value) || value <= 0))
            issues.Add(new("Layout.Ratios", "布局比例必须全部为大于 0 的有限数值。"));
        if (draft.Layout.Gap < 0)
            issues.Add(new("Layout.Gap", "窗口间隔不能为负数。"));
        if (draft.Layout.Mode == "native")
        {
            var profiles = nativeProfiles ?? NativeLayoutCatalog.CreateDefaults();
            var profile = NativeLayoutCatalog.FindById(profiles, draft.Layout.NativeProfileId);
            if (profile is null && string.IsNullOrWhiteSpace(draft.Layout.NativeProfileId))
            {
                var legacy = NativeLayoutCatalog.FindByMenuNumber(NativeLayoutCatalog.CreateDefaults(), draft.Layout.NativeLayout);
                if (legacy is not null) profile = NativeLayoutCatalog.FindById(profiles, legacy.Id);
            }
            if (profile is null)
                issues.Add(new("Layout.NativeLayout", "原生布局档案无效。"));
            else if (draft.Windows.Count != profile.Zones.Count || draft.Layout.NativeZones.Count != profile.Zones.Count)
                issues.Add(new("Layout", $"“{profile.Name}”需要 {profile.Zones.Count} 个应用和区域。"));
            if (draft.Layout.NativeZones.Distinct().Count() != draft.Layout.NativeZones.Count ||
                draft.Layout.NativeZones.Any(zone => zone < 1 || profile is not null && zone > profile.Zones.Count))
                issues.Add(new("Layout.NativeZones", "原生区域编号必须有效且不能重复。"));
            if (profile is { IsVerified: false })
                issues.Add(new("Layout.NativeLayout", $"“{profile.Name}”尚未在当前显示环境验证；首次执行会使用 Win+Z 数字 {profile.MenuNumber} 完成真实贴靠和几何校验。", ValidationSeverity.Warning));
        }
        return issues;
    }
}

public static class WorkflowIniSerializer
{
    public static string Serialize(WorkflowDraft draft)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[workflow]")
            .Append("id=").AppendLine(draft.Id.Trim())
            .Append("name=").AppendLine(draft.Name.Trim())
            .Append("description=").AppendLine(draft.Description.Trim())
            .Append("hotkey=").AppendLine(draft.Hotkey.Trim());
        for (var index = 0; index < draft.Windows.Count; index++)
        {
            var window = draft.Windows[index];
            builder.AppendLine().Append("[window.").Append(index + 1).AppendLine("]")
                .Append("name=").AppendLine(window.Name.Trim())
                .Append("exe=").AppendLine(window.Launch.Exe.Trim())
                .Append("args=").AppendLine(window.Launch.Arguments.Trim())
                .Append("workdir=").AppendLine(window.Launch.WorkingDirectory.Trim())
                .Append("match=").AppendLine(window.Match)
                .Append("process_args_contains=").AppendLine(window.ProcessArgumentsContains.Trim())
                .Append("reuse=").AppendLine(window.Reuse ? "true" : "false")
                .Append("wait_seconds=").AppendLine(window.WaitSeconds.ToString(CultureInfo.InvariantCulture));
        }
        builder.AppendLine().AppendLine("[layout]")
            .Append("mode=").AppendLine(draft.Layout.Mode)
            .Append("orientation=").AppendLine(draft.Layout.Orientation)
            .Append("ratios=").AppendLine(string.Join(',', draft.Layout.Ratios.Select(value => value.ToString("0.######", CultureInfo.InvariantCulture))))
            .Append("gap=").AppendLine(draft.Layout.Gap.ToString(CultureInfo.InvariantCulture))
            .Append("monitor=").AppendLine(draft.Layout.Monitor)
            .Append("native_profile=").AppendLine(draft.Layout.NativeProfileId)
            .Append("native_layout=").AppendLine(draft.Layout.NativeLayout.ToString(CultureInfo.InvariantCulture))
            .Append("native_zones=").AppendLine(string.Join(',', draft.Layout.NativeZones))
            .Append("native_delay_ms=").AppendLine(draft.Layout.NativeDelay.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}
