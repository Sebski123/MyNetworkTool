using System;
using System.Drawing;
using System.Windows.Forms;

namespace NetworkingTool.Tray;

/// <summary>
/// Owns the persistent tray presence. Using an <see cref="ApplicationContext"/> (rather than a
/// top-level form) means closing/hiding the main window does not exit the process — only the
/// "Exit" menu item does. The single <see cref="MainForm"/> instance is created lazily and reused.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Dispose the NotifyIcon first (it removes the tray icon), then the Icon handle it used.
            _notifyIcon.Dispose();
            _trayIcon.Dispose();
            _mainForm?.Dispose();
        }
        base.Dispose(disposing);
    }
}
