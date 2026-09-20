using System.Text;

namespace DesktopWorkflow.App;

public sealed class WorkflowLogger(string path)
{
    private const long MaxLogSize = 10L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly object _syncRoot = new();

    public string LogPath => path;

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}";
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            lock (_syncRoot)
            {
                RotateIfNeeded(Utf8WithoutBom.GetByteCount(line));
                File.AppendAllText(path, line, Utf8WithoutBom);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"日志写入失败：{exception.Message}{Environment.NewLine}{line}");
        }
    }

    private void RotateIfNeeded(int nextEntrySize)
    {
        if (!File.Exists(path) || new FileInfo(path).Length + nextEntrySize <= MaxLogSize)
        {
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        var archivePath = System.IO.Path.Combine(
            directory,
            $"{System.IO.Path.GetFileNameWithoutExtension(path)}.1{System.IO.Path.GetExtension(path)}");
        File.Move(path, archivePath, true);
    }
}
