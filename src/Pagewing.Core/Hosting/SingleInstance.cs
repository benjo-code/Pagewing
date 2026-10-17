using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Pagewing.Core.Hosting;

/// <summary>
/// Makes sure only one Pagewing process per user runs, and hands the files of later launches to it.
/// </summary>
/// <remarks>
/// <para>
/// The primary instance is elected through an exclusive lock file in the temp directory
/// (<see cref="FileShare.None"/>). The operating system releases the lock when the process ends, even
/// after a crash, so there is no stale lock. A later launch calls <see cref="TrySend"/>, which connects
/// to the primary's named pipe (a Unix domain socket on Linux) and sends the file paths.
/// </para>
/// <para>
/// Everything arriving on the pipe is untrusted: counts and lengths are bounded, truncated or garbled
/// messages are dropped, and one bad client never stops the listener.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The most paths one message may carry. <see cref="TrySend"/> splits longer lists.</summary>
    internal const int MaxPaths = 64;

    /// <summary>The longest path, in UTF-16 characters, a message may carry.</summary>
    internal const int MaxPathChars = 32_768;

    // UTF-8 needs at most 3 bytes for one UTF-16 character.
    private const int MaxPathBytes = MaxPathChars * 3;

    // Count (int32), then per path a 7-bit encoded length (at most 5 bytes) and the UTF-8 bytes.
    private const int MaxMessageBytes = sizeof(int) + (MaxPaths * (5 + MaxPathBytes));

    private const int MaxNameLength = 64;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Set while the listener runs the callback, so that Dispose called from the callback does not wait for itself.
    [ThreadStatic]
    private static bool insideCallback;

    private readonly string name;
    private readonly FileStream lockFile;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private Task? loop;
    private bool disposed;

    private SingleInstance(string name, FileStream lockFile)
    {
        this.name = name;
        this.lockFile = lockFile;
    }

    /// <summary>Gets the instance name used by default: one instance per user.</summary>
    public static string DefaultName => "pagewing-" + Environment.UserName;

    /// <summary>
    /// Tries to become the primary instance by taking the exclusive lock file for <paramref name="name"/>.
    /// </summary>
    /// <param name="name">The instance name, for example <see cref="DefaultName"/>.</param>
    /// <returns>The primary instance, or <see langword="null"/> when another process holds the lock.</returns>
    public static SingleInstance? TryBecomePrimary(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var path = Path.Combine(Path.GetTempPath(), FileSafeName(name) + ".lock");
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
        }
        catch (IOException)
        {
            // Sharing violation (Windows) or flock conflict (Unix): another instance holds the lock.
            return null;
        }

        return new SingleInstance(name, stream);
    }

    /// <summary>
    /// Sends <paramref name="paths"/> to the primary instance of <paramref name="name"/>. An empty list only
    /// asks it to come to the front.
    /// </summary>
    /// <param name="name">The instance name.</param>
    /// <param name="paths">The file paths. More than 64 are sent as several messages.</param>
    /// <param name="timeout">
    /// How long to keep trying. The primary opens its pipe only after the first frame, so the first
    /// attempts may find no listener yet.
    /// </param>
    /// <returns><see langword="true"/> when everything was handed over; <see langword="false"/> otherwise.</returns>
    public static bool TrySend(string name, IReadOnlyList<string> paths, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(paths);

        foreach (var path in paths)
        {
            ArgumentNullException.ThrowIfNull(path);
            if (path.Length > MaxPathChars)
            {
                return false;
            }
        }

        var pipeName = PipeNameFor(name);
        var deadline = Environment.TickCount64 + (long)Math.Max(0, timeout.TotalMilliseconds);
        var offset = 0;
        do
        {
            var count = Math.Min(MaxPaths, paths.Count - offset);
            var message = Encode(paths, offset, count);
            if (!TrySendMessage(pipeName, message, deadline))
            {
                return false;
            }

            offset += count;
        }
        while (offset < paths.Count);

        return true;
    }

    /// <summary>Gets the pipe name for an instance name.</summary>
    internal static string PipeNameFor(string name) => FileSafeName(name);

    /// <summary>
    /// Parses a message. Returns <see langword="false"/> for anything that is not exactly one well-formed
    /// message within the limits.
    /// </summary>
    internal static bool TryParse(byte[] data, out string[] paths)
    {
        paths = [];
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var reader = new BinaryReader(stream, StrictUtf8);
            var count = reader.ReadInt32();
            if (count is < 0 or > MaxPaths)
            {
                return false;
            }

            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                var length = reader.Read7BitEncodedInt();
                if (length < 0 || length > MaxPathBytes || length > stream.Length - stream.Position)
                {
                    return false;
                }

                result[i] = StrictUtf8.GetString(reader.ReadBytes(length));
                if (result[i].Length > MaxPathChars)
                {
                    return false;
                }
            }

            if (stream.Position != stream.Length)
            {
                return false;
            }

            paths = result;
            return true;
        }
        catch (Exception exception) when (exception is EndOfStreamException or FormatException or ArgumentException)
        {
            // Truncated input, a malformed length prefix or invalid UTF-8 (DecoderFallbackException).
            return false;
        }
    }

    /// <summary>
    /// Starts accepting messages from later launches. <paramref name="onMessage"/> runs on a thread-pool
    /// thread, once per message; an empty list means "only activate". It must not block on the thread that
    /// calls <see cref="Dispose"/>. Exceptions it throws are swallowed so that the listener keeps working.
    /// </summary>
    /// <param name="onMessage">Receives the paths of one message.</param>
    public void StartListening(Action<IReadOnlyList<string>> onMessage)
    {
        ArgumentNullException.ThrowIfNull(onMessage);

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (loop is not null)
            {
                throw new InvalidOperationException("The instance is already listening.");
            }

            var token = stop.Token;
            loop = Task.Run(() => RunAsync(onMessage, token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Stops listening and releases the lock. No callback runs after this returns (unless called from the
    /// callback itself, which then is the last one).
    /// </summary>
    public void Dispose()
    {
        Task? running;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            running = loop;
        }

        stop.Cancel();
        if (running is not null && !insideCallback)
        {
            // The loop only ends by cancellation, and all failures are handled inside it.
            running.GetAwaiter().GetResult();
            stop.Dispose();
        }

        // The lock file stays behind on purpose: deleting it would race with a process that just opened it.
        lockFile.Dispose();
    }

    private static string FileSafeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.' ? c : '_');
        }

        var safe = builder.ToString();
        if (safe == name && safe.Length <= MaxNameLength)
        {
            return safe;
        }

        // Keep different original names apart after replacing characters or shortening.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0, 4);
        return safe[..Math.Min(safe.Length, MaxNameLength - 9)] + "-" + hash;
    }

    private static byte[] Encode(IReadOnlyList<string> paths, int offset, int count)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(count);
            for (var i = 0; i < count; i++)
            {
                writer.Write(paths[offset + i]);
            }
        }

        return stream.ToArray();
    }

    private static bool TrySendMessage(string pipeName, byte[] message, long deadline)
    {
        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);

                // Short slices, so that a pipe that does not exist yet is retried until the deadline.
                client.Connect((int)Math.Clamp(remaining, 1, 100));
                client.Write(message);
                client.Flush();
                if (OperatingSystem.IsWindows())
                {
                    // Keep the connection open until the primary has read everything.
                    client.WaitForPipeDrain();
                }

                return true;
            }
            catch (TimeoutException)
            {
                // No listener yet; try again until the deadline.
            }
            catch (IOException)
            {
                // The pipe is not there yet, is busy, or broke during the transfer.
                Thread.Sleep(10);
            }
            catch (UnauthorizedAccessException)
            {
                // The pipe belongs to another user, so there is no instance of ours to talk to.
                return false;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }
        }
    }

    private static async Task<byte[]?> ReadMessageAsync(Stream stream, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(ReadTimeout);
        using var message = new MemoryStream();
        var chunk = new byte[8192];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return message.ToArray();
                }

                if (message.Length + read > MaxMessageBytes)
                {
                    return null;
                }

                message.Write(chunk, 0, read);
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            // The client stalled; drop it.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private NamedPipeServerStream CreateServer() =>
        new(
            PipeNameFor(name),
            PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task RunAsync(Action<IReadOnlyList<string>> onMessage, CancellationToken token)
    {
        // The next instance is created before a connection is handled, so that clients never find the pipe missing.
        NamedPipeServerStream? waiting = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    waiting ??= CreateServer();
                    await waiting.WaitForConnectionAsync(token).ConfigureAwait(false);
                    var connected = waiting;
                    waiting = null;
                    try
                    {
                        waiting = CreateServer();
                        await HandleClientAsync(connected, onMessage, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        await connected.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The pipe failed (for example a client vanished during connect). Start over after a pause.
                    if (waiting is not null)
                    {
                        await waiting.DisposeAsync().ConfigureAwait(false);
                        waiting = null;
                    }

                    try
                    {
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            if (waiting is not null)
            {
                await waiting.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task HandleClientAsync(Stream client, Action<IReadOnlyList<string>> onMessage, CancellationToken token)
    {
        var data = await ReadMessageAsync(client, token).ConfigureAwait(false);
        if (data is null || !TryParse(data, out var paths) || token.IsCancellationRequested)
        {
            return;
        }

        insideCallback = true;
        try
        {
            onMessage(paths);
        }
        catch (Exception exception)
        {
            // A failing callback must not stop the listener; the owner of the callback is responsible for its errors.
            Debug.WriteLine("Single-instance callback failed: " + exception.Message);
        }
        finally
        {
            insideCallback = false;
        }
    }
}
