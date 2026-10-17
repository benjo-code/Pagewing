using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Pagewing.App.Resources;

namespace Pagewing.App.Services;

/// <summary>The file dialog of the platform, through the <see cref="TopLevel.StorageProvider"/> of a window.</summary>
public sealed class AvaloniaFilePicker : IFilePicker
{
    private readonly TopLevel _owner;

    public AvaloniaFilePicker(TopLevel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public async Task<string?> PickPdfAsync()
    {
        var pdf = new FilePickerFileType(Strings.PdfFileType)
        {
            Patterns = ["*.pdf"],
            MimeTypes = ["application/pdf"],
            AppleUniformTypeIdentifiers = ["com.adobe.pdf"],
        };
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.OpenFile,
            AllowMultiple = false,
            FileTypeFilter = [pdf],
        });

        // Only local files can be opened; a picker that returns something else counts as cancelled.
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
