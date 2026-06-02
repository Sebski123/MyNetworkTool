using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkingTool.Shared;

/// <summary>
/// The closed set of operations the elevated service will perform. The pipe protocol carries
/// only these structured actions — never raw command strings — which is the core security
/// boundary of the tool. Serialized as its string name (e.g. "SetStaticIp").
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IpcAction
{
    /// <summary>Connectivity/health check. Returns success with a version string.</summary>
    Ping,
    /// <summary>Read-only: enumerate network adapters. Result in <see cref="IpcResponse.Adapters"/>.</summary>
    ListAdapters,
    /// <summary>Read-only: live IP/DNS/profile state for one adapter. Result in <see cref="IpcResponse.Details"/>.</summary>
    GetAdapterDetails,
    /// <summary>Read-only: enumerate configured proxy presets. Result in <see cref="IpcResponse.Presets"/>.</summary>
    ListProxyPresets,
    /// <summary>Read-only: current machine proxy values, returned in <see cref="IpcResponse.Message"/>.</summary>
    GetProxyStatus,
    SetStaticIp,
    SetDhcp,
    SetNetworkProfile,
    /// <summary>Administratively enable or disable an adapter (see <see cref="IpcRequest.Enable"/>).</summary>
    SetAdapterEnabled,
    SetProxyPreset,
    ResetProxy
}

/// <summary>
/// A single request sent over the pipe as one line of JSON. Only fields relevant to the chosen
/// <see cref="Action"/> are populated; the service validates everything before acting.
/// </summary>
public sealed class IpcRequest
{
    public IpcAction Action { get; set; }

    /// <summary>Stable adapter identifier — NetworkInterface.Id (the interface GUID). Preferred over name.</summary>
    public string? InterfaceId { get; set; }

    public string? IpAddress { get; set; }
    public int? PrefixLength { get; set; }
    public string? Gateway { get; set; }
    public string[]? DnsServers { get; set; }

    /// <summary>For <see cref="IpcAction.ListAdapters"/>: restrict to physical adapters (Get-NetAdapter -Physical).</summary>
    public bool? PhysicalOnly { get; set; }

    /// <summary>For <see cref="IpcAction.SetAdapterEnabled"/>: true enables the adapter, false disables it.</summary>
    public bool? Enable { get; set; }

    /// <summary>"Private" or "Public" only.</summary>
    public string? Category { get; set; }

    /// <summary>Name of a server-known proxy preset.</summary>
    public string? PresetName { get; set; }

    /// <summary>"Machine" (default) or "User". First draft applies at Machine scope.</summary>
    public string? ProxyScope { get; set; }
}

/// <summary>The single line of JSON the service writes back for every request.</summary>
public sealed class IpcResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>Populated only for <see cref="IpcAction.ListAdapters"/>.</summary>
    public AdapterInfo[]? Adapters { get; set; }

    /// <summary>Populated only for <see cref="IpcAction.GetAdapterDetails"/>.</summary>
    public AdapterDetails? Details { get; set; }

    /// <summary>Populated only for <see cref="IpcAction.ListProxyPresets"/>.</summary>
    public ProxyPresetInfo[]? Presets { get; set; }

    public static IpcResponse Ok(string message) => new() { Success = true, Message = message };
    public static IpcResponse Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>Describes a proxy preset for display in the tray UI (values are server-defined).</summary>
public sealed class ProxyPresetInfo
{
    public string Name { get; set; } = string.Empty;
    public string? HttpProxy { get; set; }
    public string? HttpsProxy { get; set; }
}

/// <summary>
/// Shared JSON settings so client and server serialize identically. Web defaults give
/// camelCase property names and case-insensitive reads, matching the documented wire format
/// (e.g. {"action":"SetStaticIp","interfaceId":"...","ipAddress":"..."}).
/// </summary>
public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
