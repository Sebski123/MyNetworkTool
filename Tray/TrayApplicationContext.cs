using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using NetworkingTool.Install;
using NetworkingTool.Shared;

namespace NetworkingTool.Tray;

/// <summary>
/// Owns the persistent tray presence. Using an <see cref="ApplicationContext"/> (rather than a
/// top-level form) means closing/hiding the main window does not exit the process — only the
/// "Exit" menu item does. The single <see cref="MainForm"/> instance is created lazily and reused.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan StartupUpdateCheckInterval = TimeSpan.FromHours(24);

    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly System.Windows.Forms.Timer _startupUpdateTimer;
    private MainForm? _mainForm;

    public TrayApplicationContext()
    {
        // Load the custom icon at the small (tray) size so it stays crisp in the notification area.
        _trayIcon = AppIcon.Load(SystemInformation.SmallIconSize);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (s, e) => ShowMainForm());
        menu.Items.Add("Exit", null, (s, e) => ExitApp());

        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "MyNetworkTool",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (s, e) => ShowMainForm();

        // Run a delayed, non-interactive update probe so logon startup stays quiet and responsive.
        _startupUpdateTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _startupUpdateTimer.Tick += async (s, e) => await RunSilentStartupUpdateCheckAsync();
        _startupUpdateTimer.Start();

        // Create the window up front (kept hidden) and start fetching adapters, proxy presets and
        // proxy status in the background. The IPC continuations resume on the UI thread once the
        // message loop starts, so by the time the user opens the GUI its data is already populated
        // and there is no startup wait.
        _mainForm = new MainForm();
        _ = _mainForm.EnsureInitialDataLoadedAsync();
    }

    private void ShowMainForm()
    {
        if (_mainForm == null || _mainForm.IsDisposed)
        {
            _mainForm = new MainForm();
        }

        // No-op if the background prewarm already ran; only does work if the form was recreated.
        _ = _mainForm.EnsureInitialDataLoadedAsync();

        _mainForm.Show();
        _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.Activate();
        _mainForm.BringToFront();
    }

    private void ExitApp()
    {
        _notifyIcon.Visible = false;
        ExitThread();
    }

    private async Task RunSilentStartupUpdateCheckAsync()
    {
        _startupUpdateTimer.Stop();

        if (!ShouldRunStartupUpdateCheckNow())
        {
            return;
        }

        MarkStartupUpdateCheckAttempt();

        var update = await Task.Run(() =>
        {
            if (Installer.TryGetAvailableGitHubUpdate(out string tagName, out string releaseUrl))
            {
                return (Available: true, TagName: tagName, ReleaseUrl: releaseUrl);
            }

            return (Available: false, TagName: string.Empty, ReleaseUrl: string.Empty);
        });

        if (!update.Available)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = "MyNetworkTool update available";
        _notifyIcon.BalloonTipText = "New version " + update.TagName + " is available. Launch MyNetworkTool manually to install.";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(10_000);
    }

    private static bool ShouldRunStartupUpdateCheckNow()
    {
        try
        {
            if (!File.Exists(Constants.TrayUpdateCheckMarkerPath))
            {
                return true;
            }

            string text = File.ReadAllText(Constants.TrayUpdateCheckMarkerPath).Trim();
            if (!long.TryParse(text, out long lastCheckUtcTicks))
            {
                return true;
            }

            var lastCheckUtc = new DateTime(lastCheckUtcTicks, DateTimeKind.Utc);
            return DateTime.UtcNow - lastCheckUtc >= StartupUpdateCheckInterval;
        }
        catch
        {
            // If marker state is unreadable, default to checking once now.
            return true;
        }
    }

    private static void MarkStartupUpdateCheckAttempt()
    {
        try
        {
            string? dir = Path.GetDirectoryName(Constants.TrayUpdateCheckMarkerPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(Constants.TrayUpdateCheckMarkerPath, DateTime.UtcNow.Ticks.ToString());
        }
        catch
        {
            // Marker write is best-effort; skipping it only means checks may run more often.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Dispose the NotifyIcon first (it removes the tray icon), then the Icon handle it used.
            _startupUpdateTimer.Dispose();
            _notifyIcon.Dispose();
            _trayIcon.Dispose();
            _mainForm?.Dispose();
        }
        base.Dispose(disposing);
    }
}
