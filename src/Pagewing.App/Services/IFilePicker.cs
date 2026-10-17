namespace Pagewing.App.Services;

/// <summary>Lets the user choose a PDF file (the system file dialog; a fake in tests).</summary>
public interface IFilePicker
{
    /// <summary>Shows the dialog with the filter "PDF documents" (<c>*.pdf</c>).</summary>
    /// <returns>The full local path of the chosen file, or <see langword="null"/> when the user cancelled.</returns>
    Task<string?> PickPdfAsync();
}
