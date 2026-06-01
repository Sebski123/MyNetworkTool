using System;

namespace NetworkingTool.Shared;

/// <summary>
/// Live IP/DNS/profile state for a single adapter, returned by the service for
/// <see cref="IpcAction.GetAdapterDetails"/> and shown in the tray when an adapter is selected.
/// The adapter's administrative on/off status is not duplicated here — that comes from the
/// <see cref="AdapterInfo.Status"/> already carried in the adapter list.
/// </summary>
public sealed class AdapterDetails
{
    /// <summary>NetworkInterface / Get-NetAdapter interface GUID, echoed back for correlation.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>True if IPv4 is configured for DHCP; false for a static configuration.</summary>
    public bool IsDhcpEnabled { get; set; }

    /// <summary>Configured IPv4 addresses with their CIDR prefix lengths.</summary>
    public IpAddressInfo[] Ipv4Addresses { get; set; } = Array.Empty<IpAddressInfo>();

    /// <summary>IPv4 default gateway (next hop for 0.0.0.0/0), if any.</summary>
    public string? Gateway { get; set; }

    /// <summary>Configured IPv4 DNS server addresses.</summary>
    public string[] DnsServers { get; set; } = Array.Empty<string>();

    /// <summary>Network connection profile name, if the adapter is connected.</summary>
    public string? ProfileName { get; set; }

    /// <summary>Network category for the profile ("Private", "Public", "DomainAuthenticated"), if any.</summary>
    public string? ProfileCategory { get; set; }
}

/// <summary>A single IPv4 address paired with its CIDR prefix length.</summary>
public sealed class IpAddressInfo
{
    public string Address { get; set; } = string.Empty;
    public int PrefixLength { get; set; }
}
