using System.Threading;
using System.Windows.Forms;
using NetworkingTool.Shared;

namespace NetworkingTool.Tray;

/// <summary>
/// The single integration point the rest of the app calls to start the unelevated
/// system-tray UI. Initializes WinForms and runs the application message loop using a
/// <see cref="TrayApplicationContext"/> (so there is no top-level form keeping the app alive;
/// the tray icon does).
/// </summary>
public static class TrayEntry
{
    private static bool _winFormsInitialized;

    /// <summary>
    /// Initializes WinForms application settings (high-DPI mode, visual styles, text rendering).
    /// Must be called before the first <see cref="System.Windows.Forms.Form"/> is created.
    /// Safe to call multiple times — subsequent calls are no-ops.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (!_winFormsInitialized)
        {
            ApplicationConfiguration.Initialize();   // source-generated; available because UseWindowsForms=true
            _winFormsInitialized = true;
        }
    }

    public static int Run()
    {
        // Enforce a single tray instance per session. Holding the mutex for the lifetime of the
        // message loop means a second launch (e.g. a manual run on top of the logon auto-start)
        // finds it already owned and exits silently — no duplicate tray icon. The OS releases the
        // mutex when this process exits.
        using var mutex = new Mutex(initiallyOwned: true, Constants.TrayMutexName, out bool createdNew);
        if (!createdNew)
        {
            return 0;
        }

        EnsureInitialized();
        Application.Run(new TrayApplicationContext());
        return 0;
    }
}
