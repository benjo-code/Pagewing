using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;

namespace Pagewing.App.Controls;

/// <summary>
/// The form layer of the document view in the mode "Fill in form" (milestone 11, design system 7.7): over the fields of the
/// visible pages it draws a thin outline while the fields are highlighted, a star on a required field and a lock on a field
/// Pagewing cannot change (never colour alone), and a focus ring around the field that has the focus, in viewport coordinates
/// like <see cref="DocumentCanvas"/>, over the pages and under the scroll bars. The tint over the fields is PDFium's
/// (<c>FPDF_SetFormFieldHighlightColor</c>, <see cref="DocumentCanvas.FormHighlight"/>); this layer adds what the tint cannot
/// say. It takes no input and is made when the mode first starts (never in the startup path or in the reading view).
/// </summary>
/// <remarks>
/// <para>
/// The outline is <c>accent.default</c> of the light theme, 1 DIP inside the edge of the field: fields keep their light
/// appearance on a dark page too, and the outline reaches 3:1 against white and against PDFium's tint. The focus ring is
/// <see cref="FocusRingThickness"/> DIP of <c>focus.ring</c> just outside the field, the star and the lock sit in small badges
/// (<c>surface.raised</c> with a <c>border.strong</c> edge and a <c>text.primary</c> symbol) on the top right corner of the
/// field; those take the tokens of the light theme on a light page and of the dark theme on a dark page, so they keep 3:1
/// against the ground around the field, as the selection of the drawing layer does.
/// </para>
/// <para>
/// The layer draws nothing but these marks, no background: Avalonia's compositor redraws the bounds of what a visual drew
/// before and after a change, so a change redraws the rectangles of the fields, never the pages around them.
/// <see cref="LastInvalidatedRect"/> is the rectangle the layer computes for its last change: the focus rings before and after
/// when only the focus moved, else all marks. Each field is also an element of the layer (<see cref="FormFieldElement"/>):
/// its name is <see cref="FormField.DisplayName"/>, its role and state (checked, value) come from an automation peer, and its
/// help text says whether it is required or locked and why; the elements take no pointer, so the wheel and the scroll bars
/// keep working over them.
/// </para>
/// </remarks>
internal sealed class FormOverlay : Control
{
    /// <summary>The width of the outline of a field while the fields are highlighted, in DIPs.</summary>
    internal const double OutlineThickness = 1;

    /// <summary>The width of the focus ring, in DIPs.</summary>
    internal const double FocusRingThickness = 2;

    /// <summary>The side of a badge with the star or the lock, in DIPs.</summary>
    internal const double BadgeSize = 16;

    private const double BadgeIconSize = 12;
    private const double BadgeGap = 2;
    private const double IconStroke = 1.5;

    // Tabler outline "lock" and "asterisk" (24 x 24, stroke 2), parsed when the layer is first made.
    private static readonly Geometry LockIcon = StreamGeometry.Parse(
        "M5 13a2 2 0 0 1 2 -2h10a2 2 0 0 1 2 2v6a2 2 0 0 1 -2 2h-10a2 2 0 0 1 -2 -2v-6 M11 16a1 1 0 1 0 2 0a1 1 0 0 0 -2 0 M8 11v-4a4 4 0 1 1 8 0v4");

    private static readonly Geometry StarIcon = StreamGeometry.Parse("M12 12l8 -4.5 M12 12v9 M12 12l-8 -4.5 M12 12l8 4.5 M12 3v9 M12 12l-8 4.5");

    private readonly DocumentCanvas _canvas;
    private readonly Dictionary<FormWidgetKey, FormFieldElement> _elements = [];
    private IReadOnlyList<FormField> _fields = [];
    private FormWidgetKey? _focused;
    private bool _outline;
    private Palette? _palette;

    static FormOverlay()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<FormOverlay>(false);
        FocusableProperty.OverrideDefaultValue<FormOverlay>(false);
        ClipToBoundsProperty.OverrideDefaultValue<FormOverlay>(true);
    }

    /// <summary>Initializes a layer that places its marks on the pages of <paramref name="canvas"/>.</summary>
    public FormOverlay(DocumentCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        _canvas = canvas;
    }

    /// <summary>Raised when a screen reader toggles a check box or radio button (its toggle pattern).</summary>
    internal event Action<FormField>? ToggleRequested;

    /// <summary>Raised when a screen reader sets the value of a text field (its value pattern).</summary>
    internal event Action<FormField, string>? ValueRequested;

    /// <summary>Gets the fields shown (those of the visible pages, in the order of their pages and <c>/Annots</c>).</summary>
    internal IReadOnlyList<FormField> Fields => _fields;

    /// <summary>Gets the field with the focus ring, or <see langword="null"/>.</summary>
    internal FormWidgetKey? Focused => _focused;

    /// <summary>Gets whether the fields are outlined (the fields are highlighted).</summary>
    internal bool ShowsOutline => _outline;

    /// <summary>Gets the rectangle (viewport DIPs) the last change of the layer redraws (see the remarks).</summary>
    internal Rect LastInvalidatedRect { get; private set; }

    /// <summary>Gets the colour of the outline (for tests).</summary>
    internal Color OutlineColor => Colors().Outline;

    /// <summary>Gets the colour of the focus ring on the page shown (for tests).</summary>
    internal Color FocusRingColor => Colors().Ring;

    /// <summary>Gets the colours of a badge on the page shown: its inside, its edge and its symbol (for tests).</summary>
    internal (Color Fill, Color Edge, Color Symbol) BadgeColors => (Colors().BadgeFill, Colors().BadgeEdge, Colors().Symbol);

    /// <summary>Whether a field is marked as required (a star).</summary>
    internal static bool HasStar(FormField field) => field.Traits.HasFlag(FormFieldTraits.Required) && field.Lock == FormLock.None;

    /// <summary>Whether a field is marked as locked (a lock).</summary>
    internal static bool HasLock(FormField field) => field.Lock != FormLock.None;

    /// <summary>Gets the element of a field shown (for screen readers), or <see langword="null"/>.</summary>
    internal FormFieldElement? ElementOf(FormWidgetKey key) => _elements.GetValueOrDefault(key);

    /// <summary>
    /// Shows <paramref name="fields"/> (the fields of the visible pages), the focus ring on <paramref name="focused"/> and the
    /// outlines when <paramref name="outline"/> is on.
    /// </summary>
    internal void Show(IReadOnlyList<FormField> fields, FormWidgetKey? focused, bool outline)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var onlyFocus = ReferenceEquals(fields, _fields) && outline == _outline;
        if (onlyFocus && focused == _focused)
        {
            return;
        }

        var before = onlyFocus ? RingRect(_focused) : MarksRect();
        _fields = fields;
        _focused = focused;
        _outline = outline;
        var after = onlyFocus ? RingRect(_focused) : MarksRect();
        UpdateElements();
        LastInvalidatedRect = Union(before, after) ?? default;
        InvalidateArrange();
        InvalidateVisual();
    }

    /// <summary>The view scrolled or zoomed, or the page theme changed: the marks are placed and drawn again.</summary>
    internal void Refresh()
    {
        LastInvalidatedRect = MarksRect() ?? default;
        InvalidateArrange();
        InvalidateVisual();
    }

    /// <summary>The rectangle of a field in viewport DIPs, or <see langword="null"/> when its page is not laid out.</summary>
    internal Rect? ViewRectOf(FormField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (_canvas.PagePlacement(field.Key.Page) is not { } placement)
        {
            return null;
        }

        var (pageRect, scale) = placement;
        var bounds = field.Bounds;
        return new Rect(pageRect.X + (bounds.Left * scale), pageRect.Y + (bounds.Top * scale), bounds.Width * scale, bounds.Height * scale);
    }

    public override void Render(DrawingContext context)
    {
        // Clipped as the canvas clips its pages: a mark never paints over the status bar or a panel.
        using var clip = context.PushClip(new RoundedRect(new Rect(Bounds.Size), _canvas.CornerRadius));
        var colors = Colors();
        foreach (var field in _fields)
        {
            if (ViewRectOf(field) is not { } rect)
            {
                continue;
            }

            if (_outline && field.Lock == FormLock.None)
            {
                context.DrawRectangle(null, colors.OutlinePen, rect.Deflate(OutlineThickness / 2));
            }

            foreach (var (badge, icon) in Badges(field, rect))
            {
                DrawBadge(context, colors, badge, icon);
            }
        }

        if (RingRect(_focused) is { } ring)
        {
            context.DrawRectangle(null, colors.RingPen, ring.Deflate(FocusRingThickness / 2));
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in VisualChildren)
        {
            if (child is Layoutable element)
            {
                element.Measure(Size.Infinity);
            }
        }

        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var field in _fields)
        {
            if (_elements.TryGetValue(field.Key, out var element))
            {
                element.Arrange(ViewRectOf(field) ?? default);
            }
        }

        return finalSize;
    }

    private static void DrawBadge(DrawingContext context, Palette colors, Rect badge, Geometry icon)
    {
        context.DrawRectangle(colors.BadgeFillBrush, colors.BadgeEdgePen, badge, 3, 3);
        var scale = BadgeIconSize / 24;
        var inset = (BadgeSize - BadgeIconSize) / 2;
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(badge.X + inset, badge.Y + inset));
        context.DrawGeometry(null, colors.SymbolPen, icon);
    }

    private static Rect? Union(Rect? a, Rect? b) => a is { } first ? (b is { } second ? first.Union(second) : first) : b;

    /// <summary>The badges of a field: the lock on its top right corner, the star there or left of the lock.</summary>
    private static IEnumerable<(Rect Badge, Geometry Icon)> Badges(FormField field, Rect rect)
    {
        var x = rect.Right - (BadgeSize / 2);
        var y = rect.Top - (BadgeSize / 2);
        if (HasLock(field))
        {
            yield return (new Rect(x, y, BadgeSize, BadgeSize), LockIcon);
            x -= BadgeSize + BadgeGap;
        }

        if (HasStar(field))
        {
            yield return (new Rect(x, y, BadgeSize, BadgeSize), StarIcon);
        }
    }

    /// <summary>The outer rectangle of the focus ring of <paramref name="key"/> (viewport DIPs), or <see langword="null"/>.</summary>
    private Rect? RingRect(FormWidgetKey? key)
    {
        if (key is not { } focused)
        {
            return null;
        }

        foreach (var field in _fields)
        {
            if (field.Key == focused && ViewRectOf(field) is { } rect)
            {
                return rect.Inflate(FocusRingThickness + 1);
            }
        }

        return null;
    }

    /// <summary>The union of all marks of the fields shown (viewport DIPs), or <see langword="null"/> without any.</summary>
    private Rect? MarksRect()
    {
        Rect? all = null;
        foreach (var field in _fields)
        {
            if (ViewRectOf(field) is { } rect)
            {
                // The ring outside and the badges over the top right corner.
                all = Union(all, rect.Inflate(Math.Max(FocusRingThickness + 1, BadgeSize / 2) + 1));
            }
        }

        return all;
    }

    /// <summary>One element per field shown, for screen readers; the elements of fields no longer shown go.</summary>
    private void UpdateElements()
    {
        var shown = new HashSet<FormWidgetKey>();
        foreach (var field in _fields)
        {
            if (!shown.Add(field.Key))
            {
                continue;
            }

            if (!_elements.TryGetValue(field.Key, out var element))
            {
                element = new FormFieldElement(this);
                _elements[field.Key] = element;
                LogicalChildren.Add(element);
                VisualChildren.Add(element);
            }

            element.Field = field;
        }

        foreach (var key in _elements.Keys.Where(key => !shown.Contains(key)).ToList())
        {
            var element = _elements[key];
            _elements.Remove(key);
            LogicalChildren.Remove(element);
            VisualChildren.Remove(element);
        }
    }

    internal void RequestToggle(FormField field) => ToggleRequested?.Invoke(field);

    internal void RequestValue(FormField field, string value) => ValueRequested?.Invoke(field, value);

    /// <summary>
    /// The colours of the marks: the outline from the light theme (fields stay light), the ring and the badges from the light
    /// theme on a light page and from the dark theme on a dark one (pages stay light in high contrast).
    /// </summary>
    private Palette Colors()
    {
        var variant = _canvas.PageTheme == PageTheme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var outline = ColorOf("Pw.Color.Accent.Default", ThemeVariant.Light, Avalonia.Media.Colors.Black);
        var ring = ColorOf("Pw.Color.Focus.Ring", variant, Avalonia.Media.Colors.Black);
        var fill = ColorOf("Pw.Color.Surface.Raised", variant, Avalonia.Media.Colors.White);
        var edge = ColorOf("Pw.Color.Border.Strong", variant, Avalonia.Media.Colors.Gray);
        var symbol = ColorOf("Pw.Color.Text.Primary", variant, Avalonia.Media.Colors.Black);
        if (_palette is { } known && known.Outline == outline && known.Ring == ring && known.BadgeFill == fill && known.BadgeEdge == edge && known.Symbol == symbol)
        {
            return known;
        }

        var palette = new Palette(outline, ring, fill, edge, symbol);
        _palette = palette;
        return palette;
    }

    private Color ColorOf(string key, ThemeVariant variant, Color fallback) =>
        this.TryFindResource(key, variant, out var value) && value is Color color ? color : fallback;

    /// <summary>The colours and pens of the marks.</summary>
    private sealed class Palette(Color outline, Color ring, Color badgeFill, Color badgeEdge, Color symbol)
    {
        public Color Outline { get; } = outline;

        public Color Ring { get; } = ring;

        public Color BadgeFill { get; } = badgeFill;

        public Color BadgeEdge { get; } = badgeEdge;

        public Color Symbol { get; } = symbol;

        public Pen OutlinePen { get; } = new(new SolidColorBrush(outline), OutlineThickness);

        public Pen RingPen { get; } = new(new SolidColorBrush(ring), FocusRingThickness);

        public IBrush BadgeFillBrush { get; } = new SolidColorBrush(badgeFill);

        public Pen BadgeEdgePen { get; } = new(new SolidColorBrush(badgeEdge), 1);

        // The symbol is drawn in its 24-unit box scaled to BadgeIconSize: the pen is in those units.
        public Pen SymbolPen { get; } = new(new SolidColorBrush(symbol), IconStroke * 24 / BadgeIconSize, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    }
}

/// <summary>
/// A field of the form layer as screen readers see it (milestone 11): named after <see cref="FormField.DisplayName"/>, with
/// the role of its kind, its state (checked, value) as a pattern of <see cref="FormFieldAutomationPeer"/> and, as help text,
/// whether it is required, checked or locked and why. It takes no pointer and no focus.
/// </summary>
internal sealed class FormFieldElement : Control
{
    private readonly FormOverlay _overlay;
    private FormField? _field;

    static FormFieldElement()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<FormFieldElement>(false);
        FocusableProperty.OverrideDefaultValue<FormFieldElement>(false);
    }

    /// <summary>Initializes an element of <paramref name="overlay"/>.</summary>
    public FormFieldElement(FormOverlay overlay)
    {
        _overlay = overlay;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Content);
    }

    /// <summary>Gets or sets the field (its name, role, state and help text follow it).</summary>
    internal FormField? Field
    {
        get => _field;
        set
        {
            if (Equals(_field, value))
            {
                return;
            }

            _field = value;
            if (value is not null)
            {
                AutomationProperties.SetName(this, value.DisplayName);
                AutomationProperties.SetHelpText(this, HelpTextOf(value));
            }
        }
    }

    /// <summary>Gets whether a check box is checked or a radio button chosen.</summary>
    internal bool IsChosen => _field is { } shown && IsChosenField(shown);

    /// <summary>The help text of a field: required, checked, and why it is locked, as text (never colour alone).</summary>
    internal static string? HelpTextOf(FormField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var parts = new List<string>();
        if (field.Traits.HasFlag(FormFieldTraits.Required))
        {
            parts.Add(Strings.FormRequired);
        }

        if (IsChosenField(field))
        {
            parts.Add(Strings.FormFieldChecked);
        }

        if (TabViewModel.LockTextOf(field) is { } locked)
        {
            parts.Add(locked);
        }

        return parts.Count == 0 ? null : string.Join(". ", parts.Select(part => part.TrimEnd('.')));
    }

    /// <summary>Whether a check box is checked or a radio button is the chosen one of its group.</summary>
    internal static bool IsChosenField(FormField field) =>
        field.Kind == FormFieldKind.CheckBox
            ? field.State.Checked == true
            : field.Kind == FormFieldKind.RadioButton && field.ExportValue is { } export && string.Equals(field.State.Text, export, StringComparison.Ordinal);

    internal void RequestToggle()
    {
        if (_field is { } field)
        {
            _overlay.RequestToggle(field);
        }
    }

    internal void RequestValue(string value)
    {
        if (_field is { } field)
        {
            _overlay.RequestValue(field, value);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FormFieldAutomationPeer(this);
}

/// <summary>
/// The automation peer of a field of the form layer: the role of its kind (edit, check box, radio button, combo box, list,
/// button), a toggle pattern for a check box or radio button and a value pattern for a text field or combo box (the text of a
/// password field is never told). Created only when an assistive technology asks.
/// </summary>
internal sealed class FormFieldAutomationPeer(FormFieldElement owner) : ControlAutomationPeer(owner), IToggleProvider, IValueProvider
{
    public ToggleState ToggleState => Element.IsChosen ? ToggleState.On : ToggleState.Off;

    public bool IsReadOnly => Element.Field is not { Lock: FormLock.None };

    public string? Value => Element.Field is not { } shown ? null : ValueOf(shown);

    private FormFieldElement Element => (FormFieldElement)Owner;

    private bool IsToggle => Element.Field?.Kind is FormFieldKind.CheckBox or FormFieldKind.RadioButton;

    private bool IsValue => Element.Field?.Kind is FormFieldKind.Text or FormFieldKind.ComboBox;

    /// <summary>The value told to screen readers: the text of a text field (never of a password field), the text or option of a combo box.</summary>
    internal static string? ValueOf(FormField field) => field.Kind switch
    {
        FormFieldKind.Text when !field.Traits.HasFlag(FormFieldTraits.Password) => field.State.Text,
        FormFieldKind.ComboBox => field.State.Text ?? (field.State.SelectedOptions is [var first, ..] && first >= 0 && first < field.Options.Count ? field.Options[first].Label : null),
        _ => null,
    };

    public void Toggle()
    {
        EnsureEnabled();
        if (!IsReadOnly)
        {
            Element.RequestToggle();
        }
    }

    public void SetValue(string? value)
    {
        EnsureEnabled();
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The field cannot be changed.");
        }

        Element.RequestValue(value ?? string.Empty);
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => Element.Field?.Kind switch
    {
        FormFieldKind.Text => AutomationControlType.Edit,
        FormFieldKind.CheckBox => AutomationControlType.CheckBox,
        FormFieldKind.RadioButton => AutomationControlType.RadioButton,
        FormFieldKind.ComboBox => AutomationControlType.ComboBox,
        FormFieldKind.ListBox => AutomationControlType.List,
        FormFieldKind.PushButton => AutomationControlType.Button,
        _ => AutomationControlType.Custom,
    };

    protected override object? GetProviderCore(Type providerType) =>
        (providerType == typeof(IToggleProvider) && !IsToggle) || (providerType == typeof(IValueProvider) && !IsValue)
            ? null
            : base.GetProviderCore(providerType);
}
