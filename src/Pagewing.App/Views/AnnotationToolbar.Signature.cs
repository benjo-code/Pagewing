using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.Core.Annotations;
using Pagewing.Core.Signatures;

namespace Pagewing.App.Views;

/// <summary>
/// The entry "Signature" of the toolbar (milestone 12): a button that opens the menu of the saved signatures, each with its small
/// preview, its name and a button that deletes it, and "New signature …" at the end, which opens the dialog. Choosing one makes the
/// placing tool (<see cref="TabViewModel.ChooseSignatureAsync"/>); a choice with the keyboard places it in the centre of the visible
/// page. The library is read when the menu opens, never before. In the narrowest toolbar the entry moves into the menu "⋯" together
/// with the shapes. The command "Insert signature" (<see cref="MainViewModel.SignatureMenuRequested"/>) opens the same menu.
/// </summary>
public sealed partial class AnnotationToolbar
{
    private const string FlyoutName = "SignatureMenu";

    private MainViewModel? _window;
    private Flyout? _signatureFlyout;

    /// <summary>Gets the content of the open signature menu (for tests); <see langword="null"/> while it is closed.</summary>
    internal Panel? SignatureMenu => _signatureFlyout?.Content as Panel;

    /// <summary>Gets whether the signature menu is open.</summary>
    internal bool IsSignatureMenuOpen => _signatureFlyout?.IsOpen == true;

    /// <summary>
    /// Connects the toolbar to its window: the library, the dialog and the command "Insert signature" which asks for the menu.
    /// Called by the window when it makes the toolbar.
    /// </summary>
    internal void ConnectWindow(MainViewModel window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_window is not null)
        {
            _window.SignatureMenuRequested -= OnSignatureMenuRequested;
        }

        _window = window;
        window.SignatureMenuRequested += OnSignatureMenuRequested;
        var flyout = _signatureFlyout ??= CreateSignatureFlyout();
        SignatureTool.Flyout = flyout;
    }

    private Flyout CreateSignatureFlyout()
    {
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) => flyout.Content = BuildSignatureMenu();
        flyout.Closed += (_, _) => flyout.Content = null;
        return flyout;
    }

    private void OnSignatureMenuRequested() =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_signatureFlyout is null)
                {
                    return;
                }

                Control target = SignatureTool.IsEffectivelyVisible ? SignatureTool : MoreButton.IsEffectivelyVisible ? MoreButton : this;
                _signatureFlyout.ShowAt(target);
            },
            DispatcherPriority.Input);

    /// <summary>Makes the content of the menu from the library as it is now: the saved signatures and "New signature …".</summary>
    private StackPanel BuildSignatureMenu()
    {
        var menu = new StackPanel { Name = FlyoutName, Spacing = 2, MinWidth = 260 };
        AutomationProperties.SetName(menu, Strings.SignatureMenuName);
        AutomationProperties.SetAccessibilityView(menu, AccessibilityView.Content);
        menu.Children.Add(new TextBlock { Classes = { "caption" }, Text = Strings.SignatureMenuName, Margin = new Thickness(8, 4, 8, 4), Foreground = Foreground });
        var signatures = _window?.LoadSignatures() ?? [];
        foreach (var signature in signatures)
        {
            menu.Children.Add(MakeRow(menu, signature));
        }

        var create = new Button
        {
            Name = "SignatureMenuNew",
            Content = Strings.SignatureMenuNew,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(create, Strings.SignatureMenuNew);
        create.Click += (_, _) =>
        {
            _signatureFlyout?.Hide();
            _window?.ShowNewSignature();
        };
        menu.Children.Add(create);
        return menu;
    }

    private Grid MakeRow(StackPanel menu, SavedSignature signature)
    {
        var thumbnail = new SignatureThumbnail { Width = 64, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(thumbnail);
        content.Children.Add(new TextBlock { Text = signature.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 160 });
        if (signature.Kind == SavedSignatureKind.Drawn && signature.Strokes is { } strokes)
        {
            var rgb = DrawPalette.ColorOf(signature.Color);
            thumbnail.SetStrokes(strokes, Color.FromRgb(rgb.R, rgb.G, rgb.B), signature.Aspect);
        }
        else
        {
            _ = ShowImageAsync(thumbnail, signature);
        }

        var choose = new Button
        {
            Name = "SignatureEntry" + signature.Id,
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(choose, signature.Name);
        choose.Click += (_, _) =>
        {
            // With the pointer the signature follows it; with the keyboard it lands in the centre of the visible page.
            var fromKeyboard = !choose.IsPointerOver;
            _signatureFlyout?.Hide();
            if (_window is { } window)
            {
                _ = window.ChooseSignatureAsync(signature, fromKeyboard);
            }
        };

        this.TryFindResource("Pw.Icon.X", ActualThemeVariant, out var cross);
        var delete = new Button { Name = "SignatureDelete" + signature.Id, Classes = { "icon" }, Margin = new Thickness(4, 0, 0, 0), Content = new StrokeIcon { Width = 16, Height = 16, Data = cross as Geometry } };
        var deleteName = Strings.SignatureDelete + ": " + signature.Name;
        AutomationProperties.SetName(delete, deleteName);
        ToolTip.SetTip(delete, Strings.SignatureDelete);
        delete.Click += (_, _) => DeleteSignature(menu, signature);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(choose);
        Grid.SetColumn(delete, 1);
        row.Children.Add(delete);
        return row;
    }

    private void DeleteSignature(StackPanel menu, SavedSignature signature)
    {
        try
        {
            _window?.SignatureLibrary.Remove(signature.Id);
        }
        catch (IOException)
        {
            // The library could not be written: the entry stays, as the menu shows it again below.
        }

        if (_signatureFlyout is { IsOpen: true } flyout)
        {
            flyout.Content = BuildSignatureMenu();
        }

        _ = menu;
    }

    private async Task ShowImageAsync(SignatureThumbnail thumbnail, SavedSignature signature)
    {
        try
        {
            if (_window?.SignatureLibrary.ReadPng(signature.Id) is { } png)
            {
                thumbnail.SetImage(await SignatureImageCodec.DecodeAsync(png, CancellationToken.None).ConfigureAwait(true));
            }
        }
        catch (Exception ex) when (ex is SignatureImageException or IOException or InvalidOperationException)
        {
            // No preview then: the name is still there.
        }
    }

    /// <summary>The entry "Signature" of the menu "⋯": a submenu with the saved signatures and "New signature …".</summary>
    private MenuItem MakeSignatureSubmenu()
    {
        var submenu = new MenuItem { Name = "MoreSignature", Header = Strings.ToolSignature };
        AutomationProperties.SetName(submenu, Strings.ToolSignature);
        foreach (var signature in _window?.LoadSignatures() ?? [])
        {
            var item = new MenuItem { Name = "MoreSignature" + signature.Id, Header = signature.Name };
            AutomationProperties.SetName(item, signature.Name);
            item.Click += (_, _) =>
            {
                if (_window is { } window)
                {
                    _ = window.ChooseSignatureAsync(signature, fromKeyboard: !item.IsPointerOver);
                }
            };
            submenu.Items.Add(item);
        }

        var create = new MenuItem { Name = "MoreSignatureNew", Header = Strings.SignatureMenuNew };
        AutomationProperties.SetName(create, Strings.SignatureMenuNew);
        create.Click += (_, _) => _window?.ShowNewSignature();
        submenu.Items.Add(create);
        return submenu;
    }
}
