using System;
using System.IO;
using NetworkingTool.Shared;

namespace NetworkingTool.Service;

/// <summary>
/// Tiny thread-safe file logger, intentionally independent of the host's <c>ILogger</c> so that
/// diagnostics always land on disk — even before (or after) the generic host is up, and even if
/// the EventLog provider is unavailable. Never throws: logging failures must not take down the
/// service.
/// </summary>
public static class ServiceLog
{
    private static readonly object _lock = new();

    /// <summary>Appends a single timestamped line to <see cref="Constants.ServiceLogPath"/>.</summary>
    public static void Write(string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Constants.DataDir);
                File.AppendAllText(
                    Constants.ServiceLogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }

    /// <summary>Appends a timestamped line plus the full exception detail.</summary>
    public static void Write(string message, Exception ex)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Constants.DataDir);
                File.AppendAllText(
                    Constants.ServiceLogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}{ex}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
