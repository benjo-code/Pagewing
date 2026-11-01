using System.ComponentModel;
using Pagewing.App.Resources;

namespace Pagewing.App.ViewModels;

/// <summary>
/// "Insert pages from a file" in a window (milestone 13, spec 5.3): the file dialog of the window asks for the file (the same
/// dialog as "Open"), then the dialog <see cref="OverlayKind.InsertFile"/> opens it, asks for its password if it needs one and
/// takes the pages to insert. Nothing of it is made before the first use. A file counts against the limit of
/// <see cref="Core.Pages.PageEditLimits.MaxInsertedFiles"/> before it is picked, so a refusal never opens anything. The dialog
/// closes when the tab leaves the mode, the window, or the front.
/// </summary>
public sealed partial class MainViewModel
{
    private InsertFileViewModel? _insertFile;
    private TabViewModel? _insertTab;
    private PropertyChangedEventHandler? _insertTabHandler;

    /// <summary>Gets the view model of the dialog "Insert pages from a file" while it is shown, or <see langword="null"/> (a new one is made for each file).</summary>
    public InsertFileViewModel? InsertFile => _insertFile;

    /// <summary>
    /// Asks for a file to insert into the plan of <paramref name="tab"/> and shows the dialog for it. A tab that already has the
    /// most files gets the notice of the pages bar instead, before the file dialog.
    /// </summary>
    internal async Task InsertFileAsync(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (_disposed || _picking || Tabs.IndexOf(tab) < 0 || !tab.IsOrganizing)
        {
            return;
        }

        if (!tab.CanInsertAnotherFile)
        {
            tab.RefuseAnotherFile();
            return;
        }

        var path = await PickFileToInsertAsync(tab);
        if (path is not null && !_disposed && Tabs.IndexOf(tab) >= 0 && tab.IsOrganizing)
        {
            ShowInsertDialog(tab, path);
        }
    }

    private async Task<string?> PickFileToInsertAsync(TabViewModel tab)
    {
        _picking = true;
        try
        {
            return await _picker.PickPdfAsync();
        }
        catch (OperationCanceledException)
        {
            // Treated like a cancelled dialog.
            return null;
        }
        catch (Exception)
        {
            // The dialog itself failed: the user is told, in the pages bar, that nothing was inserted.
            tab.ReportInsertPickFailure();
            return null;
        }
        finally
        {
            _picking = false;
        }
    }

    // A new dialog for the file: the one that is shown (for "Choose another file") goes first and closes its file.
    private void ShowInsertDialog(TabViewModel tab, string path)
    {
        if (Overlay == OverlayKind.InsertFile)
        {
            HideOverlay();
        }

        var dialog = new InsertFileViewModel(path, SaveEngine, tab.TryInsertFile, () => tab.InsertPositionText);
        dialog.Cancelled += HideOverlay;
        dialog.Inserted += HideOverlay;
        dialog.ChooseOtherRequested += () => _ = ChooseOtherFileToInsertAsync(tab);

        // The selection of the list decides where the pages go; the dialog says it and follows changes.
        _insertTabHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(TabViewModel.SelectedPagePositions) or nameof(TabViewModel.PagePlan))
            {
                dialog.RefreshPosition();
            }
        };
        tab.PropertyChanged += _insertTabHandler;
        _insertFile = dialog;
        _insertTab = tab;
        ShowOverlay(OverlayKind.InsertFile);
        _ = dialog.OpenAsync();
    }

    private async Task ChooseOtherFileToInsertAsync(TabViewModel tab)
    {
        if (_picking || !ReferenceEquals(_insertTab, tab))
        {
            return;
        }

        // Cancelling the second dialog keeps the first one as it was.
        var path = await PickFileToInsertAsync(tab);
        if (path is not null && !_disposed && ReferenceEquals(_insertTab, tab) && Tabs.IndexOf(tab) >= 0 && tab.IsOrganizing)
        {
            ShowInsertDialog(tab, path);
        }
    }

    /// <summary>The dialog goes when its tab leaves the mode, leaves the window or is not in front any more.</summary>
    private void CancelInsertOf(TabViewModel? tab)
    {
        if (Overlay == OverlayKind.InsertFile && (tab is null || ReferenceEquals(_insertTab, tab)))
        {
            HideOverlay();
        }
    }

    /// <summary>Lets go of the dialog and the file it opened (the overlay went).</summary>
    private void DisposeInsertFile()
    {
        if (_insertTab is { } tab && _insertTabHandler is { } handler)
        {
            tab.PropertyChanged -= handler;
        }

        _insertTabHandler = null;
        _insertTab = null;
        var dialog = _insertFile;
        _insertFile = null;
        dialog?.Dispose();
    }
}
