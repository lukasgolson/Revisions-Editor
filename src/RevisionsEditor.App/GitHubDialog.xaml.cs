using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RevisionsEditor.App;

/// <summary>
/// Asks for a GitHub repository URL: to connect the open workspace to it, or (with a folder) to download
/// a workspace from it on another computer.
/// </summary>
public partial class GitHubDialog : Window
{
    private readonly bool _clone;
    private bool _folderEdited;

    public GitHubDialog(bool clone, string? currentUrl = null, string? defaultParentFolder = null)
    {
        InitializeComponent();
        _clone = clone;
        UrlTextBox.Text = currentUrl ?? string.Empty;

        if (clone)
        {
            Title = "Open from GitHub";
            IntroText.Text = "Download a workspace that another of your computers sends to GitHub. Its history, review branches and signed provenance come with it.";
            OkButton.Content = "_Download";
            FolderPanel.Visibility = Visibility.Visible;
            _defaultParent = defaultParentFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            UpdateSuggestedFolder();
        }
        else
        {
            Title = "Connect to GitHub";
            IntroText.Text = "Keep this workspace in a GitHub repository so your other computers can work on the paper too. Each computer syncs its commits; edits made on different computers are merged paragraph by paragraph.";
            OkButton.Content = "_Connect";
        }

        HintText.Text = clone
            ? "Use the address of the repository your other computer is connected to. The first download opens a GitHub sign-in window (Git Credential Manager)."
            : "Create an empty private repository at github.com/new (no README), then paste its address, for example https://github.com/you/thesis-paper.git. "
                + "The first sync opens a GitHub sign-in window (Git Credential Manager).";
        UrlTextBox.SelectAll();
        UpdateOkButton();
    }

    private readonly string _defaultParent = string.Empty;

    public string Url => UrlTextBox.Text.Trim();
    public string Folder => FolderTextBox.Text.Trim();

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (ReferenceEquals(sender, FolderTextBox) && FolderTextBox.IsKeyboardFocusWithin)
        {
            _folderEdited = true;
        }
        if (ReferenceEquals(sender, UrlTextBox) && _clone && !_folderEdited)
        {
            UpdateSuggestedFolder();
        }
        UpdateOkButton();
    }

    private void UpdateSuggestedFolder()
    {
        string name = RepositoryName(Url);
        FolderTextBox.Text = Path.Combine(_defaultParent, name.Length == 0 ? "paper" : name);
    }

    private static string RepositoryName(string url)
    {
        string trimmed = url.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        int slash = trimmed.LastIndexOfAny(['/', ':']);
        string name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '-');
        }
        return name;
    }

    private bool IsPlausibleUrl(string url)
        => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(url); // a local or network repository also works

    private void UpdateOkButton()
        => OkButton.IsEnabled = IsPlausibleUrl(Url) && (!_clone || Folder.Length > 0);

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to put the workspace", InitialDirectory = _defaultParent };
        if (dlg.ShowDialog(this) == true)
        {
            string name = RepositoryName(Url);
            FolderTextBox.Text = Path.Combine(dlg.FolderName, name.Length == 0 ? "paper" : name);
            _folderEdited = true;
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (_clone && Directory.Exists(Folder) && Directory.EnumerateFileSystemEntries(Folder).Any())
        {
            MessageBox.Show(this, $"'{Folder}' already exists and is not empty. Choose another folder.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
