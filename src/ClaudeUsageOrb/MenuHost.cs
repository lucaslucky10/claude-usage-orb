using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClaudeUsageOrb;

internal sealed record MenuSpec(string Text, Action? Click = null, bool? Checked = null, IReadOnlyList<MenuSpec>? Children = null, bool Separator = false)
{
    public static MenuSpec Line { get; } = new("", Separator: true);
}

/// <summary>
/// Opens a WPF context menu from the orb or the tray without stealing focus from the app the user is in.
/// Because the menu is not in the foreground, WPF would not notice clicks elsewhere, so while it is open
/// low-level mouse/keyboard hooks close it on any click outside this process or on Esc.
/// </summary>
internal sealed class MenuHost
{
    private readonly Window anchor;
    private readonly Native.LowLevelProc mouseProc;
    private readonly Native.LowLevelProc keyboardProc;
    private IntPtr mouseHook;
    private IntPtr keyboardHook;
    private ContextMenu? open;

    public MenuHost()
    {
        anchor = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            Topmost = true,
            Title = "Claude Usage Orb menu",
        };
        anchor.SourceInitialized += (_, _) =>
            Native.SetExStyleFlag(new WindowInteropHelper(anchor).Handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, true);
        mouseProc = OnMouse;
        keyboardProc = OnKeyboard;
    }

    public bool IsOpen => open is not null;

    public void Show(IReadOnlyList<MenuSpec> items)
    {
        Close();

        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = anchor };
        ApplyTheme(menu);
        foreach (MenuSpec spec in items)
        {
            menu.Items.Add(Build(spec));
        }

        anchor.Show();
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(open, menu))
            {
                open = null;
                Unhook();
                anchor.Hide();
            }
        };
        open = menu;
        menu.IsOpen = true;
        Hook();
    }

    public void Close() => open?.SetCurrentValue(ContextMenu.IsOpenProperty, false);

    private void Hook()
    {
        IntPtr module = Native.GetModuleHandle(null);
        if (mouseHook == IntPtr.Zero)
        {
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
        }
        if (keyboardHook == IntPtr.Zero)
        {
            keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyboardProc, module, 0);
        }
    }

    private void Unhook()
    {
        if (mouseHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(mouseHook);
            mouseHook = IntPtr.Zero;
        }
        if (keyboardHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(keyboardHook);
            keyboardHook = IntPtr.Zero;
        }
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && open is not null && (int)wParam is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            IntPtr target = Native.WindowFromPoint(info.pt);
            Native.GetWindowThreadProcessId(target, out uint pid);
            if (pid != (uint)Environment.ProcessId)
            {
                // Never block the click itself; just close the menu once the hook has returned.
                anchor.Dispatcher.BeginInvoke(Close);
            }
        }
        return Native.CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    private IntPtr OnKeyboard(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && open is not null && (int)wParam is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (info.vkCode == Native.VK_ESCAPE)
            {
                anchor.Dispatcher.BeginInvoke(Close);
            }
        }
        return Native.CallNextHookEx(keyboardHook, code, wParam, lParam);
    }

    private static object Build(MenuSpec spec)
    {
        if (spec.Separator)
        {
            return new Separator();
        }
        var item = new MenuItem { Header = spec.Text };
        if (spec.Checked is { } isChecked)
        {
            // The Fluent template only draws the check glyph for checkable items. The menu is rebuilt
            // on every open, so WPF's own toggle-on-click never leaks into the settings.
            item.IsCheckable = true;
            item.IsChecked = isChecked;
        }
        if (spec.Children is { Count: > 0 } children)
        {
            foreach (MenuSpec child in children)
            {
                item.Items.Add(Build(child));
            }
        }
        else if (spec.Click is { } click)
        {
            item.Click += (_, _) => click();
        }
        return item;
    }

    private static ResourceDictionary? fluent;

    /// <summary>Uses the Windows 11 (Fluent) look in the system's light/dark mode when available.</summary>
    private static void ApplyTheme(ContextMenu menu)
    {
        try
        {
            if (fluent is null)
            {
                string variant = AppsUseLightTheme() ? "Light" : "Dark";
                fluent = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.{variant}.xaml", UriKind.Absolute),
                };
            }
            menu.Resources.MergedDictionaries.Add(fluent);
        }
        catch (Exception ex)
        {
            Log.Warn($"Fluent menu theme unavailable: {ex.Message}");
            fluent = new ResourceDictionary();
        }
    }

    private static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }
}
