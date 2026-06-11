using System;
using NetworkingTool.Install;
using NetworkingTool.Service;
using NetworkingTool.Tray;

namespace NetworkingTool;

/// <summary>
/// Single-executable, multi-mode entry point.
///
///   MyNetworkTool.exe            -> if service installed, run tray; else offer to install
///   MyNetworkTool.exe install    -> self-elevate, copy to Program Files, register + start service
///   MyNetworkTool.exe uninstall  -> self-elevate, stop + delete service, remove startup, delete files
///   MyNetworkTool.exe service    -> run the Windows Service host (started by the SCM as LocalSystem)
///   MyNetworkTool.exe tray       -> run the unelevated system-tray UI
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : string.Empty;

        try
        {
            return mode switch
            {
                "service"   => ServiceEntry.Run(args),
                "install"   => Installer.Install(),
                "uninstall" => Installer.Uninstall(),
                "tray"      => TrayEntry.Run(),
                ""          => DefaultLaunch(),
                _           => UnknownMode(mode),
            };
        }
        catch (Exception ex)
        {
            // Service mode logs its own failures; for interactive modes show the user something.
            if (mode != "service")
            {
                Installer.ShowMessage($"MyNetworkTool failed:\n\n{ex.Message}", isError: true);
            }
            return 1;
        }
    }

    /// <summary>No-argument launch: route to the tray if installed, otherwise guide the user to install.</summary>
    private static int DefaultLaunch()
    {
        // Initialize WinForms before any Form (update prompt, install prompt, etc.) is created.
        // ApplicationConfiguration.Initialize() calls SetCompatibleTextRenderingDefault, which
        // throws if a Form has already been instantiated, so this must come first.
        TrayEntry.EnsureInitialized();

        if (Installer.IsServiceInstalled())
        {
            return TrayEntry.Run();
        }

        if (Installer.PromptForInstall())
        {
            return Installer.RelaunchElevatedInstall();
        }

        return 0;
    }

    private static int UnknownMode(string mode)
    {
        Installer.ShowMessage(
            $"Unknown mode '{mode}'.\n\nUsage:\n" +
            "  MyNetworkTool.exe            (run tray, or prompt to install)\n" +
            "  MyNetworkTool.exe install\n" +
            "  MyNetworkTool.exe uninstall\n" +
            "  MyNetworkTool.exe service\n" +
            "  MyNetworkTool.exe tray",
            isError: true);
        return 1;
    }
}
