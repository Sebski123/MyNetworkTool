using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace NetworkingTool.Install;

/// <summary>
/// Thin, robust wrappers over the Win32 Service Control Manager (SCM) via P/Invoke on
/// advapi32.dll. Using native CreateService instead of shelling out to sc.exe avoids all
/// command-line quoting ambiguity for the binPath (which contains a quoted Program Files
/// path plus the "service" argument).
///
/// Every public method opens its SCM/service handles, does its work in a try, and ALWAYS
/// closes the handles in a finally block. Failures throw a <see cref="Win32Exception"/>
/// carrying the last Win32 error plus contextual text, except <see cref="Exists"/> which is
/// deliberately lenient so it is safe to call unelevated.
/// </summary>
internal static class ServiceControl
{
    // ---- P/Invoke declarations (advapi32 Service Control Manager) ----

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateService(IntPtr hSCManager, string lpServiceName, string lpDisplayName,
        uint dwDesiredAccess, uint dwServiceType, uint dwStartType, uint dwErrorControl,
        string lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId, string? lpDependencies,
        string? lpServiceStartName, string? lpPassword);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool StartService(IntPtr hService, int dwNumServiceArgs, string[]? lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ChangeServiceConfig2(IntPtr hService, uint dwInfoLevel, ref SERVICE_DESCRIPTION lpInfo);

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS { public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SERVICE_DESCRIPTION { public string lpDescription; }

    // ---- SCM / service constants ----

    const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    const uint SC_MANAGER_CONNECT    = 0x0001;

    const uint SERVICE_ALL_ACCESS    = 0xF01FF;
    const uint SERVICE_QUERY_STATUS  = 0x0004;
    const uint SERVICE_START         = 0x0010;
    const uint SERVICE_STOP          = 0x0020;
    const uint SERVICE_DELETE        = 0x10000;

    const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    const uint SERVICE_AUTO_START        = 0x00000002;
    const uint SERVICE_ERROR_NORMAL      = 0x00000001;
    const uint SERVICE_CONTROL_STOP      = 0x00000001;

    // Current states (SERVICE_STATUS.dwCurrentState).
    const uint SERVICE_STOPPED       = 1;
    const uint SERVICE_START_PENDING = 2;
    const uint SERVICE_STOP_PENDING  = 3;
    const uint SERVICE_RUNNING       = 4;

    const uint SERVICE_CONFIG_DESCRIPTION = 1;

    // Common Win32 error codes we tolerate.
    const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    const int ERROR_SERVICE_EXISTS = 1073;
    const int ERROR_SERVICE_NOT_ACTIVE = 1062;

    /// <summary>
    /// Returns true if the named service is registered. Deliberately lenient: any failure to
    /// open the service (does-not-exist or otherwise) is treated as "not installed" and does
    /// NOT throw, so this is safe to call from an unelevated process. Opening the SCM with
    /// SC_MANAGER_CONNECT succeeds for non-administrators.
    /// </summary>
    internal static bool Exists(string serviceName)
    {
        IntPtr scm = IntPtr.Zero;
        IntPtr svc = IntPtr.Zero;
        try
        {
            scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Failed to open the Service Control Manager.");
            }

            svc = OpenService(scm, serviceName, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero)
            {
                // Any failure to open => treat as not installed (lenient).
                return false;
            }

            return true;
        }
        finally
        {
            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }

    /// <summary>
    /// Creates the service (LocalSystem, automatic start) and sets its description.
    /// Requires administrator rights (opens the SCM for ALL_ACCESS).
    /// </summary>
    internal static void Create(string serviceName, string displayName, string binaryPath, string description)
    {
        IntPtr scm = IntPtr.Zero;
        IntPtr svc = IntPtr.Zero;
        try
        {
            scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Failed to open the Service Control Manager (administrator rights required).");
            }

            svc = CreateService(
                scm,
                serviceName,
                displayName,
                SERVICE_ALL_ACCESS,
                SERVICE_WIN32_OWN_PROCESS,
                SERVICE_AUTO_START,
                SERVICE_ERROR_NORMAL,
                binaryPath,
                null,          // lpLoadOrderGroup
                IntPtr.Zero,   // lpdwTagId
                null,          // lpDependencies
                null,          // lpServiceStartName => LocalSystem
                null);         // lpPassword

            if (svc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_SERVICE_EXISTS)
                {
                    throw new Win32Exception(err,
                        $"The service '{serviceName}' already exists.");
                }

                throw new Win32Exception(err,
                    $"Failed to create the service '{serviceName}'.");
            }

            // Set the human-readable description; failure here is non-fatal.
            var d = new SERVICE_DESCRIPTION { lpDescription = description };
            ChangeServiceConfig2(svc, SERVICE_CONFIG_DESCRIPTION, ref d);
        }
        finally
        {
            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }

    /// <summary>Starts the service. ERROR_SERVICE_ALREADY_RUNNING (1056) is ignored.</summary>
    internal static void Start(string serviceName)
    {
        IntPtr scm = IntPtr.Zero;
        IntPtr svc = IntPtr.Zero;
        try
        {
            scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Failed to open the Service Control Manager.");
            }

            svc = OpenService(scm, serviceName, SERVICE_START | SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"Failed to open the service '{serviceName}' to start it.");
            }

            if (!StartService(svc, 0, null))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_SERVICE_ALREADY_RUNNING)
                {
                    return; // Already running is success for our purposes.
                }

                throw new Win32Exception(err,
                    $"Failed to start the service '{serviceName}'.");
            }
        }
        finally
        {
            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }

    /// <summary>
    /// Stops the service and waits up to ~15s for it to reach the STOPPED state. "Not running"
    /// conditions are tolerated.
    /// </summary>
    internal static void Stop(string serviceName)
    {
        IntPtr scm = IntPtr.Zero;
        IntPtr svc = IntPtr.Zero;
        try
        {
            scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Failed to open the Service Control Manager.");
            }

            svc = OpenService(scm, serviceName, SERVICE_STOP | SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero)
            {
                int openErr = Marshal.GetLastWin32Error();
                if (openErr == ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    return; // Nothing to stop.
                }

                throw new Win32Exception(openErr,
                    $"Failed to open the service '{serviceName}' to stop it.");
            }

            var status = new SERVICE_STATUS();
            if (QueryServiceStatus(svc, ref status) && status.dwCurrentState == SERVICE_STOPPED)
            {
                return; // Already stopped.
            }

            if (!ControlService(svc, SERVICE_CONTROL_STOP, ref status))
            {
                int ctrlErr = Marshal.GetLastWin32Error();
                // Tolerate "not running" / "does not exist" — the goal state is reached anyway.
                if (ctrlErr == ERROR_SERVICE_NOT_ACTIVE || ctrlErr == ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    return;
                }

                throw new Win32Exception(ctrlErr,
                    $"Failed to send the stop control to the service '{serviceName}'.");
            }

            // Poll for up to ~15 seconds (30 * 500ms) until STOPPED.
            for (int i = 0; i < 30; i++)
            {
                if (!QueryServiceStatus(svc, ref status))
                {
                    return; // Cannot query => give up gracefully.
                }

                if (status.dwCurrentState == SERVICE_STOPPED)
                {
                    return;
                }

                Thread.Sleep(500);
            }
        }
        finally
        {
            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }

    /// <summary>Deletes the service. ERROR_SERVICE_DOES_NOT_EXIST (1060) is tolerated.</summary>
    internal static void Delete(string serviceName)
    {
        IntPtr scm = IntPtr.Zero;
        IntPtr svc = IntPtr.Zero;
        try
        {
            scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Failed to open the Service Control Manager (administrator rights required).");
            }

            svc = OpenService(scm, serviceName, SERVICE_DELETE);
            if (svc == IntPtr.Zero)
            {
                int openErr = Marshal.GetLastWin32Error();
                if (openErr == ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    return; // Nothing to delete.
                }

                throw new Win32Exception(openErr,
                    $"Failed to open the service '{serviceName}' to delete it.");
            }

            if (!DeleteService(svc))
            {
                int delErr = Marshal.GetLastWin32Error();
                if (delErr == ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    return;
                }

                throw new Win32Exception(delErr,
                    $"Failed to delete the service '{serviceName}'.");
            }
        }
        finally
        {
            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }
}
