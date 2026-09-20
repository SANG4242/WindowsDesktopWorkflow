using Microsoft.Win32;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DesktopWorkflow.App;

public enum ApplicationCandidateSource
{
    RunningWindow,
    InstalledApplication,
    File
}

public sealed class ApplicationCandidate
{
    public string DisplayName { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string Arguments { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public string SuggestedTitle { get; init; } = string.Empty;
    public string ProcessArgumentsContains { get; init; } = string.Empty;
    public ApplicationCandidateSource Source { get; init; }
    public string SourceSummary { get; init; } = string.Empty;
    public bool IsSupported { get; init; } = true;
    public string UnsupportedReason { get; init; } = string.Empty;
    public nint WindowHandle { get; init; }

    public System.Windows.Media.ImageSource? Icon => IconHelper.GetIcon(ExecutablePath);

    public string Detail => IsSupported
        ? $"{SourceSummary}  {ExecutablePath}".Trim()
        : UnsupportedReason;

    public WindowTargetDraft ToDraft() => new()
    {
        Name = DisplayName,
        Launch = new LaunchSpecification
        {
            Exe = ExecutablePath,
            Arguments = Arguments,
            WorkingDirectory = WorkingDirectory
        },
        TitleCandidates = string.IsNullOrWhiteSpace(SuggestedTitle) ? [] : [SuggestedTitle],
        ProcessName = ProcessName,
        ProcessArgumentsContains = ProcessArgumentsContains,
        SourceSummary = SourceSummary
    };
}

public sealed class ShortcutResolver
{
    public ApplicationCandidate Resolve(string path, ApplicationCandidateSource source = ApplicationCandidateSource.File)
    {
        var fullPath = Path.GetFullPath(path);
        if (string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return FromExecutable(fullPath, source, source == ApplicationCandidateSource.File ? "手动选择的 EXE" : "已安装应用");
        }
        if (!string.Equals(Path.GetExtension(fullPath), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("只支持 EXE 或 Windows 快捷方式（LNK）。");
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new PlatformNotSupportedException("当前系统无法使用 Windows 快捷方式解析组件。");
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [fullPath]);
            if (shortcut is null) throw new InvalidDataException("无法读取快捷方式。");
            var shortcutType = shortcut.GetType();
            var target = Expand((string?)shortcutType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null));
            var arguments = (string?)shortcutType.InvokeMember("Arguments", BindingFlags.GetProperty, null, shortcut, null) ?? string.Empty;
            var workingDirectory = Expand((string?)shortcutType.InvokeMember("WorkingDirectory", BindingFlags.GetProperty, null, shortcut, null));
            var name = Path.GetFileNameWithoutExtension(fullPath);
            if (string.IsNullOrWhiteSpace(target) || !string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return new ApplicationCandidate
                {
                    DisplayName = name,
                    Source = source,
                    SourceSummary = $"快捷方式：{fullPath}",
                    IsSupported = false,
                    UnsupportedReason = "该快捷方式不能解析为普通 Win32 EXE。"
                };
            }
            target = Path.GetFullPath(target);
            return new ApplicationCandidate
            {
                DisplayName = name,
                ExecutablePath = target,
                Arguments = arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Path.GetDirectoryName(target) ?? string.Empty : workingDirectory,
                ProcessName = Path.GetFileName(target),
                SuggestedTitle = name,
                Source = source,
                SourceSummary = $"快捷方式：{fullPath}",
                IsSupported = File.Exists(target),
                UnsupportedReason = File.Exists(target) ? string.Empty : $"快捷方式目标不存在：{target}"
            };
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    public static ApplicationCandidate FromExecutable(string path, ApplicationCandidateSource source, string summary)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        var name = Path.GetFileNameWithoutExtension(fullPath);
        return new ApplicationCandidate
        {
            DisplayName = name,
            ExecutablePath = fullPath,
            WorkingDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty,
            ProcessName = Path.GetFileName(fullPath),
            SuggestedTitle = name,
            Source = source,
            SourceSummary = summary,
            IsSupported = File.Exists(fullPath),
            UnsupportedReason = File.Exists(fullPath) ? string.Empty : $"程序不存在：{fullPath}"
        };
    }

    private static string Expand(string? value) => Environment.ExpandEnvironmentVariables(value ?? string.Empty).Trim().Trim('"');
}

public sealed class RunningWindowDiscoveryService
{
    private static readonly Regex ChromeProfilePattern = new("--profile-directory=(?:\\\"[^\\\"]+\\\"|\\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<ApplicationCandidate> Discover(bool includeSystemWindows = false)
    {
        var results = new List<ApplicationCandidate>();
        var currentProcessId = Environment.ProcessId;
        NativeMethods.EnumWindows((window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window)) return true;
            var title = NativeMethods.GetWindowTitle(window).Trim();
            if (title.Length == 0) return true;
            var processId = (int)NativeMethods.GetProcessId(window);
            if (processId == currentProcessId) return true;

            var processName = NativeMethods.GetProcessName(window);
            var executablePath = TryGetExecutablePath(processId);
            if (string.IsNullOrWhiteSpace(processName) && !string.IsNullOrWhiteSpace(executablePath)) processName = Path.GetFileName(executablePath);
            if (!includeSystemWindows && IsSystemShell(processName)) return true;
            var commandLine = NativeMethods.GetProcessCommandLine(window);
            var profile = ChromeProfilePattern.Match(commandLine).Value;
            var displayName = string.IsNullOrWhiteSpace(executablePath)
                ? Path.GetFileNameWithoutExtension(processName)
                : FileVersionInfo.GetVersionInfo(executablePath).FileDescription;
            if (string.IsNullOrWhiteSpace(displayName)) displayName = Path.GetFileNameWithoutExtension(processName);
            results.Add(new ApplicationCandidate
            {
                WindowHandle = window,
                DisplayName = displayName,
                ExecutablePath = executablePath,
                Arguments = ExtractArguments(commandLine, executablePath),
                WorkingDirectory = string.IsNullOrWhiteSpace(executablePath) ? string.Empty : Path.GetDirectoryName(executablePath) ?? string.Empty,
                ProcessName = processName,
                WindowTitle = title,
                SuggestedTitle = SuggestStableTitle(title, displayName),
                ProcessArgumentsContains = profile,
                Source = ApplicationCandidateSource.RunningWindow,
                SourceSummary = $"当前窗口：{title}",
                IsSupported = true,
                UnsupportedReason = string.IsNullOrWhiteSpace(executablePath) ? "无法读取该窗口的程序路径，请在高级设置中补充。" : string.Empty
            });
            return true;
        }, 0);
        return results.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.WindowTitle, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string TryGetExecutablePath(int processId)
    {
        try { return Process.GetProcessById(processId).MainModule?.FileName ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool IsSystemShell(string processName) => processName.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("ShellExperienceHost.exe", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("SearchHost.exe", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("TextInputHost.exe", StringComparison.OrdinalIgnoreCase);

    private static string SuggestStableTitle(string title, string displayName)
    {
        foreach (var suffix in new[] { " - Google Chrome", " — Mozilla Firefox", " - Microsoft Edge" })
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) title = title[..^suffix.Length];
        }
        if (title.Length <= 48 && !string.IsNullOrWhiteSpace(title)) return title;
        return displayName;
    }

    private static string ExtractArguments(string commandLine, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return string.Empty;
        var value = commandLine.Trim();
        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            return closingQuote >= 0 ? value[(closingQuote + 1)..].Trim() : string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(executablePath) && value.StartsWith(executablePath, StringComparison.OrdinalIgnoreCase))
        {
            return value[executablePath.Length..].Trim();
        }
        var firstSpace = value.IndexOf(' ');
        return firstSpace >= 0 ? value[(firstSpace + 1)..].Trim() : string.Empty;
    }
}

public sealed class InstalledApplicationDiscoveryService(ShortcutResolver shortcutResolver)
{
    public IReadOnlyList<ApplicationCandidate> Discover()
    {
        var candidates = new List<ApplicationCandidate>();
        foreach (var directory in StartMenuDirectories())
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var path in SafeEnumerateFiles(directory, "*.lnk"))
            {
                try { candidates.Add(shortcutResolver.Resolve(path, ApplicationCandidateSource.InstalledApplication)); }
                catch { }
            }
        }
        AddAppPaths(candidates, Registry.CurrentUser);
        AddAppPaths(candidates, Registry.LocalMachine);
        AddUninstallEntries(candidates, RegistryHive.CurrentUser, RegistryView.Default);
        AddUninstallEntries(candidates, RegistryHive.LocalMachine, RegistryView.Registry64);
        AddUninstallEntries(candidates, RegistryHive.LocalMachine, RegistryView.Registry32);

        return candidates
            .Where(item => !string.IsNullOrWhiteSpace(item.DisplayName))
            .GroupBy(item => $"{item.ExecutablePath}\u001f{item.Arguments}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.IsSupported).First())
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> StartMenuDirectories()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path, string pattern)
    {
        try { return Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories).ToArray(); }
        catch { return []; }
    }

    private static void AddAppPaths(List<ApplicationCandidate> candidates, RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
            if (key is null) return;
            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(subKeyName);
                var path = subKey?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(path)) continue;
                candidates.Add(ShortcutResolver.FromExecutable(path.Trim('"'), ApplicationCandidateSource.InstalledApplication, "Windows App Paths"));
            }
        }
        catch { }
    }

    private static void AddUninstallEntries(List<ApplicationCandidate> candidates, RegistryHive hive, RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key is null) return;
            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(subKeyName);
                var name = subKey?.GetValue("DisplayName") as string;
                var displayIcon = subKey?.GetValue("DisplayIcon") as string;
                var path = ExtractExecutable(displayIcon);
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                var candidate = ShortcutResolver.FromExecutable(path, ApplicationCandidateSource.InstalledApplication, "已安装程序注册信息");
                candidates.Add(new ApplicationCandidate
                {
                    DisplayName = name,
                    ExecutablePath = candidate.ExecutablePath,
                    WorkingDirectory = candidate.WorkingDirectory,
                    ProcessName = candidate.ProcessName,
                    SuggestedTitle = name,
                    Source = candidate.Source,
                    SourceSummary = candidate.SourceSummary,
                    IsSupported = candidate.IsSupported,
                    UnsupportedReason = candidate.UnsupportedReason
                });
            }
        }
        catch { }
    }

    private static string ExtractExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded.StartsWith('"'))
        {
            var end = expanded.IndexOf('"', 1);
            return end > 1 ? expanded[1..end] : string.Empty;
        }
        var exeIndex = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0 ? expanded[..(exeIndex + 4)] : string.Empty;
    }
}
