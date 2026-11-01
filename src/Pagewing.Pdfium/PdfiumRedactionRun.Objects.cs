using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>An image object that the page still has to show after the edits, to tell afterwards which one PDFium did not write.</summary>
internal readonly record struct ExpectedImage(long Key, UserRect Bounds, long Width, long Height);

/// <summary>
/// The part of the redaction run that deals with objects other than text: images, paths and form objects, planned and applied as edits
/// of the page. A form object that holds something under a rectangle is taken apart on the page (its children go to the page with the
/// matrix <c>child × form</c> at the place of the form), because PDFium does not write changes of a form object back to its XObject.
/// </summary>
internal sealed partial class PdfiumRedactionRun
{
    /// <summary>Bits of a key that hold the position of a child inside a form object (the object index of the page sits above them).</summary>
    private const int KeyShift = 24;

    /// <summary>The key of an object: the index of the page object, and for a child of a form object its position (0 or more) among the children that the form is taken apart into.</summary>
    internal static long KeyOf(int index, int leaf) => ((long)index << KeyShift) | (uint)(leaf + 1);

    private enum EditKind
    {
        Text,
        TextWithoutChars,
        Form,
        Flatten,
        ImageOverwrite,
        ImageWhole,
        PathStrips,
        PathWhole,
    }

    private sealed class Edit(long key, int index, nint handle, EditKind kind)
    {
        public long Key { get; } = key;

        public int Index { get; set; } = index;

        public nint Handle { get; } = handle;

        public EditKind Kind { get; } = kind;

        public int[] Chars { get; init; } = [];

        public string? Reason { get; set; }

        public string? WholeText { get; set; }

        public bool Skip { get; set; }

        public int WholeLength { get; set; }

        /// <summary>Where the object is (user space), for the check of the written page when the object is removed as a whole.</summary>
        public UserRect? Region { get; set; }

        public List<nint> Texts { get; init; } = [];

        /// <summary>The pixels of an image that lie under the rectangles (empty for an image that is only replaced).</summary>
        public List<PixelBox> Boxes { get; init; } = [];

        public int ImageWidth { get; init; }

        public int ImageHeight { get; init; }

        /// <summary>Whether the image is replaced by a new image object because PDFium would drop it when it writes the page.</summary>
        public bool Rescue { get; init; }

        /// <summary>The rectangles that stay of a filled rectangle path.</summary>
        public List<UserRect> Pieces { get; init; } = [];

        public int FillMode { get; init; }

        public uint[] Fill { get; init; } = [0, 0, 0, 255];

        /// <summary>For a form object that is taken apart: its children in drawing order.</summary>
        public List<Leaf> Leaves { get; init; } = [];
    }

    /// <summary>A child of a form object that is taken apart.</summary>
    private sealed class Leaf(nint handle, nint parent, int type, Affine? composed, Affine accum, UserRect bounds)
    {
        public nint Handle { get; } = handle;

        /// <summary>The form object that holds it now (the form itself or a nested one).</summary>
        public nint Parent { get; } = parent;

        public int Type { get; } = type;

        /// <summary>The matrix <c>child × form</c> through all levels, or <see langword="null"/> when a matrix could not be read.</summary>
        public Affine? Composed { get; } = composed;

        /// <summary>The matrix of the parent form objects up to the page (without the matrix of the child itself).</summary>
        public Affine Accum { get; } = accum;

        /// <summary>The bounds in page space.</summary>
        public UserRect Bounds { get; } = bounds;

        public Edit? Edit { get; set; }
    }

    private sealed class FormTree
    {
        public Edit? Edit { get; set; }

        public string? Cannot { get; set; }

        public UserRect? Bounds { get; set; }

        public List<Leaf> Leaves { get; } = [];

        public List<nint> TextHandles { get; } = [];
    }

    private sealed class PlanContext
    {
        public required PdfiumRedaction.RawPage Raw { get; init; }

        public required nint NativeDocument { get; init; }

        public required nint[] Handles { get; init; }

        public required int[] Types { get; init; }

        public required int Count { get; init; }

        public required CharTable Table { get; init; }

        public required bool[] Hit { get; init; }

        public required Dictionary<nint, List<int>> ByObject { get; init; }

        public required AreaIndex Areas { get; init; }

        public required List<UserRect> UserAreas { get; init; }

        public required IReadOnlyDictionary<long, string> Forced { get; init; }

        public required List<Edit> Edits { get; init; }

        public required List<nint> Unknown { get; init; }

        public required PassState Pass { get; init; }

        public required bool Scratch { get; init; }

        public required double PageArea { get; init; }

        /// <summary>The images the page shows once all edits are done.</summary>
        public List<ExpectedImage> Expected { get; } = [];

        public int Visits { get; set; }
    }

    private static IEnumerable<Edit> AllEdits(IEnumerable<Edit> edits)
    {
        foreach (var edit in edits)
        {
            yield return edit;
            foreach (var leaf in edit.Leaves)
            {
                if (leaf.Edit is not null)
                {
                    yield return leaf.Edit;
                }
            }
        }
    }

    private static void NoteRegion(PassState pass, Edit edit)
    {
        if (edit.Region is { } region)
        {
            pass.Removed.Add(region);
        }
    }

    /// <summary>Notes where characters were removed: runs of consecutive characters, each as one rectangle (the page may differ there).</summary>
    private static void NoteChars(PassState pass, CharTable table, IEnumerable<int> chars)
    {
        UserRect? current = null;
        foreach (var c in chars)
        {
            if (!table.BoxUsable(c))
            {
                continue;
            }

            var box = table.Box(c);
            var height = box.Top - box.Bottom;
            if (current is { } run && run.Bottom < box.Top && box.Bottom < run.Top && box.Left <= run.Right + height && box.Right >= run.Left - height)
            {
                current = new UserRect(Math.Min(run.Left, box.Left), Math.Min(run.Bottom, box.Bottom), Math.Max(run.Right, box.Right), Math.Max(run.Top, box.Top));
            }
            else
            {
                if (current is { } done)
                {
                    pass.Removed.Add(done);
                }

                current = box;
            }
        }

        if (current is { } last)
        {
            pass.Removed.Add(last);
        }
    }

    /// <summary>The text of what is removed as a whole (the text page must be open).</summary>
    private static void ReadWholeText(PdfiumRedaction.RawPage raw, nint textPage, Edit edit)
    {
        if (edit.Kind == EditKind.TextWithoutChars)
        {
            var text = raw.ReadText(edit.Handle, textPage);
            edit.Reason = "the text object has no characters PDFium can read";
            edit.WholeText = text;
            edit.WholeLength = text?.Length ?? 0;

            // A text object that shows nothing (no text, or only white space) has nothing to remove.
            edit.Skip = text is not null && text.Trim().Length == 0;
        }
        else if (edit.Kind == EditKind.Form)
        {
            var length = 0;
            var parts = new List<string>();
            foreach (var text in edit.Texts)
            {
                var value = raw.ReadText(text, textPage);
                if (value is null)
                {
                    edit.Reason += "; a text in it is longer than the limit and is not listed";
                    continue;
                }

                length += value.Length;
                if (value.Trim().Length > 0)
                {
                    parts.Add(value);
                }
            }

            edit.WholeText = string.Join(' ', parts);
            edit.WholeLength = length;
        }
    }

    private static Edit Whole(EditKind kind, long key, int index, nint handle, string reason, UserRect region) => new(key, index, handle, kind) { Reason = reason, Region = region };

    /// <summary>Plans what happens to the objects of the page that are not hit text objects.</summary>
    private IEnumerable<bool> PlanObjects(PlanContext c)
    {
        var cannot = new List<FormTree>();
        var located = new HashSet<nint>();
        for (var i = 0; i < c.Count; i++)
        {
            var handle = c.Handles[i];
            switch (c.Types[i])
            {
                case FpdfConstants.PageObjText:
                    if (!c.ByObject.ContainsKey(handle))
                    {
                        // Text objects the text page knows no character of: removed as a whole when they touch a rectangle.
                        if (!PdfiumRedactionNative.TryGetBounds(handle, out var bounds) || seam?.BoundsUnreadable?.Invoke(i, c.Scratch) == true)
                        {
                            c.Pass.Refusal = "The bounds of a text object could not be read, so it cannot be told whether it is under a rectangle.";
                            yield break;
                        }

                        if (c.Areas.Hit(bounds))
                        {
                            c.Edits.Add(new Edit(KeyOf(i, -1), i, handle, EditKind.TextWithoutChars) { Region = bounds });
                        }
                    }

                    break;
                case FpdfConstants.PageObjImage:
                    if (!PdfiumRedactionNative.TryGetBounds(handle, out var imageBounds))
                    {
                        c.Pass.Refusal = "The bounds of an image could not be read, so it cannot be told whether it is under a rectangle.";
                        yield break;
                    }

                    var imageMatrix = PdfiumRedactionObjects.TryGetMatrix(handle, out var m) ? m : (Affine?)null;
                    var image = PlanImage(c, KeyOf(i, -1), i, handle, imageBounds, imageMatrix, c.Expected);
                    if (c.Pass.Refusal is not null)
                    {
                        yield break;
                    }

                    if (image is not null)
                    {
                        c.Edits.Add(image);
                    }

                    break;
                case FpdfConstants.PageObjPath:
                    if (!PdfiumRedactionNative.TryGetBounds(handle, out var pathBounds))
                    {
                        c.Pass.Refusal = "The bounds of a path could not be read, so it cannot be told whether it is under a rectangle.";
                        yield break;
                    }

                    var path = PlanPath(c, KeyOf(i, -1), i, handle, pathBounds, Affine.Identity);
                    if (c.Pass.Refusal is not null)
                    {
                        yield break;
                    }

                    if (path is not null)
                    {
                        c.Edits.Add(path);
                    }

                    break;
                case FpdfConstants.PageObjForm:
                    var known = PdfiumRedactionNative.TryGetBounds(handle, out var formBounds);
                    if (known && !c.Areas.Hit(formBounds))
                    {
                        break;
                    }

                    var tree = new FormTree { Bounds = known ? formBounds : null };
                    foreach (var step in Traverse(c, tree, i, handle))
                    {
                        yield return step;
                    }

                    if (c.Pass.Refusal is not null)
                    {
                        yield break;
                    }

                    if (tree.Edit is not null)
                    {
                        c.Edits.Add(tree.Edit);
                        if (tree.Edit.Kind == EditKind.Flatten)
                        {
                            foreach (var leaf in tree.Leaves)
                            {
                                if (leaf.Edit is { Kind: EditKind.Text })
                                {
                                    located.Add(leaf.Handle);
                                }
                            }
                        }
                        else
                        {
                            foreach (var text in tree.TextHandles)
                            {
                                located.Add(text);
                            }

                            cannot.Add(tree);
                        }
                    }

                    break;
                default:
                    break;
            }

            if ((i + 1) % (SliceObjects * 8) == 0)
            {
                yield return true;
            }
        }

        // Hit text that is not in a text object of the page must be in a form object that is taken apart or removed.
        foreach (var obj in c.Unknown)
        {
            if (located.Contains(obj))
            {
                continue;
            }

            var chars = c.ByObject[obj];
            var inRemoved = cannot.Any(t => t.Bounds is not { } bounds || chars.Any(ch => c.Hit[ch] && c.Table.Box(ch).Touches(bounds)));
            if (!inRemoved)
            {
                c.Pass.Refusal = "A text object under a rectangle could not be located on the page.";
                yield break;
            }
        }
    }

    /// <summary>
    /// Plans a form object that touches a rectangle: reads all its children (nested form objects in place), decides what happens to each,
    /// and takes the form apart only if something in it is under a rectangle. A form that cannot be read to the end is removed as a whole.
    /// </summary>
    private static IEnumerable<bool> Traverse(PlanContext c, FormTree tree, int index, nint form)
    {
        var raw = c.Raw;
        if (!PdfiumRedactionObjects.TryGetMatrix(form, out var top))
        {
            tree.Cannot = "the matrix of the form object could not be read";
        }
        else
        {
            var stack = new Stack<Frame>();
            var count = raw.CountFormObjects(form);
            if (CountProblem(count) is { } problem)
            {
                tree.Cannot = problem;
            }
            else
            {
                stack.Push(new Frame(form, top, 0, count));
            }

            while (stack.Count > 0 && tree.Cannot is null)
            {
                var frame = stack.Peek();
                if (frame.Next >= frame.Count)
                {
                    stack.Pop();
                    continue;
                }

                var child = raw.GetFormObject(frame.Form, frame.Next++);
                if (++c.Visits > PdfiumRedaction.MaxObjects)
                {
                    c.Pass.Refusal = $"The form objects of the page hold more than {PdfiumRedaction.MaxObjects} objects.";
                    yield break;
                }

                if (child == 0)
                {
                    tree.Cannot = "an object of it could not be read";
                    break;
                }

                var type = Fpdf.FPDFPageObj_GetType(child);
                if (type == FpdfConstants.PageObjForm)
                {
                    if (frame.Depth + 1 >= PdfiumRedaction.MaxFormDepth)
                    {
                        tree.Cannot = $"it nests form objects deeper than {PdfiumRedaction.MaxFormDepth} levels";
                        break;
                    }

                    var nestedCount = raw.CountFormObjects(child);
                    if (!PdfiumRedactionObjects.TryGetMatrix(child, out var nested))
                    {
                        tree.Cannot = "the matrix of a nested form object could not be read";
                    }
                    else if (CountProblem(nestedCount) is { } nestedProblem)
                    {
                        tree.Cannot = nestedProblem;
                    }
                    else
                    {
                        stack.Push(new Frame(child, nested.Then(frame.Accum), frame.Depth + 1, nestedCount));
                    }

                    continue;
                }

                var own = PdfiumRedactionObjects.TryGetMatrix(child, out var matrix) ? matrix : (Affine?)null;
                if (!PdfiumRedactionNative.TryGetBounds(child, out var local))
                {
                    tree.Cannot = "the bounds of an object in it could not be read";
                    break;
                }

                var leaf = new Leaf(child, frame.Form, type, own?.Then(frame.Accum), frame.Accum, frame.Accum.Bounds(local));
                tree.Leaves.Add(leaf);
                if (type == FpdfConstants.PageObjText)
                {
                    tree.TextHandles.Add(child);
                }

                if ((c.Visits & (SliceObjects - 1)) == 0)
                {
                    yield return true;
                }
            }
        }

        var key = KeyOf(index, -1);
        if (tree.Cannot is not null)
        {
            tree.Edit = new Edit(key, index, form, EditKind.Form)
            {
                Reason = $"the form object is too large to take apart ({tree.Cannot})",
                Texts = tree.TextHandles,
                Region = tree.Bounds,
            };
            yield break;
        }

        // What is under a rectangle inside the form.
        var expected = new List<ExpectedImage>();
        var movable = true;
        var any = false;
        for (var j = 0; j < tree.Leaves.Count; j++)
        {
            var leaf = tree.Leaves[j];
            var leafKey = KeyOf(index, j);
            if (leaf.Composed is null)
            {
                movable = false;
            }

            switch (leaf.Type)
            {
                case FpdfConstants.PageObjText:
                    if (c.ByObject.TryGetValue(leaf.Handle, out var chars))
                    {
                        if (chars.Any(ch => c.Hit[ch] && !IsWhite(c.Table.Unicode[ch])))
                        {
                            leaf.Edit = new Edit(leafKey, index, leaf.Handle, EditKind.Text) { Chars = [.. chars] };
                        }
                    }
                    else if (c.Areas.Hit(leaf.Bounds))
                    {
                        leaf.Edit = new Edit(leafKey, index, leaf.Handle, EditKind.TextWithoutChars) { Region = leaf.Bounds };
                    }

                    break;
                case FpdfConstants.PageObjImage:
                    leaf.Edit = PlanImage(c, leafKey, index, leaf.Handle, leaf.Bounds, leaf.Composed, expected);
                    break;
                case FpdfConstants.PageObjPath:
                    // The path reads its own matrix itself: it gets the matrix of its parents only.
                    leaf.Edit = leaf.Composed is not null ? PlanPath(c, leafKey, index, leaf.Handle, leaf.Bounds, leaf.Accum) : null;
                    break;
                default:
                    movable &= leaf.Composed is not null;
                    break;
            }

            if (c.Pass.Refusal is not null)
            {
                yield break;
            }

            any |= leaf.Edit is not null;
            if ((j & (SliceObjects - 1)) == SliceObjects - 1)
            {
                yield return true;
            }
        }

        if (!any)
        {
            yield break;
        }

        if (!movable)
        {
            tree.Edit = new Edit(key, index, form, EditKind.Form)
            {
                Reason = "the form object cannot be taken apart (an object in it has no matrix PDFium can read)",
                Texts = tree.TextHandles,
                Region = tree.Bounds,
            };
            yield break;
        }

        tree.Edit = new Edit(key, index, form, EditKind.Flatten) { Leaves = tree.Leaves };
        c.Expected.AddRange(expected);
    }

    private static string? CountProblem(int count) =>
        count < 0 ? "its objects could not be read"
        : count > PdfiumRedaction.MaxFormChildren ? $"it holds {count} objects, at most {PdfiumRedaction.MaxFormChildren} are searched"
        : null;

    private sealed class Frame(nint form, Affine accum, int depth, int count)
    {
        public nint Form { get; } = form;

        public Affine Accum { get; } = accum;

        public int Depth { get; } = depth;

        public int Count { get; } = count;

        public int Next { get; set; }
    }

    /// <summary>The edit for an image: overwrite the pixels under the rectangles, remove it as a whole, replace it by a new image object, or nothing.</summary>
    private static Edit? PlanImage(PlanContext c, long key, int index, nint handle, UserRect pageBounds, Affine? composed, List<ExpectedImage> expected)
    {
        var haveSize = PdfiumRedactionObjects.TryGetPixelSize(handle, out var width, out var height);
        var touches = c.Areas.Hit(pageBounds);
        var rescue = c.Forced.ContainsKey(key);
        var shown = composed is { } m ? m.Bounds(new UserRect(0, 0, 1, 1)) : pageBounds;
        if (touches)
        {
            if (!haveSize || composed is null)
            {
                return Whole(EditKind.ImageWhole, key, index, handle, "the size or the matrix of the image could not be read", pageBounds);
            }

            if (!composed.Value.IsAxisParallel)
            {
                return Whole(EditKind.ImageWhole, key, index, handle, "the image is turned or sheared, so its pixels under the rectangle cannot be told apart", pageBounds);
            }

            if (width * height > PdfiumRedaction.MaxImagePixels)
            {
                c.Pass.Refusal = $"An image under a rectangle has {width} x {height} pixels; at most {PdfiumRedaction.MaxImagePixels} pixels are handled.";
                return null;
            }

            var boxes = new List<PixelBox>();
            foreach (var area in c.UserAreas)
            {
                if (area.Touches(pageBounds) && PdfiumRedactionObjects.TryPixelBox(composed.Value, (int)width, (int)height, area, out var box))
                {
                    boxes.Add(box);
                }
            }

            if (boxes.Count > 0)
            {
                expected.Add(new ExpectedImage(key, shown, width, height));
                return new Edit(key, index, handle, EditKind.ImageOverwrite) { Boxes = boxes, ImageWidth = (int)width, ImageHeight = (int)height, Rescue = rescue, Region = pageBounds };
            }
        }

        if (haveSize)
        {
            expected.Add(new ExpectedImage(key, shown, width, height));
        }

        if (!rescue)
        {
            return null;
        }

        if (!haveSize || width * height > PdfiumRedaction.MaxImagePixels || composed is null)
        {
            c.Pass.Refusal = "An image that PDFium would drop when it writes the page again cannot be kept (its size or matrix is unreadable, or it is too large).";
            return null;
        }

        return new Edit(key, index, handle, EditKind.ImageOverwrite) { ImageWidth = (int)width, ImageHeight = (int)height, Rescue = true, Region = pageBounds };
    }

    /// <summary>The edit for a path that touches a rectangle: cut a filled rectangle into the pieces outside, remove anything else as a whole, or refuse a large shape.</summary>
    private static Edit? PlanPath(PlanContext c, long key, int index, nint handle, UserRect pageBounds, Affine accum)
    {
        if (!c.Areas.Hit(pageBounds))
        {
            return null;
        }

        var big = PdfiumRedactionObjects.IsFilled(handle) && (pageBounds.Right - pageBounds.Left) * (pageBounds.Top - pageBounds.Bottom) > PdfiumRedaction.LargeShapeShare * c.PageArea;
        var rectangle = PdfiumRedactionObjects.ReadFilledRectangle(handle, accum);
        if (rectangle is not null)
        {
            var holes = new List<UserRect>();
            foreach (var area in c.UserAreas)
            {
                if (area.Left < rectangle.Rect.Right && rectangle.Rect.Left < area.Right && area.Bottom < rectangle.Rect.Top && rectangle.Rect.Bottom < area.Top)
                {
                    holes.Add(area);
                }
            }

            if (holes.Count == 0)
            {
                return null;
            }

            var pieces = PdfiumRedactionObjects.Subtract(rectangle.Rect, holes, PdfiumRedaction.MaxPathPieces);
            if (pieces is not null)
            {
                return new Edit(key, index, handle, EditKind.PathStrips) { Pieces = pieces, FillMode = rectangle.FillMode, Fill = rectangle.Fill, Region = pageBounds };
            }

            if (big)
            {
                c.Pass.Refusal = $"A shape that covers more than half of the page would be cut into more than {PdfiumRedaction.MaxPathPieces} pieces by the rectangles.";
                return null;
            }

            return Whole(EditKind.PathWhole, key, index, handle, $"cutting the rectangle around the areas would leave more than {PdfiumRedaction.MaxPathPieces} pieces", pageBounds);
        }

        if (big)
        {
            c.Pass.Refusal = "A shape that covers more than half of the page and is not a plain filled rectangle is under a rectangle; it cannot be cut and is never removed as a whole.";
            return null;
        }

        return Whole(EditKind.PathWhole, key, index, handle, "the path is not a plain filled rectangle (a curve, a turned or sheared shape, a stroke or another outline)", pageBounds);
    }

    /// <summary>Applies one edit to the page (children of a form object that is taken apart are applied from the last to the first).</summary>
    private void ApplyEdit(
        PdfiumRedaction.RawPage raw,
        nint nativeDocument,
        Edit edit,
        CharTable table,
        bool[] hit,
        IReadOnlyDictionary<long, string> forced,
        PassState pass,
        List<(long Key, int[] Survivors)> splitChecks)
    {
        switch (edit.Kind)
        {
            case EditKind.Form:
                RemoveWhole(raw, edit);
                NoteRegion(pass, edit);
                pass.FormsTouched++;
                pass.CharsRemoved += edit.WholeLength;
                pass.Fallbacks.Add(new PdfiumFallback(PdfiumFallbackKind.FormObjectRemovedWhole, edit.Reason ?? "removed as a whole"));
                if (!string.IsNullOrWhiteSpace(edit.WholeText))
                {
                    pass.RemovedText.Add(edit.WholeText);
                }

                break;
            case EditKind.TextWithoutChars:
                RemoveWhole(raw, edit);
                NoteRegion(pass, edit);
                pass.RemovedWhole++;
                pass.CharsRemoved += edit.WholeLength;
                pass.Fallbacks.Add(new PdfiumFallback(PdfiumFallbackKind.TextObjectRemovedWhole, edit.Reason ?? "removed as a whole"));
                if (!string.IsNullOrWhiteSpace(edit.WholeText))
                {
                    pass.RemovedText.Add(edit.WholeText);
                }

                break;
            case EditKind.ImageWhole:
                RemoveWhole(raw, edit);
                NoteRegion(pass, edit);
                pass.ImagesRemovedWhole++;
                pass.Fallbacks.Add(new PdfiumFallback(PdfiumFallbackKind.ImageRemovedWhole, edit.Reason ?? "removed as a whole"));
                break;
            case EditKind.PathWhole:
                RemoveWhole(raw, edit);
                NoteRegion(pass, edit);
                pass.PathsRemovedWhole++;
                pass.Fallbacks.Add(new PdfiumFallback(PdfiumFallbackKind.PathRemovedWhole, edit.Reason ?? "removed as a whole"));
                break;
            case EditKind.ImageOverwrite:
                ApplyImage(raw, nativeDocument, edit, pass);
                break;
            case EditKind.PathStrips:
                ApplyPath(raw, edit, pass);
                break;
            case EditKind.Flatten:
                ApplyFlatten(raw, nativeDocument, edit, table, hit, forced, pass, splitChecks);
                break;
            default:
                EditText(raw, nativeDocument, edit, table, hit, forced, pass, splitChecks);
                break;
        }
    }

    private void ApplyFlatten(
        PdfiumRedaction.RawPage raw,
        nint nativeDocument,
        Edit form,
        CharTable table,
        bool[] hit,
        IReadOnlyDictionary<long, string> forced,
        PassState pass,
        List<(long Key, int[] Survivors)> splitChecks)
    {
        // The form leaves the page, its children come back one by one at its place with the matrix child x form.
        if (!raw.Remove(form.Handle))
        {
            throw new InvalidOperationException("PDFium could not take a form object out of the page.");
        }

        for (var j = 0; j < form.Leaves.Count; j++)
        {
            var leaf = form.Leaves[j];
            if (!raw.RemoveFromForm(leaf.Parent, leaf.Handle))
            {
                throw new InvalidOperationException("PDFium could not take an object out of a form object.");
            }

            if (!PdfiumRedactionObjects.TrySetMatrix(leaf.Handle, leaf.Composed ?? throw new InvalidOperationException("A matrix is missing.")))
            {
                throw new InvalidOperationException("PDFium could not set the matrix of an object of a form object.");
            }

            if (!raw.InsertAt(leaf.Handle, form.Index + j))
            {
                throw new InvalidOperationException("PDFium could not put an object of a form object on the page.");
            }

            if (leaf.Edit is { } edit)
            {
                edit.Index = form.Index + j;
            }
        }

        raw.Destroy(form.Handle);
        pass.FormsTouched++;
        for (var j = form.Leaves.Count - 1; j >= 0; j--)
        {
            if (form.Leaves[j].Edit is { Skip: false } edit)
            {
                ApplyEdit(raw, nativeDocument, edit, table, hit, forced, pass, splitChecks);
            }
        }
    }

    private void ApplyImage(PdfiumRedaction.RawPage raw, nint nativeDocument, Edit edit, PassState pass)
    {
        var page = raw.Access();
        var width = edit.ImageWidth;
        var height = edit.ImageHeight;
        if (!PdfiumRedactionObjects.TryReadPixels(nativeDocument, page, edit.Handle, width, height, out var pixels)
            || !PdfiumRedactionObjects.TryGetMatrix(edit.Handle, out var matrix))
        {
            RemoveWhole(raw, edit);
            NoteRegion(pass, edit);
            pass.ImagesRemovedWhole++;
            pass.Fallbacks.Add(new PdfiumFallback(PdfiumFallbackKind.ImageRemovedWhole, "the pixels of the image could not be read"));
            return;
        }

        if (edit.Boxes.Count > 0)
        {
            var gray = fill == PdfiumRedactionFill.White ? (byte)255 : (byte)0;
            foreach (var box in edit.Boxes)
            {
                for (var y = box.Y0; y < box.Y1; y++)
                {
                    var row = ((long)y * width) + box.X0;
                    for (var x = 0; x < box.Width; x++)
                    {
                        var at = (row + x) * 4;
                        pixels[at] = gray;
                        pixels[at + 1] = gray;
                        pixels[at + 2] = gray;
                        pixels[at + 3] = 255;
                    }
                }
            }
        }

        if (edit.Rescue)
        {
            var replacement = PdfiumRedactionObjects.CreateImage(nativeDocument, width, height, pixels, matrix);
            if (replacement == 0)
            {
                RemoveWhole(raw, edit);
                NoteRegion(pass, edit);
                pass.ImagesRemovedWhole++;
                pass.Fallbacks.Add(new PdfiumFallback(PdfiumFallbackKind.ImageRemovedWhole, "PDFium could not make a new image object for it"));
                return;
            }

            if (!raw.Remove(edit.Handle))
            {
                raw.Destroy(replacement);
                throw new InvalidOperationException("PDFium could not remove a page object.");
            }

            raw.Destroy(edit.Handle);
            if (!raw.InsertAt(replacement, edit.Index))
            {
                raw.Destroy(replacement);
                throw new InvalidOperationException("PDFium could not insert a page object.");
            }
        }
        else if (edit.Boxes.Count > 0 && !PdfiumRedactionObjects.TryWritePixels(edit.Handle, width, height, pixels))
        {
            RemoveWhole(raw, edit);
            NoteRegion(pass, edit);
            pass.ImagesRemovedWhole++;
            pass.Fallbacks.Add(new PdfiumFallback(PdfiumFallbackKind.ImageRemovedWhole, "PDFium could not store the changed pixels"));
            return;
        }

        if (edit.Boxes.Count > 0)
        {
            pass.ImagesOverwritten++;

            // Whole pixels are overwritten: where an image has big pixels (a scan at low resolution) the overwritten cells reach out of
            // the rectangle by up to one pixel. The page differs there on purpose, so the render check has to leave those cells out too.
            foreach (var box in edit.Boxes)
            {
                pass.Removed.Add(matrix.Bounds(new UserRect(
                    (double)box.X0 / width,
                    1.0 - ((double)box.Y1 / height),
                    (double)box.X1 / width,
                    1.0 - ((double)box.Y0 / height))));
            }
        }
    }

    private static void ApplyPath(PdfiumRedaction.RawPage raw, Edit edit, PassState pass)
    {
        var created = new List<nint>(edit.Pieces.Count);
        foreach (var piece in edit.Pieces)
        {
            var rectangle = PdfiumRedactionObjects.CreateFilledRectangle(piece, edit.FillMode, edit.Fill);
            if (rectangle == 0)
            {
                foreach (var obj in created)
                {
                    raw.Destroy(obj);
                }

                RemoveWhole(raw, edit);
                NoteRegion(pass, edit);
                pass.PathsRemovedWhole++;
                pass.Fallbacks.Add(new PdfiumFallback(PdfiumFallbackKind.PathRemovedWhole, "PDFium could not make the pieces of the path"));
                return;
            }

            created.Add(rectangle);
        }

        if (!raw.Remove(edit.Handle))
        {
            foreach (var obj in created)
            {
                raw.Destroy(obj);
            }

            throw new InvalidOperationException("PDFium could not remove a page object.");
        }

        raw.Destroy(edit.Handle);
        for (var k = 0; k < created.Count; k++)
        {
            if (!raw.InsertAt(created[k], edit.Index + k))
            {
                for (var rest = k; rest < created.Count; rest++)
                {
                    raw.Destroy(created[rest]);
                }

                throw new InvalidOperationException("PDFium could not insert a page object.");
            }
        }

        pass.PathsCropped++;
    }
}
