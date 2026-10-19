using Avalonia.Controls;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>The card of a print job (<see cref="MainViewModel.PrintJob"/>); its DataContext is the <see cref="PrintJobViewModel"/>.</summary>
public sealed partial class PrintJobCard : UserControl
{
    public PrintJobCard()
    {
        InitializeComponent();
    }
}
