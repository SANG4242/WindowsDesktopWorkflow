using System.Text.Json;
using System.Windows.Forms;

namespace DesktopWorkflow.App;

public sealed record NormalizedRectangle(double X, double Y, double Width, double Height);

public sealed class NativeLayoutProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int DefaultMenuNumber { get; init; }
    public int MenuNumber { get; set; }
    public required IReadOnlyList<NormalizedRectangle> Zones { get; init; }
    public bool IsVerified { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string StatusText => IsVerified ? "上次验证通过" : "待验证";
    public string DisplayName => $"{Name} · Win+Z 数字 {MenuNumber} · {Zones.Count} 个区域 · {StatusText}";

    public NativeLayoutProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        DefaultMenuNumber = DefaultMenuNumber,
        MenuNumber = MenuNumber,
        Zones = Zones,
        IsVerified = IsVerified,
        VerifiedAt = VerifiedAt
    };
}

public static class NativeLayoutCatalog
{
    private const double Gap = 0.012;

    public static IReadOnlyList<NativeLayoutProfile> CreateDefaults() =>
    [
        Profile("equal-columns", "左右等分", 4,
            R(0, 0, 0.5 - Gap / 2, 1),
            R(0.5 + Gap / 2, 0, 0.5 - Gap / 2, 1)),
        Profile("wide-left", "左宽右窄", 5,
            R(0, 0, 2d / 3 - Gap / 2, 1),
            R(2d / 3 + Gap / 2, 0, 1d / 3 - Gap / 2, 1)),
        Profile("three-columns", "三列等分", 6,
            R(0, 0, 1d / 3 - Gap, 1),
            R(1d / 3 + Gap / 2, 0, 1d / 3 - Gap, 1),
            R(2d / 3 + Gap / 2, 0, 1d / 3 - Gap / 2, 1)),
        Profile("left-and-right-stack", "左半区、右侧上下", 7,
            R(0, 0, 0.5 - Gap / 2, 1),
            R(0.5 + Gap / 2, 0, 0.5 - Gap / 2, 0.5 - Gap / 2),
            R(0.5 + Gap / 2, 0.5 + Gap / 2, 0.5 - Gap / 2, 0.5 - Gap / 2)),
        Profile("four-grid", "四宫格", 8,
            R(0, 0, 0.5 - Gap / 2, 0.5 - Gap / 2),
            R(0.5 + Gap / 2, 0, 0.5 - Gap / 2, 0.5 - Gap / 2),
            R(0, 0.5 + Gap / 2, 0.5 - Gap / 2, 0.5 - Gap / 2),
            R(0.5 + Gap / 2, 0.5 + Gap / 2, 0.5 - Gap / 2, 0.5 - Gap / 2)),
        Profile("narrow-wide-narrow", "窄宽窄三列", 9,
            R(0, 0, 0.25 - Gap / 2, 1),
            R(0.25 + Gap / 2, 0, 0.5 - Gap, 1),
            R(0.75 + Gap / 2, 0, 0.25 - Gap / 2, 1))
    ];

    public static NativeLayoutProfile? FindByMenuNumber(IReadOnlyList<NativeLayoutProfile> profiles, int menuNumber) =>
        profiles.FirstOrDefault(profile => profile.MenuNumber == menuNumber);

    public static NativeLayoutProfile? FindById(IReadOnlyList<NativeLayoutProfile> profiles, string id) =>
        profiles.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<NormalizedRectangle> CreateLinearZones(string orientation, IReadOnlyList<double> ratios)
    {
        if (ratios.Count == 0 || ratios.Any(value => value <= 0 || !double.IsFinite(value))) return [];
        var total = ratios.Sum();
        var cursor = 0d;
        var result = new List<NormalizedRectangle>();
        foreach (var ratio in ratios)
        {
            var size = ratio / total;
            result.Add(orientation == "vertical" ? R(0, cursor, 1, size) : R(cursor, 0, size, 1));
            cursor += size;
        }
        return result;
    }

    private static NativeLayoutProfile Profile(string id, string name, int menuNumber, params NormalizedRectangle[] zones) => new()
    {
        Id = id,
        Name = name,
        DefaultMenuNumber = menuNumber,
        MenuNumber = menuNumber,
        Zones = zones
    };

    private static NormalizedRectangle R(double x, double y, double width, double height) => new(x, y, width, height);
}

public sealed class NativeLayoutProfileStore(string path)
{
    private sealed class EnvironmentState
    {
        public Dictionary<string, int> MenuNumbers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DateTimeOffset> VerifiedLayouts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class State
    {
        public Dictionary<string, EnvironmentState> Environments { get; init; } = [];
    }

    public IReadOnlyList<NativeLayoutProfile> Load()
    {
        var profiles = NativeLayoutCatalog.CreateDefaults();
        var state = LoadState();
        if (!state.Environments.TryGetValue(GetEnvironmentKey(), out var environment)) return profiles;
        foreach (var profile in profiles)
        {
            if (environment.MenuNumbers.TryGetValue(profile.Id, out var menuNumber)) profile.MenuNumber = menuNumber;
            if (!environment.VerifiedLayouts.TryGetValue(profile.Id, out var verifiedAt)) continue;
            profile.IsVerified = true;
            profile.VerifiedAt = verifiedAt;
        }
        return profiles;
    }

    public void SaveMenuNumbers(IReadOnlyDictionary<string, int> menuNumbers)
    {
        var defaults = NativeLayoutCatalog.CreateDefaults();
        if (menuNumbers.Count != defaults.Count || menuNumbers.Values.Any(number => number is < 1 or > 9) ||
            menuNumbers.Values.Distinct().Count() != menuNumbers.Count ||
            defaults.Any(profile => !menuNumbers.ContainsKey(profile.Id)))
        {
            throw new InvalidDataException("六种固定布局必须分别映射到 1–9 中互不重复的数字。");
        }

        var state = LoadState();
        var environment = GetOrCreateEnvironment(state);
        foreach (var profile in defaults)
        {
            var newNumber = menuNumbers[profile.Id];
            var oldNumber = environment.MenuNumbers.TryGetValue(profile.Id, out var savedNumber)
                ? savedNumber
                : profile.DefaultMenuNumber;
            environment.MenuNumbers[profile.Id] = newNumber;
            if (oldNumber != newNumber) environment.VerifiedLayouts.Remove(profile.Id);
        }
        SaveState(state);
    }

    public void MarkVerified(string profileId)
    {
        var state = LoadState();
        var environment = GetOrCreateEnvironment(state);
        environment.VerifiedLayouts[profileId] = DateTimeOffset.Now;
        SaveState(state);
    }

    public void MarkUnverified(string profileId)
    {
        var state = LoadState();
        var environment = GetOrCreateEnvironment(state);
        if (environment.VerifiedLayouts.Remove(profileId)) SaveState(state);
    }

    private State LoadState()
    {
        if (!File.Exists(path)) return new State();
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State(); }
        catch { return new State(); }
    }

    private EnvironmentState GetOrCreateEnvironment(State state)
    {
        var environmentKey = GetEnvironmentKey();
        if (!state.Environments.TryGetValue(environmentKey, out var environment))
        {
            environment = new EnvironmentState();
            state.Environments[environmentKey] = environment;
        }
        return environment;
    }

    private void SaveState(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, true);
    }

    private static string GetEnvironmentKey()
    {
        var displays = Screen.AllScreens.OrderBy(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(screen => $"{screen.DeviceName}:{screen.Bounds.X},{screen.Bounds.Y},{screen.Bounds.Width},{screen.Bounds.Height}:{screen.WorkingArea.Width}x{screen.WorkingArea.Height}");
        return $"{Environment.OSVersion.Version}|{string.Join('|', displays)}";
    }
}
