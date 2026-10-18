using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The side panel beside (or, in a narrow window, over) the document: a header with the title and "Close panel", and
/// the content of the open panel (<see cref="Kind"/>). The content is built from the data template of its kind (keyed
/// by the name of the kind in <c>SidePanel.axaml</c>) when the panel opens and released when it closes or another one
/// opens, so a closed panel costs nothing. Its <c>DataContext</c> is the active tab, which the content inherits.
/// </summary>
public sealed partial class SidePanel : UserControl
{
    private SidePanelKind _kind;

    public SidePanel() => InitializeComponent();

    /// <summary>
    /// Gets or sets which panel the content shows; <see cref="SidePanelKind.None"/> releases the content. Set by the
    /// window, which keeps the old kind while the panel fades out.
    /// </summary>
    public SidePanelKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value)
            {
                return;
            }

            _kind = value;
            PanelTitle.Text = TitleOf(value);
            PanelContent.Content = value == SidePanelKind.None ? null : BuildContent(value);
        }
    }

    /// <summary>Gets or sets whether the panel lies over the document (narrow window): it then has a border.subtle edge on the document side.</summary>
    public bool IsOverlay
    {
        get => PanelFrame.Classes.Contains("overlay");
        set => PanelFrame.Classes.Set("overlay", value);
    }

    /// <summary>Gets or sets the command of the close button (<see cref="MainViewModel.CloseSidePanelCommand"/>).</summary>
    public ICommand? CloseCommand
    {
        get => ClosePanelButton.Command;
        set => ClosePanelButton.Command = value;
    }

    private static string TitleOf(SidePanelKind kind) => kind switch
    {
        SidePanelKind.Pages => Strings.PagesPanelTitle,
        SidePanelKind.Contents => Strings.ContentsPanelTitle,
        _ => string.Empty,
    };

    /// <summary>Builds the content of a panel from its data template (the extension point for the content views).</summary>
    private Control? BuildContent(SidePanelKind kind) =>
        Resources.TryGetResource(kind.ToString(), ActualThemeVariant, out var template) && template is IDataTemplate dataTemplate
            ? dataTemplate.Build(null)
            : null;
}
