using Avalonia.Automation;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The report after a redaction that passed its check (<see cref="PendingSaveKind.RedactionReport"/>): what was removed, cleaned,
/// lost, checked, and what was not; "Close" alone. See <see cref="SaveCard"/>.
/// </summary>
public sealed partial class RedactReportCard : SaveCard
{
    public RedactReportCard()
    {
        InitializeComponent();
        Register(RedactCloseButton, PendingSaveChoice.Close);
        AutomationProperties.SetName(RedactCloseButton, (string)RedactCloseButton.Content!);
        AutomationProperties.SetName(RedactReportSections, Strings.RedactReportName);
    }
}
