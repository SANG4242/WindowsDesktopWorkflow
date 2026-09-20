namespace DesktopWorkflow.App;

public sealed class WorkflowDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Hotkey { get; init; } = string.Empty;
    public required string SourcePath { get; init; }
    public List<WindowDefinition> Windows { get; init; } = [];
    public LayoutDefinition Layout { get; init; } = new();
}

public sealed class WindowDefinition
{
    public required string Name { get; init; }
    public required string Exe { get; init; }
    public string Args { get; init; } = string.Empty;
    public string WorkDir { get; init; } = string.Empty;
    public required string Match { get; init; }
    public string ProcessArgsContains { get; init; } = string.Empty;
    public bool Reuse { get; init; } = true;
    public int WaitSeconds { get; init; } = 25;
}

public sealed class LayoutDefinition
{
    public string Mode { get; set; } = "proportional";
    public string Orientation { get; init; } = "horizontal";
    public List<double> Ratios { get; set; } = [0.5, 0.5];
    public int Gap { get; init; }
    public string Monitor { get; init; } = "primary";
    public string NativeProfileId { get; init; } = string.Empty;
    public int NativeLayout { get; init; } = 5;
    public List<int> NativeZones { get; init; } = [1, 2];
    public int NativeDelay { get; init; } = 450;

    public LayoutDefinition Clone() => new()
    {
        Mode = Mode,
        Orientation = Orientation,
        Ratios = [.. Ratios],
        Gap = Gap,
        Monitor = Monitor,
        NativeProfileId = NativeProfileId,
        NativeLayout = NativeLayout,
        NativeZones = [.. NativeZones],
        NativeDelay = NativeDelay
    };
}

public sealed class WorkflowSetting
{
    public string Hotkey { get; set; } = string.Empty;
}
