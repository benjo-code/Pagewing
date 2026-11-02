using Avalonia.Threading;
using Pagewing.App.ViewModels;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Controls;

/// <summary>
/// The pages of files inserted into the plan (milestone 13, "Insert pages from a file"). Every inserted file has thumbnails of
/// its own (<see cref="InsertedSource.EnsureThumbnails"/>, made when one of its entries is first in view), so a page of another
/// file is drawn from that file at its real size, turned like any page, and not from the tab's own thumbnails. Only the entries
/// that are in view are planned, per file, nearest first; an entry shows the stand-in tile until its picture is there. The list
/// follows the files of the tab: it detaches from a file that is gone and empties the caches whenever the picture would be
/// another size (render scaling, column width).
/// </summary>
internal sealed partial class ThumbnailList
{
    // The thumbnails of the inserted files the list draws from, by the number of the source.
    private readonly Dictionary<int, ForeignAttachment> _foreign = [];

    // The pixel size the last plan asked for, per (source, page).
    private Dictionary<(int Source, int Page), (int Width, int Height)> _foreignPlanned = [];

    /// <summary>Gets how many inserted files the list draws thumbnails from now (for tests).</summary>
    internal int ForeignSourceCount => _foreign.Count;

    /// <summary>The size an entry of an inserted file has before it is turned: the size its file reported, else the A4 stand-in.</summary>
    private PdfPageSize ForeignFormat(PageSlot slot)
    {
        if (_tab is { } tab && slot.SourceIndex >= 0 && tab.InsertedSources.TryGetValue(slot.Source, out var inserted)
            && slot.SourceIndex < inserted.Sizes.Count && IsUsable(inserted.Sizes[slot.SourceIndex]))
        {
            return inserted.Sizes[slot.SourceIndex];
        }

        return StandInSize;
    }

    /// <summary>The files of the tab changed (one went in or out, or more of their page sizes are known): the entries take their sizes, the next plan draws them.</summary>
    private void OnInsertedChanged()
    {
        if (!_planMode || _planItems is null || _tab is null)
        {
            return;
        }

        PruneForeign();
        foreach (var item in _planItems)
        {
            if (item.Slot is { Blank: null, Source.Value: not 0 } slot)
            {
                item.SetFormat(FormatOf(slot));
            }
        }

        SchedulePlan();
    }

    /// <summary>Hands the thumbnails of the entries in view that belong to inserted files to the thumbnails of those files.</summary>
    private void PlanForeign(List<int> realized)
    {
        var tab = _tab!;
        if (tab.InsertedSources.Count == 0 && _foreign.Count == 0)
        {
            // The usual case, and the list of a document: nothing of another file.
            return;
        }

        var pagesBySource = new Dictionary<int, List<int>>();
        foreach (var index in realized)
        {
            if (_items[index].ForeignKey is { } key)
            {
                if (!pagesBySource.TryGetValue(key.Source, out var pages))
                {
                    pagesBySource[key.Source] = pages = [];
                }

                pages.Add(key.Page);
            }
        }

        PruneForeign();
        var planned = new Dictionary<(int Source, int Page), (int Width, int Height)>();
        foreach (var (id, inserted) in tab.InsertedSources)
        {
            if (!pagesBySource.TryGetValue(id.Value, out var pages))
            {
                // Nothing of this file in view: its work stops.
                if (_foreign.TryGetValue(id.Value, out var idle))
                {
                    idle.Source.Scheduler.Update([]);
                }

                continue;
            }

            if (inserted.EnsureThumbnails(tab.EffectivePageTheme) is not { } source)
            {
                continue;
            }

            AttachForeign(id.Value, source);

            // The page of this file nearest to the first one in view first.
            var first = pages[0];
            pages.Sort((a, b) =>
            {
                var byDistance = Math.Abs(a - first).CompareTo(Math.Abs(b - first));
                return byDistance != 0 ? byDistance : a.CompareTo(b);
            });
            var plan = ThumbnailPlanner.Plan(inserted.Sizes, pages, _renderScaling, _frameWidth);

            // An image of another size is from another scaling or width: it is rendered anew (the keys do not carry the size).
            if (plan.Any(request => source.Cache.TryGet(request.Key, out var cached) && (cached.Width, cached.Height) != (request.Rect.Width, request.Rect.Height)))
            {
                source.Scheduler.Update([]);
                source.Cache.Clear();
                foreach (var index in realized)
                {
                    if (_items[index].ForeignKey is { } shown && shown.Source == id.Value)
                    {
                        DropBitmap(index);
                    }
                }
            }

            source.Scheduler.Update(plan);
            foreach (var request in plan)
            {
                planned[(id.Value, request.Key.Page)] = (request.Rect.Width, request.Rect.Height);
            }
        }

        _foreignPlanned = planned;

        // The scheduler skips what the cache holds, so no TileReady comes for it.
        foreach (var index in realized)
        {
            if (_items[index].ForeignKey is { } key && _foreign.TryGetValue(key.Source, out var attachment) && NeedsBitmap(index)
                && attachment.Source.Cache.TryGet(KeyOf(key.Page), out var image) && IsForeignPlannedSize(key, image))
            {
                SetBitmap(index, image);
            }
        }
    }

    private bool IsForeignPlannedSize((int Source, int Page) key, RenderedImage image) =>
        _foreignPlanned.TryGetValue(key, out var size) && size == (image.Width, image.Height);

    private void AttachForeign(int id, ThumbnailSource source)
    {
        if (_foreign.TryGetValue(id, out var existing))
        {
            if (ReferenceEquals(existing.Source, source))
            {
                return;
            }

            existing.Detach();
        }

        // What is in a cache the list did not fill may be of another scaling or width (another window drew from it before).
        source.Scheduler.Update([]);
        source.Cache.Clear();
        var handler = new ForeignHandler(this, id, source);
        source.Scheduler.TileReady += handler.OnTileReady;
        _foreign[id] = new ForeignAttachment(source, handler);
    }

    /// <summary>Lets go of the thumbnails of files the tab does not hold any more (or holds with other thumbnails).</summary>
    private void PruneForeign()
    {
        if (_foreign.Count == 0)
        {
            return;
        }

        foreach (var (id, attachment) in _foreign.ToArray())
        {
            if (_tab is null || !_tab.InsertedSources.TryGetValue(new PageSourceId(id), out var inserted) || !ReferenceEquals(inserted.Thumbnails, attachment.Source))
            {
                attachment.Detach();
                _foreign.Remove(id);
            }
        }
    }

    private void StopForeign()
    {
        foreach (var attachment in _foreign.Values)
        {
            attachment.Source.Scheduler.Update([]);
        }

        _foreignPlanned = [];
    }

    private void ClearForeignCaches()
    {
        foreach (var attachment in _foreign.Values)
        {
            attachment.Source.Scheduler.Update([]);
            attachment.Source.Cache.Clear();
        }
    }

    private void DetachForeign()
    {
        foreach (var attachment in _foreign.Values)
        {
            attachment.Detach();
        }

        _foreign.Clear();
        _foreignPlanned = [];
    }

    /// <summary>A thumbnail of an inserted file is in its cache (UI thread, posted by <see cref="ForeignHandler"/>).</summary>
    private void ApplyForeignThumbnail(int id, ThumbnailSource source, TileKey key)
    {
        if (!_planMode || !_foreign.TryGetValue(id, out var attachment) || !ReferenceEquals(attachment.Source, source) || key.Page < 0)
        {
            return;
        }

        if (!source.Cache.TryGet(key, out var image))
        {
            return;
        }

        foreach (var container in GetRealizedContainers())
        {
            var index = IndexFromContainer(container);
            if (index < 0 || index >= _items.Count || _items[index].ForeignKey != (id, key.Page) || !NeedsBitmap(index))
            {
                continue;
            }

            if (IsForeignPlannedSize((id, key.Page), image))
            {
                SetBitmap(index, image);
            }
            else
            {
                // Perhaps rendered for another scaling, or no longer wanted: the next plan decides.
                SchedulePlan();
                break;
            }
        }
    }

    /// <summary>The thumbnails of one inserted file the list listens to.</summary>
    private sealed class ForeignAttachment(ThumbnailSource source, ForeignHandler handler)
    {
        public ThumbnailSource Source { get; } = source;

        public void Detach()
        {
            Source.Scheduler.TileReady -= handler.OnTileReady;
            Source.Scheduler.Update([]);
        }
    }

    /// <summary>The scheduler handler of the thumbnails of one inserted file.</summary>
    private sealed class ForeignHandler(ThumbnailList owner, int id, ThumbnailSource source)
    {
        // Pool thread: must not block and must never throw (it would end the process).
        public void OnTileReady(TileKey key)
        {
            try
            {
                Dispatcher.UIThread.Post(() => owner.ApplyForeignThumbnail(id, source, key), DispatcherPriority.Background);
            }
            catch (Exception)
            {
                // The dispatcher is shutting down: nobody draws any more.
            }
        }
    }
}
