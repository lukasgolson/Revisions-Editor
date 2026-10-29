using System.Windows;
using System.Windows.Controls;

namespace RevisionsEditor.App;

public partial class CommitDialog : Window
{
    public sealed class CommentChoice
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public bool IsChecked { get; set; }
    }

    private readonly List<CommentChoice> _comments = [];

    /// <param name="openComments">Reviewer comments that may be addressed by this commit (id, text).</param>
    public CommitDialog(string suggestedMessage, string branch, string author, IReadOnlyList<(string Id, string Text)>? openComments = null)
    {
        InitializeComponent();
        if (openComments is { Count: > 0 })
        {
            _comments.AddRange(openComments.Select(c => new CommentChoice { Id = c.Id, Label = $"{c.Id}: {(c.Text.Length > 90 ? c.Text[..90] + "…" : c.Text)}" }));
            CommentsList.ItemsSource = _comments;
            CommentsPanel.Visibility = Visibility.Visible;
        }
        ContextText.Text = $"The current manuscript will be committed to '{branch}' as {author} and signed in the provenance chain.";
        MessageTextBox.Text = suggestedMessage;
        MessageTextBox.SelectAll();
    }

    /// <summary>The message, with the reviewer comments it addresses appended (e.g. "(addresses R1.2, R2.1)").</summary>
    public string CommitMessage
    {
        get
        {
            var ids = _comments.Where(c => c.IsChecked).Select(c => c.Id).ToList();
            string message = MessageTextBox.Text.Trim();
            return ids.Count == 0 ? message : $"{message} (addresses {string.Join(", ", ids)})";
        }
    }

    private void OnMessageTextChanged(object sender, TextChangedEventArgs e)
        => CommitButton.IsEnabled = !string.IsNullOrWhiteSpace(MessageTextBox.Text);

    private void OnCommitClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
