using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using NetworkingTool.Shared;

namespace NetworkingTool.Service;

/// <summary>
/// Manages machine-scope HTTP(S) proxy environment variables from a closed set of named presets.
/// Presets come from built-in defaults, optionally overridden by an admin-supplied presets.json in
/// the data directory. Only validated http/https URLs are ever written.
/// </summary>
public static class ProxyOperations
{
    /// <summary>Built-in presets used when no override file is present (looked up case-insensitively).</summary>
    private static ProxyPresetInfo[] BuiltInPresets() => new[]
    {
        new ProxyPresetInfo
        {
            Name = "LAN (squid1)",
            HttpProxy = "http://squid1.localdom.net:3128",
            HttpsProxy = "http://squid1.localdom.net:3128",
        },
        new ProxyPresetInfo
        {
            Name = "WiFi/VPN (squid2)",
            HttpProxy = "http://squid2.localdom.net:3128",
            HttpsProxy = "http://squid2.localdom.net:3128",
        },
    };

    public static ProxyPresetInfo[] GetPresets()
    {
        // Start from the built-ins keyed by name (case-insensitive).
        var merged = new Dictionary<string, ProxyPresetInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in BuiltInPresets())
        {
            merged[preset.Name] = preset;
        }

        try
        {
            if (File.Exists(Constants.PresetsConfigPath))
            {
                // Defense in depth: only trust the override file if it is owned by SYSTEM or
                // Administrators. The install-time ACL hardening already prevents a standard user
                // from creating it, but a file planted before that (or via any ACL gap) would be
                // owned by its non-privileged creator — ignore it so it cannot redirect the
                // machine-wide proxy.
                if (!IsTrustedConfigOwner(Constants.PresetsConfigPath))
                {
                    ServiceLog.Write("Ignoring presets.json: not owned by SYSTEM/Administrators.");
                }
                else
                {
                    string json = File.ReadAllText(Constants.PresetsConfigPath);
                    var fromConfig = IpcJson.Deserialize<ProxyPresetInfo[]>(json);
                    if (fromConfig is not null)
                    {
                        foreach (var preset in fromConfig)
                        {
                            if (preset is null || string.IsNullOrWhiteSpace(preset.Name)) continue;
                            merged[preset.Name] = preset; // config overrides built-ins by name
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Bad/unreadable config must not break the feature — fall back to built-ins only.
            ServiceLog.Write("Failed to read proxy presets config; using built-ins only.", ex);
        }

        var result = new ProxyPresetInfo[merged.Count];
        merged.Values.CopyTo(result, 0);
        return result;
    }

    /// <summary>
    /// True only if the file's owner is the LocalSystem account or the built-in Administrators
    /// group — the two principals the install-time ACL allows to write the data directory. Any
    /// other owner (e.g. a standard user who managed to create the file) is untrusted.
    /// </summary>
    private static bool IsTrustedConfigOwner(string path)
    {
        try
        {
            var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null) return false;

            return owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
        }
        catch
        {
            // If ownership cannot be read, treat the file as untrusted.
            return false;
        }
    }

    public static IpcResponse SetPreset(string? presetName, string? scope)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return IpcResponse.Fail("Preset name is required.");
            }

            ProxyPresetInfo? preset = null;
            foreach (var candidate in GetPresets())
            {
                if (string.Equals(candidate.Name, presetName, StringComparison.OrdinalIgnoreCase))
                {
                    preset = candidate;
                    break;
                }
            }

            if (preset is null)
            {
                return IpcResponse.Fail($"Unknown proxy preset '{presetName}'.");
            }

            if (!Validation.TryValidateProxyUrl(preset.HttpProxy, out var httpError))
            {
                return IpcResponse.Fail(httpError);
            }
            if (!Validation.TryValidateProxyUrl(preset.HttpsProxy, out var httpsError))
            {
                return IpcResponse.Fail(httpsError);
            }

            // The service runs as LocalSystem; setting User scope would target the service account,
            // not the interactive user, so this first draft always applies at Machine scope.
            Environment.SetEnvironmentVariable("HTTP_PROXY", preset.HttpProxy, EnvironmentVariableTarget.Machine);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", preset.HttpsProxy, EnvironmentVariableTarget.Machine);

            NativeMethods.BroadcastEnvironmentChange();

            string message = $"Applied proxy preset '{preset.Name}' (machine scope). HTTP_PROXY={preset.HttpProxy}";
            if (string.Equals(scope, "user", StringComparison.OrdinalIgnoreCase))
            {
                message += " Note: only machine scope is supported in this draft.";
            }

            return IpcResponse.Ok(message);
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    public static IpcResponse GetStatus(string? scope)
    {
        try
        {
            string? httpProxy = Environment.GetEnvironmentVariable("HTTP_PROXY", EnvironmentVariableTarget.Machine);
            string? httpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY", EnvironmentVariableTarget.Machine);

            string message =
                $"Current proxy (machine scope): HTTP_PROXY={(string.IsNullOrWhiteSpace(httpProxy) ? "(not set)" : httpProxy)}; " +
                $"HTTPS_PROXY={(string.IsNullOrWhiteSpace(httpsProxy) ? "(not set)" : httpsProxy)}";

            if (string.Equals(scope, "user", StringComparison.OrdinalIgnoreCase))
            {
                message += " Note: only machine scope is supported in this draft.";
            }

            return IpcResponse.Ok(message);
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    public static IpcResponse Reset(string? scope)
    {
        try
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", null, EnvironmentVariableTarget.Machine);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", null, EnvironmentVariableTarget.Machine);

            NativeMethods.BroadcastEnvironmentChange();

            return IpcResponse.Ok("Proxy environment variables removed (machine scope).");
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }
}
