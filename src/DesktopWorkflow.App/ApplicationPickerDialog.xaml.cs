using System.Windows;
using System.Windows.Controls;
using FileDialog = Microsoft.Win32.OpenFileDialog;

namespace DesktopWorkflow.App;

public partial class ApplicationPickerDialog : Window
{
    private readonly RunningWindowDiscoveryService _runningWindows = new();
    private readonly ShortcutResolver _shortcutResolver = new();
    private IReadOnlyList<ApplicationCandidate> _currentCandidates = [];
    private IReadOnlyList<ApplicationCandidate> _installedCandidates = [];
    private ApplicationCandidate? _fileCandidate;
    private int _previewRequestId;

    public IReadOnlyList<ApplicationCandidate> SelectedCandidates { get; private set; } = [];

    public ApplicationPickerDialog()
    {
        InitializeComponent();
        RefreshCurrent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _installedCandidates = await Task.Run(() => new InstalledApplicationDiscoveryService(_shortcutResolver).Discover());
            InstalledLoadingText.Text = $"找到 {_installedCandidates.Count} 个可解析入口。";
            ApplyFilter();
        }
        catch (Exception exception)
        {
            InstalledLoadingText.Text = $"读取已安装应用失败：{exception.Message}";
        }
    }

    private void RefreshCurrentButton_Click(object sender, RoutedEventArgs e) => RefreshCurrent();

    private void RefreshCurrent()
    {
        _currentCandidates = _runningWindows.Discover(SystemWindowsCheckBox?.IsChecked == true);
        ApplyFilter();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var keyword = SearchTextBox?.Text.Trim() ?? string.Empty;
        static bool Contains(ApplicationCandidate item, string value) => value.Length == 0
            || item.DisplayName.Contains(value, StringComparison.CurrentCultureIgnoreCase)
            || item.WindowTitle.Contains(value, StringComparison.CurrentCultureIgnoreCase)
            || item.ExecutablePath.Contains(value, StringComparison.OrdinalIgnoreCase);
        CurrentListBox.ItemsSource = _currentCandidates.Where(item => Contains(item, keyword)).ToArray();
        InstalledListBox.ItemsSource = _installedCandidates.Where(item => Contains(item, keyword)).ToArray();
        UpdateCurrentWindowPreview();
    }

    private void CandidateListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAddButtonText();
        if (ReferenceEquals(sender, CurrentListBox))
        {
            UpdateCurrentWindowPreview();
        }
    }

    private async void UpdateCurrentWindowPreview()
    {
        var selected = CurrentListBox.SelectedItem as ApplicationCandidate;
        if (selected is null || selected.WindowHandle == nint.Zero)
        {
            CurrentWindowPreviewImage.Source = null;
            CurrentWindowPreviewImage.Visibility = Visibility.Collapsed;
            CurrentWindowPreviewPlaceholder.Visibility = Visibility.Visible;
            CurrentWindowPreviewPlaceholderText.Text = "点选左侧窗口查看预览";
            CurrentWindowPreviewTitle.Text = string.Empty;
            CurrentWindowPreviewMeta.Text = string.Empty;
            return;
        }

        var requestId = ++_previewRequestId;
        CurrentWindowPreviewTitle.Text = selected.WindowTitle;
        CurrentWindowPreviewMeta.Text = $"{selected.ProcessName} · 正在读取画面……";

        var hwnd = selected.WindowHandle;
        var thumbnail = await Task.Run(() => WindowThumbnailService.Capture(hwnd));

        if (requestId != _previewRequestId) return;

        if (thumbnail is not null)
        {
            CurrentWindowPreviewImage.Source = thumbnail;
            CurrentWindowPreviewImage.Visibility = Visibility.Visible;
            CurrentWindowPreviewPlaceholder.Visibility = Visibility.Collapsed;
            CurrentWindowPreviewMeta.Text = $"{selected.ProcessName} · {thumbnail.PixelWidth} × {thumbnail.PixelHeight}";
        }
        else
        {
            CurrentWindowPreviewImage.Source = null;
            CurrentWindowPreviewImage.Visibility = Visibility.Collapsed;
            CurrentWindowPreviewPlaceholder.Visibility = Visibility.Visible;
            CurrentWindowPreviewPlaceholderText.Text = "窗口已最小化或暂时无法捕获画面";
            CurrentWindowPreviewMeta.Text = selected.ProcessName;
        }
    }

    private void UpdateAddButtonText()
    {
        var count = GetSelectedCandidates().Count;
        AddButton.Content = count > 1 ? $"添加 ({count})" : "添加";
    }

    private List<ApplicationCandidate> GetSelectedCandidates()
    {
        if (SourceTabs.SelectedIndex == 0)
        {
            return CurrentListBox.SelectedItems.OfType<ApplicationCandidate>().ToList();
        }
        if (SourceTabs.SelectedIndex == 1)
        {
            return InstalledListBox.SelectedItems.OfType<ApplicationCandidate>().ToList();
        }
        if (SourceTabs.SelectedIndex == 2 && _fileCandidate is not null)
        {
            return [_fileCandidate];
        }
        return [];
    }

    private void CandidateListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox listBox && listBox.SelectedItem is ApplicationCandidate candidate)
        {
            SelectedCandidates = [candidate];
            DialogResult = true;
            Close();
        }
    }

    private void ChooseFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FileDialog
        {
            Title = "选择应用程序或快捷方式",
            Filter = "应用程序或快捷方式 (*.exe;*.lnk)|*.exe;*.lnk|应用程序 (*.exe)|*.exe|快捷方式 (*.lnk)|*.lnk",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _fileCandidate = _shortcutResolver.Resolve(dialog.FileName);
            FileNameText.Text = _fileCandidate.DisplayName;
            FileDetailText.Text = _fileCandidate.IsSupported ? _fileCandidate.Detail : _fileCandidate.UnsupportedReason;
        }
        catch (Exception exception)
        {
            _fileCandidate = null;
            FileNameText.Text = "无法读取文件";
            FileDetailText.Text = exception.Message;
        }
    }

    private void SourceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, SourceTabs)) StatusText.Text = string.Empty;
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedCandidates();
        if (selected.Count == 0)
        {
            StatusText.Text = "请先选择至少一个应用入口。";
            return;
        }

        var unsupported = selected.FirstOrDefault(c => !c.IsSupported);
        if (unsupported is not null)
        {
            StatusText.Text = unsupported.UnsupportedReason;
            return;
        }

        SelectedCandidates = selected;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
