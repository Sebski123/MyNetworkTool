using System;
using System.Collections.Generic;
using NetworkingTool.Shared;

namespace NetworkingTool.Service;

/// <summary>
/// Maps a deserialized <see cref="IpcRequest"/> to one of the closed set of operations. This is the
/// server-side enforcement point: every input is validated here (via <see cref="Validation"/>)
/// before any system state is touched. The wire protocol carries only structured actions, never raw
/// command strings.
/// </summary>
public static class RequestHandler
{
    public static IpcResponse Handle(IpcRequest req)
    {
        IpcResponse response;
        try
        {
            switch (req.Action)
            {
                case IpcAction.Ping:
                    response = IpcResponse.Ok("MyNetworkTool service is running. protocol=2");
                    break;

                case IpcAction.ListAdapters:
                    response = NetworkOperations.ListAdapters(req.PhysicalOnly ?? false);
                    break;

                case IpcAction.GetAdapterDetails:
                    response = string.IsNullOrWhiteSpace(req.InterfaceId)
                        ? IpcResponse.Fail("InterfaceId is required.")
                        : new IpcResponse
                        {
                            Success = true,
                            Message = "OK",
                            Details = AdapterService.GetDetails(req.InterfaceId!),
                        };
                    break;

                case IpcAction.ListProxyPresets:
                    response = new IpcResponse
                    {
                        Success = true,
                        Message = "OK",
                        Presets = ProxyOperations.GetPresets(),
                    };
                    break;

                case IpcAction.GetProxyStatus:
                    response = ProxyOperations.GetStatus(req.ProxyScope);
                    break;

                case IpcAction.SetStaticIp:
                    response = HandleSetStaticIp(req);
                    break;

                case IpcAction.SetDhcp:
                    response = string.IsNullOrWhiteSpace(req.InterfaceId)
                        ? IpcResponse.Fail("InterfaceId is required.")
                        : NetworkOperations.SetDhcp(req.InterfaceId!);
                    break;

                case IpcAction.SetNetworkProfile:
                    response = HandleSetNetworkProfile(req);
                    break;

                case IpcAction.SetAdapterEnabled:
                    response = (string.IsNullOrWhiteSpace(req.InterfaceId) || !req.Enable.HasValue)
                        ? IpcResponse.Fail("InterfaceId and Enable are required.")
                        : NetworkOperations.SetAdapterEnabled(req.InterfaceId!, req.Enable!.Value);
                    break;

                case IpcAction.SetProxyPreset:
                    response = ProxyOperations.SetPreset(req.PresetName, req.ProxyScope);
                    break;

                case IpcAction.ResetProxy:
                    response = ProxyOperations.Reset(req.ProxyScope);
                    break;

                case IpcAction.InstallLatestUpdate:
                    response = UpdateOperations.InstallLatestUpdate();
                    break;

                default:
                    response = IpcResponse.Fail("Unsupported action.");
                    break;
            }
        }
        catch (Exception ex)
        {
            response = IpcResponse.Fail(ex.Message);
        }

        // Log action + outcome only; never secrets or full request payloads.
        ServiceLog.Write($"Handled action {req.Action}: success={response.Success} message={response.Message}");
        return response;
    }

    private static IpcResponse HandleSetStaticIp(IpcRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.InterfaceId))
        {
            return IpcResponse.Fail("InterfaceId is required.");
        }

        if (!Validation.TryValidateIp(req.IpAddress, out var ip, out var ipError))
        {
            return IpcResponse.Fail(ipError);
        }

        if (!req.PrefixLength.HasValue ||
            !Validation.IsValidPrefixLength(req.PrefixLength.Value, ip!.AddressFamily))
        {
            return IpcResponse.Fail(
                $"Invalid prefix length for the supplied IP address ({ip!.AddressFamily}).");
        }

        // Normalize the gateway through the parsed IPAddress: ToString() emits only canonical
        // hex/dot/colon, so no quote/semicolon/space from the raw client string can survive into
        // the downstream PowerShell command. Never forward req.Gateway verbatim.
        string? gatewayNorm = null;
        if (!string.IsNullOrWhiteSpace(req.Gateway))
        {
            if (!Validation.TryValidateIp(req.Gateway, out var gw, out var gatewayError))
            {
                return IpcResponse.Fail(gatewayError);
            }
            gatewayNorm = gw!.ToString();
        }

        // Likewise normalize every DNS server to its canonical form.
        string[]? dnsNorm = null;
        if (req.DnsServers is not null && req.DnsServers.Length > 0)
        {
            var list = new List<string>(req.DnsServers.Length);
            foreach (var dns in req.DnsServers)
            {
                if (!Validation.TryValidateIp(dns, out var d, out var dnsError))
                {
                    return IpcResponse.Fail(dnsError);
                }
                list.Add(d!.ToString());
            }
            dnsNorm = list.ToArray();
        }

        return NetworkOperations.SetStaticIp(
            req.InterfaceId!, ip!.ToString(), req.PrefixLength!.Value, gatewayNorm, dnsNorm);
    }

    private static IpcResponse HandleSetNetworkProfile(IpcRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.InterfaceId))
        {
            return IpcResponse.Fail("InterfaceId is required.");
        }

        if (!Validation.IsValidCategory(req.Category, out var category))
        {
            return IpcResponse.Fail("Category must be Private or Public.");
        }

        return NetworkOperations.SetNetworkProfile(req.InterfaceId!, category);
    }
}
