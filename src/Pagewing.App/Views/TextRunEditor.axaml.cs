using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.TextEdit;

namespace Pagewing.App.Views;

/// <summary>
/// The field over one line of the page (milestone 16): opened by the document view for a run. It knows its tab and run; the view places it over the run at the
/// zoom of the view (<see cref="Place"/>) and decides what becomes of the text (<see cref="CommitRequested"/>, <see cref="DiscardRequested"/>). Enter and
/// leaving the field ask to apply, Esc to drop; Ctrl+Z is the field's own (the window gives it to text fields).
/// </summary>
public sealed partial class TextRunEditor : UserControl
{
    private static readonly IBrush Paper = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IBrush DarkPaper = new ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(0x20, 0x20, 0x20));

    private bool _starting;

    public TextRunEditor()
    {
        InitializeComponent();
        Field.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Enter),
            Command = new RelayCommand(() => CommitRequested?.Invoke(this, false)),
        });
        Field.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Escape),
            Command = new RelayCommand(() => DiscardRequested?.Invoke(this)),
        });
        Field.LostFocus += OnFieldLostFocus;
        Field.TextChanged += (_, _) =>
        {
            if (!_starting)
            {
                TextEdited?.Invoke(this);
            }
        };
    }

    /// <summary>Raised when the field asks to apply its text: Enter (<see langword="false"/>) or the focus left it (<see langword="true"/>).</summary>
    internal event Action<TextRunEditor, bool>? CommitRequested;

    /// <summary>Raised when the field asks to drop its text (Esc).</summary>
    internal event Action<TextRunEditor>? DiscardRequested;

    /// <summary>Raised when the user changed the text of the field.</summary>
    internal event Action<TextRunEditor>? TextEdited;

    /// <summary>Gets the tab the field belongs to.</summary>
    internal TabViewModel? Tab { get; private set; }

    /// <summary>Gets the run the field changes.</summary>
    internal TextRun? Run { get; private set; }

    /// <summary>Gets the text of the field.</summary>
    internal string Text => Field.Text ?? string.Empty;

    /// <summary>Gets the text field itself (for tests).</summary>
    internal TextBox Input => Field;

    /// <summary>Gets whether a reason why the text cannot be applied is shown.</summary>
    internal bool HasProblem => ProblemFrame.IsVisible;

    /// <summary>Starts the field for a run: its text and colour (content of the PDF, so set in code).</summary>
    internal void Start(TabViewModel tab, TextRun run, string? text = null)
    {
        _starting = true;
        try
        {
            Tab = tab;
            Run = run;
            var ink = new ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(run.Color.R, run.Color.G, run.Color.B));

            // The paper behind the text: white, but dark for light ink (white text of a slide stays readable while it is typed).
            var luminance = ((0.299 * run.Color.R) + (0.587 * run.Color.G) + (0.114 * run.Color.B)) / 255;
            IBrush paper = luminance > 0.6 ? DarkPaper : Paper;

            // The paper behind the text in every state of the field (Fluent paints its own surface on focus and hover).
            foreach (var key in (ReadOnlySpan<string>)["TextControlBackground", "TextControlBackgroundFocused", "TextControlBackgroundPointerOver"])
            {
                Field.Resources[key] = paper;
            }

            foreach (var key in (ReadOnlySpan<string>)["TextControlForeground", "TextControlForegroundFocused", "TextControlForegroundPointerOver"])
            {
                Field.Resources[key] = ink;
            }

            Field.Background = paper;
            Field.Foreground = ink;
            Field.Text = text ?? run.Text;
            Field.CaretIndex = Field.Text.Length;
            ShowProblem(null);
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>Places the field over the run: <paramref name="viewRect"/> is the run in the viewport (DIPs) and <paramref name="scale"/> the DIPs per point.</summary>
    internal void Place(Rect viewRect, double scale)
    {
        if (Run is not { } run)
        {
            return;
        }

        var padding = 2.0;
        Margin = new Thickness(viewRect.X, viewRect.Y, 0, 0);
        Field.MinWidth = Math.Max(48, viewRect.Width);
        Field.MinHeight = Math.Max(1, viewRect.Height);
        Field.Padding = new Thickness(padding);
        Field.FontSize = Math.Max(1, run.FontSize * scale);
        Field.VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
    }

    /// <summary>Puts the keyboard focus into the field.</summary>
    internal void FocusField() => Field.Focus(NavigationMethod.Pointer);

    /// <summary>Shows why the text cannot be applied (the field stays open), or hides it.</summary>
    internal void ShowProblem(string? problem)
    {
        ProblemText.Text = problem;
        ProblemFrame.IsVisible = problem is not null;
        AutomationProperties.SetHelpText(Field, problem ?? Strings.TextEditFieldHint);
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
