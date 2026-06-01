using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ComponentModel;
using System.Windows.Forms;
using Microsoft.Win32;
using NetworkingTool.Shared;

namespace NetworkingTool.Install;

/// <summary>
/// One-time, self-elevating installer/uninstaller for MyNetworkTool.
///
/// The interactive (possibly unelevated) process re-launches itself elevated with the
/// "install"/"uninstall" argument; the elevated child performs the real work:
///   - copy the single-file exe into Program Files,
///   - register + start the LocalSystem background service (via the SCM, native API),
///   - add the per-user HKCU\...\Run entry so the tray auto-starts at logon.
///
/// All feedback is surfaced both to an attached parent console (best effort) and via a
/// MessageBox, because the elevated relaunch has no inherited console.
/// </summary>
public static class Installer
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    // Used to schedule deletion of a locked install folder on next reboot.
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    // Win32 error returned when the user declines (or cancels) the UAC prompt.
    private const int ERROR_CANCELLED = 1223;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);

    // ---- Elevation helpers ----

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Shows a message to the user. Best-effort writes to an attached parent console AND always
    /// pops a MessageBox (the reliable channel for the elevated, console-less relaunch).
    /// </summary>
    public static void ShowMessage(string message, bool isError)
    {
        try
        {
            if (AttachConsole(ATTACH_PARENT_PROCESS))
            {
                Console.WriteLine(message);
            }
        }
        catch
        {
            // Console attach is purely best-effort; ignore any failure.
        }

        MessageBox.Show(
            message,
            "MyNetworkTool",
            MessageBoxButtons.OK,
            isError ? MessageBoxIcon.Error : MessageBoxIcon.Information);
    }

    /// <summary>
    /// Returns true if the background service is registered. Safe to call unelevated — any
    /// failure is swallowed and reported as "not installed".
    /// </summary>
    public static bool IsServiceInstalled()
    {
        try { return ServiceControl.Exists(Constants.ServiceName); }
        catch { return false; }
    }

    // ---- Version / update detection ----

    /// <summary>Reads the file version of an executable, or null if it cannot be read.</summary>
    private static Version? TryGetFileVersion(string path)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(path);
            return new Version(fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart, fvi.FilePrivatePart);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True when a DIFFERENT, OLDER copy is already installed than the one now running — i.e. the
    /// user launched a newer build and the installed version should be updated. Returns false when
    /// not installed, when running the installed copy itself, or when either version can't be read,
    /// so a version-read failure can never block a normal launch.
    /// </summary>
    public static bool IsUpdateAvailable()
    {
        try
        {
            if (!IsServiceInstalled() || !File.Exists(Constants.InstalledExePath))
            {
                return false;
            }

            string running = Path.GetFullPath(Environment.ProcessPath!);
            string installed = Path.GetFullPath(Constants.InstalledExePath);

            // Running the installed copy itself (e.g. logon auto-start) is never an update.
            if (string.Equals(running, installed, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Version? runningVer = TryGetFileVersion(running);
            Version? installedVer = TryGetFileVersion(installed);
            return runningVer is not null && installedVer is not null && runningVer > installedVer;
        }
        catch
        {
            return false;
        }
    }

    // ---- Tray restart after an update ----

    /// <summary>
    /// Terminates any OTHER running tray UI instances so a freshly updated tray can take over the
    /// single-instance slot. Best-effort and safe to call unelevated: tray processes run as the
    /// current interactive user (their module is readable, so they are stopped), whereas the
    /// LocalSystem service is not accessible to an unelevated caller (the probe throws and it is
    /// skipped). The current process is never targeted. Waits briefly for each stopped tray to exit
    /// so its single-instance mutex is released before a replacement launches.
    /// </summary>
    public static void StopOtherTrayInstances()
    {
        int self = Environment.ProcessId;
        string processName = Path.GetFileNameWithoutExtension(Constants.ExeFileName); // "MyNetworkTool"

        foreach (Process p in Process.GetProcessesByName(processName))
        {
            try
            {
                if (p.Id == self) continue;

                // Probe accessibility: reading the main module succeeds for our own session's tray
                // processes but throws for the LocalSystem service (access denied). A readable
                // MyNetworkTool process is therefore a tray to stop, not the service.
                _ = p.MainModule;

                p.Kill();
                p.WaitForExit(5000);
            }
            catch
            {
                // The service (inaccessible) or a process that already exited — leave it alone.
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    /// <summary>
    /// Launches the installed tray (the Program Files copy). Started unelevated because this caller
    /// is unelevated, which is required for the tray. Returns true if the process was started. Used
    /// after an update so the new version's tray replaces the one stopped by
    /// <see cref="StopOtherTrayInstances"/>.
    /// </summary>
    public static bool LaunchInstalledTray()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Constants.InstalledExePath,
                Arguments = "tray",
                UseShellExecute = false,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Asks the user whether they want to run the one-time elevated install now.</summary>
    public static bool PromptForInstall()
    {
        var r = MessageBox.Show(
            "MyNetworkTool is not installed yet.\n\n" +
            "The one-time setup needs administrator rights to register the background service. " +
            "After that, changing network settings will not prompt for UAC.\n\n" +
            "Install now?",
            "MyNetworkTool setup",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        return r == DialogResult.Yes;
    }

    /// <summary>Re-launches this exe elevated with the "install" argument.</summary>
    public static int RelaunchElevatedInstall() => RelaunchElevated("install");

    /// <summary>
    /// Re-launches the current executable elevated (UAC "runas") with the given mode argument,
    /// waits for it to exit, and returns its exit code. A declined UAC prompt is reported and
    /// returns 1.
    /// </summary>
    private static int RelaunchElevated(string mode)
    {
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = mode,
            UseShellExecute = true,
            Verb = "runas",
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                ShowMessage("Failed to launch the elevated installer process.", true);
                return 1;
            }

            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            ShowMessage("Administrator rights are required and were not granted.", true);
            return 1;
        }
    }

    // ---- Main entry points (called from Program.cs) ----

    /// <summary>
    /// Installs MyNetworkTool. If not elevated, re-launches elevated and returns the child's
    /// exit code. When elevated, copies the exe, registers + starts the service, and adds the
    /// logon startup entry.
    /// </summary>
    public static int Install()
    {
        if (!IsElevated())
        {
            return RelaunchElevated("install");
        }

        try
        {
            string source = Environment.ProcessPath!;

            // Whether a previous install is present decides the upgrade path (step 5) and the wording
            // of the success message (step 8): "updated" vs "installed".
            bool wasInstalled = ServiceControl.Exists(Constants.ServiceName);

            // 1-2. Ensure the install directory exists.
            Directory.CreateDirectory(Constants.InstallDir);

            // 3. Copy the running exe into Program Files (unless we are already running from there).
            if (!string.Equals(
                    Path.GetFullPath(source),
                    Path.GetFullPath(Constants.InstalledExePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Copy(source, Constants.InstalledExePath, overwrite: true);
                }
                catch
                {
                    // The target may be locked by a running service instance. Stop it and retry once.
                    try { ServiceControl.Stop(Constants.ServiceName); } catch { }
                    File.Copy(source, Constants.InstalledExePath, overwrite: true);
                }
            }

            // 4. Ensure the data directory exists (service log / presets).
            Directory.CreateDirectory(Constants.DataDir);

            // 5. (Re)register the service. The embedded quotes are essential so the SCM parses
            //    the Program Files path and the trailing "service" argument correctly.
            string binPath = "\"" + Constants.InstalledExePath + "\" service";

            if (wasInstalled)
            {
                // Idempotent / upgrade path: tear down the old registration so the binPath refreshes.
                ServiceControl.Stop(Constants.ServiceName);
                ServiceControl.Delete(Constants.ServiceName);
            }

            ServiceControl.Create(
                Constants.ServiceName,
                Constants.ServiceDisplayName,
                binPath,
                Constants.ServiceDescription);

            // 6. Start the freshly registered service.
            ServiceControl.Start(Constants.ServiceName);

            // 7. Add the logon startup entry. We write HKLM (not HKCU) on purpose: this installer
            //    runs elevated, and under over-the-shoulder UAC (a standard user elevating with a
            //    *different* admin's credentials — the core scenario for this tool) the elevated
            //    process's HKCU is the ADMIN's hive, so an HKCU write would never auto-start the
            //    tray for the standard user who actually uses it. HKLM\...\Run launches the tray
            //    for every interactive user at logon, unelevated, which is exactly what we want.
            using (var key = Registry.LocalMachine.CreateSubKey(Constants.RunRegistryKey, writable: true))
            {
                key.SetValue(Constants.RunValueName, "\"" + Constants.InstalledExePath + "\" tray");
            }

            // 8. Report success ("updated" when a previous install was present, else "installed").
            ShowMessage(
                (wasInstalled ? "MyNetworkTool updated successfully.\n\n" : "MyNetworkTool installed successfully.\n\n") +
                "- Service '" + Constants.ServiceName + "' is registered (LocalSystem, automatic start) and running.\n" +
                "- The tray app will auto-start at logon, or run MyNetworkTool.exe now.",
                false);

            // 9.
            return 0;
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, true);
            return 1;
        }
    }

    /// <summary>
    /// Uninstalls MyNetworkTool. If not elevated, re-launches elevated and returns the child's
    /// exit code. When elevated, stops + deletes the service, clears the startup entry, and
    /// best-effort removes the install folder.
    /// </summary>
    public static int Uninstall()
    {
        if (!IsElevated())
        {
            return RelaunchElevated("uninstall");
        }

        try
        {
            // 1-2. Stop and delete the service (tolerate every failure).
            try { ServiceControl.Stop(Constants.ServiceName); } catch { }
            try { ServiceControl.Delete(Constants.ServiceName); } catch { }

            // 3. Remove the HKLM Run value (matches where Install() wrote it).
            using (var key = Registry.LocalMachine.OpenSubKey(Constants.RunRegistryKey, writable: true))
            {
                key?.DeleteValue(Constants.RunValueName, throwOnMissingValue: false);
            }

            // 4. Best-effort delete of the install folder. A running image (this very exe, if it
            //    lives in InstallDir) cannot be deleted now; schedule the leftovers for removal on
            //    next reboot. MoveFileEx(dir, null, DELAY) only removes a directory at boot if it
            //    is EMPTY, so schedule each file first, then directories deepest-first, then root.
            bool filesRemoved;
            try
            {
                if (Directory.Exists(Constants.InstallDir))
                {
                    Directory.Delete(Constants.InstallDir, recursive: true);
                }
                filesRemoved = true;
            }
            catch
            {
                filesRemoved = false;
                try
                {
                    if (Directory.Exists(Constants.InstallDir))
                    {
                        foreach (var file in Directory.GetFiles(Constants.InstallDir, "*", SearchOption.AllDirectories))
                        {
                            try { MoveFileEx(file, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }
                        }
                        foreach (var dir in Directory.GetDirectories(Constants.InstallDir, "*", SearchOption.AllDirectories)
                                                     .OrderByDescending(d => d.Length))
                        {
                            try { MoveFileEx(dir, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }
                        }
                        try { MoveFileEx(Constants.InstallDir, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }
                    }
                }
                catch { /* scheduling is optional best-effort */ }
            }

            // 5. Report result.
            ShowMessage(
                "MyNetworkTool uninstalled. Service removed and startup entry cleared." +
                (filesRemoved
                    ? ""
                    : "\n\nInstall folder could not be fully removed (files in use); remove it manually if needed: " + Constants.InstallDir),
                false);

            // 6.
            return 0;
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, true);
            return 1;
        }
    }
}
