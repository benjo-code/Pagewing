using Avalonia.Controls;

namespace Pagewing.App.Views;

public sealed partial class StatusBar : UserControl
{
    /// <summary>Below this width the status bar drops the word "Page" (design system section 6, narrow window).</summary>
    public const double NarrowWidth = 760;

    public StatusBar() => InitializeComponent();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        PageLabel.IsVisible = e.NewSize.Width >= NarrowWidth;
    }
}
