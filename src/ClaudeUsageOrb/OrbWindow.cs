using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClaudeUsageOrb;

/// <summary>Borderless, transparent, non-activating, always-on-top window hosting the orb.</summary>
internal sealed class OrbWindow : Window
{
    public const double ShadowMargin = 6;

    private readonly Settings settings;

    public OrbWindow(Settings settings)
    {
        this.settings = settings;
        Title = "Claude Usage Orb";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = settings.AlwaysOnTop;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        Focusable = false;

        Visual = new OrbVisual { ShadowMargin = ShadowMargin };
        Content = Visual;
        ApplySizeOnly();
        Opacity = settings.Opacity;

        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            Native.SetExStyleFlag(Handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, true);
            Native.SetExStyleFlag(Handle, Native.WS_EX_APPWINDOW, false);
            ApplyClickThrough();
            RestorePosition();
        };

        MouseLeftButtonDown += OnLeftDown;
        MouseRightButtonUp += (_, e) => { e.Handled = true; MenuRequested?.Invoke(); };
        MouseEnter += (_, _) => HoverChanged?.Invoke(true);
        MouseLeave += (_, _) => HoverChanged?.Invoke(false);
    }

    public OrbVisual Visual { get; }
    public IntPtr Handle { get; private set; }

    public event Action? RefreshRequested;
    public event Action? MenuRequested;
    public event Action<bool>? HoverChanged;
    public event Action? Moved;

    private double Scale => Native.ScaleFor(Handle);
    private int MarginPx => (int)Math.Round(ShadowMargin * Scale);

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            RefreshRequested?.Invoke();
            return;
        }
        if (settings.LockPosition || Handle == IntPtr.Zero)
        {
            return;
        }

        Native.GetWindowRect(Handle, out var before);
        HoverChanged?.Invoke(false);
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        Native.GetWindowRect(Handle, out var after);
        if (before.Left != after.Left || before.Top != after.Top)
        {
            SnapAndSave();
            Moved?.Invoke();
        }
    }

    public void ApplyClickThrough()
    {
        if (Handle != IntPtr.Zero)
        {
            Native.SetExStyleFlag(Handle, Native.WS_EX_TRANSPARENT, settings.ClickThrough);
        }
    }

    private void ApplySizeOnly()
    {
        double outer = settings.OrbSize + ShadowMargin * 2;
        Width = outer;
        Height = outer;
        Visual.Diameter = settings.OrbSize;
        Visual.InvalidateVisual();
    }

    /// <summary>Changes the orb size while keeping its center where it was.</summary>
    public void ApplySize()
    {
        if (Handle == IntPtr.Zero)
        {
            ApplySizeOnly();
            return;
        }
        Native.GetWindowRect(Handle, out var r);
        int cx = (r.Left + r.Right) / 2;
        int cy = (r.Top + r.Bottom) / 2;
        ApplySizeOnly();
        int px = (int)Math.Round((settings.OrbSize + ShadowMargin * 2) * Scale);
        Native.SetWindowPos(Handle, IntPtr.Zero, cx - px / 2, cy - px / 2, px, px, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        SnapAndSave();
    }

    public void BringToFrontTopmost()
    {
        if (Handle == IntPtr.Zero || !IsVisible)
        {
            return;
        }
        Native.SetWindowPos(Handle, settings.AlwaysOnTop ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    private void RestorePosition()
    {
        int size = (int)Math.Round((settings.OrbSize + ShadowMargin * 2) * Native.ScaleFor(IntPtr.Zero));
        if (settings.PosX is { } x && settings.PosY is { } y)
        {
            var rect = new Native.RECT(x, y, x + size, y + size);
            if (Native.MonitorFromRect(ref rect, Native.MONITOR_DEFAULTTONULL) != IntPtr.Zero)
            {
                Native.SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                return;
            }
        }
        MoveToDefault(save: false);
    }

    /// <summary>Bottom-right corner of the primary work area, just above the taskbar and next to the tray.</summary>
    public void MoveToDefault(bool save = true)
    {
        IntPtr monitor = Native.MonitorFromWindow(Native.FindWindow("Shell_TrayWnd", null), Native.MONITOR_DEFAULTTONEAREST);
        var work = Native.GetMonitorInfo(monitor).rcWork;
        Native.GetWindowRect(Handle, out var r);
        int gap = (int)Math.Round(10 * Scale) - MarginPx;
        Native.SetWindowPos(Handle, IntPtr.Zero, work.Right - r.Width - gap, work.Bottom - r.Height - gap, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        if (save)
        {
            SnapAndSave();
        }
        else
        {
            Native.GetWindowRect(Handle, out var placed);
            settings.PosX = placed.Left;
            settings.PosY = placed.Top;
        }
    }

    /// <summary>Puts the orb on the taskbar itself, sized to fit it, left of the notification area.</summary>
    public void DockOntoTaskbar()
    {
        IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
        if (Native.GetTaskbarRect() is not { } bar || tray == IntPtr.Zero)
        {
            MoveToDefault();
            return;
        }
        double scale = Native.ScaleFor(tray);
        bool horizontal = bar.Width > bar.Height;
        double thicknessDip = (horizontal ? bar.Height : bar.Width) / scale;
        settings.OrbSize = (int)Math.Clamp(Math.Round(thicknessDip - 8), 32, 72);
        ApplySizeOnly();

        int px = (int)Math.Round((settings.OrbSize + ShadowMargin * 2) * scale);
        int x, y;
        if (horizontal)
        {
            int anchor = bar.Right - (int)(320 * scale);
            IntPtr notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out var n) && n.Width > 0 && n.Left > bar.Left)
            {
                anchor = n.Left;
            }
            x = anchor - px - (int)(4 * scale);
            y = bar.Top + (bar.Height - px) / 2;
        }
        else
        {
            x = bar.Left + (bar.Width - px) / 2;
            y = bar.Bottom - (int)(220 * scale) - px;
        }
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, px, px, Native.SWP_NOACTIVATE);
        settings.PosX = x;
        settings.PosY = y;
        settings.Save();
    }

    /// <summary>
    /// Keeps the orb fully on one monitor, snaps it to work-area edges, and centers it on the taskbar
    /// when it was dropped onto the taskbar.
    /// </summary>
    public void SnapAndSave()
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }
        Native.GetWindowRect(Handle, out var r);
        var info = Native.GetMonitorInfo(Native.MonitorFromRect(ref r, Native.MONITOR_DEFAULTTONEAREST));
        var mon = info.rcMonitor;
        var work = info.rcWork;
        int m = MarginPx;
        int snap = (int)Math.Round(14 * Scale);
        int edge = (int)Math.Round(6 * Scale);

        // Visual circle bounds (without the shadow margin).
        int left = r.Left + m, top = r.Top + m, right = r.Right - m, bottom = r.Bottom - m;
        int cx = (left + right) / 2, cy = (top + bottom) / 2;
        int x = r.Left, y = r.Top;

        bool onBottomBar = work.Bottom < mon.Bottom && cy > work.Bottom;
        bool onTopBar = work.Top > mon.Top && cy < work.Top;
        bool onLeftBar = work.Left > mon.Left && cx < work.Left;
        bool onRightBar = work.Right < mon.Right && cx > work.Right;

        if (onBottomBar)
        {
            y = work.Bottom + (mon.Bottom - work.Bottom - r.Height) / 2;
        }
        else if (onTopBar)
        {
            y = mon.Top + (work.Top - mon.Top - r.Height) / 2;
        }
        else
        {
            // Near or past an edge: tuck it in with a small margin.
            if (top < work.Top + snap) y = work.Top + edge - m;
            if (bottom > work.Bottom - snap) y = work.Bottom - edge - (r.Height - m);
        }

        if (onLeftBar)
        {
            x = mon.Left + (work.Left - mon.Left - r.Width) / 2;
        }
        else if (onRightBar)
        {
            x = work.Right + (mon.Right - work.Right - r.Width) / 2;
        }
        else
        {
            if (left < work.Left + snap) x = work.Left + edge - m;
            if (right > work.Right - snap) x = work.Right - edge - (r.Width - m);
        }

        x = Math.Clamp(x, mon.Left - m, mon.Right - r.Width + m);
        y = Math.Clamp(y, mon.Top - m, mon.Bottom - r.Height + m);

        if (x != r.Left || y != r.Top)
        {
            Native.SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        settings.PosX = x;
        settings.PosY = y;
        settings.Save();
    }

    /// <summary>After monitors change, pull the orb back if its monitor disappeared.</summary>
    public void EnsureOnScreen()
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }
        Native.GetWindowRect(Handle, out var r);
        if (Native.MonitorFromRect(ref r, Native.MONITOR_DEFAULTTONULL) == IntPtr.Zero)
        {
            MoveToDefault();
        }
        else
        {
            SnapAndSave();
        }
    }
}
