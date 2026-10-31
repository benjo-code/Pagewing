using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.Core.Annotations;
using Pagewing.Core.Signatures;

namespace Pagewing.App.ViewModels;

/// <summary>The two ways to make a signature in the dialog.</summary>
public enum SignatureCardTab
{
    /// <summary>Drawn with the mouse, a pen or a finger.</summary>
    Draw,

    /// <summary>Imported from a PNG or JPG; the paper is made transparent here.</summary>
    Image,
}

/// <summary>Why a signature cannot be saved (yet); the dialog names it.</summary>
public enum SignatureProblem
{
    /// <summary>Nothing usable: no stroke of any extent yet (Draw), or nothing left after the cut-out (Image).</summary>
    Empty,

    /// <summary>No image was chosen yet.</summary>
    NoImage,

    /// <summary>The file is larger than <see cref="SignatureLimits.MaxFileBytes"/>.</summary>
    TooLarge,

    /// <summary>A side of the image is longer than <see cref="SignatureLimits.MaxSourceSide"/> pixels.</summary>
    TooManyPixels,

    /// <summary>The file cannot be read as a PNG or JPG image.</summary>
    Unreadable,

    /// <summary>The library holds <see cref="SignatureLimits.MaxSignatures"/> signatures already.</summary>
    LibraryFull,

    /// <summary>The signature could not be written to the library.</summary>
    SaveFailed,
}

/// <summary>
/// The dialog "New signature" (<see cref="OverlayKind.Signature"/>): the strokes of the pad (thinned and smoothed like a freehand
/// drawing), or an imported image with its cut-out (the paper becomes transparent above a threshold the slider sets; the preview
/// shows the result); a name; "Save" puts it in the library (<see cref="SignatureStore"/>). It cannot be saved while
/// <see cref="Problem"/> is set, and says why. Made anew for each showing, never in the startup path.
/// </summary>
public sealed class SignatureCardViewModel : ViewModelBase
{
    /// <summary>The longest side a stroke must reach in the pad (DIP), or it counts as a dot.</summary>
    internal const double MinExtent = 6;

    private const double SimplifyTolerance = 0.5;

    private readonly SignatureStore _store;
    private readonly Func<Task<string?>>? _pickImage;
    private readonly List<IReadOnlyList<DisplayPoint>> _strokes = [];
    private readonly RelayCommand _save;
    private readonly RelayCommand _undoStroke;
    private readonly RelayCommand _clear;
    private SignatureCardTab _tab;
    private string _name = string.Empty;
    private SettingsChoice<DrawColor> _selectedColor;
    private IReadOnlyList<IReadOnlyList<DisplayPoint>> _strokeSnapshot = [];
    private SignatureImage? _source;
    private SignatureImage? _preview;
    private int _threshold = 128;
    private string? _imageFileName;
    private SignatureProblem? _fileProblem;
    private bool _saveFailed;
    private bool _saving;

    /// <summary>Creates the dialog for <paramref name="store"/>.</summary>
    /// <param name="store">The library the signature goes into.</param>
    /// <param name="pickImage">Lets the user choose an image file (the path, or <see langword="null"/> when cancelled); without it the button is not offered.</param>
    public SignatureCardViewModel(SignatureStore store, Func<Task<string?>>? pickImage = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _pickImage = pickImage;
        Colors = [.. DrawPalette.All.Select(color => new SettingsChoice<DrawColor>(color, Strings.DrawColorName(color)))];
        _selectedColor = Colors.Single(choice => choice.Value == DrawColor.Black);
        _save = new RelayCommand(() => _ = SaveAsync(), () => Problem is null && !_saving);
        _undoStroke = new RelayCommand(UndoStroke, () => _strokes.Count > 0);
        _clear = new RelayCommand(Clear, () => _strokes.Count > 0 || _source is not null);
        SaveCommand = _save;
        UndoStrokeCommand = _undoStroke;
        ClearCommand = _clear;
        CancelCommand = new RelayCommand(() => Cancelled?.Invoke());
        ChooseFileCommand = new RelayCommand(() => _ = ChooseFileAsync(), () => _pickImage is not null);
    }

    /// <summary>Occurs after the signature was saved, with what the library now holds.</summary>
    public event Action<SavedSignature>? Saved;

    /// <summary>Occurs when the user cancels.</summary>
    public event Action? Cancelled;

    /// <summary>Gets or sets the tab shown.</summary>
    public SignatureCardTab Tab
    {
        get => _tab;
        set
        {
            if (SetProperty(ref _tab, value))
            {
                _saveFailed = false;
                OnPropertyChanged(nameof(IsDrawTab));
                OnPropertyChanged(nameof(IsImageTab));
                Refresh();
            }
        }
    }

    /// <summary>Gets or sets whether the tab "Draw" is shown (setting <see langword="false"/> changes nothing).</summary>
    public bool IsDrawTab
    {
        get => Tab == SignatureCardTab.Draw;
        set
        {
            if (value)
            {
                Tab = SignatureCardTab.Draw;
            }
        }
    }

    /// <summary>Gets or sets whether the tab "Image" is shown (setting <see langword="false"/> changes nothing).</summary>
    public bool IsImageTab
    {
        get => Tab == SignatureCardTab.Image;
        set
        {
            if (value)
            {
                Tab = SignatureCardTab.Image;
            }
        }
    }

    /// <summary>Gets or sets the name; a blank one becomes "Signature n" when saved.</summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    /// <summary>Gets the colours offered for the ink of a drawn signature (the palette of the drawing tools).</summary>
    public IReadOnlyList<SettingsChoice<DrawColor>> Colors { get; }

    /// <summary>Gets or sets the colour chosen for the ink (black at first).</summary>
    public SettingsChoice<DrawColor> SelectedColor
    {
        get => _selectedColor;
        set
        {
            if (value is not null && SetProperty(ref _selectedColor, value))
            {
                OnPropertyChanged(nameof(Color));
            }
        }
    }

    /// <summary>Gets the ink of a drawn signature.</summary>
    public DrawColor Color => _selectedColor.Value;

    /// <summary>Gets the strokes drawn so far, in pad coordinates (DIP), thinned and smoothed; every stroke has two points or more.</summary>
    public IReadOnlyList<IReadOnlyList<DisplayPoint>> Strokes => _strokeSnapshot;

    /// <summary>Gets or sets the brightness (0 to 255) at and above which the paper of an imported image is transparent.</summary>
    public int Threshold
    {
        get => _threshold;
        set
        {
            if (SetProperty(ref _threshold, Math.Clamp(value, 0, 255)))
            {
                UpdatePreview();
            }
        }
    }

    /// <summary>Gets the imported image after the cut-out (at most 1,200 pixels a side), or <see langword="null"/> while there is none.</summary>
    public SignatureImage? Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    /// <summary>Gets the name of the chosen image file (no path), or <see langword="null"/>.</summary>
    public string? ImageFileName
    {
        get => _imageFileName;
        private set => SetProperty(ref _imageFileName, value);
    }

    /// <summary>Gets whether the tab shown has something that could be saved.</summary>
    public bool HasContent => _tab == SignatureCardTab.Draw ? HasUsableStroke() : _preview is { IsEmpty: false };

    /// <summary>Gets why the signature cannot be saved now, or <see langword="null"/> when it can.</summary>
    public SignatureProblem? Problem
    {
        get
        {
            if (_store.Items.Count >= SignatureLimits.MaxSignatures)
            {
                return SignatureProblem.LibraryFull;
            }

            if (_saveFailed)
            {
                return SignatureProblem.SaveFailed;
            }

            if (_tab == SignatureCardTab.Draw)
            {
                return HasUsableStroke() ? null : SignatureProblem.Empty;
            }

            if (_fileProblem is { } fileProblem)
            {
                return fileProblem;
            }

            return _source is null ? SignatureProblem.NoImage : _preview is { IsEmpty: false } ? null : SignatureProblem.Empty;
        }
    }

    /// <summary>Gets the text that names <see cref="Problem"/> in the current language, or <see langword="null"/>.</summary>
    public string? ProblemText => Problem is { } problem ? TextOf(problem, _tab) : null;

    /// <summary>Gets the command "Save".</summary>
    public ICommand SaveCommand { get; }

    /// <summary>Gets the command "Cancel".</summary>
    public ICommand CancelCommand { get; }

    /// <summary>Gets the command "Clear" (all strokes, or the chosen image).</summary>
    public ICommand ClearCommand { get; }

    /// <summary>Gets the command "Undo stroke" (the last stroke).</summary>
    public ICommand UndoStrokeCommand { get; }

    /// <summary>Gets the command "Choose image…".</summary>
    public ICommand ChooseFileCommand { get; }

    /// <summary>Gets the text for a problem on a tab (the same problem reads differently on each).</summary>
    public static string TextOf(SignatureProblem problem, SignatureCardTab tab) => problem switch
    {
        SignatureProblem.Empty => tab == SignatureCardTab.Draw ? Strings.SignatureProblemNothingDrawn : Strings.SignatureProblemBlank,
        SignatureProblem.NoImage => Strings.SignatureProblemNoImage,
        SignatureProblem.TooLarge => Strings.SignatureProblemTooLarge,
        SignatureProblem.TooManyPixels => Strings.SignatureProblemTooManyPixels,
        SignatureProblem.Unreadable => Strings.SignatureProblemUnreadable,
        SignatureProblem.LibraryFull => Strings.SignatureProblemLibraryFull,
        SignatureProblem.SaveFailed => Strings.SignatureProblemSaveFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, null),
    };

    /// <summary>
    /// Adds a stroke of the pad: thinned (Douglas-Peucker, 0.5 DIP) and smoothed like a freehand drawing. A stroke of fewer than two
    /// points, or one that never leaves a spot of <see cref="MinExtent"/> DIP, is a dot and is left out.
    /// </summary>
    /// <param name="points">The points of the stroke in pad coordinates.</param>
    public void AddStroke(IReadOnlyList<DisplayPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2 || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)) || Extent(points) < 1)
        {
            return;
        }

        _strokes.Add(StrokeSimplifier.Smooth(StrokeSimplifier.Simplify(points, SimplifyTolerance)));
        _saveFailed = false;
        StrokesChanged();
    }

    /// <summary>Lets the user choose an image file and reads it.</summary>
    public async Task ChooseFileAsync()
    {
        if (_pickImage is null || await _pickImage().ConfigureAwait(true) is not { } path)
        {
            return;
        }

        await LoadFileAsync(path).ConfigureAwait(true);
    }

    /// <summary>
    /// Reads an image file: refuses a file that is too large (without reading it), has too many pixels or cannot be read, naming
    /// the reason; otherwise makes it the image of the dialog with the automatic threshold of its brightness. Nothing of the
    /// file reaches a message.
    /// </summary>
    /// <param name="path">The path of the file.</param>
    public async Task LoadFileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _saveFailed = false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                Reject(SignatureProblem.Unreadable);
                return;
            }

            if (info.Length > SignatureLimits.MaxFileBytes)
            {
                Reject(SignatureProblem.TooLarge);
                return;
            }

            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            var image = await SignatureImageCodec.DecodeAsync(bytes, CancellationToken.None).ConfigureAwait(true);
            _source = SignatureCutout.FitWithin(image, SignatureLimits.MaxStoredSide);
            _fileProblem = null;
            ImageFileName = Path.GetFileName(path);
            _threshold = SignatureCutout.AutoThreshold(_source);
            OnPropertyChanged(nameof(Threshold));
            UpdatePreview();
        }
        catch (SignatureImageException ex)
        {
            Reject(ex.Reason switch
            {
                SignatureImageProblem.TooLarge => SignatureProblem.TooLarge,
                SignatureImageProblem.TooManyPixels => SignatureProblem.TooManyPixels,
                SignatureImageProblem.Empty => SignatureProblem.Empty,
                _ => SignatureProblem.Unreadable,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Reject(SignatureProblem.Unreadable);
        }
    }

    /// <summary>
    /// Saves the signature into the library and raises <see cref="Saved"/>; does nothing while <see cref="Problem"/> is set. A
    /// drawn signature is saved as strokes, an imported one as the PNG of the cut-out, trimmed to the ink.
    /// </summary>
    public async Task SaveAsync()
    {
        if (Problem is not null || _saving)
        {
            return;
        }

        _saving = true;
        _save.RaiseCanExecuteChanged();
        try
        {
            var name = string.IsNullOrWhiteSpace(_name) ? Strings.SignatureDefaultName + " " + (_store.Items.Count + 1) : _name.Trim();
            SavedSignature saved;
            if (_tab == SignatureCardTab.Draw)
            {
                saved = _store.AddDrawn(name, _strokeSnapshot, Color);
            }
            else
            {
                var cut = SignatureCutout.TrimToContent(_preview!);
                var png = await Task.Run(() => SignatureImageCodec.EncodePng(cut)).ConfigureAwait(true);
                saved = _store.AddImage(name, png, (double)cut.Width / cut.Height);
            }

            Saved?.Invoke(saved);
        }
        catch (SignatureLimitException)
        {
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _saveFailed = true;
            Refresh();
        }
        finally
        {
            _saving = false;
            _save.RaiseCanExecuteChanged();
        }
    }

    private bool HasUsableStroke() => _strokes.Count > 0 && Extent(_strokes.SelectMany(stroke => stroke).ToList()) >= MinExtent;

    private static double Extent(IReadOnlyList<DisplayPoint> points)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }

        return Math.Max(maxX - minX, maxY - minY);
    }

    private void UndoStroke()
    {
        if (_strokes.Count > 0)
        {
            _strokes.RemoveAt(_strokes.Count - 1);
            StrokesChanged();
        }
    }

    private void Clear()
    {
        _saveFailed = false;
        if (_tab == SignatureCardTab.Draw)
        {
            _strokes.Clear();
            StrokesChanged();
        }
        else
        {
            _source = null;
            _fileProblem = null;
            ImageFileName = null;
            Preview = null;
            Refresh();
        }
    }

    private void StrokesChanged()
    {
        _strokeSnapshot = [.. _strokes];
        OnPropertyChanged(nameof(Strokes));
        Refresh();
    }

    private void UpdatePreview()
    {
        Preview = _source is null ? null : SignatureCutout.Apply(_source, _threshold);
        Refresh();
    }

    private void Reject(SignatureProblem problem)
    {
        _source = null;
        _fileProblem = problem;
        ImageFileName = null;
        Preview = null;
        Refresh();
    }

    /// <summary>Tells the bound controls that what depends on the content may have changed.</summary>
    private void Refresh()
    {
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(ProblemText));
        _save.RaiseCanExecuteChanged();
        _undoStroke.RaiseCanExecuteChanged();
        _clear.RaiseCanExecuteChanged();
    }
}
