using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The card about the signature before a save: the warning of a DocMDP level that forbids comments
/// (<see cref="PendingSaveKind.SignatureWarning"/>) or the note for a signature that allows them
/// (<see cref="PendingSaveKind.SignatureNote"/>); see <see cref="SaveCard"/>.
/// </summary>
public sealed partial class SignatureWarningCard : SaveCard
{
    public SignatureWarningCard()
    {
        InitializeComponent();
        Register(SignatureSaveButton, PendingSaveChoice.Save);
        Register(SignatureCancelButton, PendingSaveChoice.Cancel);
        Register(SignatureSaveAsButton, PendingSaveChoice.SaveAs);
        Register(SignatureSaveAnywayButton, PendingSaveChoice.SaveAnyway);
    }
}
