using System;
using System.IO;
using System.Text.Json;

namespace ClaudeUsageOrb;

internal sealed class Settings
{
    public static readonly int[] Sizes = { 40, 56, 72, 96, 128 };
    public static readonly int[] PollChoices = { 60, 120, 300 };

    /// <summary>Orb diameter in device-independent pixels.</summary>
    public int OrbSize { get; set; } = 72;
    public double Opacity { get; set; } = 1.0;

    /// <summary>Top-left of the orb window in physical screen pixels.</summary>
    public int? PosX { get; set; }
    public int? PosY { get; set; }

    public bool ShowOrb { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool LockPosition { get; set; }
    public bool ClickThrough { get; set; }
    public bool ShowPaceMarker { get; set; } = true;

    /// <summary>"session" = 5-hour window, "lowest" = whichever limit has the least left.</summary>
    public string CenterMetric { get; set; } = "session";

    public int PollSeconds { get; set; } = 120;

    /// <summary>Overrides CLAUDE_CONFIG_DIR / ~/.claude when set.</summary>
    public string? ClaudeConfigDir { get; set; }

    /// <summary>"ko", "en" or null for the system language.</summary>
    public string? Language { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"settings.json unreadable, using defaults: {ex.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Normalize();
            string tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save settings: {ex.Message}");
        }
    }

    private void Normalize()
    {
        OrbSize = Math.Clamp(OrbSize, 32, 160);
        Opacity = Math.Clamp(Opacity, 0.3, 1.0);
        PollSeconds = Math.Clamp(PollSeconds, 60, 900);
        if (CenterMetric is not ("session" or "lowest"))
        {
            CenterMetric = "session";
        }
    }
}
