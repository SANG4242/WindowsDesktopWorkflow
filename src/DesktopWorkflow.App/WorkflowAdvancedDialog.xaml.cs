using System.Windows;

namespace DesktopWorkflow.App;

public partial class WorkflowAdvancedDialog : Window
{
    public string WorkflowId { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;

    public WorkflowAdvancedDialog(string currentId, string currentDescription)
    {
        InitializeComponent();
        IdTextBox.Text = currentId;
        DescriptionTextBox.Text = currentDescription;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        WorkflowId = IdTextBox.Text.Trim();
        Description = DescriptionTextBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
