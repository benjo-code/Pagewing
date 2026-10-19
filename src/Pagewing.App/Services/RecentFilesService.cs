using Pagewing.Core.Settings;

namespace Pagewing.App.Services;

/// <summary>
/// The recently opened files of the running app and where each was last read (<see cref="RecentFiles"/>), kept in
/// <see cref="FileName"/> in the config folder. The file is not read in the startup path: only when <see cref="Files"/>
/// is first asked for or <see cref="EnsureLoaded"/> runs (the windows post that after the first sharp page, or after the
/// first frame of the start screen). A file opened before that is noted and goes to the top once the list is read.
/// Every change is written at once, atomically; reading and writing never throw (a broken file gives an empty list and
/// is kept as <c>&lt;name&gt;.bad</c>, a failed write keeps the list in memory and sets <see cref="LastSaveFailed"/>).
/// UI thread only.
/// </summary>
public sealed class RecentFilesService
{
    /// <summary>The name of the history file in the config folder (<see cref="UserDataPaths.ConfigDirectory()"/>).</summary>
    public const string FileName = "recent.json";

    // Null for a detached history: it never touches a file.
    private readonly RecentFilesStore? _store;
    private RecentFiles? _files;

    // Files opened before the list was read, oldest first, with the time they were opened.
    private List<(string Path, DateTimeOffset At)>? _pendingOpens;

    private RecentFilesService(RecentFilesStore? store, RecentFiles? files)
    {
        _store = store;
        _files = files;
    }

    /// <summary>Raised on the UI thread after the list was read or changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the list, newest first. The first call reads the file (if it was not read yet); treat the list as read-only
    /// and change it through this service.
    /// </summary>
    public RecentFiles Files
    {
        get
        {
            EnsureLoaded();
            return _files!;
        }
    }

    /// <summary>Gets whether the list has been read (always for a detached history).</summary>
    public bool IsLoaded => _files is not null;

    /// <summary>Gets the history file, or <see langword="null"/> for a <see cref="Detached"/> history.</summary>
    public string? FilePath => _store?.FilePath;

    /// <summary>Gets whether the last write failed (the list stays in memory; the next successful write clears it).</summary>
    public bool LastSaveFailed { get; private set; }

    /// <summary>Gets how often the file was read (tests check that the startup path does not read it).</summary>
    internal int Reads { get; private set; }

    /// <summary>Gets or sets the clock of the times the entries carry (tests set their own).</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>The history of the user (<see cref="FileName"/> in the config folder); nothing is read yet.</summary>
    public static RecentFilesService Load() => Load(UserDataPaths.ConfigDirectory());

    /// <summary>The history in <paramref name="directory"/> (tests give a temporary folder); nothing is read yet.</summary>
    internal static RecentFilesService Load(string directory) =>
        new(new RecentFilesStore(Path.Combine(directory, FileName)), null);

    /// <summary>A history in memory only, empty at first; it never reads or writes a file (tests, measurements).</summary>
    public static RecentFilesService Detached() => new(null, new RecentFiles());

    /// <summary>
    /// Reads the file unless that happened already, puts the files opened meanwhile on top (and writes them) and
    /// raises <see cref="Changed"/>. Later calls do nothing.
    /// </summary>
    public void EnsureLoaded()
    {
        if (_files is not null)
        {
            return;
        }

        Reads++;
        var files = _store!.Load();
        _files = files;
        var pending = _pendingOpens;
        _pendingOpens = null;
        if (pending is { Count: > 0 })
        {
            foreach (var (path, at) in pending)
            {
                files.Touch(path, at);
            }

            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// A file was opened: it goes to the top and keeps its position, and the list is written. Before the list was
    /// read (the file of the command line, opened in the startup path) it is only noted and comes to the top once the
    /// list is read.
    /// </summary>
    public void Opened(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var now = Clock.GetUtcNow();
        if (_files is null)
        {
            (_pendingOpens ??= []).Add((path, now));
            return;
        }

        _files.Touch(path, now);
        Commit();
    }

    /// <summary>A file was closed where <paramref name="view"/> says: it goes to the top with that position, and the list is written.</summary>
    public void Closed(string path, RememberedView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        Closed([(path, view)]);
    }

    /// <summary>
    /// Several files were closed (a window with its tabs): each goes to the top with its position, the last one on
    /// top, and the list is written once. A view with unusable values (<see cref="RememberedView.IsValid"/>) is left out.
    /// </summary>
    public void Closed(IReadOnlyList<(string Path, RememberedView View)> closed)
    {
        ArgumentNullException.ThrowIfNull(closed);
        var files = Files;
        var now = Clock.GetUtcNow();
        var changed = false;
        foreach (var (path, view) in closed)
        {
            if (!string.IsNullOrWhiteSpace(path) && view is not null && view.IsValid())
            {
                files.Remember(path, view, now);
                changed = true;
            }
        }

        if (changed)
        {
            Commit();
        }
    }

    /// <summary>Removes the file from the list ("Remove from the history"); the list is written.</summary>
    public void Remove(string path)
    {
        if (Files.Remove(path))
        {
            Commit();
        }
    }

    /// <summary>Removes every file and with it every position ("Clear history"); the list is written.</summary>
    public void Clear()
    {
        _pendingOpens = null;
        Files.Clear();
        Commit();
    }

    private void Commit()
    {
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        if (_store is not null && _files is not null)
        {
            LastSaveFailed = !_store.TrySave(_files);
        }
    }
}
