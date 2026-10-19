using Avalonia.Controls;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>The card "Set as the default for PDFs?" (<see cref="MainViewModel.DefaultAppPrompt"/>); its DataContext is the <see cref="DefaultAppViewModel"/>.</summary>
public sealed partial class DefaultAppCard : UserControl
{
    public DefaultAppCard()
    {
        InitializeComponent();
    }
}
