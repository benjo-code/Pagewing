using Avalonia.Threading;
using Pagewing.Core.Settings;

namespace Pagewing.App.Services;

/// <summary>
/// The recently opened files of the running app and where each was last read (<see cref="RecentFiles"/>), kept in
/// <see cref="FileName"/> in the config folder. The file is not read in the startup path: only when <see cref="Files"/>
/// is first asked for or <see cref="EnsureLoaded"/> runs (the windows post that after the first sharp page, or after the
/// first frame of the start screen). A file opened before that is noted and goes to the top once the list is read.
/// Opening a file changes the list in memory only; the write waits for the first sharp page of that file
/// (<see cref="SaveLater"/>, one write for every change made meanwhile), so it never delays the render of an open.
/// Closing, removing and clearing write at once (<see cref="Flush"/> writes what waits, the window does that when it
/// closes). A change that changes nothing (the file is on top with that position already) writes nothing. Writing is
/// atomic; reading and writing never throw (a broken file gives an empty list and is kept as <c>&lt;name&gt;.bad</c>, a
/// failed write keeps the list in memory and sets <see cref="LastSaveFailed"/>). UI thread only.
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

    // The list in memory differs from the file; a job that writes it is waiting.
    private bool _dirty;
    private bool _savePosted;

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

    /// <summary>Gets whether the list in memory has changes that are not written yet.</summary>
    public bool HasUnsavedChanges => _dirty || _pendingOpens is { Count: > 0 };

    /// <summary>Gets how often the file was read (tests check that the startup path does not read it).</summary>
    internal int Reads { get; private set; }

    /// <summary>Gets how often the file was written (tests check when the writes happen).</summary>
    internal int Writes { get; private set; }

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
    /// Reads the file unless that happened already, puts the files opened meanwhile on top (written later, like any
    /// open) and raises <see cref="Changed"/>. Later calls do nothing.
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
        if (pending is not null)
        {
            foreach (var (path, at) in pending)
            {
                _dirty |= Touch(files, path, at);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// A file was opened: it goes to the top and keeps its position, in memory. The write comes with
    /// <see cref="SaveLater"/> (after the file's first sharp page) or the next change that writes. Before the list was
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

        if (Touch(_files, path, now))
        {
            _dirty = true;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Writes what waits in a job of <see cref="DispatcherPriority.Background"/> (after rendering): one write for any
    /// number of changes. The windows call it after the first sharp page of a file. Nothing waits: nothing is written.
    /// </summary>
    public void SaveLater()
    {
        if (_savePosted || !HasUnsavedChanges || _store is null)
        {
            return;
        }

        _savePosted = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _savePosted = false;
                Flush();
            },
            DispatcherPriority.Background);
    }

    /// <summary>Writes what waits now (a window that closes); nothing waits: nothing is written.</summary>
    public void Flush()
    {
        if (!HasUnsavedChanges)
        {
            return;
        }

        // The files opened before the list was read may all have been on top already: then nothing changed.
        EnsureLoaded();
        if (_dirty)
        {
            Save();
        }
    }

    /// <summary>A file was closed where <paramref name="view"/> says: it goes to the top with that position, and the list is written.</summary>
    public void Closed(string path, RememberedView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        Closed([(path, view)]);
    }

    /// <summary>
    /// Several files were closed (a window with its tabs): each goes to the top with its position, the last one on
    /// top, and the list is written once, with whatever waited. A view with unusable values
    /// (<see cref="RememberedView.IsValid"/>) is left out; a file on top with that position already changes nothing.
    /// </summary>
    public void Closed(IReadOnlyList<(string Path, RememberedView View)> closed)
    {
        ArgumentNullException.ThrowIfNull(closed);
        var files = Files;
        var now = Clock.GetUtcNow();
        var changed = false;
        foreach (var (path, view) in closed)
        {
            if (string.IsNullOrWhiteSpace(path) || view is null || !view.IsValid() || IsOnTop(files, path, view))
            {
                continue;
            }

            files.Remember(path, view, now);
            changed = true;
        }

        if (changed)
        {
            _dirty = true;
            Changed?.Invoke();
        }

        Flush();
    }

    /// <summary>Removes the file from the list ("Remove from the history"); the list is written.</summary>
    public void Remove(string path)
    {
        if (Files.Remove(path))
        {
            _dirty = true;
            Changed?.Invoke();
        }

        Flush();
    }

    /// <summary>Removes every file and with it every position ("Clear history"); the list is written.</summary>
    public void Clear()
    {
        _pendingOpens = null;
        var files = Files;
        if (files.Items.Count > 0)
        {
            files.Clear();
            _dirty = true;
            Changed?.Invoke();
        }

        Flush();
    }

    /// <summary>Moves the file to the top; returns whether that changed anything (it was not on top already).</summary>
    private static bool Touch(RecentFiles files, string path, DateTimeOffset now)
    {
        if (files.Items.Count > 0 && ReferenceEquals(files.TryGet(path), files.Items[0]))
        {
            return false;
        }

        files.Touch(path, now);
        return true;
    }

    private static bool IsOnTop(RecentFiles files, string path, RememberedView view) =>
        files.Items.Count > 0 && files.TryGet(path) is { } entry && ReferenceEquals(entry, files.Items[0]) && entry.View == view;

    private void Save()
    {
        _dirty = false;
        if (_store is not null && _files is not null)
        {
            Writes++;
            LastSaveFailed = !_store.TrySave(_files);
        }
    }
}
