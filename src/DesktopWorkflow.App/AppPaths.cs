namespace DesktopWorkflow.App;

public sealed class AppPaths
{
    public required string DataRoot { get; init; }
    public string WorkflowDirectory => Path.Combine(DataRoot, "workflows");
    public string SettingsDirectory => Path.Combine(DataRoot, "settings");
    public string LogDirectory => Path.Combine(DataRoot, "logs");
    public string SettingsPath => Path.Combine(SettingsDirectory, "user-settings.ini");
    public string SessionPath => Path.Combine(SettingsDirectory, "active-session.json");
    public string NativeLayoutProfilesPath => Path.Combine(SettingsDirectory, "native-layout-profiles.json");
    public string LogPath => Path.Combine(LogDirectory, "desktop-workflow.log");

    public static AppPaths Resolve(string[] args)
    {
        var explicitRoot = GetArgument(args, "--workspace-root");
        var root = string.IsNullOrWhiteSpace(explicitRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopWorkflow")
            : explicitRoot;
        var paths = new AppPaths { DataRoot = Path.GetFullPath(root) };
        paths.EnsureDirectories();
        return paths;
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(WorkflowDirectory);
        Directory.CreateDirectory(SettingsDirectory);
        Directory.CreateDirectory(LogDirectory);

        // 如果 LocalAppData 中没有工作流，自动从应用程序所在项目目录或便携目录同步种子工作流与设置
        try
        {
            if (!Directory.EnumerateFiles(WorkflowDirectory, "*.ini").Any())
            {
                var appDir = AppContext.BaseDirectory;
                var candidates = new[]
                {
                    Path.Combine(appDir, "..", "..", "..", "..", "workflows"),
                    Path.Combine(appDir, "..", "workflows"),
                    Path.Combine(appDir, "workflows")
                };

                foreach (var candidate in candidates)
                {
                    var fullPath = Path.GetFullPath(candidate);
                    if (Directory.Exists(fullPath) && Directory.EnumerateFiles(fullPath, "*.ini").Any())
                    {
                        foreach (var file in Directory.EnumerateFiles(fullPath, "*.ini"))
                        {
                            var target = Path.Combine(WorkflowDirectory, Path.GetFileName(file));
                            if (!File.Exists(target)) File.Copy(file, target);
                        }
                        var candidateSettings = Path.Combine(Path.GetDirectoryName(fullPath)!, "settings");
                        if (Directory.Exists(candidateSettings))
                        {
                            foreach (var sFile in Directory.EnumerateFiles(candidateSettings))
                            {
                                var sTarget = Path.Combine(SettingsDirectory, Path.GetFileName(sFile));
                                if (!File.Exists(sTarget)) File.Copy(sFile, sTarget);
                            }
                        }
                        break;
                    }
                }
            }
        }
        catch
        {
            // 容错处理，不阻断主启动流
        }
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }
}
