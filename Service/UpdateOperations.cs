using System.Threading;
using NetworkingTool.Install;
using NetworkingTool.Shared;

namespace NetworkingTool.Service;

/// <summary>
/// Privileged update operations exposed by the LocalSystem service.
/// </summary>
public static class UpdateOperations
{
    // Single-flight guard: 0 = idle, 1 = an install is running. An update download can take minutes
    // and holds a pipe-server slot for its duration; without this, a handful of concurrent
    // InstallLatestUpdate requests could occupy every slot at once and starve the service. Extra
    // callers are rejected immediately instead of each starting their own download.
    private static int _installInProgress;

    public static IpcResponse InstallLatestUpdate()
    {
        if (Interlocked.CompareExchange(ref _installInProgress, 1, 0) != 0)
        {
            return IpcResponse.Fail("An update installation is already in progress. Please wait for it to finish.");
        }

        try
        {
            return Installer.TryInstallLatestUpdateFromService(out string message)
                ? IpcResponse.Ok(message)
                : IpcResponse.Fail(message);
        }
        finally
        {
            Interlocked.Exchange(ref _installInProgress, 0);
        }
    }
}
