using System.Windows;
using RevisionsEditor.App.ViewModels;

namespace RevisionsEditor.App;

/// <summary>
/// Confirms which committed version a reviewed Word document was made from (detected from its text before
/// the tracked changes), so reviews returned out of order are compared with the right version.
/// </summary>
public partial class ImportBaseDialog : Window
{
    public ImportBaseDialog(string fileName, IReadOnlyList<MainViewModel.ImportBaseOption> options, bool bestIsLatest)
    {
        InitializeComponent();
        BaseComboBox.ItemsSource = options;
        BaseComboBox.SelectedIndex = 0;

        var best = options[0];
        IntroText.Text = bestIsLatest
            ? $"'{fileName}' matches the latest version on main ({best.Score:P0} of its text before the tracked changes)."
            : $"'{fileName}' was made from an earlier version: its text before the tracked changes matches {best.Commit.ShortSha} ({best.Commit.Message}, {best.Commit.Timestamp.ToLocalTime():g}) by {best.Score:P0}.";
    }

    public MainViewModel.ImportBaseOption? SelectedOption => BaseComboBox.SelectedItem as MainViewModel.ImportBaseOption;

    private void OnImportClick(object sender, RoutedEventArgs e) => DialogResult = SelectedOption != null;
}
