using Pagewing.Core.Text;

namespace Pagewing.App.Services;

/// <summary>
/// Opens an address outside the document in the system browser or mail program: the only place that does. It takes an
/// <see cref="ExternalLink"/>, which only <see cref="ExternalLink.TryParse"/> makes, so nothing that did not pass that
/// gate can reach it. The window calls it only after the user confirmed the address (<see cref="ViewModels.MainViewModel.ConfirmLinkCommand"/>).
/// </summary>
public interface ILinkLauncher
{
    /// <summary>Opens <see cref="ExternalLink.Url"/>, exactly as given. Never throws for a launch that fails.</summary>
    /// <param name="link">The confirmed address.</param>
    /// <returns>Completes when the launch was handed to the system (or failed).</returns>
    Task LaunchAsync(ExternalLink link);
}
