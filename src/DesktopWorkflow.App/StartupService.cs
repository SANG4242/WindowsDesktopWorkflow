using Microsoft.Win32;

namespace DesktopWorkflow.App;

public sealed class StartupService
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "DesktopWorkflow";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        var value = key?.GetValue(ValueName) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        var executable = $"\"{Environment.ProcessPath}\"";
        return value.StartsWith(executable, StringComparison.OrdinalIgnoreCase) &&
            value.Contains("--startup", StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("无法打开当前用户开机启动注册表项。");
        if (enabled)
        {
            key.SetValue(ValueName, Command(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }

    private static string Command() => $"\"{Environment.ProcessPath}\" --startup";
}
