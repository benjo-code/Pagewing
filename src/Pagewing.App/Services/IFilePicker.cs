namespace Pagewing.App.Services;

/// <summary>Lets the user choose a PDF file (the system file dialog; a fake in tests).</summary>
public interface IFilePicker
{
    /// <summary>Shows the dialog with the filter "PDF documents" (<c>*.pdf</c>).</summary>
    /// <returns>The full local path of the chosen file, or <see langword="null"/> when the user cancelled.</returns>
    Task<string?> PickPdfAsync();

    /// <summary>
    /// Shows the save dialog ("Save as") with the filter "PDF documents", the suggested file name and, if given, the folder
    /// it starts in. The dialog of the system asks before an existing file is replaced.
    /// </summary>
    /// <param name="suggestedName">The file name the dialog suggests (the name of the open file).</param>
    /// <param name="folder">The folder the dialog starts in (that of the open file), or <see langword="null"/>.</param>
    /// <returns>The full local path to save to, or <see langword="null"/> when the user cancelled.</returns>
    Task<string?> PickSavePdfAsync(string suggestedName, string? folder);
}
