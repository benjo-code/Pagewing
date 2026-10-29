using System.Windows.Input;
using Pagewing.App.Services;
using Pagewing.Core.Settings;
using Pagewing.Core.Signatures;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The signatures of a window (milestone 12): the dialog "New signature" (<see cref="OverlayKind.Signature"/>) and the library
/// behind it. Nothing of it is made before it is needed: the library folder is read when the dialog or a menu of signatures
/// opens, the dialog's view model when it is shown.
/// </summary>
public sealed partial class MainViewModel
{
    private SignatureCardViewModel? _signatureCard;
    private SignatureStore? _signatureStore;
    private RelayCommand? _insertSignatureCommand;

    /// <summary>
    /// Gets or sets what opens the library of signatures (the folder in the user's configuration); the workspace gives all windows
    /// one library, tests give a folder of their own. Called once, when the library is first needed.
    /// </summary>
    internal Func<SignatureStore> SignatureStoreProvider { get; set; } = static () => SignatureStore.Load(UserDataPaths.SignaturesDirectory());

    /// <summary>Gets or sets what lets the user choose an image file for a signature instead of the system dialog (tests put a fake in).</summary>
    internal Func<Task<string?>>? SignatureImagePicker { get; set; }

    /// <summary>Gets the view model of the dialog "New signature" while it is shown, or <see langword="null"/> (a new one is made for each showing).</summary>
    public SignatureCardViewModel? SignatureCard => _signatureCard;

    /// <summary>Occurs when a signature of the dialog was saved into the library, before the dialog closes.</summary>
    internal event Action<SavedSignature>? SignatureSaved;

    /// <summary>Gets the library of signatures, opening it on first use.</summary>
    internal SignatureStore SignatureLibrary => _signatureStore ??= SignatureStoreProvider();

    /// <summary>
    /// Ctrl+Shift+G, "Insert signature": starts the mode "Comment" in the active tab if it is not on, then opens the menu of the
    /// saved signatures (<see cref="SignatureMenuRequested"/>, which the toolbar answers) or, while the library is empty, the dialog
    /// "New signature". Executable where the mode can run: after the first sharp page, with a document that allows annotating.
    /// </summary>
    public ICommand InsertSignatureCommand => _insertSignatureCommand ??= new RelayCommand(() => _ = InsertSignatureAsync(), () => ActiveTab is { CanChooseSignature: true });

    /// <summary>Occurs when the menu of the saved signatures should open (the toolbar of the mode shows it).</summary>
    internal event Action? SignatureMenuRequested;

    /// <summary>Shows the dialog "New signature".</summary>
    public void ShowNewSignature() => ShowOverlay(OverlayKind.Signature);

    /// <summary>
    /// Reads the saved signatures: the folder of the library is read the first time (never before a menu or the dialog needs it,
    /// and reading creates nothing).
    /// </summary>
    internal IReadOnlyList<SavedSignature> LoadSignatures() => SignatureLibrary.Items;

    /// <summary>
    /// Chooses a saved signature for the active tab: the placing tool with its ghost, or with <paramref name="fromKeyboard"/> placed
    /// at once in the centre of the visible page (<see cref="TabViewModel.ChooseSignatureAsync"/>).
    /// </summary>
    internal Task ChooseSignatureAsync(SavedSignature signature, bool fromKeyboard)
    {
        ArgumentNullException.ThrowIfNull(signature);
        return ActiveTab is not { } tab
            ? Task.CompletedTask
            : tab.ChooseSignatureAsync(signature, signature.Kind == SavedSignatureKind.Image ? SignatureLibrary.ReadPng(signature.Id) : null, fromKeyboard);
    }

    private async Task InsertSignatureAsync()
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        await tab.BeginAnnotateAsync().ConfigureAwait(true);
        if (!tab.IsAnnotating || !ReferenceEquals(tab, ActiveTab))
        {
            return;
        }

        if (LoadSignatures().Count == 0)
        {
            ShowNewSignature();
        }
        else
        {
            SignatureMenuRequested?.Invoke();
        }
    }

    private SignatureCardViewModel CreateSignatureCard()
    {
        var card = new SignatureCardViewModel(SignatureLibrary, PickSignatureImageAsync);
        card.Cancelled += HideOverlay;
        card.Saved += saved =>
        {
            SignatureSaved?.Invoke(saved);
            HideOverlay();
        };
        return card;
    }

    private Task<string?> PickSignatureImageAsync() =>
        SignatureImagePicker is { } pick ? pick() : _picker is IImagePicker images ? images.PickImageAsync() : Task.FromResult<string?>(null);
}
