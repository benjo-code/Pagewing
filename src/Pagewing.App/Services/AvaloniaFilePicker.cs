using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Pagewing.App.Resources;

namespace Pagewing.App.Services;

/// <summary>The file dialog of the platform, through the <see cref="TopLevel.StorageProvider"/> of a window.</summary>
public sealed class AvaloniaFilePicker : IFilePicker, IImagePicker
{
    private readonly TopLevel _owner;

    public AvaloniaFilePicker(TopLevel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public async Task<string?> PickPdfAsync()
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.OpenFile,
            AllowMultiple = false,
            FileTypeFilter = [PdfType()],
        });

        // Only local files can be opened; a picker that returns something else counts as cancelled.
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickImageAsync()
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.SignatureChooseImage.TrimEnd('…', ' ', '.'),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PNG, JPG") { Patterns = ["*.png", "*.jpg", "*.jpeg"], MimeTypes = ["image/png", "image/jpeg"] }],
        });

        // Only local files can be read; a picker that returns something else counts as cancelled.
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSavePdfAsync(string suggestedName, string? folder)
    {
        var storage = _owner.StorageProvider;
        IStorageFolder? start = null;
        if (!string.IsNullOrEmpty(folder))
        {
            try
            {
                start = await storage.TryGetFolderFromPathAsync(folder);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // The folder is gone or not reachable: the dialog starts where the system likes.
            }
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.SaveAsTitle,
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = start,
            DefaultExtension = "pdf",
            FileTypeChoices = [PdfType()],
            ShowOverwritePrompt = true,
        });

        // Only a local file can be written; anything else counts as cancelled.
        return file?.TryGetLocalPath();
    }

    private static FilePickerFileType PdfType() => new(Strings.PdfFileType)
    {
        Patterns = ["*.pdf"],
        MimeTypes = ["application/pdf"],
        AppleUniformTypeIdentifiers = ["com.adobe.pdf"],
    };
}
