using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Pagewing.Core.Distribution;

namespace Pagewing.App.Platform.Windows;

/// <summary>
/// Default app for PDF files on Windows. Windows keeps that choice to the user, so this only reads <c>UserChoiceLatest</c>
/// (or <c>UserChoice</c> where the former does not exist),
/// makes sure Pagewing is registered as an app for PDF files and opens the system page where the user picks it.
/// Pagewing never writes <c>UserChoice</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDefaultAppService : IDefaultAppService
{
    private const string UserChoiceKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice";
    private const string UserChoiceLatestKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoiceLatest\ProgId";
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
            if (ReadChosenProgId() != FileTypeRegistration.ProgId)
            {
                return DefaultAppStatus.NotDefault;
            }

            // UserChoice survives a moved or deleted program folder; then the registered command is stale and a double-click fails.
            return new WindowsFileTypeRegistry(_root).IsStaleFor(_exePath) ? DefaultAppStatus.NotDefault : DefaultAppStatus.IsDefault;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return DefaultAppStatus.Unknown;
        }
    }

    // Newer Windows 11 builds write the choice to UserChoiceLatest\ProgId and leave UserChoice on the previous app, so the
    // newer key wins when it exists.
    private string? ReadChosenProgId()
    {
        using var latest = _root.OpenSubKey(UserChoiceLatestKey);
        if (latest?.GetValue("ProgId") is string latestProgId)
        {
            return latestProgId;
        }

        using var key = _root.OpenSubKey(UserChoiceKey);
        return key?.GetValue("ProgId") as string;
    }

    private async Task<DefaultAppResult> SetDefaultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var registry = new WindowsFileTypeRegistry(_root);
            if (!registry.IsRegistered || !registry.IsCurrentFor(_exePath))
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
