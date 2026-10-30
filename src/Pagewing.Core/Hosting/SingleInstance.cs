using System.IO.Pipes;
using System.Runtime.ExceptionServices;
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
/// On Linux the lock file and the pipe live in <c>$XDG_RUNTIME_DIR</c> (private to the user) when it is
/// set, otherwise in the temp directory. If the lock file cannot be used at all (for example because
/// another user created it first in a shared temp directory), <see cref="TryBecomePrimary"/> still
/// returns an instance so that the app starts; it is then uncoordinated and neither listens nor is
/// reachable (best effort).
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
    private readonly FileStream? lockFile;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private Task? loop;
    private bool disposed;

    private SingleInstance(string name, FileStream? lockFile)
    {
        this.name = name;
        this.lockFile = lockFile;
    }

    /// <summary>
    /// Gets or sets what happens when the callback of <see cref="StartListening"/> throws. The default
    /// rethrows the exception on a separate thread-pool work item, so it surfaces like any unhandled
    /// exception while the listener keeps working. Set before <see cref="StartListening"/>; for tests.
    /// </summary>
    internal Action<Exception> CallbackFailed { get; set; } = RethrowOnThreadPool;

    /// <summary>
    /// Gets a value indicating whether the lock file is held. <see langword="false"/> for an
    /// uncoordinated instance that could not use the lock file.
    /// </summary>
    internal bool IsCoordinated => lockFile is not null;

    /// <summary>Gets the instance name used by default: one instance per user.</summary>
    public static string DefaultName => "pagewing-" + Environment.UserName;

    /// <summary>
    /// Tries to become the primary instance by taking the exclusive lock file for <paramref name="name"/>.
    /// </summary>
    /// <param name="name">The instance name, for example <see cref="DefaultName"/>.</param>
    /// <returns>
    /// The primary instance, or <see langword="null"/> when another process holds the lock. When the lock
    /// file cannot be used at all, an uncoordinated instance (see the class remarks) instead of an exception.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or reserved.</exception>
    public static SingleInstance? TryBecomePrimary(string name)
    {
        ValidateName(name);

        FileStream stream;
        try
        {
            stream = new FileStream(LockPathFor(name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or DirectoryNotFoundException or PathTooLongException or NotSupportedException)
        {
            // The lock file is unusable (owned by someone else, no such directory, ...): do not keep the app from starting.
            return new SingleInstance(name, null);
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
    /// <returns>
    /// <see langword="true"/> when everything was handed over; <see langword="false"/> otherwise, also when
    /// connecting or writing takes longer than <paramref name="timeout"/> in total.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or reserved.</exception>
    public static bool TrySend(string name, IReadOnlyList<string> paths, TimeSpan timeout)
    {
        ValidateName(name);
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

    /// <summary>
    /// Gets the pipe name for an instance name: an absolute socket path in the runtime directory on
    /// Linux when there is one (an absolute path works as pipe name on Unix), otherwise the plain name.
    /// </summary>
    internal static string PipeNameFor(string name)
    {
        var safe = FileSafeName(name);
        var directory = RuntimeDirectory();
        if (directory is not null)
        {
            var path = Path.Combine(directory, safe + ".pipe");

            // The sun_path of a Unix socket holds about 108 bytes.
            if (Encoding.UTF8.GetByteCount(path) < 100)
            {
                return path;
            }
        }

        return safe;
    }

    /// <summary>Gets the path of the lock file for an instance name.</summary>
    internal static string LockPathFor(string name) =>
        Path.Combine(RuntimeDirectory() ?? Path.GetTempPath(), FileSafeName(name) + ".lock");

    /// <summary>
    /// Returns <paramref name="xdgRuntimeDir"/> when it is an existing absolute directory; otherwise
    /// <see langword="null"/>.
    /// </summary>
    internal static string? RuntimeDirectoryFrom(string? xdgRuntimeDir) =>
        !string.IsNullOrEmpty(xdgRuntimeDir) && Path.IsPathRooted(xdgRuntimeDir) && Directory.Exists(xdgRuntimeDir)
            ? xdgRuntimeDir
            : null;

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
    /// calls <see cref="Dispose"/>. An exception it throws does not stop the listener; it is rethrown on a
    /// separate thread-pool work item and so surfaces like any unhandled exception.
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

            if (lockFile is null)
            {
                // Uncoordinated: listening could split the pipe with the real primary.
                loop = Task.CompletedTask;
                return;
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

        try
        {
            stop.Cancel();
            if (running is not null && !insideCallback)
            {
                // The loop handles every failure itself and only ends by cancellation.
                running.GetAwaiter().GetResult();
                stop.Dispose();
            }
        }
        finally
        {
            // The lock file stays behind on purpose: deleting it would race with a process that just opened it.
            lockFile?.Dispose();
        }
    }

    private static void RethrowOnThreadPool(Exception exception)
    {
        var captured = ExceptionDispatchInfo.Capture(exception);
        ThreadPool.UnsafeQueueUserWorkItem(static state => ((ExceptionDispatchInfo)state!).Throw(), captured);
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // .NET refuses "anonymous" as a pipe name on Unix.
        if (string.Equals(FileSafeName(name), "anonymous", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The name is reserved.", nameof(name));
        }
    }

    private static string? RuntimeDirectory() =>
        OperatingSystem.IsWindows() ? null : RuntimeDirectoryFrom(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"));

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
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);

                // Short slices, so that a pipe that does not exist yet is retried until the deadline.
                client.Connect((int)Math.Clamp(remaining, 1, 100));

                // The write is bounded too: the primary may not be reading (stuck, or busy with a long callback).
                // Closing the client afterwards is enough; the data stays in the pipe until the primary has read it.
                using var writeLimit = new CancellationTokenSource();
                writeLimit.CancelAfter((int)Math.Clamp(deadline - Environment.TickCount64, 1, int.MaxValue));
                client.WriteAsync(message, writeLimit.Token).AsTask().GetAwaiter().GetResult();
                return true;
            }
            catch (OperationCanceledException)
            {
                // The deadline passed while writing.
                return false;
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

    private NamedPipeServerStream CreateServer()
    {
        var pipeName = PipeNameFor(name);
        var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        // Every instance of the loop asks again: .NET shares one bound socket between the instances of a name and binds a
        // new one when the last instance went away, so this keeps the mode for the socket that really accepts.
        RestrictSocket(pipeName);
        return server;
    }

    /// <summary>
    /// Gets the path of the socket file .NET makes on Unix for a pipe name: the name itself when it is rooted (the runtime
    /// folder case of <see cref="PipeNameFor"/>), otherwise <c>CoreFxPipe_&lt;name&gt;</c> in the temp folder.
    /// </summary>
    internal static string SocketPathFor(string pipeName) =>
        Path.IsPathRooted(pipeName) ? pipeName : Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);

    /// <summary>
    /// On Unix sets the socket file of <paramref name="pipeName"/> to read and write for the owner (0600). .NET leaves it at
    /// the mode of the umask (0755 on a hosted CI runner); connecting needs write access to the file and
    /// <see cref="PipeOptions.CurrentUserOnly"/> also checks the peer, but the file should not invite anybody. Between the
    /// bind and this call the file has the mode of the umask; in the runtime folder (<c>$XDG_RUNTIME_DIR</c>, 0700) nobody
    /// else can reach it at all. Does nothing on Windows, for a file that is not there and for any failure.
    /// </summary>
    internal static void RestrictSocket(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            // Only a file: a folder at that path is not ours to change.
            var path = SocketPathFor(pipeName);
            if (Directory.Exists(path))
            {
                return;
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PlatformNotSupportedException)
        {
            // Hardening only: a file that is not there (yet) or that cannot be changed must not stop the listener.
        }
    }

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
                catch (Exception)
                {
                    // The pipe failed: a client vanished during connect, or creating the pipe is not possible
                    // (for example SocketException from a stale socket of another user, or a path that is too
                    // long). Nothing here may end the listener, so start over after a pause.
                    if (waiting is not null)
                    {
                        await waiting.DisposeAsync().ConfigureAwait(false);
                        waiting = null;
                    }

                    try
                    {
                        await Task.Delay(250, token).ConfigureAwait(false);
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

    private async Task HandleClientAsync(Stream client, Action<IReadOnlyList<string>> onMessage, CancellationToken token)
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
            // A failing callback must not stop the listener.
            CallbackFailed(exception);
        }
        finally
        {
            insideCallback = false;
        }
    }
}
