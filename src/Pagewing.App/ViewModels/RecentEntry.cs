using System.Text;
using Pagewing.App.Resources;

namespace Pagewing.App.ViewModels;

/// <summary>
/// An entry of "Recently opened" on the start screen: a file of the history, shown by its name and its folder as the
/// path says (the disk is not asked whether it is still there).
/// </summary>
/// <param name="Path">The full path of the file.</param>
public sealed record RecentEntry(string Path)
{
    // Path.GetFileName and Path.GetDirectoryName only take the path apart: since .NET Core 2.1 they reject no characters
    // (checked on .NET 10 with NUL, '<>|"*?', ':' and 40 000 characters), so neither needs a catch.

    /// <summary>Gets the file name, or the whole path when it names none.</summary>
    public string FileName { get; } = System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;

    /// <summary>Gets the folder of the file, or an empty text when the path names none.</summary>
    public string Folder { get; } = System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    /// <summary>Gets the name a screen reader says: "file name, folder".</summary>
    public string AutomationName => Folder.Length > 0 ? $"{FileName}, {Folder}" : FileName;

    /// <summary>Gets the name of the button that removes the entry: "Remove “file name” from the history".</summary>
    public string RemoveName => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.StartRecentRemoveName), FileName);
}
