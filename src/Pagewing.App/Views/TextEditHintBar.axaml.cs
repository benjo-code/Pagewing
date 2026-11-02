using Avalonia.Controls;
using Avalonia.Interactivity;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The bar of the mode "Edit text" (milestone 16): what the mode is (lines, not paragraphs) and what stays in the file, the live hint for the open field and
/// the switch "Whole line in replacement font". Its <c>DataContext</c> is the active tab; the window shows the bar only while
/// <see cref="TabViewModel.IsTextEditing"/> holds.
/// </summary>
public sealed partial class TextEditHintBar : UserControl
{
    public TextEditHintBar()
    {
        InitializeComponent();
        TextEditWholeRunButton.Click += OnWholeRunClick;
    }

    /// <summary>Gets the frame of the bar (the window rounds its top left corner when it lies at the top of the document area).</summary>
    internal Border HintFrame => TextEditHintFrame;

    private void OnWholeRunClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TabViewModel tab)
        {
            tab.SetTextEditWholeRun(TextEditWholeRunButton.IsChecked == true);
        }
    }
}
