using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The card before saving a form whose required fields are empty (<see cref="PendingSaveKind.RequiredFields"/>): "Go to the
/// first empty field" (default) and "Save anyway"; see <see cref="SaveCard"/>.
/// </summary>
public sealed partial class RequiredFieldsCard : SaveCard
{
    public RequiredFieldsCard()
    {
        InitializeComponent();
        Register(RequiredFieldsGoToButton, PendingSaveChoice.GoToField);
        Register(RequiredFieldsSaveAnywayButton, PendingSaveChoice.SaveAnyway);
    }
}
