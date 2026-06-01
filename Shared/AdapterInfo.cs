using System;

namespace NetworkingTool.Shared;

/// <summary>
/// A snapshot of a network adapter, returned by the service for <see cref="IpcAction.ListAdapters"/>
/// and shown in the tray UI. <see cref="Id"/> (the interface GUID) is the stable identifier the
/// client passes back in subsequent requests; <see cref="InterfaceIndex"/> is what the service uses
/// internally with the Net* cmdlets.
/// </summary>
public sealed class AdapterInfo
{
    /// <summary>NetworkInterface.Id — the interface GUID string, e.g. "{2C1A...}". Stable across reboots.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Friendly connection name / alias, e.g. "Ethernet".</summary>
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>IPv4 interface index used with Set-NetIPInterface / New-NetIPAddress -InterfaceIndex.</summary>
    public int InterfaceIndex { get; set; }

    public string Status { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string[] IpAddresses { get; set; } = Array.Empty<string>();
    public bool IsDhcpEnabled { get; set; }

    public override string ToString() =>
        $"{Name}  [idx {InterfaceIndex}]  {Description}  ({Status})";
}
