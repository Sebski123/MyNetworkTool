using System;
using System.Linq;
using System.Text;
using NetworkingTool.Shared;

namespace NetworkingTool.Service;

/// <summary>
/// Applies the closed set of network changes. Each method resolves the adapter first (to confirm
/// existence and obtain the integer interface index), then drives the change through
/// <c>powershell.exe</c> running a fixed Net* cmdlet script built only from validated/whitelisted
/// values. No arbitrary command path exists: the only process started is powershell.exe with a
/// constant argument list.
/// </summary>
public static class NetworkOperations
{
    public static IpcResponse SetStaticIp(
        string interfaceId, string ipAddress, int prefixLength, string? gateway, string[]? dnsServers)
    {
        try
        {
            var adapter = AdapterService.ResolveByIdOrThrow(interfaceId);
            if (IsAdapterDisabled(adapter))
            {
                return IpcResponse.Fail(
                    $"Adapter '{adapter.Name}' is disabled. Enable it before applying IP settings.");
            }

            int idx = adapter.InterfaceIndex;
            if (idx <= 0)
            {
                return IpcResponse.Fail(
                    $"Could not determine a valid interface index for adapter '{adapter.Name}' (it may be disabled).");
            }

            // Defense in depth: even though the caller normalizes IPs, refuse to embed anything
            // that is not a bare IP literal (hex digits, '.', ':') into the script. This makes
            // command injection impossible regardless of how this method is reached.
            if (!IsIpToken(ipAddress) ||
                (!string.IsNullOrEmpty(gateway) && !IsIpToken(gateway)) ||
                (dnsServers is not null && dnsServers.Any(d => !IsIpToken(d))))
            {
                return IpcResponse.Fail("Rejected: an address contained characters that are not valid in an IP literal.");
            }

            // Step 1 — address + (optional) default gateway. Applied as one transaction.
            var ipCommands = new StringBuilder();
            ipCommands.Append($"Set-NetIPInterface -InterfaceIndex {idx} -Dhcp Disabled -ErrorAction SilentlyContinue; ");
            ipCommands.Append($"Remove-NetIPAddress -InterfaceIndex {idx} -Confirm:$false -ErrorAction SilentlyContinue; ");
            ipCommands.Append($"Remove-NetRoute -InterfaceIndex {idx} -Confirm:$false -ErrorAction SilentlyContinue; ");
            ipCommands.Append($"New-NetIPAddress -InterfaceIndex {idx} -IPAddress '{ipAddress}' -PrefixLength {prefixLength}");
            if (!string.IsNullOrEmpty(gateway))
            {
                ipCommands.Append($" -DefaultGateway '{gateway}'");
            }
            ipCommands.Append("; ");

            var (ipExit, ipOut, ipErr) = PowerShellRunner.Run(PowerShellRunner.Wrap(ipCommands.ToString()));
            if (ipExit != 0)
            {
                return IpcResponse.Fail(ipErr.Trim().Length > 0 ? ipErr : ipOut);
            }

            // Step 2 — DNS servers as a SEPARATE invocation, so a DNS failure does not get reported
            // as if the (already-applied) address/gateway failed.
            if (dnsServers is { Length: > 0 })
            {
                string dnsList = string.Join(",", dnsServers.Select(d => $"'{d}'"));
                var dnsCommand = $"Set-DnsClientServerAddress -InterfaceIndex {idx} -ServerAddresses {dnsList}; ";

                var (dnsExit, dnsOut, dnsErr) = PowerShellRunner.Run(PowerShellRunner.Wrap(dnsCommand));
                if (dnsExit != 0)
                {
                    return IpcResponse.Fail(
                        "Static IP and gateway were applied, but setting DNS servers failed: " +
                        (dnsErr.Trim().Length > 0 ? dnsErr : dnsOut));
                }
            }

            return IpcResponse.Ok("Static IP applied successfully.");
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    public static IpcResponse SetDhcp(string interfaceId)
    {
        try
        {
            var adapter = AdapterService.ResolveByIdOrThrow(interfaceId);
            if (IsAdapterDisabled(adapter))
            {
                return IpcResponse.Fail(
                    $"Adapter '{adapter.Name}' is disabled. Enable it before applying IP settings.");
            }

            int idx = adapter.InterfaceIndex;
            if (idx <= 0)
            {
                return IpcResponse.Fail(
                    $"Could not determine a valid interface index for adapter '{adapter.Name}' (it may be disabled).");
            }

            var commands = new StringBuilder();
            commands.Append($"Set-DnsClientServerAddress -InterfaceIndex {idx} -ResetServerAddresses; ");
            commands.Append($"Remove-NetIPAddress -InterfaceIndex {idx} -Confirm:$false -ErrorAction SilentlyContinue; ");
            commands.Append($"Remove-NetRoute -InterfaceIndex {idx} -Confirm:$false -ErrorAction SilentlyContinue; ");
            commands.Append($"Set-NetIPInterface -InterfaceIndex {idx} -Dhcp Enabled; ");

            var (exitCode, stdout, stderr) = PowerShellRunner.Run(PowerShellRunner.Wrap(commands.ToString()));
            if (exitCode == 0)
            {
                return IpcResponse.Ok("Adapter set to DHCP successfully.");
            }

            return IpcResponse.Fail(stderr.Trim().Length > 0 ? stderr : stdout);
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    public static IpcResponse SetNetworkProfile(string interfaceId, string category)
    {
        try
        {
            var adapter = AdapterService.ResolveByIdOrThrow(interfaceId);
            int idx = adapter.InterfaceIndex;
            if (idx <= 0)
            {
                return IpcResponse.Fail(
                    $"Could not determine a valid interface index for adapter '{adapter.Name}' (it may be disabled).");
            }

            // category is already validated to the literal "Private"/"Public" by the caller.
            string command = $"Set-NetConnectionProfile -InterfaceIndex {idx} -NetworkCategory {category}; ";

            var (exitCode, stdout, stderr) = PowerShellRunner.Run(PowerShellRunner.Wrap(command));
            if (exitCode == 0)
            {
                return IpcResponse.Ok($"Network profile set to {category}.");
            }

            return IpcResponse.Fail(stderr.Trim().Length > 0 ? stderr : stdout);
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    public static IpcResponse SetAdapterEnabled(string interfaceId, bool enable)
    {
        try
        {
            var adapter = AdapterService.ResolveByIdOrThrow(interfaceId);
            int idx = adapter.InterfaceIndex;
            if (idx <= 0)
            {
                return IpcResponse.Fail(
                    $"Could not determine a valid interface index for adapter '{adapter.Name}'.");
            }

            // Embed ONLY the integer interface index (never the free-form adapter name, which Windows
            // lets an admin rename to arbitrary text and would otherwise be a script-injection sink).
            // Enable/Disable-NetAdapter take no -InterfaceIndex, so resolve by index via the pipeline.
            string verb = enable ? "Enable-NetAdapter" : "Disable-NetAdapter";
            string command = $"Get-NetAdapter -InterfaceIndex {idx} | {verb} -Confirm:$false; ";

            var (exitCode, stdout, stderr) = PowerShellRunner.Run(PowerShellRunner.Wrap(command));
            if (exitCode == 0)
            {
                return IpcResponse.Ok(
                    $"Adapter '{adapter.Name}' {(enable ? "enabled" : "disabled")} successfully.");
            }

            return IpcResponse.Fail(stderr.Trim().Length > 0 ? stderr : stdout);
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    public static IpcResponse ListAdapters(bool physicalOnly)
    {
        try
        {
            return new IpcResponse
            {
                Success = true,
                Message = "OK",
                Adapters = AdapterService.List(physicalOnly),
            };
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    /// <summary>
    /// True only if <paramref name="s"/> consists solely of characters valid in an IP literal
    /// (hex digits, '.', ':'). Used as a final guard before embedding a value into the script.
    /// </summary>
    private static bool IsIpToken(string? s) =>
        !string.IsNullOrEmpty(s) && s.All(c => char.IsAsciiHexDigit(c) || c == '.' || c == ':');

    private static bool IsAdapterDisabled(AdapterInfo adapter) =>
        string.Equals(adapter.Status, "Disabled", StringComparison.OrdinalIgnoreCase);
}
