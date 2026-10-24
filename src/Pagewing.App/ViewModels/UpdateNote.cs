namespace Pagewing.App.ViewModels;

/// <summary>The note in the place of the update card after the command "Check for updates" (design system 7.13).</summary>
public enum UpdateNote
{
    /// <summary>No note.</summary>
    None,

    /// <summary>Nothing newer than the running version.</summary>
    UpToDate,

    /// <summary>The check failed (no network, a timeout, a refused manifest).</summary>
    CheckFailed,
}
