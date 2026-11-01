using Pagewing.Core.Saving;

namespace Pagewing.Cli.Commands;

/// <summary>
/// Words why the checked, atomic write of a file failed, for every command that writes a PDF (English, stable wording for
/// scripts).
/// </summary>
internal static class SaveFailureText
{
    /// <summary>Describes <paramref name="ex"/> as one sentence or two.</summary>
    /// <param name="ex">The failure.</param>
    /// <param name="target">The file that was to be written.</param>
    /// <param name="nothingWritten">
    /// Whether the command wrote nothing at all (one output file). A command that writes several files says what was written
    /// itself, so it passes <see langword="false"/> and the sentence ends without "Nothing was written."
    /// </param>
    public static string Describe(SaveFailureException ex, string target, bool nothingWritten = true)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var none = nothingWritten ? " Nothing was written." : string.Empty;
        return ex.Reason switch
        {
            SaveFailureReason.TargetExists => $"'{target}' already exists. Use --force to replace it.{none}",
            SaveFailureReason.NotWritable => $"Cannot write '{target}': the file or its folder is not writable.{none}",
            SaveFailureReason.DiskFull => $"Cannot write '{target}': the disk is full.{none}",
            SaveFailureReason.WriteFailed => $"Cannot write '{target}': {ex.Message}{none}",
            SaveFailureReason.VerificationFailed => $"The written file failed the check, so it was not kept: {ex.Message}",
            SaveFailureReason.SourceModified or SaveFailureReason.OriginalChanged =>
                $"{(ex.FilePath is { Length: > 0 } path ? $"The input file '{path}'" : "The input file")} was changed by another program while the command ran.{(nothingWritten ? " Nothing was written; run the command again." : " Run the command again.")}",
            SaveFailureReason.EncryptedNotSupported when ex.FilePath is { Length: > 0 } locked =>
                $"The input file '{locked}' is encrypted, but the document it is inserted into is not, so {(nothingWritten ? "nothing" : "it")} was not written.",
            SaveFailureReason.EncryptedNotSupported =>
                $"The input file is encrypted and the result would not stay encrypted, so {(nothingWritten ? "nothing" : "it")} was not written.",
            _ => $"Cannot write '{target}': {ex.Message}{none}",
        };
    }
}
