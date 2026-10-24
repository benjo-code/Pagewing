using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>The card when another file stands at the path of the original (<see cref="PendingSaveKind.FileChanged"/>); see <see cref="SaveCard"/>.</summary>
public sealed partial class FileChangedCard : SaveCard
{
    public FileChangedCard()
    {
        InitializeComponent();
        Register(FileChangedOverwriteButton, PendingSaveChoice.Overwrite);
        Register(FileChangedSaveAsButton, PendingSaveChoice.SaveAs);
        Register(FileChangedCancelButton, PendingSaveChoice.Cancel);
    }
}
