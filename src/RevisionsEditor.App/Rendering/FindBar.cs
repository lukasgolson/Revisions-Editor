using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using RevisionsEditor.Core.Checks;

namespace RevisionsEditor.App.Rendering;

/// <summary>
/// The find bar: a search box with the match count, next and previous, and match-case and whole-word options,
/// and a Replace row (Ctrl+H) that replaces the current match or all of them. Enter and F3 go to the next match,
/// Shift with them to the previous one, Esc closes it. Matches in read-only text are found but not replaced.
/// </summary>
internal sealed class FindBar : Border
{
    private static readonly Thickness ButtonPadding = new(10, 2, 10, 2);

    private readonly TextBox _query = new() { Width = 220, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _replacement = new() { Width = 220, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), MinWidth = 70 };
    private readonly TextBlock _replaceNote = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ToggleButton _matchCase = new() { Content = "Aa", ToolTip = "Match case (Alt+C)", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
    private readonly ToggleButton _wholeWord = new() { Content = "Whole word", ToolTip = "Match whole words only (Alt+W)", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
    private readonly ToggleButton _expand = new() { Content = "›", ToolTip = "Show Replace (Ctrl+H)", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(6, 2, 6, 2), Width = 28 };
    private readonly Button _replaceOne = new() { Content = "_Replace", ToolTip = "Replace this match and go to the next (Alt+R)", Margin = new Thickness(8, 0, 4, 0), Padding = ButtonPadding };
    private readonly Button _replaceAll = new() { Content = "Replace _All", ToolTip = "Replace every match in editable text (Alt+A); one undo (Ctrl+Z) puts them back", Padding = ButtonPadding };
    private readonly DockPanel _replaceRow = new() { LastChildFill = true, Margin = new Thickness(34, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _debounce;
    private IFindTarget? _target;
    private List<FindMatch> _matches = [];
    private int _current = -1;

    public FindBar()
    {
        SetResourceReference(StyleProperty, "CommandStrip");
        Visibility = Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(_query, "Find");
        System.Windows.Automation.AutomationProperties.SetName(_replacement, "Replace with");
        _replaceNote.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var previous = new Button { Content = "↑", ToolTip = "Previous match (Shift+Enter, Shift+F3)", Margin = new Thickness(0, 0, 4, 0), Padding = ButtonPadding };
        var next = new Button { Content = "↓", ToolTip = "Next match (Enter, F3)", Padding = ButtonPadding };
        var close = new Button { Content = "✕", ToolTip = "Close (Esc)", Padding = ButtonPadding };
        previous.Click += (_, _) => Move(-1);
        next.Click += (_, _) => Move(1);
        close.Click += (_, _) => Close();
        _matchCase.Click += (_, _) => SearchNow(keepPosition: true);
        _wholeWord.Click += (_, _) => SearchNow(keepPosition: true);
        _expand.Click += (_, _) => ShowReplace(_expand.IsChecked == true);
        _replaceOne.Click += (_, _) => ReplaceCurrent();
        _replaceAll.Click += (_, _) => ReplaceEverything();

        var findRow = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(close, Dock.Right);
        findRow.Children.Add(close);
        var label = new TextBlock { Text = "Find:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Width = 54 };
        foreach (var element in new UIElement[] { _expand, label, _query, _count, previous, next, _matchCase, _wholeWord })
        {
            DockPanel.SetDock(element, Dock.Left);
            findRow.Children.Add(element);
        }

        var replaceLabel = new TextBlock { Text = "Replace:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Width = 54 };
        foreach (var element in new UIElement[] { replaceLabel, _replacement, _replaceOne, _replaceAll })
        {
            DockPanel.SetDock(element, Dock.Left);
            _replaceRow.Children.Add(element);
        }
        _replaceRow.Children.Add(_replaceNote);

        Child = new StackPanel { Children = { findRow, _replaceRow } };

        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(160) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            SearchNow(keepPosition: false);
        };
        _query.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        _query.PreviewKeyDown += OnKeyDown;
        _replacement.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    ReplaceEverything();
                }
                else
                {
                    ReplaceCurrent();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.C or Key.W)
            {
                var toggle = e.SystemKey == Key.C ? _matchCase : _wholeWord;
                toggle.IsChecked = toggle.IsChecked != true;
                SearchNow(keepPosition: true);
                e.Handled = true;
            }
        };
    }

    public bool IsOpen => Visibility == Visibility.Visible;
    public bool IsReplaceShown => _replaceRow.Visibility == Visibility.Visible;
    public string Query => _query.Text;
    public string ReplacementText
    {
        get => _replacement.Text;
        set => _replacement.Text = value;
    }
    public int MatchCount => _matches.Count;
    public int CurrentIndex => _current;

    /// <summary>
    /// Opens the bar on <paramref name="target"/>, with the selected text (if short) as the query, and the Replace
    /// row when <paramref name="replace"/> (the focus then goes to the replacement once there is a query).
    /// </summary>
    public void Open(IFindTarget target, string? selectedText, bool replace = false)
    {
        _target = target;
        Visibility = Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(selectedText) && selectedText.Length <= 120 && !selectedText.Contains('\n'))
        {
            _query.Text = selectedText.Trim();
        }
        if (replace)
        {
            ShowReplace(true);
        }
        if (replace && _query.Text.Trim().Length > 0)
        {
            _replacement.Focus();
            _replacement.SelectAll();
        }
        else
        {
            _query.Focus();
            _query.SelectAll();
        }
        SearchNow(keepPosition: false);
    }

    private void ShowReplace(bool show)
    {
        _expand.IsChecked = show;
        _expand.Content = show ? "⌄" : "›";
        _expand.ToolTip = show ? "Hide Replace" : "Show Replace (Ctrl+H)";
        _replaceRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdateReplaceState();
    }

    public void Close()
    {
        _debounce.Stop();
        Visibility = Visibility.Collapsed;
        _target?.Highlight([], -1);
        _target?.Focus();
        _matches = [];
        _current = -1;
    }

    /// <summary>The text changed or was laid out again: search again, staying near the current match.</summary>
    public void Refresh()
    {
        if (IsOpen)
        {
            SearchNow(keepPosition: true, reveal: false);
        }
    }

    /// <summary>Next (1) or previous (-1) match.</summary>
    public void Move(int direction)
    {
        if (_matches.Count == 0)
        {
            SearchNow(keepPosition: false);
            return;
        }
        _current = ((_current < 0 ? 0 : _current + direction) % _matches.Count + _matches.Count) % _matches.Count;
        ShowCurrent(reveal: true);
    }

    /// <summary>Replaces the current match (when it is editable text) and goes to the next one.</summary>
    internal void ReplaceCurrent()
    {
        if (_target == null)
        {
            return;
        }
        FlushPendingSearch();
        if (_current < 0 || _target.ReplaceUnavailableReason != null)
        {
            UpdateReplaceState();
            return;
        }
        var match = _matches[_current];
        if (!_target.CanReplace(match))
        {
            _replaceNote.Text = "This match is in read-only text and was skipped.";
            Move(1);
            return;
        }
        int at = _current;
        string replacement = _replacement.Text;
        _target.Replace(match, replacement);
        _matches = Search();
        // The replaced text is gone, so the same index is the next match (unless the replacement matches too).
        bool stillMatches = TextFinder.FindAll(replacement, _query.Text, _matchCase.IsChecked == true, _wholeWord.IsChecked == true).Count > 0;
        _current = _matches.Count == 0 ? -1 : (stillMatches ? at + 1 : at) % _matches.Count;
        _replaceNote.Text = string.Empty;
        ShowCurrent(reveal: true);
    }

    /// <summary>Replaces every match in editable text, and says how many in read-only text were left.</summary>
    internal void ReplaceEverything()
    {
        if (_target == null)
        {
            return;
        }
        FlushPendingSearch();
        if (_matches.Count == 0 || _target.ReplaceUnavailableReason != null)
        {
            UpdateReplaceState();
            return;
        }
        int total = _matches.Count;
        int replaced = _target.ReplaceAll(_matches, _replacement.Text);
        _matches = Search();
        _current = _matches.Count == 0 ? -1 : 0;
        ShowCurrent(reveal: false);
        int left = total - replaced;
        _replaceNote.Text = $"Replaced {replaced} of {total}."
            + (left > 0 ? $" {left} in read-only text (the title and title page, paragraphs with pending changes, figures and tables) were left." : string.Empty);
        _replaceNote.ToolTip = left > 0 ? "Edit the title in the Paper panel, accept or reject pending changes first, and edit figures and tables in their dialogs (double-click)." : null;
    }

    private void FlushPendingSearch()
    {
        if (_debounce.IsEnabled)
        {
            _debounce.Stop();
            SearchNow(keepPosition: false, reveal: false);
        }
    }

    private List<FindMatch> Search()
        => _target == null || _query.Text.Trim().Length == 0 ? [] : _target.Search(_query.Text, _matchCase.IsChecked == true, _wholeWord.IsChecked == true);

    private void SearchNow(bool keepPosition, bool reveal = true)
    {
        if (_target == null)
        {
            return;
        }
        int previous = _current;
        _matches = Search();
        _current = _matches.Count == 0 ? -1
            : keepPosition && previous >= 0 ? Math.Min(previous, _matches.Count - 1)
            : _target.IndexNearCaret(_matches);
        ShowCurrent(reveal);
    }

    private void ShowCurrent(bool reveal)
    {
        _count.Text = _query.Text.Trim().Length == 0 ? string.Empty
            : _matches.Count == 0 ? "No results"
            : $"{_current + 1} of {_matches.Count}";
        _count.SetResourceReference(TextBlock.ForegroundProperty, _matches.Count == 0 && _query.Text.Trim().Length > 0 ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
        UpdateReplaceState();
        if (_target == null)
        {
            return;
        }
        if (reveal && _current >= 0)
        {
            _target.Reveal(_matches[_current]);
        }
        _target.Highlight(_matches, _current);
    }

    private void UpdateReplaceState()
    {
        string? reason = _target?.ReplaceUnavailableReason;
        _replacement.IsEnabled = reason == null;
        _replaceOne.IsEnabled = reason == null && _current >= 0;
        _replaceAll.IsEnabled = reason == null && _matches.Count > 0;
        if (reason != null)
        {
            _replaceNote.Text = reason;
        }
        else if (_current >= 0 && _target != null && !_target.CanReplace(_matches[_current]))
        {
            _replaceNote.Text = "This match is in read-only text (the title, a paragraph with pending changes, a figure or table): Replace skips it.";
        }
        else if (_replaceNote.Text.StartsWith("This match", StringComparison.Ordinal) || _replaceNote.Text.StartsWith("The ", StringComparison.Ordinal))
        {
            _replaceNote.Text = string.Empty;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Enter:
            case Key.F3:
                // Typed but not searched yet: search now (from the cursor); otherwise go on to the next match.
                bool pending = _debounce.IsEnabled;
                _debounce.Stop();
                if (pending || _matches.Count == 0)
                {
                    SearchNow(keepPosition: false);
                }
                else
                {
                    Move(shift ? -1 : 1);
                }
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }
}
