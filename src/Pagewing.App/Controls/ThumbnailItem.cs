using System.Globalization;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Controls;

/// <summary>What an entry of the page thumbnails shows (the plan of the mode "Organize pages" has all three).</summary>
public enum PageKind
{
    /// <summary>A page of the open document: its thumbnail is rendered.</summary>
    Page,

    /// <summary>A blank page the plan adds: drawn, never rendered.</summary>
    Blank,

    /// <summary>A page of another file inserted into the plan: a stand-in tile until the file has a thumbnail source of its own.</summary>
    Foreign,
}

/// <summary>
/// One entry of the page thumbnails: the page number, the size of its frame (136 DIP wide, in the density "Kompakt" as
/// wide as its column; as high as the page format asks) and the thumbnail once it is rendered. In the mode "Organize pages"
/// an entry stands for a position of the plan (<see cref="Slot"/>): its number is the position, it may be turned (the image
/// is drawn turned), blank or from another file, and it says so in words (<see cref="RotationText"/> and the other marks).
/// UI thread only.
/// </summary>
public sealed class ThumbnailItem : ViewModelBase
{
    /// <summary>The tallest frame, in widths: a degenerate page format must not make one entry thousands of DIPs high.</summary>
    private const double MaxAspect = 16;

    // The turns of an image, shared by all entries (a transform is never changed).
    private static readonly RotateTransform Turn90 = new(90);
    private static readonly RotateTransform Turn180 = new(180);
    private static readonly RotateTransform Turn270 = new(270);

    private PdfPageSize _format;
    private double _columnWidth;
    private double _frameWidth;
    private double _frameHeight;
    private double _imageWidth;
    private double _imageHeight;
    private Bitmap? _bitmap;
    private bool _isCurrent;
    private bool _isSelected;
    private int _number;
    private PageSlot? _slot;
    private PageKind _kind;
    private int _rotation;
    private ITransform? _imageTransform;

    internal ThumbnailItem(int pageNumber, PdfPageSize format, double frameWidth)
    {
        _number = pageNumber;
        RenderPage = pageNumber - 1;
        _format = format;
        _columnWidth = frameWidth;
        Relayout();
    }

    /// <summary>Gets the number shown below the frame, 1-based: the page of the document, in the plan the position.</summary>
    public int PageNumber
    {
        get => _number;
        private set
        {
            if (SetProperty(ref _number, value))
            {
                OnPropertyChanged(nameof(PageText));
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>Gets the page number as shown below the frame.</summary>
    public string PageText => PageNumber.ToString(CultureInfo.InvariantCulture);

    /// <summary>Gets the entry of the plan this stands for, or <see langword="null"/> outside the mode "Organize pages".</summary>
    public PageSlot? Slot
    {
        get => _slot;
        private set => SetProperty(ref _slot, value);
    }

    /// <summary>
    /// Gets the page of the open document whose thumbnail this entry draws (the key of the thumbnail in the cache), or -1 when
    /// nothing is rendered for it (a blank page, a page of another file).
    /// </summary>
    internal int RenderPage { get; private set; }

    /// <summary>Gets what the entry shows.</summary>
    public PageKind Kind
    {
        get => _kind;
        private set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsBlank));
                OnPropertyChanged(nameof(IsForeign));
                OnPropertyChanged(nameof(ShowsImage));
                OnPropertyChanged(nameof(IsDrawn));
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>Gets whether this is a blank page of the plan.</summary>
    public bool IsBlank => _kind == PageKind.Blank;

    /// <summary>Gets whether this is a page of another file.</summary>
    public bool IsForeign => _kind == PageKind.Foreign;

    /// <summary>Gets whether the entry draws a rendered thumbnail (a blank page and a page of another file draw a face of their own).</summary>
    public bool ShowsImage => _kind == PageKind.Page;

    /// <summary>Gets the rotation the plan adds to the page in degrees: 0, 90, 180 or 270.</summary>
    public int Rotation
    {
        get => _rotation;
        private set
        {
            if (SetProperty(ref _rotation, value))
            {
                OnPropertyChanged(nameof(IsRotated));
                OnPropertyChanged(nameof(RotationText));
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>Gets whether the plan turns the page.</summary>
    public bool IsRotated => _rotation != 0;

    /// <summary>Gets the words that say the page is turned, such as "Rotated 90°".</summary>
    public string RotationText => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.PageMarkerRotated), _rotation.ToString(CultureInfo.InvariantCulture));

    /// <summary>Gets the width of the column the entry is laid out for in DIPs: the frame of an upright page is as wide.</summary>
    public double ColumnWidth => _columnWidth;

    /// <summary>
    /// Gets the width of the frame in DIPs as the entry takes it: <see cref="ThumbnailPlanner.Width"/> or the column width of
    /// "Kompakt"; a page that is turned by 90 or 270 degrees takes the width its height has (never wider than the column).
    /// </summary>
    public double FrameWidth
    {
        get => _frameWidth;
        private set => SetProperty(ref _frameWidth, value);
    }

    /// <summary>Gets the height of the frame in DIPs: the page format, or the format of the first page while the size of this page is not known.</summary>
    public double FrameHeight
    {
        get => _frameHeight;
        private set => SetProperty(ref _frameHeight, value);
    }

    /// <summary>
    /// Gets the width the image is laid out with before it is turned: the thumbnail as it was rendered, scaled down when the
    /// turned page would be wider than the column (never enlarged).
    /// </summary>
    public double ImageWidth
    {
        get => _imageWidth;
        private set => SetProperty(ref _imageWidth, value);
    }

    /// <summary>Gets the height the image is laid out with before it is turned.</summary>
    public double ImageHeight
    {
        get => _imageHeight;
        private set => SetProperty(ref _imageHeight, value);
    }

    /// <summary>Gets the transform that turns the image about its centre, or <see langword="null"/> for an upright page.</summary>
    public ITransform? ImageTransform
    {
        get => _imageTransform;
        private set => SetProperty(ref _imageTransform, value);
    }

    /// <summary>Gets the rendered thumbnail, or <see langword="null"/> while the frame stays empty. Owned by the list, which disposes it.</summary>
    public Bitmap? Bitmap
    {
        get => _bitmap;
        internal set
        {
            if (SetProperty(ref _bitmap, value))
            {
                OnPropertyChanged(nameof(HasBitmap));
                OnPropertyChanged(nameof(IsDrawn));
            }
        }
    }

    /// <summary>Gets whether the thumbnail is rendered.</summary>
    public bool HasBitmap => _bitmap is not null;

    /// <summary>Gets whether the frame shows a page (a thumbnail, or a blank page it draws itself) and so has the edge of a page.</summary>
    public bool IsDrawn => _bitmap is not null || _kind == PageKind.Blank;

    /// <summary>Gets whether the page belongs to the current row of the document (two pages in the two-page layouts).</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>Gets whether the entry is selected in the mode "Organize pages" (it then has a check mark besides the frame).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Gets the name for assistive technology: "Page 3", or "Page 3, current page"; in the plan also what the plan did to
    /// the page: "Page 3, Rotated 90°", "Page 4, Blank page".
    /// </summary>
    public string AutomationName =>
        string.Format(Strings.Culture, CompositeFormat.Parse(Strings.ThumbnailName), PageText)
        + (IsCurrent ? Strings.ThumbnailCurrentSuffix : string.Empty)
        + (IsRotated ? ", " + RotationText : string.Empty)
        + (IsBlank ? ", " + Strings.PageMarkerBlank : string.Empty)
        + (IsForeign ? ", " + Strings.PageMarkerOtherFile : string.Empty);

    /// <summary>Sets the width of the column the entry lies in (another density or another width of the panel).</summary>
    internal void SetColumnWidth(double width)
    {
        if (_columnWidth != width)
        {
            _columnWidth = width;
            OnPropertyChanged(nameof(ColumnWidth));
            Relayout();
        }
    }

    /// <summary>Sets the page format the frame follows (the real size of the page arrived).</summary>
    internal void SetFormat(PdfPageSize format)
    {
        _format = format;
        Relayout();
    }

    /// <summary>
    /// Makes the entry stand for position <paramref name="position"/> (0-based) of the plan: <paramref name="slot"/> turned
    /// by its rotation, in <paramref name="format"/> before it is turned.
    /// </summary>
    internal void Assign(int position, PageSlot slot, PdfPageSize format)
    {
        PageNumber = position + 1;
        Slot = slot;
        Kind = slot.Blank is not null ? PageKind.Blank : slot.Source.Value == 0 ? PageKind.Page : PageKind.Foreign;
        RenderPage = Kind == PageKind.Page ? slot.SourceIndex : -1;
        _format = format;
        Rotation = slot.ExtraRotation;
        ImageTransform = slot.ExtraRotation switch
        {
            90 => Turn90,
            180 => Turn180,
            270 => Turn270,
            _ => null,
        };
        Relayout();
    }

    private void Relayout()
    {
        var upright = FrameHeightFor(_format, _columnWidth);
        if (_rotation is 90 or 270)
        {
            // The page lies on its side: as wide as its height was, but never wider than the column, and never enlarged
            // (the thumbnail is rendered upright at the column width).
            var scale = Math.Min(1, _columnWidth / upright);
            ImageWidth = _columnWidth * scale;
            ImageHeight = upright * scale;
            FrameWidth = ImageHeight;
            FrameHeight = ImageWidth;
        }
        else
        {
            ImageWidth = _columnWidth;
            ImageHeight = upright;
            FrameWidth = _columnWidth;
            FrameHeight = upright;
        }
    }

    private static double FrameHeightFor(PdfPageSize format, double width) =>
        Math.Clamp(ThumbnailPlanner.HeightFor(format, width), 1, width * MaxAspect);
}
