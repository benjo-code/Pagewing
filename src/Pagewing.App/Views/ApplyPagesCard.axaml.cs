using Avalonia.Automation;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The card before the page changes are written (<see cref="PendingSaveKind.ApplyPages"/>): the summary, what the rewrite
/// loses, the sentence that it cannot be undone, "Save as …" (default), "Save in the original file" and "Back"; see
/// <see cref="SaveCard"/>. "Back" is the answer <see cref="PendingSaveChoice.Cancel"/>: the page changes stay.
/// </summary>
public sealed partial class ApplyPagesCard : SaveCard
{
    public ApplyPagesCard()
    {
        InitializeComponent();
        Register(ApplySaveAsButton, PendingSaveChoice.SaveAs);
        Register(ApplySaveInOriginalButton, PendingSaveChoice.SaveInOriginal);
        Register(ApplyBackButton, PendingSaveChoice.Cancel);

        // "Back", not "Cancel": nothing is lost by it. The names are what a screen reader says.
        ApplyBackButton.Content = Strings.ApplyChoiceBack;
        ApplyPagesLossHeading.Text = Strings.ApplyLossesHeading;
        foreach (var button in new[] { ApplySaveAsButton, ApplySaveInOriginalButton, ApplyBackButton })
        {
            AutomationProperties.SetName(button, (string)button.Content!);
        }

        AutomationProperties.SetName(ApplyPagesLosses, Strings.ApplyLossesHeading);
    }
}
