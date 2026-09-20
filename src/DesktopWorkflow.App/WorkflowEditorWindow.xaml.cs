using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfMessageBox = System.Windows.MessageBox;

namespace DesktopWorkflow.App;

public sealed class TargetItemViewModel
{
    public WindowTargetDraft Target { get; }
    public string Name => Target.Name;
    public string MatchSummary
    {
        get
        {
            var exe = Path.GetFileName(Target.Launch.Exe);
            if (string.IsNullOrWhiteSpace(exe)) exe = Target.ProcessName;
            var parts = new List<string> { exe };
            if (!string.IsNullOrWhiteSpace(Target.Launch.Arguments))
            {
                parts.Add($"参数: {Target.Launch.Arguments}");
            }
            if (Target.TitleCandidates.Count > 0)
            {
                parts.Add($"标题: {Target.TitleCandidates[0]}");
            }
            return string.Join(" · ", parts);
        }
    }
    public TargetItemViewModel(WindowTargetDraft target) => Target = target;
}

public sealed class ZoneAssignmentRow
{
    public int ZoneIndex { get; init; }
    public string ZoneLabel { get; init; } = string.Empty;
    public IReadOnlyList<WindowTargetDraft> TargetOptions { get; init; } = [];
    public WindowTargetDraft? SelectedTarget { get; set; }
}

public partial class WorkflowEditorWindow : Window
{
    private readonly App _app;
    private readonly WorkflowDraft _draft;
    private readonly string? _originalSourcePath;
    private readonly string _initialSnapshot;
    private IReadOnlyList<NativeLayoutProfile> _nativeProfiles;
    private readonly List<ZoneAssignmentRow> _assignmentRows = [];
    private bool _loading = true;
    private bool _idAuto;
    private bool _saved;
    private bool _discardConfirmed;
    private string _rawHotkey = string.Empty;

    public WorkflowEditorWindow(App app, WorkflowDraft draft, bool isCopy = false)
    {
        _app = app;
        _draft = draft;
        _originalSourcePath = isCopy ? null : draft.SourcePath;
        if (isCopy) _draft.SourcePath = null;
        _nativeProfiles = app.NativeLayoutProfiles;

        InitializeComponent();

        EditorTitleText.Text = _originalSourcePath is null ? "新建工作流" : $"编辑工作流 / {_draft.Name}";
        NameTextBox.Text = _draft.Name;
        IdTextBox.Text = _draft.Id;
        DescriptionTextBox.Text = _draft.Description;
        _rawHotkey = _draft.Hotkey;
        HotkeyTextBox.Text = GlobalHotkeyService.FormatForDisplay(_rawHotkey);

        // 布局模式
        var isNative = _draft.Layout.Mode == "native";
        NativeModeRadio.IsChecked = isNative;
        ProportionalModeRadio.IsChecked = !isNative;

        OrientationComboBox.SelectedValue = _draft.Layout.Orientation;
        if (OrientationComboBox.SelectedIndex < 0) OrientationComboBox.SelectedIndex = 0;
        RatiosTextBox.Text = FormatRatios(_draft.Layout.Ratios);

        NativeLayoutComboBox.ItemsSource = _nativeProfiles;
        var selectedProfile = NativeLayoutCatalog.FindById(_nativeProfiles, _draft.Layout.NativeProfileId);
        if (selectedProfile is null && string.IsNullOrWhiteSpace(_draft.Layout.NativeProfileId))
        {
            var legacy = NativeLayoutCatalog.FindByMenuNumber(NativeLayoutCatalog.CreateDefaults(), _draft.Layout.NativeLayout);
            if (legacy is not null) selectedProfile = NativeLayoutCatalog.FindById(_nativeProfiles, legacy.Id);
        }
        NativeLayoutComboBox.SelectedItem = selectedProfile ?? _nativeProfiles.FirstOrDefault();

        _idAuto = string.IsNullOrWhiteSpace(_draft.Id);
        _loading = false;

        RefreshTargetsList();
        UpdateLayoutModeVisibility();
        BuildAssignments();
        UpdatePreviewAndStats();

        _initialSnapshot = CreateSnapshot();
    }

    private void RefreshTargetsList()
    {
        TargetsListBox.ItemsSource = _draft.Windows.Select(target => new TargetItemViewModel(target)).ToList();
    }

    private void NameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && _idAuto)
        {
            _draft.Id = WorkflowDraft.CreateId(NameTextBox.Text);
        }
        if (!_loading)
        {
            _draft.Name = NameTextBox.Text.Trim();
            EditorTitleText.Text = string.IsNullOrWhiteSpace(_draft.Name) ? "新建工作流" : $"编辑工作流 / {_draft.Name}";
            UpdatePreviewAndStats();
        }
    }

    private void IdTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && IdTextBox.IsKeyboardFocused) _idAuto = false;
        if (!_loading)
        {
            _draft.Id = IdTextBox.Text.Trim();
            UpdatePreviewAndStats();
        }
    }

    private void DescriptionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
        {
            _draft.Description = DescriptionTextBox.Text;
        }
    }

    private void AddApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ApplicationPickerDialog { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedCandidates.Count == 0) return;

        // 一次性批量添加所选的多个窗口目标
        foreach (var candidate in picker.SelectedCandidates)
        {
            var target = candidate.ToDraft();
            _draft.Windows.Add(target);
            if (_draft.Layout.Ratios.Count < _draft.Windows.Count)
            {
                _draft.Layout.Ratios.Add(1d);
            }
        }
        RatiosTextBox.Text = FormatRatios(_draft.Layout.Ratios);

        RefreshTargetsList();
        BuildAssignments();
        UpdatePreviewAndStats();
    }

    private void MoveUpTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: TargetItemViewModel item })
        {
            var index = _draft.Windows.IndexOf(item.Target);
            if (index > 0)
            {
                _draft.Windows.RemoveAt(index);
                _draft.Windows.Insert(index - 1, item.Target);
                RefreshTargetsList();
                BuildAssignments();
                UpdatePreviewAndStats();
            }
        }
    }

    private void MoveDownTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: TargetItemViewModel item })
        {
            var index = _draft.Windows.IndexOf(item.Target);
            if (index >= 0 && index < _draft.Windows.Count - 1)
            {
                _draft.Windows.RemoveAt(index);
                _draft.Windows.Insert(index + 1, item.Target);
                RefreshTargetsList();
                BuildAssignments();
                UpdatePreviewAndStats();
            }
        }
    }

    private void EditTargetItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: TargetItemViewModel item })
        {
            var dialog = new WindowTargetDialog(item.Target) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                RefreshTargetsList();
                BuildAssignments();
                UpdatePreviewAndStats();
            }
        }
    }

    private void RemoveTargetItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: TargetItemViewModel item })
        {
            if (WpfMessageBox.Show(this, $"从工作流中删除“{item.Target.Name}”？", "删除应用",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var index = _draft.Windows.IndexOf(item.Target);
            if (index >= 0)
            {
                _draft.Windows.RemoveAt(index);
                if (index < _draft.Layout.Ratios.Count) _draft.Layout.Ratios.RemoveAt(index);
                RatiosTextBox.Text = FormatRatios(_draft.Layout.Ratios);
                RefreshTargetsList();
                BuildAssignments();
                UpdatePreviewAndStats();
            }
        }
    }

    private void LayoutMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _draft.Layout.Mode = NativeModeRadio.IsChecked == true ? "native" : "proportional";
        UpdateLayoutModeVisibility();
        BuildAssignments();
        UpdatePreviewAndStats();
    }

    private void UpdateLayoutModeVisibility()
    {
        var isNative = NativeModeRadio.IsChecked == true;
        NativeOptionsPanel.Visibility = isNative ? Visibility.Visible : Visibility.Collapsed;
        ProportionalOptionsPanel.Visibility = isNative ? Visibility.Collapsed : Visibility.Visible;
    }

    private void NativeLayoutComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || NativeLayoutComboBox.SelectedItem is not NativeLayoutProfile profile) return;
        _draft.Layout.NativeProfileId = profile.Id;
        _draft.Layout.NativeLayout = profile.MenuNumber;
        _draft.Layout.NativeZones = Enumerable.Range(1, profile.Zones.Count).ToList();
        BuildAssignments();
        UpdatePreviewAndStats();
    }

    private void LayoutControl_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _draft.Layout.Orientation = OrientationComboBox.SelectedValue as string ?? "horizontal";
        UpdatePreviewAndStats();
    }

    private void RatiosTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (TryParseRatios(RatiosTextBox.Text, out var ratios))
        {
            _draft.Layout.Ratios = ratios;
        }
        BuildAssignments();
        UpdatePreviewAndStats();
    }

    private void ConfigureNativeLayoutsButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedId = (NativeLayoutComboBox.SelectedItem as NativeLayoutProfile)?.Id;
        var dialog = new NativeLayoutMappingDialog(_app, _nativeProfiles) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _nativeProfiles = _app.NativeLayoutProfiles;
        NativeLayoutComboBox.ItemsSource = _nativeProfiles;
        NativeLayoutComboBox.SelectedItem = NativeLayoutCatalog.FindById(_nativeProfiles, selectedId ?? string.Empty) ?? _nativeProfiles.FirstOrDefault();
        if (NativeLayoutComboBox.SelectedItem is NativeLayoutProfile profile)
        {
            _draft.Layout.NativeProfileId = profile.Id;
            _draft.Layout.NativeLayout = profile.MenuNumber;
        }
        BuildAssignments();
        UpdatePreviewAndStats();
    }

    private void BuildAssignments()
    {
        _assignmentRows.Clear();
        var zoneCount = CurrentZoneCount();
        var options = _draft.Windows.ToList();

        for (var i = 0; i < zoneCount; i++)
        {
            var zoneNum = i + 1;
            WindowTargetDraft? assignedTarget = null;
            if (_draft.Layout.Mode == "native")
            {
                var targetIndex = _draft.Layout.NativeZones.IndexOf(zoneNum);
                if (targetIndex >= 0 && targetIndex < _draft.Windows.Count)
                {
                    assignedTarget = _draft.Windows[targetIndex];
                }
            }
            else
            {
                if (i < _draft.Windows.Count)
                {
                    assignedTarget = _draft.Windows[i];
                }
            }

            // 确保 assignedTarget 是 options 集合中的精确同一个引用
            var matchedOption = assignedTarget is not null
                ? options.FirstOrDefault(opt => opt == assignedTarget || (opt.Name == assignedTarget.Name && opt.Launch.Exe == assignedTarget.Launch.Exe))
                : (i < options.Count ? options[i] : options.FirstOrDefault());

            _assignmentRows.Add(new ZoneAssignmentRow
            {
                ZoneIndex = i,
                ZoneLabel = $"区域 {zoneNum}",
                TargetOptions = options,
                SelectedTarget = matchedOption
            });
        }

        AssignmentItems.ItemsSource = null;
        AssignmentItems.ItemsSource = _assignmentRows;
    }

    private int CurrentZoneCount()
    {
        if (NativeModeRadio.IsChecked == true)
        {
            return (NativeLayoutComboBox.SelectedItem as NativeLayoutProfile)?.Zones.Count ?? 0;
        }
        return Math.Max(1, _draft.Layout.Ratios.Count);
    }

    private void AssignmentComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SyncAssignmentsToDraft();
        UpdatePreviewAndStats();
    }

    private void SyncAssignmentsToDraft()
    {
        if (_draft.Layout.Mode == "native")
        {
            var newZones = new List<int>();
            for (var targetIdx = 0; targetIdx < _draft.Windows.Count; targetIdx++)
            {
                var target = _draft.Windows[targetIdx];
                var foundRow = _assignmentRows.FirstOrDefault(row => row.SelectedTarget == target);
                newZones.Add(foundRow is not null ? foundRow.ZoneIndex + 1 : targetIdx + 1);
            }
            _draft.Layout.NativeZones = newZones;
        }
    }

    private void UpdatePreviewAndStats()
    {
        var isNative = NativeModeRadio.IsChecked == true;
        var currentZones = CurrentZoneCount();
        WindowCountText.Text = $"窗口数：{_draft.Windows.Count}";
        ZoneCountText.Text = $"布局区域：{currentZones}";

        var previewItems = new List<LayoutZoneItem>();
        if (isNative)
        {
            if (NativeLayoutComboBox.SelectedItem is NativeLayoutProfile profile)
            {
                PreviewSubtitleText.Text = $"Windows 原生贴靠 · {profile.Name}";
                for (var i = 0; i < profile.Zones.Count; i++)
                {
                    var zoneNumber = i + 1;
                    var assignedRow = _assignmentRows.FirstOrDefault(r => r.ZoneIndex == i);
                    var winName = assignedRow?.SelectedTarget?.Name ?? $"区域 {zoneNumber}";
                    var rect = profile.Zones[i];
                    previewItems.Add(new LayoutZoneItem(rect, winName, $"区域 {zoneNumber}", i == 0));
                }
            }
        }
        else
        {
            var orientation = OrientationComboBox.SelectedValue as string ?? "horizontal";
            PreviewSubtitleText.Text = $"自定义比例 · {(orientation == "vertical" ? "上下" : "左右")}";
            var ratios = TryParseRatios(RatiosTextBox.Text, out var parsed) ? parsed : _draft.Layout.Ratios;
            if (ratios.Count == 0) ratios = [0.5, 0.5];

            var total = ratios.Sum();
            if (total <= 0) total = 1;
            var offset = 0.0;
            for (var i = 0; i < ratios.Count; i++)
            {
                var prop = ratios[i] / total;
                var rect = orientation == "vertical"
                    ? new NormalizedRectangle(0, offset, 1, prop)
                    : new NormalizedRectangle(offset, 0, prop, 1);
                offset += prop;

                var assignedRow = _assignmentRows.FirstOrDefault(r => r.ZoneIndex == i);
                var winName = assignedRow?.SelectedTarget?.Name ?? (i < _draft.Windows.Count ? _draft.Windows[i].Name : $"区域 {i + 1}");
                previewItems.Add(new LayoutZoneItem(rect, winName, $"区域 {i + 1} · {ratios[i]}", i == 0));
            }
        }

        LayoutPreview.Items = previewItems;

        // 即时轻量结构校验反馈
        ValidateDraftInline();

        // 异步尝试为已打开的窗口获取真实画面预览
        RefreshPreviewThumbnailsAsync();
    }

    private int _previewThumbnailVersion;

    private async void RefreshPreviewThumbnailsAsync()
    {
        var version = ++_previewThumbnailVersion;
        var currentItems = LayoutPreview.Items.ToList();
        if (currentItems.Count == 0) return;

        var targets = new List<WindowTargetDraft?>();
        for (var i = 0; i < currentItems.Count; i++)
        {
            var assignedRow = _assignmentRows.FirstOrDefault(r => r.ZoneIndex == i);
            var target = assignedRow?.SelectedTarget ?? (i < _draft.Windows.Count ? _draft.Windows[i] : null);
            targets.Add(target);
        }

        var thumbnails = await Task.Run(() =>
        {
            var results = new List<System.Windows.Media.ImageSource?>();
            foreach (var target in targets)
            {
                if (target is null)
                {
                    results.Add(null);
                    continue;
                }

                var hwnd = WindowMatcher.Find(target.Match, target.ProcessArgumentsContains);
                if (hwnd == 0 && !string.IsNullOrWhiteSpace(target.ProcessName))
                {
                    hwnd = WindowMatcher.FindByExecutable(target.ProcessName, target.ProcessArgumentsContains).FirstOrDefault();
                }

                results.Add(hwnd != 0 ? WindowThumbnailService.Capture(hwnd) : null);
            }
            return results;
        });

        if (version != _previewThumbnailVersion) return;

        if (thumbnails.Any(t => t is not null) && LayoutPreview.Items.Count == thumbnails.Count)
        {
            var updatedItems = new List<LayoutZoneItem>();
            for (var i = 0; i < LayoutPreview.Items.Count; i++)
            {
                updatedItems.Add(LayoutPreview.Items[i] with { Thumbnail = thumbnails[i] });
            }
            LayoutPreview.Items = updatedItems;
        }
    }

    private void ValidateDraftInline()
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(NameTextBox.Text)) issues.Add("工作流名称不能为空");
        if (_draft.Windows.Count == 0) issues.Add("请至少添加一个应用目标");

        var zoneCount = CurrentZoneCount();
        if (_draft.Windows.Count != zoneCount) issues.Add($"窗口数({_draft.Windows.Count})与区域数({zoneCount})不一致");

        var duplicateTargets = _assignmentRows
            .Where(r => r.SelectedTarget is not null)
            .GroupBy(r => r.SelectedTarget)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key!.Name)
            .ToList();
        if (duplicateTargets.Count > 0) issues.Add($"应用“{string.Join("、", duplicateTargets)}”被分配到了多个区域");

        if (issues.Count == 0)
        {
            ValidationBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 253, 244));
            ValidationBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 252, 231));
            ValidationBadgeText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 163, 74));
            ValidationBadgeText.Text = "● 配置结构正常";
            EditorStatusText.Text = string.Empty;
        }
        else
        {
            ValidationBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(254, 242, 242));
            ValidationBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(254, 202, 202));
            ValidationBadgeText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 38, 38));
            ValidationBadgeText.Text = $"⚠ {issues[0]}";
            EditorStatusText.Text = string.Join("；", issues);
        }
    }

    private bool _isRecordingHotkey;

    private void RecordHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
        {
            // 点击完成保存
            _isRecordingHotkey = false;
            RecordHotkeyButton.Content = "录入快捷键";
            RecordHotkeyButton.Style = (Style)FindResource(typeof(System.Windows.Controls.Button));
            HotkeyHintText.Text = "快捷键已锁定；如需更改请再次点击「录入快捷键」。";
            HotkeyTextBox.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");
        }
        else
        {
            // 进入录入状态
            _isRecordingHotkey = true;
            RecordHotkeyButton.Content = "完成保存";
            RecordHotkeyButton.Style = (Style)FindResource("PrimaryButton");
            HotkeyHintText.Text = "👉 正在监听输入：请在键盘上直接按下组合键（如 Ctrl+Alt+1）...";
            HotkeyTextBox.BorderBrush = (System.Windows.Media.Brush)FindResource("PrimaryBrush");
            HotkeyTextBox.Focus();
        }
    }

    private void ClearHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _rawHotkey = string.Empty;
        HotkeyTextBox.Text = string.Empty;
        _draft.Hotkey = string.Empty;
        if (_isRecordingHotkey)
        {
            _isRecordingHotkey = false;
            RecordHotkeyButton.Content = "录入快捷键";
            RecordHotkeyButton.Style = (Style)FindResource(typeof(System.Windows.Controls.Button));
            HotkeyTextBox.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");
        }
        HotkeyHintText.Text = "已清空快捷键。点击「录入快捷键」可重新配置。";
    }

    private void HotkeyTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_isRecordingHotkey) return;

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }
        if (key is Key.Delete or Key.Back)
        {
            _rawHotkey = string.Empty;
            HotkeyTextBox.Text = string.Empty;
            _draft.Hotkey = string.Empty;
            return;
        }

        _rawHotkey = GlobalHotkeyService.Compose(key, Keyboard.Modifiers, false);
        HotkeyTextBox.Text = GlobalHotkeyService.FormatForDisplay(_rawHotkey);
        _draft.Hotkey = _rawHotkey;
        HotkeyHintText.Text = string.IsNullOrEmpty(_rawHotkey)
            ? "快捷键必须包含 Ctrl 或 Alt，请重新输入。"
            : $"已识别组合键：{HotkeyTextBox.Text}。点击「完成保存」锁定快捷键。";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _draft.Name = NameTextBox.Text.Trim();
        _draft.Description = DescriptionTextBox.Text;
        if (string.IsNullOrWhiteSpace(_draft.Id)) _draft.Id = WorkflowDraft.CreateId(_draft.Name);
        _draft.Hotkey = _rawHotkey;
        SyncAssignmentsToDraft();

        var validator = new WorkflowValidator();
        var issues = validator.Validate(_draft, _app.Workflows, _originalSourcePath, _nativeProfiles);
        var errors = issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            WpfMessageBox.Show(this, string.Join("\n", errors.Select(i => $"• {i.Message}")), "配置校验未通过", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _app.SaveWorkflow(_draft);
            _saved = true;
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(this, exception.Message, "保存工作流失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_saved || _discardConfirmed || !HasUnsavedChanges())
        {
            return;
        }

        var result = WpfMessageBox.Show(
            this,
            "工作流有未保存的改动，确定放弃吗？",
            "放弃修改",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _discardConfirmed = true;
    }

    private bool HasUnsavedChanges() => CreateSnapshot() != _initialSnapshot;

    private string CreateSnapshot()
    {
        _draft.Name = NameTextBox.Text;
        _draft.Description = DescriptionTextBox.Text;
        _draft.Hotkey = _rawHotkey;
        SyncAssignmentsToDraft();
        return WorkflowIniSerializer.Serialize(_draft) + "\nraw_ratios=" + RatiosTextBox.Text;
    }

    private static string FormatRatios(IEnumerable<double> ratios) =>
        string.Join(',', ratios.Select(value => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture)));

    private static bool TryParseRatios(string value, out List<double> ratios)
    {
        ratios = [];
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) ||
                !double.IsFinite(ratio) || ratio <= 0)
            {
                ratios = [];
                return false;
            }
            ratios.Add(ratio);
        }
        return ratios.Count > 0;
    }
}
