using System.Globalization;
using System.Text.Json;
using Pagewing.Core.Annotations;
using Pagewing.Core.Settings;
using Pagewing.Pdfium;

namespace Pagewing.Core.Signatures;

/// <summary>
/// The library of saved signatures: one folder with an index (<c>signatures.json</c>) and, per imported signature, a PNG
/// named after its id. Nothing leaves this folder. Reading never throws (a corrupt index is copied to
/// <c>signatures.json.bad</c> and the library starts empty, an entry with a hostile id or damaged data is dropped); a change
/// reads the index again first, so two stores on one folder (two windows, two processes) keep each other's entries, and is
/// written atomically. A change that cannot be written throws an <see cref="IOException"/> and changes nothing.
/// </summary>
public sealed class SignatureStore
{
    /// <summary>The longest name in characters.</summary>
    public const int MaxNameLength = 60;

    /// <summary>The longest index file read (and so written); ten signatures of <see cref="MaxPointsPerSignature"/> points stay far below it.</summary>
    internal const long MaxIndexLength = 16 * 1024 * 1024;

    /// <summary>The most points a saved drawn signature may have (a busy scribble is some thousand after thinning and smoothing; ten of the most take about 12 MB of index).</summary>
    internal const int MaxPointsPerSignature = 10_000;

    private const string IndexName = "signatures.json";
    private const string LockName = "signatures.lock";
    private const double MinAspect = 0.05;
    private const double MaxAspect = 20;
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(2);

    private readonly string directory;
    private readonly object gate = new();
    private IReadOnlyList<SavedSignature> items;

    private SignatureStore(string directory, IReadOnlyList<SavedSignature> items)
    {
        this.directory = directory;
        this.items = items;
    }

    /// <summary>Gets the signatures as of the last read or change of this store, in the order they were added.</summary>
    public IReadOnlyList<SavedSignature> Items
    {
        get
        {
            lock (gate)
            {
                return items;
            }
        }
    }

    private string IndexPath => Path.Combine(directory, IndexName);

    /// <summary>Opens the library in <paramref name="directory"/> (which need not exist yet; nothing is created).</summary>
    public static SignatureStore Load(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        return new SignatureStore(directory, ReadIndex(directory));
    }

    /// <summary>Saves a drawn signature.</summary>
    /// <param name="name">The name (repaired when empty, overlong or with control characters).</param>
    /// <param name="strokes">The strokes in any coordinates (the pad's); they are moved and scaled into the unit square.</param>
    /// <param name="color">The ink.</param>
    /// <returns>The saved signature.</returns>
    /// <exception cref="ArgumentException">No stroke of two points or more, a point that is not finite, no extent, or too many strokes or points.</exception>
    /// <exception cref="SignatureLimitException">The library is full.</exception>
    /// <exception cref="IOException">The library could not be written; nothing changed.</exception>
    public SavedSignature AddDrawn(string name, IReadOnlyList<IReadOnlyList<DisplayPoint>> strokes, DrawColor color)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(strokes);
        if (!Enum.IsDefined(color))
        {
            throw new ArgumentOutOfRangeException(nameof(color));
        }

        var (normalized, aspect) = Normalize(strokes);
        return Change(items =>
        {
            RequireRoom(items);
            var saved = new SavedSignature(NewId(), RepairName(name, items), SavedSignatureKind.Drawn, aspect, color, normalized);
            items.Add(saved);
            return saved;
        });
    }

    /// <summary>Saves an imported signature: the PNG (at most <see cref="SignatureLimits.MaxStoredSide"/> pixels on a side) goes into its own file.</summary>
    /// <param name="name">The name (repaired when empty, overlong or with control characters).</param>
    /// <param name="png">The PNG file.</param>
    /// <param name="aspect">Width divided by height.</param>
    /// <returns>The saved signature.</returns>
    /// <exception cref="ArgumentException">Not a PNG, too large or too many pixels, or an aspect outside 0.05 to 20.</exception>
    /// <exception cref="SignatureLimitException">The library is full.</exception>
    /// <exception cref="IOException">The library could not be written; nothing changed.</exception>
    public SavedSignature AddImage(string name, byte[] png, double aspect)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(png);
        if (!double.IsFinite(aspect) || aspect < MinAspect || aspect > MaxAspect)
        {
            throw new ArgumentException("The aspect ratio is out of range.", nameof(aspect));
        }

        if (png.Length > SignatureLimits.MaxFileBytes || !IsPng(png) || !ImageHeader.TryReadSize(png, out var width, out var height)
            || width > SignatureLimits.MaxStoredSide || height > SignatureLimits.MaxStoredSide)
        {
            throw new ArgumentException("The image is not a PNG of a reasonable size.", nameof(png));
        }

        return Change(items =>
        {
            RequireRoom(items);
            var saved = new SavedSignature(NewId(), RepairName(name, items), SavedSignatureKind.Image, aspect, DrawColor.Black, null);
            WritePng(saved.Id, png);
            items.Add(saved);
            return saved;
        }, onFailure: saved => DeleteQuietly(PngPath(saved.Id)));
    }

    /// <summary>Gives a signature a new name (repaired like in <see cref="AddDrawn"/>); an unknown id changes nothing.</summary>
    /// <exception cref="IOException">The library could not be written; nothing changed.</exception>
    public void Rename(string id, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsId(id))
        {
            return;
        }

        Change<SavedSignature?>(items =>
        {
            var index = items.FindIndex(s => s.Id == id);
            if (index < 0)
            {
                return null;
            }

            var others = items.Where(s => s.Id != id).ToList();
            items[index] = items[index] with { Name = RepairName(name, others) };
            return items[index];
        });
    }

    /// <summary>Removes a signature and its image file; an unknown id changes nothing.</summary>
    /// <exception cref="IOException">The library could not be written; nothing changed.</exception>
    public void Remove(string id)
    {
        if (!IsId(id))
        {
            return;
        }

        var removed = Change<SavedSignature?>(items =>
        {
            var index = items.FindIndex(s => s.Id == id);
            if (index < 0)
            {
                return null;
            }

            var gone = items[index];
            items.RemoveAt(index);
            return gone;
        });
        if (removed is not null)
        {
            DeleteQuietly(PngPath(id));
        }
    }

    /// <summary>Reads the PNG of an imported signature; <see langword="null"/> for a drawn one, an unknown or damaged id, a missing or oversized file.</summary>
    public byte[]? ReadPng(string id)
    {
        if (!IsId(id))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(PngPath(id));
            return info.Exists && info.Length <= SignatureLimits.MaxFileBytes ? File.ReadAllBytes(info.FullName) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsId(string? id) =>
        id is { Length: 32 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static bool IsPng(byte[] file) =>
        file.Length >= 8 && file.AsSpan(0, 8).SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    private static void RequireRoom(List<SavedSignature> items)
    {
        if (items.Count >= SignatureLimits.MaxSignatures)
        {
            throw new SignatureLimitException();
        }
    }

    /// <summary>A usable name: no control characters, trimmed, at most <see cref="MaxNameLength"/> characters, never empty ("Signature n" with the first free n).</summary>
    private static string RepairName(string name, IEnumerable<SavedSignature> others)
    {
        var cleaned = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        if (cleaned.Length > MaxNameLength)
        {
            var length = char.IsHighSurrogate(cleaned[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength;
            cleaned = cleaned[..length].TrimEnd();
        }

        if (cleaned.Length > 0)
        {
            return cleaned;
        }

        var taken = others.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var candidate = "Signature " + n.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Moves the strokes into the unit square (a box of less than 5 % of the longer side on one axis is widened around its middle).</summary>
    private static (IReadOnlyList<IReadOnlyList<DisplayPoint>> Strokes, double Aspect) Normalize(IReadOnlyList<IReadOnlyList<DisplayPoint>> strokes)
    {
        if (strokes.Count > PdfiumDrawings.MaxInkStrokes)
        {
            throw new ArgumentException("Too many strokes.", nameof(strokes));
        }

        List<IReadOnlyList<DisplayPoint>> usable = [];
        long points = 0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var stroke in strokes)
        {
            ArgumentNullException.ThrowIfNull(stroke);
            points += stroke.Count;
            if (points > MaxPointsPerSignature)
            {
                throw new ArgumentException("Too many points.", nameof(strokes));
            }

            foreach (var p in stroke)
            {
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
                {
                    throw new ArgumentException("A point is not finite.", nameof(strokes));
                }
            }

            if (stroke.Count < 2)
            {
                continue;
            }

            usable.Add(stroke);
            foreach (var p in stroke)
            {
                minX = Math.Min(minX, p.X);
                maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);
            }
        }

        var width = maxX - minX;
        var height = maxY - minY;
        if (usable.Count == 0 || !(Math.Max(width, height) > 1e-9))
        {
            throw new ArgumentException("A signature needs at least one stroke with an extent.", nameof(strokes));
        }

        var minSide = Math.Max(width, height) * MinAspect;
        if (width < minSide)
        {
            minX -= (minSide - width) / 2;
            width = minSide;
        }

        if (height < minSide)
        {
            minY -= (minSide - height) / 2;
            height = minSide;
        }

        var normalized = usable
            .Select(stroke => (IReadOnlyList<DisplayPoint>)[.. stroke.Select(p => new DisplayPoint(Math.Clamp((p.X - minX) / width, 0, 1), Math.Clamp((p.Y - minY) / height, 0, 1)))])
            .ToList();
        return (normalized, Math.Clamp(width / height, MinAspect, MaxAspect));
    }

    private static List<SavedSignature> ReadIndex(string directory)
    {
        var path = Path.Combine(directory, IndexName);
        var text = JsonFile.ReadText(path, MaxIndexLength);
        if (text is null)
        {
            return [];
        }

        try
        {
            var document = JsonSerializer.Deserialize(text, SignatureJsonContext.Default.SignatureLibraryDocument);
            if (document?.Items is { } entries)
            {
                return Sanitize(directory, entries);
            }
        }
        catch (JsonException)
        {
            // Falls through to the backup below.
        }

        JsonFile.KeepBackup(path);
        return [];
    }

    private static List<SavedSignature> Sanitize(string directory, List<SavedSignature?> entries)
    {
        List<SavedSignature> result = [];
        HashSet<string> seen = [];
        foreach (var entry in entries)
        {
            if (result.Count >= SignatureLimits.MaxSignatures)
            {
                break;
            }

            if (entry is null || !IsId(entry.Id) || !seen.Add(entry.Id) || !Enum.IsDefined(entry.Kind) || !Enum.IsDefined(entry.Color)
                || !double.IsFinite(entry.Aspect) || entry.Aspect < MinAspect || entry.Aspect > MaxAspect)
            {
                continue;
            }

            var valid = entry.Kind == SavedSignatureKind.Drawn
                ? HasUsableStrokes(entry.Strokes)
                : File.Exists(Path.Combine(directory, entry.Id + ".png"));
            if (valid)
            {
                result.Add(entry with
                {
                    Name = RepairName(entry.Name ?? string.Empty, result),
                    Strokes = entry.Kind == SavedSignatureKind.Drawn ? entry.Strokes : null,
                });
            }
        }

        return result;
    }

    private static bool HasUsableStrokes(IReadOnlyList<IReadOnlyList<DisplayPoint>>? strokes)
    {
        if (strokes is null || strokes.Count == 0 || strokes.Count > PdfiumDrawings.MaxInkStrokes)
        {
            return false;
        }

        long points = 0;
        foreach (var stroke in strokes)
        {
            if (stroke is null || stroke.Count < 2)
            {
                return false;
            }

            points += stroke.Count;
            if (points > MaxPointsPerSignature || stroke.Any(p => !(p.X is >= 0 and <= 1) || !(p.Y is >= 0 and <= 1)))
            {
                return false;
            }
        }

        return true;
    }

    private string PngPath(string id) => Path.Combine(directory, id + ".png");

    /// <summary>Reads the index afresh, applies <paramref name="change"/>, writes the index, and makes the result the state of this store.</summary>
    private T Change<T>(Func<List<SavedSignature>, T> change, Action<T>? onFailure = null)
    {
        lock (gate)
        {
            CreateDirectory();
            using var fileLock = AcquireLock();
            var current = new List<SavedSignature>(ReadIndex(directory));
            var result = change(current);
            var json = JsonSerializer.Serialize(new SignatureLibraryDocument { Items = [.. current] }, SignatureJsonContext.Default.SignatureLibraryDocument);
            if (!JsonFile.TryWrite(IndexPath, json, createDirectory: false, unixMode: OwnerFile))
            {
                onFailure?.Invoke(result);
                throw new IOException("The library of signatures could not be written.");
            }

            items = current;
            return result;
        }
    }

    private void CreateDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, OwnerFolder);
        }
    }

    /// <summary>Takes the lock file that serialises changes of two processes; after <see cref="LockWait"/> the change goes ahead without it.</summary>
    private FileStream? AcquireLock()
    {
        var path = Path.Combine(directory, LockName);
        var until = DateTime.UtcNow + LockWait;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(20);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private void WritePng(string id, byte[] png)
    {
        var path = PngPath(id);
        var temp = path + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = OwnerFile;
            }

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(png);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            DeleteQuietly(temp);
            throw;
        }
    }

    private static void DeleteQuietly(string path) => JsonFile.DeleteQuietly(path);
}
