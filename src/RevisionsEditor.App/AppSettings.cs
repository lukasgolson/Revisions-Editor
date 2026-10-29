using System.IO;
using System.Text.Json;

namespace RevisionsEditor.App;

/// <summary>
/// Per-user settings stored in %APPDATA%\Revisions Editor\settings.json.
/// </summary>
public sealed class AppSettings
{
    private const int MaxRecentWorkspaces = 8;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Revisions Editor",
        "settings.json");

    public List<string> RecentWorkspaces { get; set; } = [];

    /// <summary>Show line numbers in the margin of the manuscript page.</summary>
    public bool ShowLineNumbers { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or unreadable settings file should never stop the app from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to persist them is not an error worth interrupting the user for.
        }
    }

    public void AddRecentWorkspace(string path)
    {
        RecentWorkspaces.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentWorkspaces.Insert(0, path);
        if (RecentWorkspaces.Count > MaxRecentWorkspaces)
        {
            RecentWorkspaces.RemoveRange(MaxRecentWorkspaces, RecentWorkspaces.Count - MaxRecentWorkspaces);
        }
    }

    public void RemoveRecentWorkspace(string path)
        => RecentWorkspaces.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
}
