using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>The pixels of an image that lie under a rectangle (blue, green, red, alpha; top row first). A size of 0 stands for an image that could not be read.</summary>
internal sealed record PdfiumImageAreaPixels(int Width, int Height, byte[] Bgra);

/// <summary>Reads what remains of images under a rectangle of a redacted document (for the check of the result).</summary>
internal static class PdfiumRedactionRead
{
    /// <summary>
    /// For every image of the page (also inside form objects) whose bounds touch the rectangle: the pixels under the rectangle, or all
    /// pixels when the image is turned or sheared (then it is not cut at the rectangle), or an entry of size 0 when they cannot be read.
    /// Must be called on the PDFium thread.
    /// </summary>
    public static List<PdfiumImageAreaPixels> ReadImageAreaPixels(PdfiumDocument document, int pageIndex, PdfiumRedactionArea area)
    {
        using var raw = PdfiumRedaction.OpenPage(document, pageIndex);
        var result = new List<PdfiumImageAreaPixels>();
        var userAreas = new List<UserRect>();
        if (!PdfiumRedactionRun.TryUserAreas(raw.Access(), [area], userAreas) || userAreas.Count == 0)
        {
            return result;
        }

        var user = userAreas[0];
        var count = raw.CountObjects();
        if (count > PdfiumRedaction.MaxObjects)
        {
            throw new InvalidOperationException($"The page has {count} objects; at most {PdfiumRedaction.MaxObjects} are handled.");
        }

        var visits = 0;
        for (var i = 0; i < count; i++)
        {
            var obj = raw.GetObject(i);
            var type = Fpdf.FPDFPageObj_GetType(obj);
            if (type == FpdfConstants.PageObjImage)
            {
                if (PdfiumRedactionNative.TryGetBounds(obj, out var bounds) && bounds.Touches(user))
                {
                    AddIfAny(result, ReadImage(raw, obj, PdfiumRedactionObjects.TryGetMatrix(obj, out var matrix) ? matrix : null, user));
                }
            }
            else if (type == FpdfConstants.PageObjForm && PdfiumRedactionNative.TryGetBounds(obj, out var formBounds) && formBounds.Touches(user)
                && PdfiumRedactionObjects.TryGetMatrix(obj, out var formMatrix))
            {
                ReadForm(raw, obj, formMatrix, 0, user, result, ref visits);
            }
        }

        return result;
    }

    private static void ReadForm(PdfiumRedaction.RawPage raw, nint form, Affine accum, int depth, UserRect user, List<PdfiumImageAreaPixels> result, ref int visits)
    {
        var count = raw.CountFormObjects(form);
        if (count < 0 || count > PdfiumRedaction.MaxFormChildren || depth + 1 >= PdfiumRedaction.MaxFormDepth)
        {
            result.Add(new PdfiumImageAreaPixels(0, 0, []));
            return;
        }

        for (var k = 0; k < count; k++)
        {
            if (++visits > PdfiumRedaction.MaxObjects)
            {
                result.Add(new PdfiumImageAreaPixels(0, 0, []));
                return;
            }

            var child = raw.GetFormObject(form, k);
            if (child == 0)
            {
                continue;
            }

            var type = Fpdf.FPDFPageObj_GetType(child);
            if (type == FpdfConstants.PageObjForm)
            {
                if (PdfiumRedactionObjects.TryGetMatrix(child, out var nested))
                {
                    ReadForm(raw, child, nested.Then(accum), depth + 1, user, result, ref visits);
                }
                else
                {
                    result.Add(new PdfiumImageAreaPixels(0, 0, []));
                }
            }
            else if (type == FpdfConstants.PageObjImage)
            {
                var composed = PdfiumRedactionObjects.TryGetMatrix(child, out var own) ? own.Then(accum) : (Affine?)null;
                var shown = composed is { } m ? m.Bounds(new UserRect(0, 0, 1, 1)) : (UserRect?)null;
                if (shown is null || shown.Value.Touches(user))
                {
                    AddIfAny(result, ReadImage(raw, child, composed, user));
                }
            }
        }
    }

    private static void AddIfAny(List<PdfiumImageAreaPixels> into, PdfiumImageAreaPixels? item)
    {
        if (item is not null)
        {
            into.Add(item);
        }
    }

    private static PdfiumImageAreaPixels? ReadImage(PdfiumRedaction.RawPage raw, nint image, Affine? matrix, UserRect user)
    {
        var unreadable = new PdfiumImageAreaPixels(0, 0, []);
        if (matrix is null || !PdfiumRedactionObjects.TryGetPixelSize(image, out var width, out var height) || width * height > PdfiumRedaction.MaxImagePixels)
        {
            return unreadable;
        }

        var w = (int)width;
        var h = (int)height;
        var turned = !matrix.Value.IsAxisParallel;
        PixelBox box = default;
        if (!turned && !PdfiumRedactionObjects.TryPixelBox(matrix.Value, w, h, user, out box))
        {
            // The bounds touch but no pixel lies under the rectangle.
            return null;
        }

        if (!PdfiumRedactionObjects.TryReadPixels(raw.NativeDocument, raw.Access(), image, w, h, out var pixels))
        {
            return unreadable;
        }

        if (turned)
        {
            return new PdfiumImageAreaPixels(w, h, pixels);
        }

        var part = new byte[(long)box.Width * box.Height * 4];
        for (var y = 0; y < box.Height; y++)
        {
            Buffer.BlockCopy(pixels, (int)((((long)(box.Y0 + y) * w) + box.X0) * 4), part, y * box.Width * 4, box.Width * 4);
        }

        return new PdfiumImageAreaPixels(box.Width, box.Height, part);
    }
}
