using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevisionsEditor.App.Dialogs;

/// <summary>
/// A small dialog laid out like the app's other dialogs: an introduction, labelled fields, and OK/Cancel.
/// The scholarly dialogs are built from it in code.
/// </summary>
public class FormWindow : Window
{
    private readonly StackPanel _fields = new();
    private readonly Button _ok = new() { IsDefault = true, MinWidth = 96 };
    private Func<bool>? _canAccept;

    protected FormWindow(string title, double width = 560)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        _ok.Click += (_, _) => Accept();
        buttons.Children.Add(_ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) });

        var root = new DockPanel { Margin = new Thickness(20, 16, 20, 20) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_fields);
        Content = root;
        OkText = "OK";
    }

    protected string OkText
    {
        set => _ok.Content = value;
    }

    /// <summary>Enables OK only while <paramref name="canAccept"/> holds; call <see cref="Revalidate"/> when inputs change.</summary>
    protected void SetValidation(Func<bool> canAccept)
    {
        _canAccept = canAccept;
        Revalidate();
    }

    protected void Revalidate() => _ok.IsEnabled = _canAccept?.Invoke() ?? true;

    protected virtual bool OnAccepting() => true;

    private void Accept()
    {
        if ((_canAccept?.Invoke() ?? true) && OnAccepting())
        {
            DialogResult = true;
        }
    }

    protected TextBlock AddIntro(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _fields.Children.Add(block);
        return block;
    }

    protected TextBlock AddHint(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _fields.Children.Add(block);
        return block;
    }

    protected T AddField<T>(string label, T control, bool first = false) where T : FrameworkElement
    {
        var caption = new Label { Content = label, Target = control, Padding = new Thickness(0, first || _fields.Children.Count == 0 ? 0 : 12, 0, 4) };
        _fields.Children.Add(caption);
        _fields.Children.Add(control);
        return control;
    }

    protected void AddElement(UIElement element) => _fields.Children.Add(element);

    protected TextBox AddTextBox(string label, string text = "", bool multiline = false, bool monospace = false, int lines = 4)
    {
        var box = new TextBox
        {
            Text = text,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            AcceptsReturn = multiline,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            MinLines = multiline ? lines : 1,
            MaxLines = multiline ? lines * 3 : 1
        };
        if (monospace)
        {
            box.FontFamily = (FontFamily)FindResource("MonoFont");
        }
        box.TextChanged += (_, _) => Revalidate();
        return AddField(label, box);
    }

    /// <summary>
    /// "This figure is a placeholder", with what it stands in for. Returns the note (empty when there is none), or
    /// null when it is not a placeholder.
    /// </summary>
    protected Func<string?> AddPlaceholderOption(string what, string? note)
    {
        var box = new CheckBox { Content = $"This {what} is a _placeholder (stamped as one until the final {what} replaces it)", IsChecked = note != null, Margin = new Thickness(0, 12, 0, 0) };
        var text = new TextBox { Text = note ?? string.Empty, IsEnabled = note != null, Margin = new Thickness(24, 6, 0, 0), ToolTip = $"What the final {what} will be, e.g. “synthetic data until the field season ends”" };
        System.Windows.Automation.AutomationProperties.SetName(text, "What it stands in for");
        box.Checked += (_, _) => text.IsEnabled = true;
        box.Unchecked += (_, _) => text.IsEnabled = false;
        _fields.Children.Add(box);
        _fields.Children.Add(text);
        return () => box.IsChecked == true ? text.Text.Trim().Replace("\"", "'").Replace("{", "(").Replace("}", ")") : null;
    }

    /// <summary>A text box with a button beside it (for example to browse for a file).</summary>
    protected TextBox AddTextBoxWithButton(string label, string buttonText, Action<TextBox> onClick, string text = "")
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox { Text = text };
        box.TextChanged += (_, _) => Revalidate();
        var button = new Button { Content = buttonText, Margin = new Thickness(8, 0, 0, 0) };
        button.Click += (_, _) => onClick(box);
        Grid.SetColumn(button, 1);
        grid.Children.Add(box);
        grid.Children.Add(button);
        var caption = new Label { Content = label, Target = box, Padding = new Thickness(0, _fields.Children.Count == 0 ? 0 : 12, 0, 4) };
        _fields.Children.Add(caption);
        _fields.Children.Add(grid);
        return box;
    }
}
