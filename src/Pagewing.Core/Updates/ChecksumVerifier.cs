using System.Buffers;
using System.Security.Cryptography;

namespace Pagewing.Core.Updates;

/// <summary>Checks a downloaded file against the size and SHA-256 of the signed manifest.</summary>
public static class ChecksumVerifier
{
    private const int ChunkSize = 81920;

    /// <summary>
    /// Whether the file has exactly <paramref name="size"/> bytes and the SHA-256 <paramref name="sha256Hex"/> (64 hex
    /// characters, either case). The size is compared first, the hash is computed in chunks without holding the file
    /// in memory. A missing or unreadable file, or a checksum that is not 64 hex characters, gives <c>false</c>;
    /// only cancellation throws.
    /// </summary>
    public static async Task<bool> MatchesAsync(string path, string sha256Hex, long size, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (sha256Hex is not { Length: 64 })
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(sha256Hex);
        }
        catch (FormatException)
        {
            return false;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != size)
            {
                return false;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, ChunkSize), ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                total += read;
            }

            return total == size && CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
