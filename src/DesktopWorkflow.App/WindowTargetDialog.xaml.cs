using System.Windows;
using WpfMessageBox = System.Windows.MessageBox;

namespace DesktopWorkflow.App;

public partial class WindowTargetDialog : Window
{
    private readonly WindowTargetDraft _target;

    public WindowTargetDialog(WindowTargetDraft target)
    {
        _target = target;
        InitializeComponent();
        SourceText.Text = string.IsNullOrWhiteSpace(target.SourceSummary) ? "工作流中的窗口目标" : target.SourceSummary;
        NameTextBox.Text = target.Name;
        ExeTextBox.Text = target.Launch.Exe;
        ArgumentsTextBox.Text = target.Launch.Arguments;
        WorkingDirectoryTextBox.Text = target.Launch.WorkingDirectory;
        ProcessNameTextBox.Text = target.ProcessName;
        TitlesTextBox.Text = target.TitleCandidatesText;
        ProcessArgumentsTextBox.Text = target.ProcessArgumentsContains;
        ReuseCheckBox.IsChecked = target.Reuse;
        WaitSecondsTextBox.Text = target.WaitSeconds.ToString();
    }

    private void TestMatchButton_Click(object sender, RoutedEventArgs e)
    {
        var processName = ProcessNameTextBox.Text.Trim();
        var titles = TitlesTextBox.Text.Trim();
        var match = string.IsNullOrWhiteSpace(titles) ? $"ahk_exe {processName}" : $"{titles} ahk_exe {processName}";
        var matches = WindowMatcher.FindAll(match, ProcessArgumentsTextBox.Text.Trim());
        MatchResultText.Text = matches.Count switch
        {
            0 => "未命中窗口，请检查进程、标题或命令行条件。",
            1 => $"命中 1 个窗口：{NativeMethods.GetWindowTitle(matches[0])}",
            _ => $"命中 {matches.Count} 个窗口：\n" + string.Join("\n", matches.Select(handle => $"• {NativeMethods.GetProcessName(handle)} · {NativeMethods.GetWindowTitle(handle)}"))
        };
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WaitSecondsTextBox.Text, out var waitSeconds) || waitSeconds <= 0)
        {
            WpfMessageBox.Show(this, "等待秒数必须是大于 0 的整数。", "应用设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(NameTextBox.Text) || string.IsNullOrWhiteSpace(ExeTextBox.Text) || string.IsNullOrWhiteSpace(ProcessNameTextBox.Text))
        {
            WpfMessageBox.Show(this, "显示名称、启动目标和进程文件名不能为空。", "应用设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _target.Name = NameTextBox.Text.Trim();
        _target.Launch.Exe = ExeTextBox.Text.Trim();
        _target.Launch.Arguments = ArgumentsTextBox.Text.Trim();
        _target.Launch.WorkingDirectory = WorkingDirectoryTextBox.Text.Trim();
        _target.ProcessName = ProcessNameTextBox.Text.Trim();
        _target.TitleCandidatesText = TitlesTextBox.Text;
        _target.ProcessArgumentsContains = ProcessArgumentsTextBox.Text.Trim();
        _target.Reuse = ReuseCheckBox.IsChecked == true;
        _target.WaitSeconds = waitSeconds;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
