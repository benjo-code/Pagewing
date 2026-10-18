namespace Pagewing.App.ViewModels;

/// <summary>How far the outline (table of contents) of a tab's document is read (<see cref="TabViewModel.OutlineState"/>).</summary>
public enum OutlineState
{
    /// <summary>Not read: the "Contents" panel has not been opened for this document yet, or no document is open.</summary>
    NotLoaded,

    /// <summary>Being read on the PDFium thread.</summary>
    Loading,

    /// <summary>Read; <see cref="TabViewModel.Outline"/> holds it (an empty list for a document without one).</summary>
    Loaded,

    /// <summary>Reading failed; the panel says so.</summary>
    Failed,
}
