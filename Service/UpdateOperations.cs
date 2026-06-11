using NetworkingTool.Install;
using NetworkingTool.Shared;

namespace NetworkingTool.Service;

/// <summary>
/// Privileged update operations exposed by the LocalSystem service.
/// </summary>
public static class UpdateOperations
{
    public static IpcResponse InstallLatestUpdate()
    {
        if (Installer.TryInstallLatestUpdateFromService(out string message))
        {
            return IpcResponse.Ok(message);
        }

        return IpcResponse.Fail(message);
    }
}
