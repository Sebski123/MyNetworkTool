using System;
using System.IO;

namespace NetworkingTool.Shared;

/// <summary>
/// Centralized, compile-time constants shared by every mode (service, tray, install).
/// Keeping these in one place guarantees the tray client and the service agree on the
/// pipe name, and that install/uninstall target the same service name and paths.
/// </summary>
public static class Constants
{
    public const string AppName = "MyNetworkTool";

    /// <summary>The local named pipe. Full path is \\.\pipe\MyNetworkTool.</summary>
    public const string PipeName = "MyNetworkTool";

    /// <summary>
    /// Name of the named mutex that enforces a single tray UI instance. No <c>Global\</c> prefix, so
    /// the scope is the local (per-session) namespace: each interactive user still gets their own
    /// tray (matching the HKLM Run auto-start that launches one tray per logon), but a second launch
    /// within the same session is blocked.
    /// </summary>
    public const string TrayMutexName = "MyNetworkTool.Tray.SingleInstance";

    public const string ServiceName = "MyNetworkToolSvc";
    public const string ServiceDisplayName = "MyNetworkTool Network Configuration Service";
    public const string ServiceDescription =
        "Applies validated network adapter and proxy changes on behalf of the MyNetworkTool " +
        "tray application, so standard users can change settings without per-change UAC prompts.";

    /// <summary>
    /// Run key path used to auto-start the tray app at logon. The installer writes this under
    /// HKLM (machine-wide) so auto-start works for the standard user even when setup was elevated
    /// with a different admin's credentials.
    /// </summary>
    public const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "MyNetworkToolTray";

    /// <summary>How long the tray client waits to connect to the service pipe.</summary>
    public const int PipeConnectTimeoutMs = 4000;

    /// <summary>Installed executable file name (the published single file copied into InstallDir).</summary>
    public const string ExeFileName = "MyNetworkTool.exe";

    /// <summary>C:\Program Files\MyNetworkTool</summary>
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

    /// <summary>C:\Program Files\MyNetworkTool\MyNetworkTool.exe</summary>
    public static string InstalledExePath => Path.Combine(InstallDir, ExeFileName);

    /// <summary>C:\ProgramData\MyNetworkTool (service log + optional presets.json live here).</summary>
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName);

    public static string ServiceLogPath => Path.Combine(DataDir, "service.log");

    /// <summary>Optional override file for proxy presets. If absent, built-in presets are used.</summary>
    public static string PresetsConfigPath => Path.Combine(DataDir, "presets.json");

    /// <summary>
    /// Per-user marker used by tray mode to throttle background update checks at logon.
    /// Stored under LocalAppData because it is UX state, not shared service configuration.
    /// </summary>
    public static string TrayUpdateCheckMarkerPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "last-update-check-utc.txt");
}
