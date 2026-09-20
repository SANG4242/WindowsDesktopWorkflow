using System.Text;

namespace DesktopWorkflow.App;

public sealed class WorkflowFileStore(
    string workflowDirectory,
    WorkflowRepository repository,
    UserSettingsStore settings,
    WorkflowValidator validator)
{
    public WorkflowDefinition Save(
        WorkflowDraft draft,
        IReadOnlyList<WorkflowDefinition> current,
        Action<string?> reloadAndConfirm)
    {
        Directory.CreateDirectory(workflowDirectory);
        var originalPath = string.IsNullOrWhiteSpace(draft.SourcePath) ? null : Path.GetFullPath(draft.SourcePath);
        var issues = validator.Validate(draft, current, originalPath);
        var errors = issues.Where(issue => issue.Severity == ValidationSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors.Select(issue => $"- {issue.Message}")));
        }

        var targetPath = originalPath ?? GetAvailablePath(draft.Name);
        if (!IsInWorkflowDirectory(targetPath))
        {
            throw new InvalidOperationException("工作流文件必须保存在用户工作流目录中。");
        }
        var originalWorkflow = originalPath is null ? null : current.FirstOrDefault(item =>
            string.Equals(item.SourcePath, originalPath, StringComparison.OrdinalIgnoreCase));
        var originalId = originalWorkflow?.Id;
        var previousSetting = new WorkflowSetting();
        var hadSettingOverride = originalId is not null && settings.TryGetOverride(originalId, out previousSetting);
        var temporaryPath = Path.Combine(workflowDirectory, $".{Guid.NewGuid():N}.tmp");
        var backupPath = Path.Combine(workflowDirectory, $".{Guid.NewGuid():N}.bak");
        var replacedExisting = File.Exists(targetPath);
        try
        {
            File.WriteAllText(temporaryPath, WorkflowIniSerializer.Serialize(draft), new UTF8Encoding(false));
            var parsed = repository.LoadFile(temporaryPath);
            if (replacedExisting)
            {
                File.Replace(temporaryPath, targetPath, backupPath, true);
            }
            else
            {
                File.Move(temporaryPath, targetPath);
            }

            try
            {
                if (hadSettingOverride)
                {
                    settings.Set(parsed.Id, new WorkflowSetting
                    {
                        Hotkey = draft.Hotkey
                    });
                    if (!string.Equals(originalId, parsed.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        settings.Remove(originalId!);
                    }
                }
                reloadAndConfirm(parsed.Id);
            }
            catch (Exception commitException)
            {
                RestoreSave(targetPath, backupPath, replacedExisting);
                RestoreSetting(originalId, parsed.Id, hadSettingOverride, previousSetting);
                reloadAndConfirm(originalId);
                throw new InvalidOperationException($"工作流更新未能应用，已恢复原配置：{commitException.Message}", commitException);
            }

            TryDelete(backupPath);
            draft.SourcePath = targetPath;
            return repository.LoadFile(targetPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public void Delete(WorkflowDefinition workflow, Action<string?> reloadAndConfirm)
    {
        var sourcePath = Path.GetFullPath(workflow.SourcePath);
        if (!IsInWorkflowDirectory(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("找不到要删除的工作流文件。", sourcePath);
        }
        var stagedPath = Path.Combine(workflowDirectory, $".{Guid.NewGuid():N}.deleting");
        var hadSettingOverride = settings.TryGetOverride(workflow.Id, out var previousSetting);
        File.Move(sourcePath, stagedPath);
        try
        {
            settings.Remove(workflow.Id);
            reloadAndConfirm(null);
            RecycleFile(stagedPath);
        }
        catch (Exception deleteException)
        {
            if (File.Exists(stagedPath)) File.Move(stagedPath, sourcePath);
            if (hadSettingOverride) settings.Set(workflow.Id, previousSetting);
            else settings.Remove(workflow.Id);
            reloadAndConfirm(workflow.Id);
            throw new InvalidOperationException($"工作流删除未能应用，已恢复原配置：{deleteException.Message}", deleteException);
        }
    }

    private void RestoreSetting(
        string? originalId,
        string currentId,
        bool hadSettingOverride,
        WorkflowSetting previousSetting)
    {
        if (!string.Equals(originalId, currentId, StringComparison.OrdinalIgnoreCase))
        {
            settings.Remove(currentId);
        }
        if (originalId is null)
        {
            return;
        }
        if (hadSettingOverride) settings.Set(originalId, previousSetting);
        else settings.Remove(originalId);
    }

    private string GetAvailablePath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string(name.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        if (stem.Length == 0) stem = "工作流";
        var candidate = Path.Combine(workflowDirectory, stem + ".ini");
        for (var suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(workflowDirectory, $"{stem} ({suffix}).ini");
        }
        return candidate;
    }

    private bool IsInWorkflowDirectory(string path)
    {
        var root = Path.GetFullPath(workflowDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void RestoreSave(string targetPath, string backupPath, bool replacedExisting)
    {
        if (replacedExisting && File.Exists(backupPath))
        {
            if (File.Exists(targetPath)) File.Replace(backupPath, targetPath, null, true);
            else File.Move(backupPath, targetPath);
        }
        else if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }
    }

    private static void RecycleFile(string path)
    {
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
