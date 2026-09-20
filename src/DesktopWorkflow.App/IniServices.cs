using System.Globalization;
using System.Text;

namespace DesktopWorkflow.App;

public sealed class IniDocument
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public static IniDocument Load(string path)
    {
        var document = new IniDocument();
        var section = string.Empty;
        foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                document.GetSection(section);
                continue;
            }
            var separator = line.IndexOf('=');
            if (section.Length == 0 || separator < 1)
            {
                continue;
            }
            document.GetSection(section)[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return document;
    }

    public IEnumerable<string> Sections => _sections.Keys;

    public string Get(string section, string key, string fallback = "") =>
        _sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value)
            ? value
            : fallback;

    public int GetInt(string section, string key, int fallback) =>
        int.TryParse(Get(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    public double GetDouble(string section, string key, double fallback) =>
        double.TryParse(Get(section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    public bool GetBool(string section, string key, bool fallback)
    {
        var value = Get(section, key).ToLowerInvariant();
        return value.Length == 0 ? fallback : value is "true" or "1" or "yes";
    }

    private Dictionary<string, string> GetSection(string section)
    {
        if (!_sections.TryGetValue(section, out var values))
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _sections[section] = values;
        }
        return values;
    }
}

public sealed class WorkflowRepository(string workflowDirectory)
{
    public WorkflowLoadResult Load()
    {
        Directory.CreateDirectory(workflowDirectory);
        var workflows = new List<WorkflowDefinition>();
        var diagnostics = new List<WorkflowLoadDiagnostic>();
        foreach (var path in Directory.GetFiles(workflowDirectory, "*.ini")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                workflows.Add(LoadFile(path));
            }
            catch (Exception exception)
            {
                diagnostics.Add(new WorkflowLoadDiagnostic(path, exception.Message));
            }
        }

        var duplicateIds = workflows.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => new { group.Key, Workflows = group.ToArray() })
            .ToArray();
        foreach (var group in duplicateIds)
        {
            foreach (var workflow in group.Workflows)
            {
                diagnostics.Add(new WorkflowLoadDiagnostic(workflow.SourcePath, $"工作流 ID 重复：{group.Key}"));
                workflows.Remove(workflow);
            }
        }
        return new WorkflowLoadResult
        {
            Workflows = workflows,
            Diagnostics = diagnostics
        };
    }

    public WorkflowDefinition LoadFile(string path)
    {
        var ini = IniDocument.Load(path);
        var id = ini.Get("workflow", "id").Trim();
        var name = ini.Get("workflow", "name").Trim();
        if (id.Length == 0 || name.Length == 0)
        {
            throw new InvalidDataException($"配置缺少 workflow.id 或 workflow.name：{path}");
        }

        var windowSections = ini.Sections
            .Where(section => section.StartsWith("window.", StringComparison.OrdinalIgnoreCase))
            .Select(section => new
            {
                Section = section,
                IsValid = int.TryParse(section[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0,
                Number = int.TryParse(section[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0
            })
            .OrderBy(item => item.Number)
            .ToArray();
        if (windowSections.Any(item => !item.IsValid) ||
            !windowSections.Select(item => item.Number).SequenceEqual(Enumerable.Range(1, windowSections.Length)))
        {
            throw new InvalidDataException($"{name} 的 window.N 必须从 1 开始连续编号。");
        }

        var windows = new List<WindowDefinition>();
        foreach (var item in windowSections)
        {
            var windowName = ini.Get(item.Section, "name").Trim();
            if (windowName.Length == 0)
            {
                throw new InvalidDataException($"{name} 的 {item.Section}.name 为空。");
            }
            windows.Add(new WindowDefinition
            {
                Name = windowName,
                Exe = ini.Get(item.Section, "exe").Trim(),
                Args = ini.Get(item.Section, "args").Trim(),
                WorkDir = ini.Get(item.Section, "workdir").Trim(),
                Match = ini.Get(item.Section, "match").Trim(),
                ProcessArgsContains = ini.Get(item.Section, "process_args_contains").Trim(),
                Reuse = ini.GetBool(item.Section, "reuse", true),
                WaitSeconds = ini.GetInt(item.Section, "wait_seconds", 25)
            });
        }

        var mode = ini.Get("layout", "mode", "proportional").Trim().ToLowerInvariant();
        var orientation = ini.Get("layout", "orientation", "horizontal").Trim().ToLowerInvariant();
        var ratios = ParseDoubles(ini.Get("layout", "ratios", "0.5,0.5"));
        var nativeProfileId = ini.Get("layout", "native_profile").Trim().ToLowerInvariant();
        var nativeLayout = ini.GetInt("layout", "native_layout", 5);
        var nativeZones = ParseInts(ini.Get("layout", "native_zones", "1,2"));
        if (windows.Count == 0 || ratios.Count != windows.Count)
        {
            throw new InvalidDataException($"{name} 的窗口数量与 ratios 数量不一致。");
        }
        if (ratios.Any(ratio => !double.IsFinite(ratio) || ratio <= 0))
        {
            throw new InvalidDataException($"{name} 的 ratios 必须全部是大于 0 的有限数值。");
        }
        if (mode is not "native" and not "proportional")
        {
            throw new InvalidDataException($"{name} 的布局模式无效：{mode}");
        }
        if (orientation is not "horizontal" and not "vertical")
        {
            throw new InvalidDataException($"{name} 的排列方向无效：{orientation}");
        }
        if (mode == "native")
        {
            var profiles = NativeLayoutCatalog.CreateDefaults();
            var profile = string.IsNullOrWhiteSpace(nativeProfileId)
                ? NativeLayoutCatalog.FindByMenuNumber(profiles, nativeLayout)
                : NativeLayoutCatalog.FindById(profiles, nativeProfileId);
            if (profile is null || windows.Count != profile.Zones.Count || nativeZones.Count != profile.Zones.Count ||
                nativeZones.Distinct().Count() != nativeZones.Count || nativeZones.Any(zone => zone < 1 || zone > profile.Zones.Count))
            {
                throw new InvalidDataException($"{name} 的原生布局、窗口数量与区域映射不一致。");
            }
        }
        foreach (var window in windows)
        {
            if (!File.Exists(window.Exe))
            {
                throw new FileNotFoundException($"{name} 找不到程序：{window.Exe}");
            }
            if (window.Match.Length == 0)
            {
                throw new InvalidDataException($"{name} 的窗口匹配规则为空。");
            }
            if (window.WaitSeconds <= 0)
            {
                throw new InvalidDataException($"{name} 的窗口等待时间必须大于 0：{window.Name}");
            }
        }

        return new WorkflowDefinition
        {
            Id = id,
            Name = name,
            Description = ini.Get("workflow", "description").Trim(),
            Hotkey = ini.Get("workflow", "hotkey").Trim(),
            SourcePath = path,
            Windows = windows,
            Layout = new LayoutDefinition
            {
                Mode = mode,
                Orientation = orientation,
                Ratios = ratios,
                Gap = ini.GetInt("layout", "gap", 0),
                Monitor = ini.Get("layout", "monitor", "primary").Trim().ToLowerInvariant(),
                NativeProfileId = nativeProfileId,
                NativeLayout = nativeLayout,
                NativeZones = nativeZones,
                NativeDelay = Math.Clamp(ini.GetInt("layout", "native_delay_ms", 700), 200, 3000)
            }
        };
    }

    private static List<double> ParseDoubles(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => double.Parse(part.Trim(), CultureInfo.InvariantCulture)).ToList();

    private static List<int> ParseInts(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => int.Parse(part.Trim(), CultureInfo.InvariantCulture)).ToList();
}

public sealed class UserSettingsStore(string path)
{
    private readonly Dictionary<string, WorkflowSetting> _values = new(StringComparer.OrdinalIgnoreCase);

    public void Load()
    {
        _values.Clear();
        if (!File.Exists(path))
        {
            return;
        }
        var ini = IniDocument.Load(path);
        foreach (var section in ini.Sections.Where(section => section.StartsWith("workflow.", StringComparison.OrdinalIgnoreCase)))
        {
            var id = section[9..];
            _values[id] = new WorkflowSetting
            {
                Hotkey = ini.Get(section, "hotkey", "__missing__").Trim()
            };
        }
    }

    public WorkflowSetting Get(WorkflowDefinition workflow)
    {
        if (_values.TryGetValue(workflow.Id, out var value))
        {
            return new WorkflowSetting
            {
                Hotkey = value.Hotkey == "__missing__" ? workflow.Hotkey : value.Hotkey
            };
        }
        return new WorkflowSetting
        {
            Hotkey = workflow.Hotkey
        };
    }

    public bool TryGetOverride(string id, out WorkflowSetting setting)
    {
        if (_values.TryGetValue(id, out var value))
        {
            setting = new WorkflowSetting
            {
                Hotkey = value.Hotkey
            };
            return true;
        }
        setting = new WorkflowSetting();
        return false;
    }

    public void Set(string id, WorkflowSetting setting)
    {
        var hadPrevious = _values.TryGetValue(id, out var previous);
        _values[id] = new WorkflowSetting
        {
            Hotkey = setting.Hotkey
        };
        try
        {
            Save();
        }
        catch
        {
            if (hadPrevious) _values[id] = previous!;
            else _values.Remove(id);
            throw;
        }
    }

    public void Remove(string id)
    {
        if (!_values.Remove(id, out var previous))
        {
            return;
        }
        try
        {
            Save();
        }
        catch
        {
            _values[id] = previous;
            throw;
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var builder = new StringBuilder("; 桌面控制窗口保存的用户设置。\r\n");
        foreach (var pair in _values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("\r\n[workflow.").Append(pair.Key).Append("]\r\n")
                .Append("hotkey=").Append(pair.Value.Hotkey).Append("\r\n");
        }
        var temporaryPath = path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, builder.ToString(), new UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
