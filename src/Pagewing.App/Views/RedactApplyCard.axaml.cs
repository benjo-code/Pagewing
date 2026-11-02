using Avalonia.Automation;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The card before the redaction marks are applied (<see cref="PendingSaveKind.ApplyRedactions"/>): the summary, the lists, the
/// sentence that redacting cannot be undone, "Save as …" (default), "Save in the original file" and "Back"; and the card of a
/// refusal (<see cref="PendingSaveKind.RedactionRefused"/>) and of a failed check (<see cref="PendingSaveKind.RedactionFailed"/>),
/// which offer "Back" alone and have no "Apply"; and the card of a refusal for pages alone (<see cref="PendingSaveKind.RedactionRescue"/>),
/// which offers each of those pages as an image (a check box each, never preselected) with "Continue" and "Back"; see <see cref="SaveCard"/>. "Back" is the answer
/// <see cref="PendingSaveChoice.Cancel"/>: the marks stay.
/// </summary>
public sealed partial class RedactApplyCard : SaveCard
{
    public RedactApplyCard()
    {
        InitializeComponent();
        Register(RedactContinueButton, PendingSaveChoice.Continue);
        Register(RedactSaveAsButton, PendingSaveChoice.SaveAs);
        Register(RedactSaveInOriginalButton, PendingSaveChoice.SaveInOriginal);
        Register(RedactBackButton, PendingSaveChoice.Cancel);

        // "Back", not "Cancel": nothing is lost by it. The names are what a screen reader says.
        RedactBackButton.Content = Strings.ApplyChoiceBack;
        foreach (var button in new[] { RedactContinueButton, RedactSaveAsButton, RedactSaveInOriginalButton, RedactBackButton })
        {
            AutomationProperties.SetName(button, (string)button.Content!);
        }
    }
}
