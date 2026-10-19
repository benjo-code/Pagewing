using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Pagewing.Core.Distribution;

namespace Pagewing.App.Platform.Windows;

/// <summary>
/// Default app for PDF files on Windows. Windows keeps that choice to the user, so this only reads <c>UserChoice</c>,
/// makes sure Pagewing is registered as an app for PDF files and opens the system page where the user picks it.
/// Pagewing never writes <c>UserChoice</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDefaultAppService : IDefaultAppService
{
    private const string UserChoiceKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice";
    private const string SettingsUri = "ms-settings:defaultapps?registeredAppUser=Pagewing";

    private readonly RegistryKey _root;
    private readonly string _exePath;
    private readonly Func<string, Task> _openUri;
    private readonly Action _notifyAssociationsChanged;

    public WindowsDefaultAppService()
        : this(
            Registry.CurrentUser,
            Environment.ProcessPath ?? throw new InvalidOperationException("The path of the program is unknown."),
            OpenWithShell,
            ShellNative.NotifyAssociationsChanged)
    {
    }

    /// <summary>Creates the service over a registry root (<c>HKCU</c>, or a sandbox key in tests) with its two outside calls.</summary>
    /// <param name="root">Where registration and <c>UserChoice</c> live.</param>
    /// <param name="exePath">The program to register.</param>
    /// <param name="openUri">Opens a URI such as the settings page.</param>
    /// <param name="notifyAssociationsChanged">Tells the shell that file types changed.</param>
    public WindowsDefaultAppService(RegistryKey root, string exePath, Func<string, Task> openUri, Action notifyAssociationsChanged)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentNullException.ThrowIfNull(openUri);
        ArgumentNullException.ThrowIfNull(notifyAssociationsChanged);
        _root = root;
        _exePath = exePath;
        _openUri = openUri;
        _notifyAssociationsChanged = notifyAssociationsChanged;
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public Task<DefaultAppStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.Run(ReadStatus, CancellationToken.None);

    public Task<DefaultAppResult> MakeDefaultAsync(CancellationToken cancellationToken) =>
        Task.Run(() => SetDefaultAsync(cancellationToken), CancellationToken.None);

    private static Task OpenWithShell(string uri)
    {
        // The shell resolves the ms-settings: protocol, so this one start needs ShellExecute.
        using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    // Registry, shell notification and process start run on the thread pool, never on the caller's (UI) thread.
    private DefaultAppStatus ReadStatus()
    {
        try
        {
            using var key = _root.OpenSubKey(UserChoiceKey);
            var progId = key?.GetValue("ProgId") as string;
            return progId == FileTypeRegistration.ProgId ? DefaultAppStatus.IsDefault : DefaultAppStatus.NotDefault;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return DefaultAppStatus.Unknown;
        }
    }

    private async Task<DefaultAppResult> SetDefaultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var registry = new WindowsFileTypeRegistry(_root);
            if (!registry.IsRegistered)
            {
                registry.Register(_exePath);
                _notifyAssociationsChanged();
            }

            await _openUri(SettingsUri).ConfigureAwait(false);
            return DefaultAppResult.OpenedSystemSettings;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return DefaultAppResult.Failed;
        }
    }
}
