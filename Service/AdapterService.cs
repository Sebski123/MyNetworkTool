using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using NetworkingTool.Shared;

namespace NetworkingTool.Service;

/// <summary>
/// Read-only enumeration and lookup of network adapters via PowerShell <c>Get-NetAdapter</c> — the
/// same authoritative, visible set Windows shows in Control Panel (and that the <c>-Physical</c>
/// switch narrows to hardware adapters). Producing the list this way (rather than from
/// <see cref="System.Net.NetworkInformation.NetworkInterface"/>, which also returns dozens of hidden
/// pseudo-interfaces and drops administratively-disabled adapters) gives us exactly the adapters the
/// user expects, and keeps disabled adapters listable so they can be re-enabled.
///
/// Every script embeds only constant switches and the integer interface index resolved here — never
/// a caller-supplied GUID or name — matching the injection-safety model of <see cref="NetworkOperations"/>.
/// </summary>
public static class AdapterService
{
    public static AdapterInfo[] List(bool physicalOnly)
    {
        // One compact JSON object per line (JSON Lines) so a single adapter does not get collapsed
        // into a bare object the way `... | ConvertTo-Json` does in Windows PowerShell 5.1.
        string physical = physicalOnly ? "-Physical " : string.Empty;
        string script =
            $"Get-NetAdapter {physical}| ForEach-Object {{ " +
            "[pscustomobject]@{ " +
            "Id=[string]$_.InterfaceGuid; " +
            "Name=[string]$_.Name; " +
            "Description=[string]$_.InterfaceDescription; " +
            "InterfaceIndex=[int]$_.ifIndex; " +
            "Status=[string]$_.Status; " +
            "Type=[string]$_.MediaType; " +
            "MacAddress=[string]$_.MacAddress " +
            "} | ConvertTo-Json -Compress }";

        var (exitCode, stdout, stderr) = PowerShellRunner.Run(script);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                "Get-NetAdapter failed: " + (stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim()));
        }

        var results = new List<AdapterInfo>();
        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') continue;

            try
            {
                var info = IpcJson.Deserialize<AdapterInfo>(trimmed);
                if (info is not null) results.Add(info);
            }
            catch
            {
                // Skip a malformed line rather than failing the whole enumeration.
            }
        }

        return results.ToArray();
    }

    /// <summary>
    /// Returns the live IPv4 / DNS / connection-profile state for a single adapter. Best-effort:
    /// any piece of configuration that is absent comes back empty rather than throwing.
    ///
    /// Resolution (GUID -> interface index) is folded into the SAME PowerShell invocation as the
    /// state query, so this runs <b>one</b> powershell.exe rather than two (a separate
    /// <see cref="ResolveByIdOrThrow"/> enumeration plus the query) — halving the process-startup
    /// cost that dominates this read. The only non-constant text embedded is the canonical braced
    /// GUID from <see cref="Validation.TryValidateInterfaceId"/> (hex/hyphen/brace only), keeping the
    /// injection-safety model intact.
    /// </summary>
    public static AdapterDetails GetDetails(string interfaceId)
    {
        if (!Validation.TryValidateInterfaceId(interfaceId, out var guid, out var idError))
        {
            throw new ArgumentException(idError);
        }

        var details = new AdapterDetails { Id = interfaceId };

        // One process: resolve the adapter by its (validated, canonical) GUID, then read its IPv4
        // state. Emit a single flat JSON object. IPs and DNS servers are joined into comma-separated
        // strings (not nested JSON arrays) to avoid PowerShell 5.1's single-element-array collapse;
        // we split them back into typed arrays below. PowerShell string -eq is case-insensitive, so
        // the braced-lowercase $g matches Get-NetAdapter's braced-uppercase InterfaceGuid; a $null
        // InterfaceGuid simply fails to match rather than throwing. Name is carried in every shape so
        // the result matches the pre-merge behaviour (which always set it).
        string script =
            $"$g='{guid}'; " +
            "$a=Get-NetAdapter | Where-Object { $_.InterfaceGuid -eq $g } | Select-Object -First 1; " +
            "if(-not $a){ Write-Output '{\"NotFound\":true}'; exit 0 } " +
            "$idx=[int]$a.ifIndex; " +
            "if($idx -le 0){ [pscustomobject]@{ Name=[string]$a.Name } | ConvertTo-Json -Compress; exit 0 } " +
            "$ci=Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue; " +
            "$ad=Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue; " +
            "$dns=(Get-DnsClientServerAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1).ServerAddresses; " +
            "$gw=(Get-NetRoute -InterfaceIndex $idx -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Select-Object -First 1).NextHop; " +
            "$pr=Get-NetConnectionProfile -InterfaceIndex $idx -ErrorAction SilentlyContinue | Select-Object -First 1; " +
            "[pscustomobject]@{ " +
            "Name=[string]$a.Name; " +
            "IsDhcpEnabled=[bool]($ci.Dhcp -eq 'Enabled'); " +
            "Ipv4=(@($ad | ForEach-Object { \"$($_.IPAddress)/$($_.PrefixLength)\" }) -join ','); " +
            "Gateway=[string]$gw; " +
            "Dns=(@($dns | ForEach-Object { [string]$_ }) -join ','); " +
            "ProfileName=[string]$pr.Name; " +
            "ProfileCategory=[string]$pr.NetworkCategory " +
            "} | ConvertTo-Json -Compress";

        var (exitCode, stdout, stderr) = PowerShellRunner.Run(script);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                "Reading adapter state failed: " + (stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim()));
        }

        var jsonLine = stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith('{'));
        if (jsonLine is null) return details;

        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;

        // An adapter with this GUID must exist — the UI only asks about ids it just enumerated, and
        // a missing one is a real error (matches ResolveByIdOrThrow / the documented wire contract).
        if (root.TryGetProperty("NotFound", out var nf) && nf.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException("Adapter not found: " + interfaceId);
        }

        details.Name = GetString(root, "Name");
        details.IsDhcpEnabled = root.TryGetProperty("IsDhcpEnabled", out var d) &&
            d.ValueKind == JsonValueKind.True;
        details.Gateway = NullIfEmpty(GetString(root, "Gateway"));
        details.ProfileName = NullIfEmpty(GetString(root, "ProfileName"));
        details.ProfileCategory = NullIfEmpty(GetString(root, "ProfileCategory"));
        details.DnsServers = SplitList(GetString(root, "Dns"));
        details.Ipv4Addresses = SplitList(GetString(root, "Ipv4"))
            .Select(ParseIpAddress)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToArray();

        return details;
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? string.Empty
            : string.Empty;

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string[] SplitList(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? Array.Empty<string>()
            : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IpAddressInfo? ParseIpAddress(string addrPrefix)
    {
        // Expected form "192.168.1.10/24".
        var parts = addrPrefix.Split('/', 2);
        if (parts.Length == 0 || parts[0].Length == 0) return null;

        int prefix = 0;
        if (parts.Length == 2)
        {
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out prefix);
        }

        return new IpAddressInfo { Address = parts[0], PrefixLength = prefix };
    }

    /// <summary>
    /// Confirms an adapter exists by its stable <c>Id</c> and returns it so callers can use the
    /// resolved <see cref="AdapterInfo.InterfaceIndex"/> with the Net* cmdlets. Resolves against the
    /// full visible set (including physical-only-filtered and disabled adapters).
    /// </summary>
    public static AdapterInfo ResolveByIdOrThrow(string? interfaceId)
    {
        if (string.IsNullOrEmpty(interfaceId))
        {
            throw new ArgumentException("interfaceId is required.");
        }

        var match = List(physicalOnly: false).FirstOrDefault(
            a => string.Equals(a.Id, interfaceId, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            throw new InvalidOperationException("Adapter not found: " + interfaceId);
        }

        return match;
    }
}
