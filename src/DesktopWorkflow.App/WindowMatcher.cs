namespace DesktopWorkflow.App;

public static class WindowMatcher
{
    public static nint Find(string match, string processArgsContains = "") =>
        FindAll(match, processArgsContains).FirstOrDefault();

    public static IReadOnlyList<nint> FindAll(string match, string processArgsContains = "")
    {
        Parse(match, out var title, out var executable);
        var titleCandidates = title.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = new List<nint>();
        NativeMethods.EnumWindows((window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window))
            {
                return true;
            }
            var actualTitle = NativeMethods.GetWindowTitle(window);
            var actualExecutable = NativeMethods.GetProcessName(window);
            var titleMatches = titleCandidates.Length == 0 ||
                titleCandidates.Any(candidate => actualTitle.Contains(candidate, StringComparison.OrdinalIgnoreCase));
            var executableMatches = executable.Length == 0 || string.Equals(actualExecutable, executable, StringComparison.OrdinalIgnoreCase);
            var argumentsMatch = ProcessArgumentsMatch(window, processArgsContains);
            if (titleMatches && executableMatches && argumentsMatch)
            {
                results.Add(window);
            }
            return true;
        }, 0);
        return results;
    }

    public static IReadOnlyList<nint> FindByExecutable(string executable, string processArgsContains = "") =>
        string.IsNullOrWhiteSpace(executable) ? [] : FindAll($"ahk_exe {executable}", processArgsContains);

    public static bool ProcessArgumentsMatch(nint window, string requiredFragment) =>
        string.IsNullOrWhiteSpace(requiredFragment) ||
        NativeMethods.GetProcessCommandLine(window).Contains(requiredFragment, StringComparison.OrdinalIgnoreCase);

    public static string GetExecutable(string match)
    {
        Parse(match, out _, out var executable);
        return executable;
    }

    private static void Parse(string match, out string title, out string executable)
    {
        const string marker = "ahk_exe ";
        var markerIndex = match.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            title = match.Trim();
            executable = string.Empty;
            return;
        }
        title = match[..markerIndex].Trim();
        executable = match[(markerIndex + marker.Length)..].Trim();
    }
}
