using Avalonia.Controls;
using Avalonia.Interactivity;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

public sealed partial class StatusBar : UserControl
{
    /// <summary>Below this width the status bar drops the word "Page" (design system section 6, narrow window).</summary>
    public const double NarrowWidth = 760;

    public StatusBar()
    {
        InitializeComponent();

        // An entry that was not committed with Enter does not stay in the field.
        PageField.LostFocus += OnPageFieldLostFocus;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        PageLabel.IsVisible = e.NewSize.Width >= NarrowWidth;
    }

    private void OnPageFieldLostFocus(object? sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.ResetPageField();
}
