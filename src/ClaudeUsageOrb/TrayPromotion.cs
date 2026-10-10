using System;
using Microsoft.Win32;

namespace ClaudeUsageOrb;

/// <summary>
/// Windows 11 hides new tray icons in the overflow (^) by default. Explorer keeps the per-app choice in
/// HKCU\Control Panel\NotifyIconSettings\&lt;id&gt;\IsPromoted, the same value the Settings app writes.
/// We only ever touch the entry that belongs to this executable, and only when the user asks.
/// </summary>
internal static class TrayPromotion
{
    private const string Root = @"Control Panel\NotifyIconSettings";

    /// <summary>null when Windows has not registered our icon yet (or on Windows 10).</summary>
    public static bool? IsPromoted()
    {
        try
        {
            using RegistryKey? key = FindOwnKey(writable: false);
            return key is null ? null : key.GetValue("IsPromoted") is int value && value == 1;
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading tray promotion failed: {ex.Message}");
            return null;
        }
    }

    public static void Set(bool promoted)
    {
        try
        {
            using RegistryKey? key = FindOwnKey(writable: true);
            key?.SetValue("IsPromoted", promoted ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Warn($"Changing tray promotion failed: {ex.Message}");
        }
    }

    private static RegistryKey? FindOwnKey(bool writable)
    {
        string? self = Environment.ProcessPath;
        if (self is null)
        {
            return null;
        }
        using RegistryKey? root = Registry.CurrentUser.OpenSubKey(Root);
        if (root is null)
        {
            return null;
        }
        foreach (string name in root.GetSubKeyNames())
        {
            RegistryKey? sub = root.OpenSubKey(name, writable);
            // System apps are stored as "{KnownFolderGuid}\app.exe"; ours is a plain absolute path.
            if (sub?.GetValue("ExecutablePath") is string path && string.Equals(path, self, StringComparison.OrdinalIgnoreCase))
            {
                return sub;
            }
            sub?.Dispose();
        }
        return null;
    }
}
