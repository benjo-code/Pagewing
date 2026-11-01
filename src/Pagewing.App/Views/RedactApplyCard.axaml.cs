using Avalonia.Automation;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The card before the redaction marks are applied (<see cref="PendingSaveKind.ApplyRedactions"/>): the summary, the lists, the
/// sentence that redacting cannot be undone, "Save as …" (default), "Save in the original file" and "Back"; and the card of a
/// refusal (<see cref="PendingSaveKind.RedactionRefused"/>) and of a failed check (<see cref="PendingSaveKind.RedactionFailed"/>),
/// which offer "Back" alone and have no "Apply"; see <see cref="SaveCard"/>. "Back" is the answer
/// <see cref="PendingSaveChoice.Cancel"/>: the marks stay.
/// </summary>
public sealed partial class RedactApplyCard : SaveCard
{
    public RedactApplyCard()
    {
        InitializeComponent();
        Register(RedactSaveAsButton, PendingSaveChoice.SaveAs);
        Register(RedactSaveInOriginalButton, PendingSaveChoice.SaveInOriginal);
        Register(RedactBackButton, PendingSaveChoice.Cancel);

        // "Back", not "Cancel": nothing is lost by it. The names are what a screen reader says.
        RedactBackButton.Content = Strings.ApplyChoiceBack;
        foreach (var button in new[] { RedactSaveAsButton, RedactSaveInOriginalButton, RedactBackButton })
        {
            AutomationProperties.SetName(button, (string)button.Content!);
        }
    }
}
