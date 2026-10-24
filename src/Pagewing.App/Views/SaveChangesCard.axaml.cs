using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The card before a tab with unsaved changes closes (<see cref="PendingSaveKind.SaveChanges"/>), or warns that its changes
/// cannot be saved and are lost (<see cref="PendingSaveKind.DiscardChanges"/>); see <see cref="SaveCard"/>.
/// </summary>
public sealed partial class SaveChangesCard : SaveCard
{
    public SaveChangesCard()
    {
        InitializeComponent();
        Register(SaveChangesSaveButton, PendingSaveChoice.Save);
        Register(SaveChangesDontSaveButton, PendingSaveChoice.DontSave);
        Register(SaveChangesDiscardButton, PendingSaveChoice.Discard);
        Register(SaveChangesCancelButton, PendingSaveChoice.Cancel);
    }
}
