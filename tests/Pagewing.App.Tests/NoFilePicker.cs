using Pagewing.App.Services;

namespace Pagewing.App.Tests;

/// <summary>A file picker whose dialog is always cancelled, for window tests that do not open through it.</summary>
internal sealed class NoFilePicker : IFilePicker
{
    public Task<string?> PickPdfAsync() => Task.FromResult<string?>(null);
}
