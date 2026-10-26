using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Annotations;
using Pagewing.Core.Text;

namespace Pagewing.App.Views;

/// <summary>
/// The field of a text box on the page (milestone 10): opened by the document view for a new text box (the tool's drag or
/// click) or for a selected one (Enter). It knows its page, box and the text box it changes (if any); the view places it
/// over the box at the zoom of the view (<see cref="Place"/>) and decides what becomes of the text
/// (<see cref="CommitRequested"/>, <see cref="DiscardRequested"/>). Ctrl+Enter and leaving the field ask to apply, Esc to
/// drop; Ctrl+Z is the field's own (the window gives it to text fields).
/// </summary>
public sealed partial class TextBoxEditor : UserControl
{
    private static readonly IBrush Paper = new ImmutableSolidColorBrush(Colors.White);

    public TextBoxEditor()
    {
        InitializeComponent();
        Field.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Enter, KeyModifiers.Control),
            Command = new RelayCommand(() => CommitRequested?.Invoke(this, false)),
        });
        Field.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Escape),
            Command = new RelayCommand(() => DiscardRequested?.Invoke(this)),
        });
        Field.LostFocus += OnFieldLostFocus;
        Field.TextChanged += (_, _) => ShowProblem(null);
    }

    /// <summary>Raised when the field asks to apply its text: Ctrl+Enter (<see langword="false"/>) or the focus left it (<see langword="true"/>).</summary>
    internal event Action<TextBoxEditor, bool>? CommitRequested;

    /// <summary>Raised when the field asks to drop its text (Esc).</summary>
    internal event Action<TextBoxEditor>? DiscardRequested;

    /// <summary>Gets the tab the field belongs to.</summary>
    internal TabViewModel? Tab { get; private set; }

    /// <summary>Gets the 0-based page of the box.</summary>
    internal int Page { get; private set; }

    /// <summary>Gets the box in page points.</summary>
    internal PageRect Box { get; private set; }

    /// <summary>Gets the text box the field changes, or <see langword="null"/> for a new one.</summary>
    internal PdfAnnotation? Target { get; private set; }

    /// <summary>Gets the font size of the text in points.</summary>
    internal double FontSizePoints { get; private set; }

    /// <summary>Gets the text of the field.</summary>
    internal string Text => Field.Text ?? string.Empty;

    /// <summary>Gets whether a reason why the text cannot be applied is shown.</summary>
    internal bool HasProblem => ProblemFrame.IsVisible;

    /// <summary>Starts the field for a box: its text, size and colour (content of the PDF, so set in code).</summary>
    internal void Start(TabViewModel tab, int page, PageRect box, PdfAnnotation? target, string text, double fontSize, AnnotationColor color)
    {
        Tab = tab;
        Page = page;
        Box = box;
        Target = target;
        FontSizePoints = fontSize;
        var ink = new ImmutableSolidColorBrush(Color.FromRgb(color.R, color.G, color.B));

        // The paper behind the text in every state of the field (Fluent paints its own surface on focus and hover).
        foreach (var key in (ReadOnlySpan<string>)["TextControlBackground", "TextControlBackgroundFocused", "TextControlBackgroundPointerOver"])
        {
            Field.Resources[key] = Paper;
        }

        foreach (var key in (ReadOnlySpan<string>)["TextControlForeground", "TextControlForegroundFocused", "TextControlForegroundPointerOver"])
        {
            Field.Resources[key] = ink;
        }

        Field.Background = Paper;
        Field.Foreground = ink;
        Field.Text = text;
        Field.CaretIndex = text.Length;
        ShowProblem(null);
    }

    /// <summary>
    /// Places the field over the box: <paramref name="topLeft"/> and <paramref name="scale"/> (DIP per point) of the page in
    /// the view; it may grow down to <paramref name="maxHeight"/> DIP (the bottom of the page) as the text grows.
    /// </summary>
    internal void Place(Point topLeft, double scale, double maxHeight)
    {
        var padding = TextBoxLayout.Padding * scale;
        Margin = new Thickness(topLeft.X + (Box.Left * scale), topLeft.Y + (Box.Top * scale), 0, 0);
        Field.Width = Math.Max(1, Box.Width * scale);
        Field.MinHeight = Math.Max(1, Box.Height * scale);
        Field.MaxHeight = Math.Max(Field.MinHeight, maxHeight);
        Field.Padding = new Thickness(padding - 1);
        Field.FontSize = Math.Max(1, FontSizePoints * scale);

        // Geist: ascent 1.005 and descent 0.295 em, the line height the text box is set with (TextBoxLayout).
        Field.LineHeight = Field.FontSize * 1.3;
    }

    /// <summary>Puts the keyboard focus into the field.</summary>
    internal void FocusField() => Field.Focus(NavigationMethod.Pointer);

    /// <summary>Shows why the text cannot be applied (the field stays open), or hides it.</summary>
    internal void ShowProblem(string? problem)
    {
        ProblemText.Text = problem;
        ProblemFrame.IsVisible = problem is not null;
        AutomationProperties.SetHelpText(Field, problem ?? Strings.TextBoxEditorHint);
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        // The focus went to a part of the field itself (its context menu) or nowhere yet: not leaving.
        if (Field.IsKeyboardFocusWithin)
        {
            return;
        }

        CommitRequested?.Invoke(this, true);
    }
}
