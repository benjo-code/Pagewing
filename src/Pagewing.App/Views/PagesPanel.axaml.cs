using Avalonia.Controls;

namespace Pagewing.App.Views;

/// <summary>
/// The content of the "Pages" panel: the page thumbnails of the active tab (<see cref="Controls.ThumbnailList"/>, which
/// follows the tab in its <c>DataContext</c>). Built when the panel opens, released when it closes.
/// </summary>
public sealed partial class PagesPanel : UserControl
{
    public PagesPanel() => InitializeComponent();
}
