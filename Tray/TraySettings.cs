using System;
using System.IO;
using System.Text.Json;
using NetworkingTool.Shared;

namespace NetworkingTool.Tray;

/// <summary>
/// Per-user tray UI preferences, persisted as a small JSON file under
/// <c>%LOCALAPPDATA%\MyNetworkTool\ui-settings.json</c>. These are presentation-only choices made by
/// the unelevated tray (not privileged state), so they live in the user profile, separate from the
/// service's machine-wide data under ProgramData. All IO is best-effort: a missing or corrupt file
/// simply yields defaults, and a failed save is swallowed — a UI preference must never break the app.
/// </summary>
public sealed class TraySettings
{
    /// <summary>When true, the adapter list shows only physical adapters (Get-NetAdapter -Physical).</summary>
    public bool PhysicalOnly { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Constants.AppName,
        "ui-settings.json");

    public static TraySettings Load()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path)) return new TraySettings();

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TraySettings>(json) ?? new TraySettings();
        }
        catch
        {
            return new TraySettings();
        }
    }

    public void Save()
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Best effort — losing a UI preference must never surface as an error to the user.
        }
    }
}
