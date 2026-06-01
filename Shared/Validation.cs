using System;
using System.Net;
using System.Net.Sockets;

namespace NetworkingTool.Shared;

/// <summary>
/// Pure input-validation helpers used by the service before it touches any system state.
/// Everything the elevated service acts on must pass through here first.
/// </summary>
public static class Validation
{
    /// <summary>Parses and validates an IPv4/IPv6 address.</summary>
    public static bool TryValidateIp(string? value, out IPAddress? ip, out string error)
    {
        ip = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "IP address is empty.";
            return false;
        }

        value = value.Trim();
        if (!IPAddress.TryParse(value, out var parsed))
        {
            error = $"'{value}' is not a valid IP address.";
            return false;
        }

        if (parsed.AddressFamily != AddressFamily.InterNetwork &&
            parsed.AddressFamily != AddressFamily.InterNetworkV6)
        {
            error = $"'{value}' is not an IPv4 or IPv6 address.";
            return false;
        }

        // Reject IPv6 zone/scope identifiers. IPAddress.TryParse accepts an arbitrary textual
        // zone id (e.g. "fe80::1%0...") and keeps it only in the raw string — the canonical
        // ToString() drops it. A zone id is meaningless for a configured gateway/DNS server and
        // is the exact carrier used to smuggle quotes/semicolons into a downstream command, so
        // it is refused outright here.
        if (value.Contains('%'))
        {
            error = "IPv6 zone/scope identifiers are not allowed in addresses.";
            return false;
        }

        ip = parsed;
        return true;
    }

    /// <summary>
    /// Validates an interface id (a <see cref="System.Net.NetworkInterface.Id"/> GUID) and re-emits it
    /// in canonical braced form. The canonical value contains only hex digits, hyphens and braces, so —
    /// like a re-emitted <see cref="IPAddress"/> — it is safe to embed in a PowerShell script (no quote,
    /// semicolon, space or newline can survive). Callers must embed <paramref name="canonical"/>, never
    /// the raw input.
    /// </summary>
    public static bool TryValidateInterfaceId(string? id, out string canonical, out string error)
    {
        canonical = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "interfaceId is required.";
            return false;
        }

        if (!Guid.TryParse(id.Trim(), out var g))
        {
            error = $"'{id}' is not a valid interface id.";
            return false;
        }

        canonical = g.ToString("B"); // {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}, lowercase hex only
        return true;
    }

    /// <summary>Validates a CIDR prefix length against the address family (0..32 v4, 0..128 v6).</summary>
    public static bool IsValidPrefixLength(int prefix, AddressFamily family)
    {
        int max = family == AddressFamily.InterNetworkV6 ? 128 : 32;
        return prefix >= 0 && prefix <= max;
    }

    /// <summary>Only "Private" and "Public" are allowed; returns the canonical casing.</summary>
    public static bool IsValidCategory(string? category, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(category)) return false;

        var c = category.Trim();
        if (string.Equals(c, "Private", StringComparison.OrdinalIgnoreCase)) { normalized = "Private"; return true; }
        if (string.Equals(c, "Public", StringComparison.OrdinalIgnoreCase)) { normalized = "Public"; return true; }
        return false;
    }

    /// <summary>Validates a proxy URL: must be an absolute http/https URL.</summary>
    public static bool TryValidateProxyUrl(string? value, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Proxy URL is empty.";
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            error = $"'{value}' is not a valid absolute URL.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = $"Proxy scheme '{uri.Scheme}' is not allowed (only http/https).";
            return false;
        }

        return true;
    }
}
