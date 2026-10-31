using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>The card after a failed save (<see cref="PendingSaveKind.SaveError"/>), with the cause per reason; see <see cref="SaveCard"/>.</summary>
public sealed partial class SaveErrorCard : SaveCard
{
    public SaveErrorCard()
    {
        InitializeComponent();
        Register(SaveErrorSaveAsButton, PendingSaveChoice.SaveAs);
        Register(SaveErrorCancelButton, PendingSaveChoice.Cancel);
    }
}
