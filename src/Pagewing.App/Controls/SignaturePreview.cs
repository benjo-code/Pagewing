using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Pagewing.Core.Signatures;

namespace Pagewing.App.Controls;

/// <summary>
/// Shows an imported signature as it will be placed: the cut-out image on a checkerboard (so transparent parts can be told from
/// white ink or paper), fitted into the control with its aspect ratio. The checkerboard is light, like a page, whatever the theme.
/// The bitmap is made when <see cref="Image"/> changes, never while drawing.
/// </summary>
public sealed class SignaturePreview : Control
{
    /// <summary>The edge of a square of the checkerboard in DIP.</summary>
    private const double Square = 8;

    private static readonly IBrush Light = new SolidColorBrush(Color.FromRgb(255, 255, 255));
    private static readonly IBrush Dark = new SolidColorBrush(Color.FromRgb(224, 224, 224));

    public static readonly StyledProperty<SignatureImage?> ImageProperty =
        AvaloniaProperty.Register<SignaturePreview, SignatureImage?>(nameof(Image));

    private WriteableBitmap? _bitmap;

    static SignaturePreview()
    {
        AffectsRender<SignaturePreview>(ImageProperty);
        FocusableProperty.OverrideDefaultValue<SignaturePreview>(false);
        AutomationProperties.AccessibilityViewProperty.OverrideDefaultValue<SignaturePreview>(AccessibilityView.Content);
        ImageProperty.Changed.AddClassHandler<SignaturePreview>((preview, _) => preview.MakeBitmap());
    }

    /// <summary>Gets or sets the image (BGRA, not premultiplied), or <see langword="null"/> for an empty checkerboard.</summary>
    public SignatureImage? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        using (context.PushClip(bounds))
        {
            for (var y = 0; y * Square < bounds.Height; y++)
            {
                for (var x = 0; x * Square < bounds.Width; x++)
                {
                    context.DrawRectangle((x + y) % 2 == 0 ? Light : Dark, null, new Rect(x * Square, y * Square, Square, Square));
                }
            }
        }

        if (_bitmap is null)
        {
            return;
        }

        var scale = Math.Min(bounds.Width / _bitmap.PixelSize.Width, bounds.Height / _bitmap.PixelSize.Height);
        var size = new Size(_bitmap.PixelSize.Width * scale, _bitmap.PixelSize.Height * scale);
        var target = new Rect((bounds.Width - size.Width) / 2, (bounds.Height - size.Height) / 2, size.Width, size.Height);
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        {
            context.DrawImage(_bitmap, new Rect(_bitmap.Size), target);
        }
    }

    private void MakeBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        if (Image is { } image)
        {
            _bitmap = SignatureBitmaps.From(image);
        }

        InvalidateVisual();
    }
}
