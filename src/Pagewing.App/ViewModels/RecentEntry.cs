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
    /// <summary>Gets the file name.</summary>
    public string FileName { get; } = SafeFileName(Path);

    /// <summary>Gets the folder of the file, or an empty text when the path names none.</summary>
    public string Folder { get; } = SafeFolder(Path);

    /// <summary>Gets the name a screen reader says: "file name, folder".</summary>
    public string AutomationName => Folder.Length > 0 ? $"{FileName}, {Folder}" : FileName;

    /// <summary>Gets the name of the button that removes the entry: "Remove “file name” from the history".</summary>
    public string RemoveName => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.StartRecentRemoveName), FileName);

    private static string SafeFileName(string path)
    {
        try
        {
            return System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static string SafeFolder(string path)
    {
        try
        {
            return System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }
}
