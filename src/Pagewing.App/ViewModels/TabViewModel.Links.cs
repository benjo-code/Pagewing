using System.Windows.Input;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;

namespace Pagewing.App.ViewModels;

/// <summary>
/// Links of the document (a click in the document view, <see cref="FollowLink"/>): a page of the document is shown at
/// once, the view left behind goes into a history (<see cref="BackCommand"/>, <see cref="ForwardCommand"/>); an address
/// outside the document is only handed to the window, which asks the user first (<see cref="MainViewModel.PendingLink"/>).
/// The tab never opens an address itself.
/// </summary>
public sealed partial class TabViewModel
{
    /// <summary>The most views the history keeps in each direction; the oldest goes first.</summary>
    public const int MaxHistory = 50;

    private readonly RelayCommand _backCommand;
    private readonly RelayCommand _forwardCommand;

    // The views left by link jumps (the most recent last), and those left by going back.
    private readonly List<ViewState> _back = [];
    private readonly List<ViewState> _forward = [];

    /// <summary>
    /// Raised when a link of the document asks to open an address that <see cref="ExternalLink.TryParse"/> accepts. The
    /// window asks the user (<see cref="MainViewModel.PendingLink"/>); nothing else may act on it.
    /// </summary>
    internal event Action<ExternalLink>? ExternalLinkRequested;

    /// <summary>Raised when a link jumps to a page of the document; the document view shows it (the view left is in the history).</summary>
    internal event Action<PageTarget>? LinkJumpRequested;

    /// <summary>Raised when <see cref="BackCommand"/> or <see cref="ForwardCommand"/> returns to a view; the document view shows it.</summary>
    internal event Action<ViewState>? ViewRestoreRequested;

    /// <summary>
    /// Gets or sets how the document view that shows this tab reads where the document is shown now, or
    /// <see langword="null"/> while no view shows it (then a link to a page and the history do nothing).
    /// </summary>
    internal Func<ViewState?>? CurrentViewReader { get; set; }

    /// <summary>Alt+Left: back to the view before the last link jump (page and scroll position).</summary>
    public ICommand BackCommand { get; }

    /// <summary>Alt+Right: forward to the view left by <see cref="BackCommand"/>.</summary>
    public ICommand ForwardCommand { get; }

    /// <summary>Gets whether there is a view to go back to.</summary>
    public bool CanGoBack => _back.Count > 0;

    /// <summary>Gets whether there is a view to go forward to.</summary>
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>
    /// Follows a link of the document. A page of the document (<see cref="PageTarget"/>) is shown without asking, after
    /// the view left behind went into the history (which drops the views to go forward to); a target that is not a page
    /// of the document does nothing. An address (<see cref="UriTarget"/>) must pass <see cref="ExternalLink.TryParse"/>,
    /// else nothing happens; then the window is asked to confirm it (<see cref="ExternalLinkRequested"/>).
    /// </summary>
    public void FollowLink(PageLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (_disposed)
        {
            return;
        }

        switch (link.Target)
        {
            case PageTarget target:
                JumpTo(target);
                break;
            case UriTarget uri when ExternalLink.TryParse(uri.Url, out var external):
                ExternalLinkRequested?.Invoke(external);
                break;
        }
    }

    private void JumpTo(PageTarget target)
    {
        if (!IsReady || target.PageIndex < 0 || target.PageIndex >= PageCount || CurrentViewReader?.Invoke() is not { } current)
        {
            return;
        }

        Push(_back, current);
        _forward.Clear();
        RaiseHistoryChanged();
        LinkJumpRequested?.Invoke(target);
    }

    private void GoBack() => Step(_back, _forward);

    private void GoForward() => Step(_forward, _back);

    /// <summary>Returns to the last view of <paramref name="from"/>; the view left goes onto <paramref name="to"/>.</summary>
    private void Step(List<ViewState> from, List<ViewState> to)
    {
        if (from.Count == 0 || !IsReady || CurrentViewReader?.Invoke() is not { } current)
        {
            return;
        }

        var view = from[^1];
        from.RemoveAt(from.Count - 1);
        Push(to, current);
        RaiseHistoryChanged();
        ViewRestoreRequested?.Invoke(view);
    }

    private static void Push(List<ViewState> list, ViewState view)
    {
        if (list.Count >= MaxHistory)
        {
            list.RemoveAt(0);
        }

        list.Add(view);
    }

    private void ClearHistory()
    {
        if (_back.Count == 0 && _forward.Count == 0)
        {
            return;
        }

        _back.Clear();
        _forward.Clear();
        RaiseHistoryChanged();
    }

    private void RaiseHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        _backCommand.RaiseCanExecuteChanged();
        _forwardCommand.RaiseCanExecuteChanged();
    }
}
