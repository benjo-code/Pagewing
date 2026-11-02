using Avalonia.Controls;

namespace Pagewing.App.Views;

/// <summary>
/// The hint "This looks like a scan. Make it searchable?" over the document area (milestone 15): one line with "Recognize text" (asks for the card, never
/// starts a run), "Not now" and "Don't ask for this file again". Its <c>DataContext</c> is the active tab; the window shows the bar only while
/// <see cref="ViewModels.TabViewModel.ShowsOcrHint"/> holds.
/// </summary>
public sealed partial class OcrHintBar : UserControl
{
    public OcrHintBar() => InitializeComponent();

    /// <summary>Gets the frame of the bar (the window rounds its top left corner when it lies at the top of the document area).</summary>
    internal Border HintFrame => OcrHintFrame;
}
