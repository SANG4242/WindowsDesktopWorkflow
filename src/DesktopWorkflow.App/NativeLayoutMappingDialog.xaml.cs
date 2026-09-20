using System.Windows;
using System.Windows.Controls;

namespace DesktopWorkflow.App;

public partial class NativeLayoutMappingDialog : Window
{
    private readonly App _app;
    private readonly List<NativeLayoutProfile> _profiles;

    public NativeLayoutMappingDialog(App app, IReadOnlyList<NativeLayoutProfile> profiles)
    {
        _app = app;
        _profiles = profiles.Select(profile => profile.Clone()).ToList();
        InitializeComponent();
        MappingsGrid.ItemsSource = _profiles;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        MappingsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        foreach (var profile in _profiles) profile.MenuNumber = profile.DefaultMenuNumber;
        MappingsGrid.Items.Refresh();
        StatusText.Text = string.Empty;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        MappingsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        MappingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var invalid = _profiles.Where(profile => profile.MenuNumber is < 1 or > 9).ToArray();
        if (invalid.Length > 0)
        {
            StatusText.Text = "数字必须在 1–9 之间。";
            return;
        }
        var duplicates = _profiles.GroupBy(profile => profile.MenuNumber).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToArray();
        if (duplicates.Length > 0)
        {
            StatusText.Text = $"数字 {string.Join("、", duplicates)} 被重复使用。";
            return;
        }
        try
        {
            _app.SaveNativeLayoutMenuNumbers(_profiles.ToDictionary(profile => profile.Id, profile => profile.MenuNumber, StringComparer.OrdinalIgnoreCase));
            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
