using Pagewing.App.Services;

namespace Pagewing.App.Tests;

/// <summary>A file picker whose dialogs are always cancelled, for window tests that do not open or save through it.</summary>
internal sealed class NoFilePicker : IFilePicker
{
    public Task<string?> PickPdfAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickSavePdfAsync(string suggestedName, string? folder) => Task.FromResult<string?>(null);
}
