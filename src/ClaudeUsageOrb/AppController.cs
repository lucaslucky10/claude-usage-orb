using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClaudeUsageOrb;

/// <summary>Wires the poller to the orb, the hover card, the tray icon and the menu.</summary>
internal sealed class AppController : IDisposable
{
    public const string RepoUrl = "https://github.com/lucaslucky10/claude-usage-orb";

    private readonly Application app;
    private readonly Settings settings;
    private readonly UsagePoller poller;
    private readonly OrbWindow orb;
    private readonly DetailPopup popup;
    private readonly TrayIcon tray;
    private readonly MenuHost menu;
    private readonly DispatcherTimer clock;
    private readonly DispatcherTimer guard;
    private readonly DispatcherTimer hoverShow;
    private readonly DispatcherTimer hoverHide;
    private readonly Native.WinEventProc foregroundProc;
    private IntPtr foregroundHook;
    private bool hiddenForFullscreen;
    private bool fullscreenOverride;

    public AppController(Application app)
    {
        this.app = app;
        settings = Settings.Load();
        Loc.Init(settings.Language);
        poller = new UsagePoller(settings);
        orb = new OrbWindow(settings);
        popup = new DetailPopup();
        tray = new TrayIcon();
        menu = new MenuHost();

        clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        clock.Tick += (_, _) => Render();
        guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        guard.Tick += (_, _) => Guard();
        hoverShow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        hoverShow.Tick += (_, _) => { hoverShow.Stop(); ShowPopup(); };
        hoverHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        hoverHide.Tick += (_, _) => { hoverHide.Stop(); popup.Hide(); };
        foregroundProc = (_, _, _, _, _, _, _) =>
        {
            menu.Close();
            Guard();
        };
    }

    public void Start()
    {
        poller.Changed += Render;

        orb.RefreshRequested += () => { poller.RefreshNow(); Render(); };
        orb.MenuRequested += () => { popup.Hide(); menu.Show(BuildMenu()); };
        orb.HoverChanged += OnOrbHover;
        orb.Moved += () => { if (popup.IsVisible) popup.PlaceNear(orb.Handle, OrbWindow.ShadowMargin); };

        popup.MouseEnter += (_, _) => hoverHide.Stop();
        popup.MouseLeave += (_, _) => hoverHide.Start();
        popup.SizeChanged += (_, _) => { if (popup.IsVisible) popup.PlaceNear(orb.Handle, OrbWindow.ShadowMargin); };

        tray.LeftClick += () =>
        {
            if (hiddenForFullscreen)
            {
                // Explicitly asked for while a full-screen app runs: stay visible until it ends.
                fullscreenOverride = true;
                hiddenForFullscreen = false;
                settings.ShowOrb = true;
            }
            else
            {
                settings.ShowOrb = !settings.ShowOrb;
            }
            settings.Save();
            UpdateVisibility();
        };
        tray.RightClick += () => menu.Show(BuildMenu());

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;

        foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            foregroundProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

        UpdateVisibility();
        poller.Start();
        clock.Start();
        guard.Start();
        Render();
        Log.Info("Started.");
    }

    /// <summary>Called when the exe is started again while already running: make the orb findable.</summary>
    public void Reveal()
    {
        settings.ShowOrb = true;
        settings.ClickThrough = false;
        fullscreenOverride = hiddenForFullscreen;
        hiddenForFullscreen = false;
        settings.Save();
        orb.ApplyClickThrough();
        UpdateVisibility();
        orb.EnsureOnScreen();
        orb.BringToFrontTopmost();
    }

    // ---------- rendering ----------

    private void Render()
    {
        var (orbModel, popupModel, trayModel) = Presenter.Build(settings, poller, DateTimeOffset.Now);
        orb.Visual.SetModel(orbModel);
        popup.Render(popupModel);
        tray.Update(trayModel.Number, System.Drawing.Color.FromArgb(trayModel.Ring.R, trayModel.Ring.G, trayModel.Ring.B),
            trayModel.Fraction, trayModel.Dim, trayModel.Tooltip);
    }

    // ---------- hover card ----------

    private void OnOrbHover(bool inside)
    {
        if (inside)
        {
            hoverHide.Stop();
            if (!popup.IsVisible)
            {
                hoverShow.Start();
            }
        }
        else
        {
            hoverShow.Stop();
            hoverHide.Start();
        }
    }

    private void ShowPopup()
    {
        if (!orb.IsVisible)
        {
            return;
        }
        Render();
        popup.Show();
        popup.PlaceNear(orb.Handle, OrbWindow.ShadowMargin);
    }

    // ---------- visibility / z-order ----------

    private void UpdateVisibility()
    {
        bool visible = settings.ShowOrb && !hiddenForFullscreen;
        if (visible && !orb.IsVisible)
        {
            orb.Show();
            orb.BringToFrontTopmost();
        }
        else if (!visible && orb.IsVisible)
        {
            popup.Hide();
            orb.Hide();
        }
    }

    /// <summary>
    /// The taskbar is itself a topmost window and jumps above other topmost windows when clicked,
    /// so the orb re-asserts its z-order whenever the foreground changes (and every 2 s as a fallback).
    /// </summary>
    private void Guard()
    {
        bool fullscreen = settings.HideInFullscreen && orb.Handle != IntPtr.Zero && FullscreenDetector.IsActive(orb.Handle);
        if (!fullscreen)
        {
            fullscreenOverride = false;
        }
        bool hide = fullscreen && !fullscreenOverride;
        if (hide != hiddenForFullscreen)
        {
            hiddenForFullscreen = hide;
            UpdateVisibility();
        }
        // While the menu is open it must stay above the orb.
        if (settings.AlwaysOnTop && !menu.IsOpen)
        {
            orb.BringToFrontTopmost();
        }
    }

    // ---------- system events ----------

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            app.Dispatcher.BeginInvoke(async () =>
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(6));
                poller.RefreshNow();
            });
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => orb.EnsureOnScreen());

    private void OnNetworkChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            app.Dispatcher.BeginInvoke(() =>
            {
                if (poller.Status == PollStatus.NetworkError)
                {
                    poller.RefreshNow();
                }
            });
        }
    }

    // ---------- menu ----------

    private IReadOnlyList<MenuSpec> BuildMenu()
    {
        void Change(Action apply)
        {
            apply();
            settings.Save();
            Render();
        }

        var sizes = Settings.Sizes.Select(px => new MenuSpec($"{Loc.SizeName(px)} ({px})",
            () => Change(() => { settings.OrbSize = px; orb.ApplySize(); }), settings.OrbSize == px)).ToList();

        var opacities = new[] { 1.0, 0.85, 0.7, 0.55 }.Select(o => new MenuSpec($"{o * 100:0}%",
            () => Change(() => { settings.Opacity = o; orb.Opacity = o; }), Math.Abs(settings.Opacity - o) < 0.01)).ToList();

        var center = new List<MenuSpec>
        {
            new(Loc.MenuCenterSession, () => Change(() => settings.CenterMetric = "session"), settings.CenterMetric == "session"),
            new(Loc.MenuCenterLowest, () => Change(() => settings.CenterMetric = "lowest"), settings.CenterMetric == "lowest"),
        };

        var polls = Settings.PollChoices.Select(sec => new MenuSpec(Loc.PollName(sec),
            () => Change(() => { settings.PollSeconds = sec; poller.RefreshNow(); }), settings.PollSeconds == sec)).ToList();

        var languages = new List<MenuSpec>
        {
            new("한국어", () => SwitchLanguage("ko"), Loc.Ko),
            new("English", () => SwitchLanguage("en"), !Loc.Ko),
        };

        var system = new List<MenuSpec>
        {
            new(Loc.MenuStartup, () => StartupRegistration.Set(!StartupRegistration.IsEnabled()), StartupRegistration.IsEnabled()),
        };
        if (TrayPromotion.IsPromoted() is { } promoted)
        {
            system.Add(new(Loc.MenuTrayPromote, () => TrayPromotion.Set(!promoted), promoted));
        }

        return
        [
            new(Loc.MenuRefresh, () => { poller.RefreshNow(); Render(); }),
            MenuSpec.Line,
            new(Loc.MenuShowOrb, () => Change(() => { settings.ShowOrb = !settings.ShowOrb; hiddenForFullscreen = false; UpdateVisibility(); }), settings.ShowOrb),
            new(Loc.MenuSize, Children: sizes),
            new(Loc.MenuOpacity, Children: opacities),
            new(Loc.MenuCenter, Children: center),
            new(Loc.MenuPoll, Children: polls),
            MenuSpec.Line,
            new(Loc.MenuAlwaysOnTop, () => Change(() => { settings.AlwaysOnTop = !settings.AlwaysOnTop; orb.Topmost = settings.AlwaysOnTop; orb.BringToFrontTopmost(); }), settings.AlwaysOnTop),
            new(Loc.MenuHideFullscreen, () => Change(() => { settings.HideInFullscreen = !settings.HideInFullscreen; Guard(); }), settings.HideInFullscreen),
            new(Loc.MenuAutoRenew, () => Change(() => { settings.AutoRenewToken = !settings.AutoRenewToken; if (settings.AutoRenewToken) poller.AllowRenewNow(); }), settings.AutoRenewToken),
            new(Loc.MenuPace, () => Change(() => settings.ShowPaceMarker = !settings.ShowPaceMarker), settings.ShowPaceMarker),
            new(Loc.MenuLock, () => Change(() => settings.LockPosition = !settings.LockPosition), settings.LockPosition),
            new(Loc.MenuClickThrough, () => Change(() => { settings.ClickThrough = !settings.ClickThrough; orb.ApplyClickThrough(); }), settings.ClickThrough),
            MenuSpec.Line,
            new(Loc.MenuDockTaskbar, () => Change(() => { settings.ShowOrb = true; UpdateVisibility(); orb.DockOntoTaskbar(); })),
            new(Loc.MenuResetPosition, () => Change(() => { settings.ShowOrb = true; UpdateVisibility(); orb.MoveToDefault(); })),
            MenuSpec.Line,
            .. system,
            new(Loc.MenuLanguage, Children: languages),
            new(Loc.MenuOpenFolder, () => Open(AppPaths.DataDir)),
            new(Loc.MenuAbout, () => Open(RepoUrl)),
            MenuSpec.Line,
            new(Loc.MenuExit, () => app.Shutdown()),
        ];
    }

    private void SwitchLanguage(string language)
    {
        settings.Language = language;
        settings.Save();
        Loc.Init(language);
        poller.Reparse();
        Render();
    }

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {target}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        if (foregroundHook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(foregroundHook);
        }
        clock.Stop();
        guard.Stop();
        poller.Dispose();
        tray.Dispose();
        settings.Save();
    }
}

internal static class FullscreenDetector
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
    };

    /// <summary>True when a borderless window fills the monitor the orb is on (video, game, presentation).</summary>
    public static bool IsActive(IntPtr orb)
    {
        if (Native.SHQueryUserNotificationState(out int state) == 0 &&
            state is Native.QUNS_RUNNING_D3D_FULL_SCREEN or Native.QUNS_PRESENTATION_MODE)
        {
            return true;
        }

        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            return false;
        }
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId || ShellClasses.Contains(Native.GetClassName(fg)))
        {
            return false;
        }
        // Maximized windows can cover the whole monitor when the taskbar auto-hides; they are not full-screen.
        if ((Native.GetStyle(fg, Native.GWL_STYLE) & Native.WS_CAPTION) == Native.WS_CAPTION || Native.IsZoomed(fg))
        {
            return false;
        }

        IntPtr monitor = Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST);
        if (monitor != Native.MonitorFromWindow(orb, Native.MONITOR_DEFAULTTONEAREST))
        {
            return false;
        }
        Native.GetWindowRect(fg, out var r);
        var m = Native.GetMonitorInfo(monitor).rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }
}
